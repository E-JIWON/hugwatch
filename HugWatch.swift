import SwiftUI
import UserNotifications

// 설정 — 설정 창에서 넣고 이 맥의 UserDefaults 에만 저장. 토큰·비밀번호는 키체인 (코드엔 아무 값도 없음)
func pref(_ k: String) -> String { (UserDefaults.standard.string(forKey: k) ?? "").trimmingCharacters(in: .whitespaces) }
func site(_ s: String) -> String {  // "company.atlassian.net" → "https://company.atlassian.net"
    let t = s.trimmingCharacters(in: CharacterSet(charactersIn: "/ "))
    return t.isEmpty || t.hasPrefix("http") ? t : "https://" + t
}
var email: String { pref("email") }              // Atlassian 계정 (Jira·Bitbucket 공통)
var jira: String { site(pref("jiraSite")) }
var bbWorkspace: String { pref("bbWorkspace") }
var daou: String { site(pref("daouURL")) }       // 다우오피스 (선택)
var daouId: String { pref("daouId") }
var repoRoots: [String] {                        // 병합 추적할 git 저장소들이 있는 폴더
    let raw = pref("repoRoots")
    return (raw.isEmpty ? "~/work, ~/github, ~/Developer, ~/projects" : raw).split(separator: ",")
        .map { NSString(string: $0.trimmingCharacters(in: .whitespaces)).expandingTildeInPath }
}
let interval: TimeInterval = 600

struct Msg: LocalizedError { let errorDescription: String?; init(_ s: String) { errorDescription = s } }
struct Item: Identifiable {
    let id, title, sub, url, fp: String
    var group = ""          // 작업 탭: 티켓 키로 묶기
    var dots: [Bool?] = []  // 작업 탭: develop·release·master 에 들어갔나 (저장소에 없는 단계는 nil)
    var notifyNew = false   // 처음 보여도 알림 (자동 추적 = 방금 PR 병합된 내 브랜치)
    var tag = "", tone = "" // 오른쪽 상태 배지 (Jira 상태). tone: blue / green / gray
    var detail = ""         // 세 번째 줄 (PR: 누가 승인·코멘트했나)
    var people: [Person] = [] // PR 리뷰어 프로필
}
struct Person: Hashable { let name, avatar, state: String }  // state: ok 승인 / ask 수정요청 / wait 대기
struct Section: Identifiable { let id: String; var items: [Item] = []; var error: String? }
// 새 소식 탭에 쌓이는 알림 기록
struct Event: Codable, Identifiable { var id = UUID().uuidString; let source, title, sub, url: String; let date: Date; var read = false }

// 할 일 (띄운 창용). 미루기: snooze 날짜까지 숨김 / someday = 나중에
struct Todo: Codable, Identifiable {
    var id = UUID().uuidString
    var text: String
    var done = false
    var doneAt: Date?
    var snooze: Date?
    var someday = false
    var isActive: Bool { !done && !someday && (snooze ?? .distantPast) <= Date() }
    var isLater: Bool { !done && !isActive }
}

let ticketRe = #"[A-Z][A-Z0-9]+-[0-9]+"#
func ticket(_ s: String) -> String? { s.range(of: ticketRe, options: .regularExpression).map { String(s[$0]) } }

// PR fp "코멘트/승인/수정요청" 이 늘어난 것 → "코멘트 +1, 승인 +1"
func prChange(_ old: String, _ new: String) -> String? {
    let o = old.split(separator: "/").compactMap { Int($0) }, n = new.split(separator: "/").compactMap { Int($0) }
    guard o.count == 3, n.count == 3 else { return nil }
    let parts = zip(["코멘트", "승인", "수정 요청"], zip(o, n)).compactMap { label, v in v.1 > v.0 ? "\(label) +\(v.1 - v.0)" : nil }
    return parts.isEmpty ? nil : parts.joined(separator: ", ")
}

// 작업 fp "sha|들어간 브랜치들" 변화 → "→ master 반영됨" / "→ develop/1.4.0 병합됨" / "브랜치에 새 커밋"
func workChange(_ old: String?, _ new: String) -> String? {
    func parse(_ f: String) -> (sha: String, into: Set<String>)? {
        let p = f.split(separator: "|", omittingEmptySubsequences: false).map(String.init)
        return p.count == 2 ? (p[0], Set(p[1].split(separator: ",").map(String.init))) : nil
    }
    guard let n = parse(new) else { return nil }
    let o = old.flatMap(parse)
    let rank = { (b: String) in b.hasPrefix("master") || b.hasPrefix("main") ? 3 : b.hasPrefix("release") ? 2 : 1 }
    let added = n.into.subtracting(o?.into ?? [])
    if let best = added.max(by: { rank($0) != rank($1) ? rank($0) < rank($1) : $0.compare($1, options: .numeric) == .orderedAscending }) {
        return rank(best) == 3 ? "→ \(best) 반영됨" : "→ \(best) 병합됨"
    }
    if let o, o.sha != n.sha { return "브랜치에 새 커밋" }
    return nil
}

// MARK: - 헬퍼

func dig(_ o: Any?, _ path: String) -> Any? {
    path.split(separator: ".").reduce(o) { ($0 as? [String: Any])?[String($1)] }
}
func str(_ o: Any?, _ path: String) -> String { dig(o, path).map { "\($0)" } ?? "" }

@discardableResult
func run(_ exe: String, _ args: [String]) throws -> String {
    let p = Process(), pipe = Pipe()
    p.executableURL = URL(fileURLWithPath: exe)
    p.arguments = args
    p.environment = ProcessInfo.processInfo.environment.merging(["GIT_TERMINAL_PROMPT": "0"]) { $1 }
    p.standardOutput = pipe
    p.standardError = FileHandle.nullDevice
    try p.run()
    let out = String(decoding: pipe.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self)
    p.waitUntilExit()
    guard p.terminationStatus == 0 else { throw Msg("\((exe as NSString).lastPathComponent) \(args.prefix(3).joined(separator: " ")) 실패") }
    return out.trimmingCharacters(in: .whitespacesAndNewlines)
}

// 비밀값은 키체인 (서비스 hugwatch). /usr/bin/security 로 읽고 써서 키체인 접근 허용 창이 안 뜸
func secret(_ key: String) throws -> String {
    do { return try run("/usr/bin/security", ["find-generic-password", "-s", "hugwatch", "-a", key, "-w"]) }
    catch { throw Msg("설정에서 \(key) 를 넣어주세요") }
}
func hasSecret(_ key: String) -> Bool { (try? secret(key)) != nil }

// 비밀값을 명령줄 인자로 넘기면 ps 에 잠깐 보여서 security -i 표준입력으로 넣음
func setSecret(_ key: String, _ value: String) {
    let p = Process(), pipe = Pipe()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/security")
    p.arguments = ["-i"]
    p.standardInput = pipe
    p.standardOutput = FileHandle.nullDevice
    p.standardError = FileHandle.nullDevice
    guard (try? p.run()) != nil else { return }
    let esc = value.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
    pipe.fileHandleForWriting.write(Data("add-generic-password -U -s hugwatch -a \(key) -w \"\(esc)\"\n".utf8))
    try? pipe.fileHandleForWriting.close()
    p.waitUntilExit()
}

func getJSON(_ url: URL, auth: String? = nil, post: [String: Any]? = nil) async throws -> Any {
    var r = URLRequest(url: url)
    r.setValue("application/json", forHTTPHeaderField: "Accept")
    if let auth { r.setValue("Basic " + Data(auth.utf8).base64EncodedString(), forHTTPHeaderField: "Authorization") }
    if let post {
        r.httpMethod = "POST"
        r.setValue("application/json", forHTTPHeaderField: "Content-Type")
        r.httpBody = try JSONSerialization.data(withJSONObject: post)
    }
    let (d, res) = try await URLSession.shared.data(for: r)
    let code = (res as? HTTPURLResponse)?.statusCode ?? 0
    guard code == 200, let j = try? JSONSerialization.jsonObject(with: d) else { throw Msg("HTTP \(code) · \(url.host ?? "")\(url.path)") }
    return j
}

// MARK: - 감시 대상

func myPRs() async throws -> [Item] {
    let auth = "\(email):\(try secret("bitbucket-token"))"
    let uuid = str(try await getJSON(URL(string: "https://api.bitbucket.org/2.0/user")!, auth: auth), "uuid")
    let me = uuid.addingPercentEncoding(withAllowedCharacters: .alphanumerics.union(.init(charactersIn: "-")))!
    let j = try await getJSON(URL(string: "https://api.bitbucket.org/2.0/workspaces/\(bbWorkspace)/pullrequests/\(me)?state=OPEN&pagelen=50&fields=%2Bvalues.participants")!, auth: auth)
    var out: [Item] = []
    for pr in dig(j, "values") as? [Any] ?? [] {
        let ps = dig(pr, "participants") as? [Any] ?? []
        let ok = ps.filter { str($0, "approved") == "1" }.map { str($0, "user.display_name") }
        let ask = ps.filter { str($0, "state") == "changes_requested" }.map { str($0, "user.display_name") }
        let waiting = ps.filter { str($0, "role") == "REVIEWER" && str($0, "approved") != "1" && str($0, "state") != "changes_requested" }.count
        let order = ["ok": 0, "ask": 1, "wait": 2]
        let people = ps.map { p -> Person in
            let st = str(p, "approved") == "1" ? "ok" : str(p, "state") == "changes_requested" ? "ask" : "wait"
            return Person(name: str(p, "user.display_name"), avatar: str(p, "user.links.avatar.href"), state: st)
        }.sorted { order[$0.state]! < order[$1.state]! }
        // 코멘트 작성자별 개수 (내 코멘트·삭제된 코멘트 제외). 못 가져오면 전체 개수로
        var by: [String: Int] = [:]
        let cj = try? await getJSON(URL(string: str(pr, "links.comments.href") + "?pagelen=100")!, auth: auth)
        for c in dig(cj, "values") as? [Any] ?? [] where str(c, "deleted") != "1" && str(c, "user.uuid") != uuid { by[str(c, "user.display_name"), default: 0] += 1 }
        let cm = cj == nil ? Int(str(pr, "comment_count")) ?? 0 : by.values.reduce(0, +)
        let repo = str(pr, "destination.repository.name")
        var parts: [String] = []
        if !ok.isEmpty { parts.append("승인 " + ok.joined(separator: ", ")) }
        if !ask.isEmpty { parts.append("수정요청 " + ask.joined(separator: ", ")) }
        if !by.isEmpty { parts.append("코멘트 " + by.sorted { $0.value > $1.value }.map { "\($0.key) \($0.value)" }.joined(separator: ", ")) }
        if waiting > 0 { parts.append("대기 \(waiting)명") }
        let detail = parts.joined(separator: " · ")
        out.append(Item(id: "\(repo)#\(str(pr, "id"))", title: str(pr, "title"),
                        sub: "\(repo) · 코멘트 \(cm) · 승인 \(ok.count)" + (ask.isEmpty ? "" : " · 수정요청 \(ask.count)"),
                        url: str(pr, "links.html.href"), fp: "\(cm)/\(ok.count)/\(ask.count)", detail: detail, people: people))
    }
    return out
}

func jiraAssigned() async throws -> [Item] {
    var c = URLComponents(string: "\(jira)/rest/api/3/search/jql")!
    c.queryItems = [.init(name: "jql", value: "assignee = currentUser() AND statusCategory != Done ORDER BY created DESC"),
                    .init(name: "fields", value: "summary,status"), .init(name: "maxResults", value: "50")]
    let auth = "\(email):\(try secret("jira-token"))"
    // 인증 실패해도 검색은 익명으로 200 + 빈 목록을 줘서, 로그인부터 확인
    do { _ = try await getJSON(URL(string: "\(jira)/rest/api/3/myself")!, auth: auth) } catch { throw Msg("Jira 인증 실패 — 토큰 다시 넣기") }
    let j = try await getJSON(c.url!, auth: auth)
    return (dig(j, "issues") as? [Any] ?? []).map { i in
        let key = str(i, "key"), status = str(i, "fields.status.name")
        let tone = status.contains("완료") ? "green" : str(i, "fields.status.statusCategory.key") == "indeterminate" ? "blue" : "gray"
        return Item(id: key, title: str(i, "fields.summary"), sub: key, url: "\(jira)/browse/\(key)", fp: "1", tag: status, tone: tone)
    }
}

// 로그인 실패하면 앱 재시작 전까지 재시도 안 함 — 10분마다 틀린 비번 넣다 계정 잠기는 것 방지
var daouFail: String?

func daouLogin() async throws {
    if let daouFail { throw Msg(daouFail) }
    do { try await daouLoginOnce() } catch { daouFail = error.localizedDescription + " (앱 재시작 시 재시도)"; throw error }
}

func daouLoginOnce() async throws {
    var r = URLRequest(url: URL(string: "\(daou)/api/login")!)
    r.httpMethod = "POST"
    r.setValue("application/json", forHTTPHeaderField: "Content-Type")
    r.httpBody = try JSONSerialization.data(withJSONObject: ["username": daouId, "password": try secret("daou-password"), "captcha": "", "returnUrl": ""])
    let (d, res) = try await URLSession.shared.data(for: r)
    let j = try? JSONSerialization.jsonObject(with: d)
    let code = (res as? HTTPURLResponse)?.statusCode ?? 0
    guard code == 200 else { throw Msg("메일 로그인 실패(\(code)): \(str(j, "message"))") }
    if ["normal", "regist"].contains(str(j, "data.certType")) { throw Msg("메일 서버에 2차 인증이 켜져 있어 자동 로그인 불가") }
}

// 다우오피스 웹의 '안 읽은 메일' 보기와 같은 요청 (최신 20통)
func mail() async throws -> [Item] {
    let url = URL(string: "\(daou)/api/mail/message/list")!
    let body = ["folder": "all", "extFolder": "on", "extType": "unseen", "flag": "U"]
    var j = try? await getJSON(url, post: body)  // 세션 살아있으면 그대로, 만료면 로그인 후 재시도
    if j == nil || str(j, "code") != "200" { try await daouLogin(); j = try await getJSON(url, post: body) }
    return (dig(j, "data.messageList") as? [Any] ?? []).map { m in
        Item(id: "\(str(m, "folderName"))/\(str(m, "id"))", title: str(m, "subject"), sub: str(m, "fromToSimple"), url: "\(daou)/app/mail", fp: "1")
    }
}

// ~/work, ~/github 아래 git 저장소: 이름 → 경로
func localRepos() -> [String: String] {
    var out: [String: String] = [:]
    for root in repoRoots {
        for name in (try? FileManager.default.contentsOfDirectory(atPath: root)) ?? [] where FileManager.default.fileExists(atPath: "\(root)/\(name)/.git") {
            out[name] = out[name] ?? "\(root)/\(name)"
        }
    }
    return out
}

// 단계 브랜치 = develop · develop/1.4.0(-studio) · release/3.6.1 · master · main. develop/feature-… 같은 임시 동기화 브랜치는 제외
let mainline = #"^((develop|release)(/[0-9][^/]*)?|master|main)$"#

// Bitbucket 병합 커밋 "Merged in <브랜치> (pull request #N)": 두 번째 부모 = 브랜치 마지막 커밋, N = PR 번호
// (PR 병합 후 브랜치가 지워져도 이걸로 추적·링크)
func mergeInfo(_ path: String, _ branch: String) -> (head: String, pr: String?)? {
    let line = (try? run("/usr/bin/git", ["-C", path, "log", "--all", "--merges", "-1", "-F", "--grep=Merged in \(branch) (pull request", "--format=%P|%s"])) ?? ""
    let parts = line.split(separator: "|", maxSplits: 1).map(String.init)
    let parents = parts.first?.split(separator: " ") ?? []
    guard parts.count == 2, parents.count >= 2 else { return nil }
    return (String(parents[1]), parts[1].range(of: #"(?<=pull request #)[0-9]+"#, options: .regularExpression).map { String(parts[1][$0]) })
}

// 최근 30일 안에 PR로 병합된 "내" 브랜치 (병합 후 지워져 로컬에 없는 것까지). 브랜치 마지막 커밋 작성자로 판별
func recentMergedMine(_ path: String) -> [String] {
    let me = (try? run("/usr/bin/git", ["-C", path, "config", "user.email"])) ?? email
    let merges: [(head: String, branch: String)] = ((try? run("/usr/bin/git", ["-C", path, "log", "--all", "--merges", "--since=30.days", "--grep=^Merged in ", "--format=%P|%s"])) ?? "")
        .split(separator: "\n").compactMap { line in
            let parts = line.split(separator: "|", maxSplits: 1).map(String.init)
            guard parts.count == 2, case let parents = parts[0].split(separator: " "), parents.count >= 2,
                  let r = parts[1].range(of: #"(?<=^Merged in )\S+(?= \(pull request)"#, options: .regularExpression) else { return nil }
            return (String(parents[1]), String(parts[1][r]))
        }
    guard !merges.isEmpty else { return [] }
    // 브랜치 마지막 커밋이 "내가 쓴 일반 커밋"일 때만 내 브랜치. 마지막이 병합 커밋이면 남의 묶음 브랜치에
    // 내 PR을 합쳐준 것뿐이라 제외 (안 그러면 같은 작업이 묶음 브랜치 이름으로 한 줄 더 생김)
    let mine = Set(((try? run("/usr/bin/git", ["-C", path, "log", "--no-walk", "--format=%H|%ae|%P"] + merges.map(\.head))) ?? "")
        .split(separator: "\n").compactMap { l -> String? in
            let f = l.split(separator: "|", omittingEmptySubsequences: false)
            return f.count == 3 && f[1] == me && !f[2].contains(" ") ? String(f[0]) : nil
        })
    var seen = Set<String>()
    return merges.filter { mine.contains($0.head) && $0.branch.range(of: mainline, options: .regularExpression) == nil && seen.insert($0.branch).inserted }.map(\.branch)
}

// 고르기 후보: 저장소마다 최근 30일 로컬 브랜치 (Fork에서 만들거나 체크아웃한 것). 병합된 내 브랜치는 자동 추적이라 제외
func recentBranches() -> [(id: String, note: String)] {
    let cutoff = Date().timeIntervalSince1970 - 30 * 86400
    return localRepos().sorted { $0.key < $1.key }.flatMap { name, path -> [(id: String, note: String)] in
        let cur = (try? run("/usr/bin/git", ["-C", path, "branch", "--show-current"])) ?? ""
        let refs = (try? run("/usr/bin/git", ["-C", path, "for-each-ref", "--sort=-committerdate", "--format=%(committerdate:unix) %(refname:short)", "refs/heads"])) ?? ""
        let local: [String] = refs.split(separator: "\n").compactMap { line in
            let p = line.split(separator: " ", maxSplits: 1).map(String.init)
            guard p.count == 2, (Double(p[0]) ?? 0) > cutoff, p[1].range(of: mainline, options: .regularExpression) == nil else { return nil }
            return p[1]
        }
        return local.map { ("\(name):\($0)", $0 == cur ? "지금" : "") }
    }
}

// "my-app/me/ABC-1" → "my-app:me/ABC-1" (맨 앞이 실제 저장소 이름일 때만; 브랜치에도 / 가 있어서)
func splitRepo(_ input: String, _ repoNames: Set<String>) -> String? {
    let p = input.split(separator: "/", maxSplits: 1).map(String.init)
    return p.count == 2 && repoNames.contains(p[0]) ? "\(p[0]):\(p[1])" : nil
}

// repo:branch / repo/branch / 브랜치 이름만 → repo:branch (이름만이면 그 원격 브랜치가 있는 저장소를 찾음)
func resolveBranch(_ input: String) -> String {
    guard !input.contains(":") else { return input }
    let repos = localRepos()
    if let r = splitRepo(input, Set(repos.keys)) { return r }
    let hit = repos.sorted { $0.key < $1.key }.first { _, path in
        (try? run("/usr/bin/git", ["-C", path, "rev-parse", "--verify", "--quiet", "origin/\(input)"])) != nil
    }
    return hit.map { "\($0.key):\(input)" } ?? input
}

// origin 주소 → 저장소 웹 주소 (Bitbucket / GitHub)
func repoWeb(_ remote: String) -> String {
    remote
        .replacingOccurrences(of: #"^(https://([^@/]+@)?|git@)"#, with: "https://", options: .regularExpression)
        .replacingOccurrences(of: #"(\.com|\.org):"#, with: "$1/", options: .regularExpression)
        .replacingOccurrences(of: #"\.git$"#, with: "", options: .regularExpression)
}

func webURL(remote: String, branch: String) -> String {
    let base = repoWeb(remote)
    return base.contains("github.com") ? "\(base)/tree/\(branch)" : "\(base)/branch/\(branch)"
}

// 저장소에 있는 단계(develop·release·master)마다 내 커밋이 들어간 브랜치 (없으면 nil)
func stages(_ into: [String], _ remote: [String]) -> [(stage: String, hit: String?)] {
    ["develop", "release", "master|main"].compactMap { st in
        let re = "^(\(st))(/[0-9][^/]*)?$"
        guard remote.contains(where: { $0.range(of: re, options: .regularExpression) != nil }) else { return nil }
        let hit = into.filter { $0.range(of: re, options: .regularExpression) != nil }.max { $0.compare($1, options: .numeric) == .orderedAscending }
        return (String(st.split(separator: "|")[0]), hit)
    }
}

// "release/5.0.0 ✓ → master ✗"
func stageLine(_ into: [String], _ remote: [String]) -> String {
    stages(into, remote).map { $0.hit.map { "\($0) ✓" } ?? "\($0.stage) ✗" }.joined(separator: " → ")
}

// 내 작업: 직접 고른 브랜치 + 최근 30일 PR로 병합된 내 브랜치(자동). 브랜치 마지막 커밋이 어느 단계까지 갔나
func workStatus(_ manual: [String]) -> [Item] {
    let repos = localRepos()
    // 최근 30일에 내 커밋이 있는 저장소만 fetch·자동 탐색 (개인 저장소까지 매번 fetch 안 하게)
    let active = repos.filter { _, path in
        let me = (try? run("/usr/bin/git", ["-C", path, "config", "user.email"])) ?? email
        return !((try? run("/usr/bin/git", ["-C", path, "log", "--all", "-1", "--since=30.days", "--author=\(me)", "--format=%h"])) ?? "").isEmpty
    }
    let manualPaths = manual.compactMap { e in e.split(separator: ":").first.flatMap { repos[String($0)] } }
    for path in Set(active.values).union(manualPaths) { _ = try? run("/usr/bin/git", ["-C", path, "fetch", "--quiet", "--prune", "origin"]) }
    let auto = active.sorted { $0.key < $1.key }.flatMap { name, path in recentMergedMine(path).map { "\(name):\($0)" } }
    var remotes: [String: [String]] = [:]
    let entries = manual.map { ($0, false) } + auto.filter { !manual.contains($0) }.map { ($0, true) }
    return entries.map { e, isAuto -> Item in
        let parts = e.split(separator: ":", maxSplits: 1).map(String.init)
        guard parts.count == 2, let path = repos[parts[0]] else {
            return Item(id: e, title: e, sub: "저장소를 못 찾음", url: "", fp: "", group: e)
        }
        let (repo, branch) = (parts[0], parts[1])
        // 병합 후 브랜치가 지워져도 마지막 커밋으로 계속 추적
        let live = try? run("/usr/bin/git", ["-C", path, "rev-parse", "origin/\(branch)"])
        let merged = mergeInfo(path, branch)
        let sha = live ?? UserDefaults.standard.string(forKey: "sha.\(e)") ?? merged?.head ?? ""
        // 링크: 병합된 PR > 살아있는 브랜치 > 커밋 (지워진 브랜치 주소는 404)
        let remoteURL = (try? run("/usr/bin/git", ["-C", path, "remote", "get-url", "origin"])) ?? ""
        let base = repoWeb(remoteURL), gh = base.contains("github.com")
        let url = merged?.pr.map { "\(base)/pull-requests/\($0)" } ?? (live != nil ? webURL(remote: remoteURL, branch: branch) : "\(base)/\(gh ? "commit" : "commits")/\(sha)")
        let group = ticket(branch) ?? branch
        guard !sha.isEmpty else { return Item(id: e, title: "\(repo) · \(branch)", sub: "원격 브랜치 없음", url: url, fp: "", group: group) }
        UserDefaults.standard.set(sha, forKey: "sha.\(e)")
        let remote = remotes[path] ?? ((try? run("/usr/bin/git", ["-C", path, "branch", "-r", "--format=%(refname:lstrip=3)"])) ?? "").split(separator: "\n").map(String.init)
        remotes[path] = remote
        let into = ((try? run("/usr/bin/git", ["-C", path, "branch", "-r", "--contains", sha, "--format=%(refname:lstrip=3)"])) ?? "")
            .split(separator: "\n").map(String.init)
            .filter { $0 != branch && $0.range(of: mainline, options: .regularExpression) != nil }
        let stg = stages(into, remote)
        return Item(id: e, title: "\(repo) · \(branch)", sub: stg.last { $0.hit != nil }?.hit ?? "아직 병합 전",
                    url: url, fp: "\(sha)|\(into.sorted().joined(separator: ","))", group: group,
                    dots: ["develop", "release", "master"].map { s in stg.first { $0.stage == s }.map { $0.hit != nil } },
                    notifyNew: isAuto)
    }
}

// MARK: - 도트 인베이더

// 원작 스페이스 인베이더(게). 두 프레임 번갈아 → 행진 (팔 내림 / 팔 올림)
let invader = [[
    "..#.....#..",
    "...#...#...",
    "..#######..",
    ".##.###.##.",
    "###########",
    "#.#######.#",
    "#.#.....#.#",
    "...##.##...",
], [
    "..#.....#..",
    "#..#...#..#",
    "#.#######.#",
    "###.###.###",
    "###########",
    ".#########.",
    "..#.....#..",
    ".#.......#.",
]]
let hop = [0, 1, 2, 2, 1, 0, 1, 2, 2, 1, 0, 0, 0, 0]  // 푱푱 + 쉼 (70ms 프레임)

// macOS 26 메뉴바는 아이콘을 ~80% 불투명도로 그려서 연한 색은 흐릿함 → 쨍한 색 + 진한 테두리
let violet = NSColor(red: 0.47, green: 0.33, blue: 1.0, alpha: 1)
let outline = NSColor(red: 0.13, green: 0.09, blue: 0.33, alpha: 1)

// 2.5pt/칸 → 레티나 5px, 테두리+점프 포함 30pt = 메뉴바 항목 높이. 빈칸 중 몸에 닿은 칸은 테두리
func spriteImage(frame: Int = 0, dy: Int = 0, color: NSColor = violet, px: CGFloat = 2.5) -> NSImage {
    let raw = invader[frame % invader.count]
    let w = raw[0].count + 2
    let blank = String(repeating: ".", count: w)
    let cells = ([blank] + raw.map { "." + $0 + "." } + [blank]).map(Array.init)
    func filled(_ x: Int, _ y: Int) -> Bool { cells.indices.contains(y) && cells[y].indices.contains(x) && cells[y][x] != "." }
    return NSImage(size: NSSize(width: CGFloat(w) * px, height: CGFloat(cells.count + 2) * px), flipped: true) { _ in
        for (y, row) in cells.enumerated() {
            for (x, ch) in row.enumerated() {
                let c: NSColor? = switch ch {
                case "#": color
                default: [(1, 0), (-1, 0), (0, 1), (0, -1)].contains { filled(x + $0.0, y + $0.1) } ? outline : nil
                }
                c?.setFill()
                if c != nil { NSRect(x: CGFloat(x) * px, y: CGFloat(y + 2 - dy) * px, width: px, height: px).fill() }
            }
        }
        return true
    }
}

// MARK: - 상태

@MainActor final class Watcher: ObservableObject {
    @Published var sections: [Section] = []
    @Published var lastCheck: Date?
    // 새 소식 생기면 팝오버 열 때까지 계속 푱푱 + 무지개
    @Published var badge = false {
        didSet {
            guard badge != oldValue else { return }
            anim?.invalidate()
            tick = 0
            if badge { anim = Timer.scheduledTimer(withTimeInterval: 0.07, repeats: true) { _ in Task { @MainActor in self.tick += 1 } } }
        }
    }
    @Published var busy = false
    @Published var tick = 0
    @Published var notifOff: String?
    @Published var candidates: [(id: String, note: String)] = []
    // 우클릭으로 숨긴 항목 ("섹션/id"). 화면에서만 빼고 감시·알림 기준값은 그대로
    @Published var hidden = Set(UserDefaults.standard.stringArray(forKey: "hidden") ?? []) {
        didSet { UserDefaults.standard.set(Array(hidden), forKey: "hidden") }
    }
    // 새 소식: 보낸 알림 기록 (최근 50개)
    @Published var events = (try? JSONDecoder().decode([Event].self, from: UserDefaults.standard.data(forKey: "events") ?? Data())) ?? [] {
        didSet { UserDefaults.standard.set(try? JSONEncoder().encode(events), forKey: "events") }
    }
    // 할 일. 완료한 지 7일 지난 건 정리
    @Published var todos = ((try? JSONDecoder().decode([Todo].self, from: UserDefaults.standard.data(forKey: "todos") ?? Data())) ?? [])
        .filter { !$0.done || ($0.doneAt ?? .distantFuture) > Date().addingTimeInterval(-7 * 86400) } {
        didSet { UserDefaults.standard.set(try? JSONEncoder().encode(todos), forKey: "todos") }
    }
    func updateTodo(_ id: String, _ change: (inout Todo) -> Void) {
        if let k = todos.firstIndex(where: { $0.id == id }) { change(&todos[k]) }
    }
    @Published var branches = UserDefaults.standard.stringArray(forKey: "branches") ?? [] {
        didSet { UserDefaults.standard.set(branches, forKey: "branches") }
    }
    // 티켓 키 → Jira 제목 (병합 탭에 무슨 작업인지 보여주기). 제목은 잘 안 바뀌어서 한 번 받으면 저장
    @Published var summaries = UserDefaults.standard.dictionary(forKey: "summaries") as? [String: String] ?? [:] {
        didSet { UserDefaults.standard.set(summaries, forKey: "summaries") }
    }
    private var anim: Timer?

    func summary(_ key: String) -> String? { summaries[key].flatMap { $0.isEmpty ? nil : $0 } }

    // 병합 탭 티켓들의 Jira 제목·상태. release 에서 먼저 배포하는 팀은 M 만으론 배포 여부를 몰라서 Jira 상태를 같이 보여줌
    @Published var jiraStatus: [String: (name: String, done: Bool)] = [:]

    func loadSummaries(_ keys: Set<String>) async {
        guard !keys.isEmpty, !email.isEmpty, !jira.isEmpty, let token = try? secret("jira-token") else { return }
        for k in keys where summaries[k] != "" {  // "" = Jira 에 없는 키, 다시 안 물어봄
            let j = try? await getJSON(URL(string: "\(jira)/rest/api/3/issue/\(k)?fields=summary,status")!, auth: "\(email):\(token)")
            summaries[k] = str(j, "fields.summary")
            if j != nil { jiraStatus[k] = (str(j, "fields.status.name"), str(j, "fields.status.statusCategory.key") == "done") }
        }
    }

    var icon: NSImage {
        badge ? spriteImage(frame: tick / 2, dy: hop[tick % hop.count], color: NSColor(hue: CGFloat(tick % 14) / 14, saturation: 0.75, brightness: 1, alpha: 1)) : spriteImage()
    }

    init() {
        guard !CommandLine.arguments.contains(where: { $0.hasPrefix("--") }) else { return }
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { ok, err in
            Task { @MainActor in self.notifOff = ok ? nil : "맥 알림 꺼짐 · 시스템 설정 → 알림 → HugWatch 허용" + (err.map { " (\($0.localizedDescription))" } ?? "") }
        }
        Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { _ in Task { @MainActor in await self.check() } }
        if email.isEmpty && daou.isEmpty { DispatchQueue.main.async { SettingsWindow.shared.show(self) } }
        Task { await check() }
    }

    func markRead(_ source: String) {
        guard events.contains(where: { !$0.read && $0.source == source }) else { return }
        events = events.map { var e = $0; if e.source == source { e.read = true }; return e }
    }

    func all(_ source: String) -> [Item] { sections.first { $0.id == source }?.items ?? [] }
    func items(_ source: String) -> [Item] { all(source).filter { !hidden.contains("\(source)/\($0.id)") } }
    func hiddenItems(_ source: String) -> [Item] { all(source).filter { hidden.contains("\(source)/\($0.id)") } }
    func error(_ source: String) -> String? { sections.first { $0.id == source }?.error }

    func addBranch(_ input: String) {
        let b = resolveBranch(input.trimmingCharacters(in: .whitespaces))
        guard !b.isEmpty, !branches.contains(b) else { return }
        branches.append(b)
        Task { await check() }
    }

    func loadCandidates() async { candidates = await Task.detached { recentBranches() }.value }

    func check() async {
        guard !busy else { return }
        busy = true
        let bs = branches
        let on = (pr: !email.isEmpty && !bbWorkspace.isEmpty && hasSecret("bitbucket-token"),
                  jira: !email.isEmpty && !jira.isEmpty && hasSecret("jira-token"),
                  mail: !daou.isEmpty && !daouId.isEmpty && hasSecret("daou-password"))
        // 출처마다: 가져오기 + (이전 fp, 항목) → 알림 (제목, 내용) 또는 nil
        var sources: [(String, () async throws -> [Item], (String?, Item) -> (String, String)?)] = [
            ("PR 리뷰", myPRs, { old, i in old.flatMap { prChange($0, i.fp) }.map { ("\(ticket(i.title) ?? "PR") · \($0)", i.detail.isEmpty ? i.title : i.detail) } }),
            ("메일", mail, { old, i in old == nil ? ("새 메일 · \(i.sub)", i.title) : nil }),
            ("Jira 할당", jiraAssigned, { old, i in old == nil ? ("\(i.id) 새로 할당됨", i.title) : nil }),
            ("내 작업 병합", { let items = await Task.detached { workStatus(bs) }.value
                              await self.loadSummaries(Set(items.compactMap { ticket($0.group) }))
                              return items },
             { old, i in old != nil || i.notifyNew ? workChange(old, i.fp).map { ("\(i.group) \($0)", self.summary(i.group) ?? i.title) } : nil }),
        ]
        sources.removeAll { ($0.0 == "PR 리뷰" && !on.pr) || ($0.0 == "Jira 할당" && !on.jira) || ($0.0 == "메일" && !on.mail) }
        var out: [Section] = []
        for (name, fetch, describe) in sources {
            var s = Section(id: name)
            do { s.items = try await fetch(); diff(name, s.items, describe) } catch { s.error = error.localizedDescription }
            out.append(s)
        }
        sections = out
        lastCheck = Date()
        busy = false
    }

    func diff(_ name: String, _ items: [Item], _ describe: (String?, Item) -> (String, String)?) {
        let key = "fp.\(name)"
        let old = UserDefaults.standard.dictionary(forKey: key) as? [String: String]
        UserDefaults.standard.set(Dictionary(items.map { ($0.id, $0.fp) }) { a, _ in a }, forKey: key)
        guard let old else { return }  // 첫 실행은 기준값만 저장
        var sent = Set<String>()  // 같은 티켓 브랜치 여러 개 → 같은 알림 한 번만
        for i in items {
            guard let (title, body) = describe(old[i.id], i).map({ ($0.0, $0.1) }), sent.insert(title).inserted else { continue }
            badge = true
            events = Array(([Event(source: name, title: title, sub: body, url: i.url, date: Date())] + events).prefix(50))
            let c = UNMutableNotificationContent()
            c.title = title
            c.body = body
            c.sound = .default
            c.userInfo = ["url": i.url]
            UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: c, trigger: nil))
        }
    }
}

// MARK: - UI

// 띄운 창 ⌘+ / ⌘− : 폰트·아이콘 크기에 곱하는 배율 (화면을 늘리면 글자가 흐려져서)
private struct ZoomKey: EnvironmentKey { static let defaultValue: CGFloat = 1 }
extension EnvironmentValues { var zoom: CGFloat { get { self[ZoomKey.self] } set { self[ZoomKey.self] = newValue } } }
struct ZFont: ViewModifier {
    @Environment(\.zoom) private var z
    let size: CGFloat, weight: Font.Weight
    func body(content: Content) -> some View { content.font(.system(size: size * z, weight: weight)) }
}
extension View { func z(_ size: CGFloat, _ weight: Font.Weight = .regular) -> some View { modifier(ZFont(size: size, weight: weight)) } }

enum Tab: String {
    case feed = "새 소식", todo = "할 일", pr = "PR", mail = "메일", jira = "Jira", work = "병합"
    var source: String { ["PR": "PR 리뷰", "메일": "메일", "Jira": "Jira 할당", "병합": "내 작업 병합"][rawValue] ?? "" }
}

let look: [String: (icon: String, color: Color)] = [
    "PR 리뷰": ("arrow.triangle.pull", .purple), "메일": ("envelope", .blue),
    "Jira 할당": ("checklist", .green), "내 작업 병합": ("arrow.triangle.merge", .orange),
]

func open(_ url: String) { if let u = URL(string: url) { NSWorkspace.shared.open(u) } }

struct Row: View {
    @Environment(\.zoom) private var z
    let source, title, sub: String
    var titleLines = 1
    var detail = ""
    var people: [Person] = []
    var trailing = AnyView(EmptyView())
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            HStack(alignment: .top, spacing: 10) {
                let l = look[source] ?? ("bell", .gray)
                Image(systemName: l.icon).z(12, .medium).foregroundStyle(l.color)
                    .frame(width: 26 * z, height: 26 * z).background(l.color.opacity(0.15), in: RoundedRectangle(cornerRadius: 7))
                VStack(alignment: .leading, spacing: 1) {
                    Text(title).z(13, .medium).lineLimit(titleLines)
                    Text(sub).z(11).foregroundStyle(.secondary).lineLimit(1)
                    if !detail.isEmpty { Text(detail).z(11).foregroundStyle(.secondary).lineLimit(2) }
                    if !people.isEmpty { Avatars(people: people) }
                }
                Spacer(minLength: 4)
                trailing
            }
            .padding(.vertical, 3)
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
    }
}

struct Pill: View {
    let text, tone: String
    var body: some View {
        let c: Color = ["blue": .blue, "green": .green][tone] ?? .gray
        Text(text).z(10, .semibold).foregroundStyle(c)
            .padding(.horizontal, 7).padding(.vertical, 2)
            .background(c.opacity(0.15), in: RoundedRectangle(cornerRadius: 5))
            .padding(.top, 4)
    }
}

// 리뷰어 프로필: 승인 초록 체크, 수정요청 주황, 대기는 흐리게. 마우스 올리면 이름
struct Avatars: View {
    @Environment(\.zoom) private var z
    let people: [Person]
    var body: some View {
        HStack(spacing: 3) {
            ForEach(people, id: \.self) { p in
                AsyncImage(url: URL(string: p.avatar)) { $0.resizable() } placeholder: { Circle().fill(.quaternary) }
                    .frame(width: 18 * z, height: 18 * z)
                    .clipShape(Circle())
                    .opacity(p.state == "wait" ? 0.4 : 1)
                    .overlay(alignment: .bottomTrailing) {
                        if p.state != "wait" {
                            Image(systemName: p.state == "ok" ? "checkmark.circle.fill" : "exclamationmark.circle.fill")
                                .z(9).foregroundStyle(p.state == "ok" ? .green : .orange)
                                .background(Circle().fill(.white).padding(1)).offset(x: 3, y: 3)
                        }
                    }
                    .help("\(p.name) · \(["ok": "승인", "ask": "수정 요청", "wait": "대기"][p.state] ?? "")")
            }
        }
        .padding(.top, 3)
    }
}

// D·R·M 칸: develop · release · master 에 들어갔으면 채움. 저장소에 없는 단계는 점선
struct Stages: View {
    @Environment(\.zoom) private var z
    let dots: [Bool?]
    var body: some View {
        HStack(spacing: 3) {
            ForEach(0..<3, id: \.self) { k in
                let on = dots.indices.contains(k) ? dots[k] : nil
                let label = ["D", "R", "M"][k]
                Text(label).z(9, .bold)
                    .frame(width: 16 * z, height: 16 * z)
                    .foregroundStyle(on == true ? Color.white : on == false ? Color.secondary : Color.secondary.opacity(0.3))
                    .background(RoundedRectangle(cornerRadius: 4).fill(on == true ? Color(nsColor: violet) : .clear))
                    .overlay(RoundedRectangle(cornerRadius: 4)
                        .stroke(on == true ? .clear : Color.secondary.opacity(on == nil ? 0.25 : 0.6), style: StrokeStyle(lineWidth: 1, dash: on == nil ? [2] : [])))
                    .help(["develop", "release", "master"][k] + (on == nil ? ": 이 저장소엔 없음" : on! ? ": 들어감" : ": 아직"))
            }
        }
        .padding(.top, 4)
    }
}

// 빈 화면: 춤추는 인베이더
struct Dancer: View {
    @Environment(\.zoom) private var z
    let text: String
    var body: some View {
        TimelineView(.animation(minimumInterval: 0.07)) { t in
            let k = Int(t.date.timeIntervalSinceReferenceDate / 0.07)
            VStack(spacing: 6) {
                Image(nsImage: spriteImage(frame: k / 4, dy: hop[k % hop.count], px: 2 * z))
                Text(text).z(11).foregroundStyle(.secondary)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, 10)
        }
    }
}

// 디자인시스템 TodoCheckbox: 안 함 = 점선 테두리, 함 = 초록 채움 + 손으로 긋는 체크
let todoGreen = Color(red: 0.353, green: 0.51, blue: 0.408)  // bongchil-design-system --color-primary #5A8268

struct CheckShape: Shape {
    func path(in r: CGRect) -> Path {  // M4 12 9 17 20 6 (24 기준)
        var p = Path()
        p.move(to: CGPoint(x: r.minX + r.width * 0.167, y: r.minY + r.height * 0.5))
        p.addLine(to: CGPoint(x: r.minX + r.width * 0.375, y: r.minY + r.height * 0.708))
        p.addLine(to: CGPoint(x: r.minX + r.width * 0.833, y: r.minY + r.height * 0.25))
        return p
    }
}

struct TodoCheck: View {
    @Environment(\.zoom) private var z
    let done: Bool
    var body: some View {
        let s = 16 * z
        ZStack {
            RoundedRectangle(cornerRadius: 5).fill(done ? todoGreen : Color.white.opacity(0.35))
            RoundedRectangle(cornerRadius: 5)
                .strokeBorder(done ? Color.clear : Color.primary.opacity(0.3), style: StrokeStyle(lineWidth: 1.5, dash: done ? [] : [3, 2]))
            CheckShape().trim(from: 0, to: done ? 1 : 0)
                .stroke(.white, style: StrokeStyle(lineWidth: 2.2 * z, lineCap: .round, lineJoin: .round))
                .frame(width: s * 0.72, height: s * 0.72)
        }
        .frame(width: s, height: s)
        .animation(.easeOut(duration: 0.25), value: done)
    }
}

// 할 일 글자: 티켓 키는 누르면 Jira 로 가는 링크
func todoText(_ t: String) -> AttributedString {
    var a = AttributedString(t)
    if let r = t.range(of: ticketRe, options: .regularExpression), let ar = Range(r, in: a) {
        a[ar].link = URL(string: "\(jira)/browse/\(t[r])")
        a[ar].foregroundColor = todoGreen
    }
    return a
}

struct TodoRow: View {
    @Environment(\.zoom) private var z
    let todo: Todo
    let toggle: () -> Void
    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 9) {
            TodoCheck(done: todo.done).onTapGesture(perform: toggle).alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 * z }
            Text(todoText(todo.text)).z(13).strikethrough(todo.done).foregroundStyle(todo.done ? .tertiary : .primary)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 4)
            if todo.someday { Pill(text: "나중에", tone: "gray") }
            else if let s = todo.snooze, s > Date() { Pill(text: Calendar.current.isDateInTomorrow(s) ? "내일" : s.formatted(.dateTime.month().day()), tone: "gray") }
        }
        .padding(.vertical, 4).padding(.horizontal, 3)
        .contentShape(Rectangle())
    }
}

// 같은 티켓·같은 저장소 브랜치 여러 개를 한 줄로 (가장 멀리 간 브랜치 기준)
struct WorkGroup {
    let items: [Item]
    var ticket: String { items[0].group }
    var repo: String { items[0].title.components(separatedBy: " · ")[0] }
    var key: String { "\(ticket)|\(repo)" }
    var dots: [Bool?] { (0..<3).map { k in let v = items.compactMap { $0.dots.indices.contains(k) ? $0.dots[k] : nil }; return v.isEmpty ? nil : v.contains(true) } }
    var furthest: Item { items.max { $0.dots.filter { $0 == true }.count < $1.dots.filter { $0 == true }.count }! }
    var done: Bool { dots.last == .some(true) }
}

// 띄워둔 창: 반투명 유리 카드 + 맨 위 고정 켜고 끄기
struct FloatingLook: ViewModifier {
    let on: Bool, onTop: Bool
    func body(content: Content) -> some View {
        if on {
            let shape = RoundedRectangle(cornerRadius: 14)
            content
                .background(.regularMaterial, in: shape)  // glassEffect 는 AppKit 패널+크기맞춤에서 죽어서 재질로
                // 위쪽 흰 하이라이트 (유리 가장자리) + 얇은 회색 외곽선
                .overlay(shape.strokeBorder(LinearGradient(colors: [.white.opacity(0.9), .white.opacity(0)], startPoint: .top, endPoint: .center), lineWidth: 1))
                .overlay(shape.stroke(Color.black.opacity(0.25), lineWidth: 0.5))
        } else { content }
    }
}

// 설정 창: 계정·주소는 UserDefaults, 토큰·비밀번호는 키체인. 저장하면 바로 확인해서 결과를 보여줌
@MainActor final class SettingsWindow {
    static let shared = SettingsWindow()
    private var win: NSWindow?
    func show(_ w: Watcher) {
        if win == nil {
            let x = NSWindow(contentViewController: NSHostingController(rootView: SettingsView(w: w)))
            x.title = "HugWatch 설정"
            x.styleMask = [.titled, .closable]
            x.isReleasedWhenClosed = false
            x.center()
            win = x
        }
        NSApp.activate()
        win?.makeKeyAndOrderFront(nil)
    }
}

struct SettingsView: View {
    @ObservedObject var w: Watcher
    @AppStorage("email") private var emailV = ""
    @AppStorage("jiraSite") private var jiraSite = ""
    @AppStorage("bbWorkspace") private var bbWS = ""
    @AppStorage("daouURL") private var daouURL = ""
    @AppStorage("daouId") private var daouIdV = ""
    @AppStorage("repoRoots") private var roots = ""
    @State private var jiraToken = ""
    @State private var bbToken = ""
    @State private var daouPw = ""
    @State private var saved: Set<String> = []
    @State private var status = ""

    var body: some View {
        Form {
            account
            jiraSection
            bitbucketSection
            mailSection
            repoSection
            saveSection
        }
        .formStyle(.grouped)
        .frame(width: 480, height: 680)
        .onAppear(perform: refreshSaved)
    }

    var account: some View {
        SwiftUI.Section {
            TextField("이메일", text: $emailV, prompt: Text("name@company.com"))
        } header: { Text("Atlassian 계정") } footer: { note("Jira·Bitbucket 로그인에 같이 써요. 회사 Atlassian 계정 이메일") }
    }

    var jiraSection: some View {
        SwiftUI.Section {
            TextField("사이트 주소", text: $jiraSite, prompt: Text("company.atlassian.net"))
            secretField("API 토큰", $jiraToken, "jira-token")
            Link("토큰 만들기 ↗", destination: URL(string: "https://id.atlassian.com/manage-profile/security/api-tokens")!)
        } header: { Text("Jira · 나에게 할당된 일") } footer: { note("'API 토큰 만들기'(범위 없는 일반 토큰). 만료일은 최대 1년") }
    }

    var bitbucketSection: some View {
        SwiftUI.Section {
            TextField("워크스페이스", text: $bbWS, prompt: Text("bitbucket.org/여기 부분"))
            secretField("API 토큰", $bbToken, "bitbucket-token")
            Link("토큰 만들기 ↗", destination: URL(string: "https://id.atlassian.com/manage-profile/security/api-tokens")!)
        } header: { Text("Bitbucket · 내 PR 리뷰") } footer: { note("'범위가 있는 API 토큰 만들기' → 앱 Bitbucket → 권한 read:user, read:pullrequest") }
    }

    var mailSection: some View {
        SwiftUI.Section {
            TextField("주소", text: $daouURL, prompt: Text("groupware.company.com"))
            TextField("아이디", text: $daouIdV, prompt: Text("로그인 아이디"))
            secretField("비밀번호", $daouPw, "daou-password")
        } header: { Text("다우오피스 메일 (선택)") } footer: { note("안 쓰면 비워두세요. 로그인에 2차 인증이 켜져 있으면 안 돼요") }
    }

    var repoSection: some View {
        SwiftUI.Section {
            TextField("폴더", text: $roots, prompt: Text("~/work, ~/github"))
        } header: { Text("병합 추적 · 저장소 폴더") } footer: { note("쉼표로 여러 개. 이 안의 git 저장소에서 내 PR 브랜치가 develop·release·master 까지 갔는지 봐요") }
    }

    var saveSection: some View {
        SwiftUI.Section {
            HStack {
                Text(status).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Spacer()
                Button("저장하고 확인") { save() }.keyboardShortcut(.defaultAction)
            }
        } footer: { note("토큰·비밀번호는 이 맥의 키체인에만 저장돼요. 각 서비스 말고 다른 곳으로는 아무것도 보내지 않아요") }
    }

    func note(_ t: String) -> some View { Text(t).font(.caption).foregroundStyle(.secondary) }

    func secretField(_ label: String, _ b: Binding<String>, _ key: String) -> some View {
        SecureField(label, text: b, prompt: Text(saved.contains(key) ? "저장됨 · 바꾸려면 새로 붙여넣기" : "붙여넣기"))
    }

    func refreshSaved() { saved = Set(["jira-token", "bitbucket-token", "daou-password"].filter(hasSecret)) }

    func save() {
        for (k, v) in [("jira-token", jiraToken), ("bitbucket-token", bbToken), ("daou-password", daouPw)] {
            let t = v.trimmingCharacters(in: .whitespacesAndNewlines)
            if !t.isEmpty { setSecret(k, t) }
        }
        jiraToken = ""; bbToken = ""; daouPw = ""
        daouFail = nil  // 새 비밀번호면 다시 로그인 시도
        refreshSaved()
        status = "확인 중…"
        Task {
            await w.check()
            status = [("PR 리뷰", "Bitbucket"), ("Jira 할당", "Jira"), ("메일", "메일"), ("내 작업 병합", "병합 추적")].compactMap { id, name in
                guard let s = w.sections.first(where: { $0.id == id }) else { return nil }
                return s.error.map { "\(name) ✗ \($0)" } ?? "\(name) ✓ \(s.items.count)개"
            }.joined(separator: "\n")
        }
    }
}

// 띄운 창. SwiftUI Window 는 제목줄 높이만큼 창이 커져 빈 '머리'가 생겨서 AppKit 패널로 직접 띄움
// 제목줄 없는 패널 (제목줄 있으면 안전영역 ↔ 크기맞춤이 무한반복하며 죽음). 키 입력은 canBecomeKey 로 허용
// nonactivating → 눌러도 다른 앱 포커스 안 뺏음
final class KeyPanel: NSPanel { override var canBecomeKey: Bool { true } }

@MainActor final class FloatPanel {
    static let shared = FloatPanel()
    private var panel: NSPanel?

    func show(_ w: Watcher) {
        if panel == nil {
            let host = NSHostingController(rootView: Panel(w: w, floating: true))
            host.sizingOptions = []  // 테두리 없는 패널은 자동 크기맞춤이 안 먹어서 Panel 이 fit() 으로 직접 알려줌
            let p = KeyPanel(contentRect: NSRect(x: 0, y: 0, width: 340, height: 200), styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
            p.contentViewController = host
            p.isMovableByWindowBackground = true
            p.isOpaque = false
            p.backgroundColor = .clear
            p.hasShadow = true
            p.hidesOnDeactivate = false
            p.isReleasedWhenClosed = false
            if !p.setFrameUsingName("HugWatchFloat") { p.center() }
            p.setFrameAutosaveName("HugWatchFloat")
            panel = p
        }
        setOnTop(UserDefaults.standard.object(forKey: "onTop") as? Bool ?? true)
        panel?.orderFrontRegardless()
        panel?.makeKey()
    }

    func close() { panel?.orderOut(nil) }

    // 내용 크기에 창을 맞춤 (위쪽 모서리 고정 — 늘어나도 아래로 자람)
    func fit(_ size: CGSize) {
        guard let p = panel, size.width > 1, size.height > 1, p.frame.size != size else { return }
        let top = p.frame.size.height > 1 ? p.frame.maxY : p.frame.midY + size.height / 2
        p.setFrame(NSRect(x: p.frame.minX - (p.frame.width > 1 ? 0 : size.width / 2), y: top - size.height, width: size.width, height: size.height), display: true)
    }
    func setOnTop(_ on: Bool) { panel?.level = on ? .floating : .normal }
}

struct Panel: View {
    @ObservedObject var w: Watcher
    var floating = false
    @State private var showHidden = false
    @State private var showPast = false
    @State private var newTodo = ""
    @State private var showLater = false
    @State private var showDone = false
    @FocusState private var todoFocus: Bool
    @AppStorage("onTop") private var onTop = true
    @AppStorage("zoom") private var zoom = 1.0
    @State private var tab = CommandLine.arguments.contains("--tab-todo") ? Tab.todo : Tab.feed  // --tab-todo: 확인용
    @State private var adding = false
    @State private var picking = false
    @State private var newBranch = ""

    var groups: [WorkGroup] {
        var order: [String] = [], by: [String: [Item]] = [:]
        for i in w.items(Tab.work.source) {
            let k = "\(i.group)|\(i.title.components(separatedBy: " · ")[0])"
            if by[k] == nil { order.append(k) }
            by[k, default: []].append(i)
        }
        return order.map { WorkGroup(items: by[$0]!) }
    }

    var body: some View {
        let gs = groups
        // Jira 완료(= 대개 배포 끝)는 아래로
        let active = gs.filter { !$0.done }.sorted { a, b in !(w.jiraStatus[a.ticket]?.done ?? false) && (w.jiraStatus[b.ticket]?.done ?? false) }
        VStack(alignment: .leading, spacing: 10) {
            if floating {  // 잡고 끄는 손잡이
                Capsule().fill(.secondary.opacity(0.5)).frame(width: 40, height: 5)
                    .frame(maxWidth: .infinity).padding(.bottom, -4)
                    .contentShape(Rectangle()).gesture(WindowDragGesture()).help("잡고 끌어서 옮기기")
            }
            HStack {
                Text("HugWatch").z(13, .bold).gesture(WindowDragGesture())
                Spacer()
                if let t = w.lastCheck { Text("\(t.formatted(.relative(presentation: .named))) 확인").z(11).foregroundStyle(.secondary) }
                if w.busy { ProgressView().controlSize(.small) }
                else { Button { Task { await w.check() } } label: { Image(systemName: "arrow.clockwise") }.buttonStyle(.borderless) }
                if floating {
                    Button { onTop.toggle() } label: { Image(systemName: onTop ? "pin.fill" : "pin") }
                        .buttonStyle(.borderless).help(onTop ? "맨 위 고정 끄기 (다른 창 뒤로 갈 수 있음)" : "맨 위 고정 (항상 다른 앱 위)")
                    Button { FloatPanel.shared.close() } label: { Image(systemName: "xmark") }
                        .buttonStyle(.borderless).help("닫기")
                } else {
                    Button { FloatPanel.shared.show(w) } label: { Image(systemName: "macwindow.on.rectangle") }
                        .buttonStyle(.borderless).help("화면에 띄워두기")
                }
            }
            let counts: [(Tab, Int)] = [(Tab, Int)]([
                (.feed, w.events.filter { !$0.read }.count), (.todo, w.todos.filter(\.isActive).count), (.pr, w.items(Tab.pr.source).count),
                (.mail, w.items(Tab.mail.source).count),
                (.jira, w.items(Tab.jira.source).count), (.work, active.count),
            ])
            .filter { t, _ in [.feed, .todo, .work].contains(t) || w.sections.contains { $0.id == t.source } }
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 4) { ForEach(counts, id: \.0) { chip($0.0, $0.1) } }
                VStack(alignment: .leading, spacing: 4) {
                    HStack(spacing: 4) { ForEach(counts.prefix(3), id: \.0) { chip($0.0, $0.1) } }
                    HStack(spacing: 4) { ForEach(counts.dropFirst(3), id: \.0) { chip($0.0, $0.1) } }
                }
            }
            VStack(alignment: .leading, spacing: 2) {
                switch tab {
                case .feed: feed
                case .todo: todoTab
                case .work: work(active, done: gs.count - active.count)
                default: list(tab.source)
                }
            }
            if let n = w.notifOff { Text(n).z(11).foregroundStyle(.red) }
            Divider()
            HStack(spacing: 12) {
                Button { SettingsWindow.shared.show(w) } label: { Label("설정", systemImage: "gearshape") }
                Spacer()
                if w.events.contains(where: { !$0.read }) { Button("모두 읽음") { w.events = w.events.map { var e = $0; e.read = true; return e } } }
                Button("알림 설정") { open("x-apple.systempreferences:com.apple.Notifications-Settings.extension?id=\(Bundle.main.bundleIdentifier ?? "")") }
                Button("종료") { NSApp.terminate(nil) }
            }
            .buttonStyle(.borderless).z(11)
        }
        .padding(14)
        .frame(width: 340 * (floating ? zoom : 1))
        .environment(\.zoom, floating ? zoom : 1)
        .background { if floating { zoomKeys } }
        .modifier(FloatingLook(on: floating, onTop: onTop))
        .onChange(of: onTop) { if floating { FloatPanel.shared.setOnTop($1) } }
        .fixedSize()
        .onGeometryChange(for: CGSize.self) { $0.size } action: { if floating { FloatPanel.shared.fit($0) } }
        .onAppear { w.badge = false }
    }

    // 칩: 색은 보라 하나만. 선택 = 진하게 · 새 알림 = 연보라 + 점 · 항목 있음 = 회색 · 비었음 = 흐리게
    // 탭 열면 그 출처 알림 읽음
    func chip(_ t: Tab, _ n: Int) -> some View {
        let accent = Color(nsColor: violet)
        let on = tab == t
        let new = w.events.contains { !$0.read && (t == .feed || $0.source == t.source) }
        let fg: Color = on ? Color(nsColor: .windowBackgroundColor) : new ? accent : n > 0 ? .primary : .secondary
        let bg: Color = on ? .primary : new ? accent.opacity(0.13) : Color.secondary.opacity(n > 0 ? 0.12 : 0.06)
        return Button {
            tab = t
            showHidden = false
            if t != .feed { w.markRead(t.source) }
        } label: {
            HStack(spacing: 3) {
                Text(t.rawValue)
                if n > 0 { Text("\(n)").fontWeight(.semibold) }
            }
            .z(11).padding(.horizontal, 7).padding(.vertical, 4)
            .foregroundStyle(fg)
            .background(bg, in: RoundedRectangle(cornerRadius: 6))
            .overlay(alignment: .topTrailing) {
                if new && !on {
                    Circle().fill(accent).frame(width: 7, height: 7)
                        .overlay(Circle().stroke(Color(nsColor: .windowBackgroundColor), lineWidth: 1.5))
                        .offset(x: 2, y: -2)
                }
            }
            .fixedSize()
        }
        .buttonStyle(.plain)
        .padding(.top, 2)  // 점 자리
    }

    // ⌘+ / ⌘= 크게, ⌘− 작게, ⌘0 원래대로 (보이지 않는 버튼에 단축키만)
    var zoomKeys: some View {
        Group {
            Button("") { zoom = min(1.8, (zoom * 10 + 1).rounded() / 10) }.keyboardShortcut("=", modifiers: .command)
            Button("") { zoom = min(1.8, (zoom * 10 + 1).rounded() / 10) }.keyboardShortcut("+", modifiers: .command)
            Button("") { zoom = max(0.8, (zoom * 10 - 1).rounded() / 10) }.keyboardShortcut("-", modifiers: .command)
            Button("") { zoom = 1 }.keyboardShortcut("0", modifiers: .command)
        }
        .opacity(0).allowsHitTesting(false)
    }

    func empty(_ t: String) -> some View { Dancer(text: w.sections.isEmpty ? "불러오는 중…" : t) }

    // ponytail: 탭마다 8줄 컷, 많아지면 ScrollView
    func eventRow(_ e: Event) -> some View {
        Row(source: e.source, title: e.title, sub: e.sub, titleLines: 2, trailing: AnyView(HStack(spacing: 6) {
            Text(e.date.formatted(.relative(presentation: .named))).z(10).foregroundStyle(.tertiary)
            Circle().fill(e.read ? Color.clear : Color(nsColor: violet)).frame(width: 7, height: 7)
        }.padding(.top, 3))) {
            open(e.url)
            if let k = w.events.firstIndex(where: { $0.id == e.id }) { w.events[k].read = true }
        }
    }

    // 할 일: 입력은 '빈 할 일 한 줄'처럼 (테두리 없이 연한 바탕 + ＋), Enter 로 추가
    // 우클릭: 완료 / 내일로 미루기 / 나중에 / 오늘로 / 삭제
    @ViewBuilder var todoTab: some View {
        let zz: CGFloat = floating ? zoom : 1
        HStack(spacing: 9) {
            Image(systemName: "plus").font(.system(size: 11 * zz, weight: .semibold)).foregroundStyle(todoFocus ? todoGreen : .secondary)
                .frame(width: 16 * zz)
            TextField("할 일 추가", text: $newTodo).textFieldStyle(.plain).font(.system(size: 13 * zz)).focused($todoFocus)
                .onSubmit {
                    let t = newTodo.trimmingCharacters(in: .whitespaces)
                    guard !t.isEmpty else { return }
                    w.todos.insert(Todo(text: t), at: 0)
                    newTodo = ""
                    todoFocus = true
                }
            if !newTodo.isEmpty { Text("⏎").font(.system(size: 10 * zz)).foregroundStyle(.tertiary) }
        }
        .padding(.vertical, 7).padding(.horizontal, 8)
        .background(RoundedRectangle(cornerRadius: 8).fill(Color.secondary.opacity(todoFocus ? 0.12 : 0.07)))
        .overlay(RoundedRectangle(cornerRadius: 8).stroke(todoGreen.opacity(todoFocus ? 0.5 : 0), lineWidth: 1))
        .padding(.bottom, 2)

        let active = w.todos.filter(\.isActive), later = w.todos.filter(\.isLater)
        let done = w.todos.filter(\.done).sorted { ($0.doneAt ?? .distantPast) > ($1.doneAt ?? .distantPast) }
        if active.isEmpty { Dancer(text: "오늘 할 일 없음 · 여유롭네요") }  // 할 일은 불러올 게 없어서 '불러오는 중' 안 씀
        ForEach(active) { todoRow($0) }
        if !later.isEmpty {
            Button(showLater ? "나중에 접기" : "나중에 \(later.count)개") { showLater.toggle() }
                .buttonStyle(.borderless).z(11).foregroundStyle(.secondary).padding(.top, 4)
            if showLater { ForEach(later) { todoRow($0).opacity(0.6) } }
        }
        if !done.isEmpty {
            Button(showDone ? "완료 접기" : "완료 \(done.count)개") { showDone.toggle() }
                .buttonStyle(.borderless).z(11).foregroundStyle(.secondary).padding(.top, 2)
            if showDone { ForEach(done) { todoRow($0) } }
        }
    }

    func todoRow(_ t: Todo) -> some View {
        TodoRow(todo: t) { w.updateTodo(t.id) { $0.done.toggle(); $0.doneAt = $0.done ? Date() : nil } }
            .contextMenu {
                Button(t.done ? "완료 취소" : "완료") { w.updateTodo(t.id) { $0.done.toggle(); $0.doneAt = $0.done ? Date() : nil } }
                if !t.done {
                    Button("내일로 미루기") { w.updateTodo(t.id) { $0.snooze = Calendar.current.startOfDay(for: Date().addingTimeInterval(86400)); $0.someday = false } }
                    Button("나중에") { w.updateTodo(t.id) { $0.someday = true; $0.snooze = nil } }
                    if t.isLater { Button("오늘로") { w.updateTodo(t.id) { $0.someday = false; $0.snooze = nil } } }
                }
                Divider()
                Button("삭제", role: .destructive) { w.todos.removeAll { $0.id == t.id } }
            }
    }

    // 안 읽은 것만. 읽은 건 '지난 알림'으로 접어둠
    @ViewBuilder var feed: some View {
        let unread = w.events.filter { !$0.read }, past = w.events.filter(\.read)
        if unread.isEmpty { empty("새 소식 없음 · 평화로워요") }
        ForEach(unread.prefix(8)) { eventRow($0) }
        if !past.isEmpty {
            Button(showPast ? "지난 알림 접기" : "지난 알림 \(past.count)개") { showPast.toggle() }
                .buttonStyle(.borderless).z(11).foregroundStyle(.secondary).padding(.top, 4)
            if showPast { ForEach(past.prefix(8)) { eventRow($0).opacity(0.6) } }
        }
    }

    // 숨긴 항목: 접어두고, 펼쳐서 우클릭 → 다시 보이기
    @ViewBuilder func hiddenSection(_ source: String) -> some View {
        let hid = w.hiddenItems(source)
        if !hid.isEmpty {
            Button(showHidden ? "숨긴 항목 접기" : "숨긴 항목 \(hid.count)개") { showHidden.toggle() }
                .buttonStyle(.borderless).z(11).foregroundStyle(.secondary).padding(.top, 4)
            if showHidden {
                ForEach(hid) { i in
                    Row(source: source, title: i.title, sub: i.sub,
                        trailing: AnyView(Text("우클릭 → 다시 보이기").z(10).foregroundStyle(.tertiary).padding(.top, 4))) { open(i.url) }
                        .opacity(0.55)
                        .contextMenu { Button("다시 보이기") { w.hidden.remove("\(source)/\(i.id)") } }
                }
            }
        }
    }

    @ViewBuilder func list(_ source: String) -> some View {
        let items = w.items(source)
        if let e = w.error(source) { Text(e).z(11).foregroundStyle(.red) }
        else if items.isEmpty { empty(["PR 리뷰": "열린 PR 없음", "메일": "안 읽은 메일 없음", "Jira 할당": "할당된 일 없음"][source] ?? "없음") }
        ForEach(items.prefix(8)) { i in
            Row(source: source, title: i.title, sub: i.sub, detail: i.detail, people: i.people, trailing: i.tag.isEmpty ? AnyView(EmptyView()) : AnyView(Pill(text: i.tag, tone: i.tone))) { open(i.url) }
                .contextMenu { Button("목록에서 숨기기") { w.hidden.insert("\(source)/\(i.id)") } }
        }
        if items.count > 8 { Text("외 \(items.count - 8)개").z(11).foregroundStyle(.secondary) }
        hiddenSection(source)
    }

    @ViewBuilder func work(_ active: [WorkGroup], done: Int) -> some View {
        Text("내 PR이 어디까지 갔나 · 보라 칸 = 들어감 · 초록 = Jira 완료").z(10).foregroundStyle(.secondary).padding(.bottom, 2)
        if let e = w.error(Tab.work.source) { Text(e).z(11).foregroundStyle(.red) }
        else if active.isEmpty { empty("진행 중인 작업 없음") }
        ForEach(active.prefix(8), id: \.key) { g in
            let f = g.furthest
            Row(source: Tab.work.source, title: w.summary(g.ticket) ?? g.ticket,
                sub: (w.summary(g.ticket) == nil ? "" : "\(g.ticket) · ") + "\(g.repo) · \(f.sub)" + (g.items.count > 1 ? " · 브랜치 \(g.items.count)개" : ""),
                trailing: AnyView(VStack(alignment: .trailing, spacing: 1) {
                    Stages(dots: g.dots)
                    if let st = w.jiraStatus[g.ticket] { Pill(text: st.name, tone: st.done ? "green" : "blue").scaleEffect(0.9, anchor: .trailing) }
                })) { open(f.url) }
                .contextMenu {
                    Button("목록에서 숨기기") { g.items.forEach { w.hidden.insert("\(Tab.work.source)/\($0.id)") } }
                    ForEach(g.items.filter { w.branches.contains($0.id) }) { i in Button("감시 해제: \(i.title)") { w.branches.removeAll { $0 == i.id } } }
                }
        }
        if active.count > 8 { Text("외 \(active.count - 8)개").z(11).foregroundStyle(.secondary) }
        hiddenSection(Tab.work.source)
        HStack {
            Text("D develop · R release · M master" + (done > 0 ? " · 끝난 \(done)개 숨김" : "")).z(10).foregroundStyle(.secondary)
            Spacer()
            Button(adding ? "닫기" : "+ 브랜치") { adding.toggle() }.buttonStyle(.borderless).z(11)
        }
        .padding(.top, 4)
        if adding {
            HStack {
                TextField("브랜치 이름 또는 저장소/브랜치", text: $newBranch)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { w.addBranch(newBranch); newBranch = "" }
                Button(picking ? "완료" : "고르기") {
                    picking.toggle()
                    Task { picking ? await w.loadCandidates() : await w.check() }
                }
            }
            if picking {
                ScrollView {
                    VStack(alignment: .leading, spacing: 3) {
                        if w.candidates.isEmpty { Text("최근 30일 안에 작업한 브랜치 없음 (불러오는 중일 수도)").z(11).foregroundStyle(.secondary) }
                        ForEach(w.candidates, id: \.id) { c in
                            Toggle(isOn: Binding(get: { w.branches.contains(c.id) },
                                                 set: { on in if on { w.branches.append(c.id) } else { w.branches.removeAll { $0 == c.id } } })) {
                                Text(c.id.replacingOccurrences(of: ":", with: " · ") + (c.note.isEmpty ? "" : "  (\(c.note))")).z(11)
                            }
                            .toggleStyle(.checkbox)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(height: 160)
            }
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    func applicationDidFinishLaunching(_ n: Notification) {
        if CommandLine.arguments.contains("--float-demo") { FloatPanel.shared.show(Watcher()); return }
        if CommandLine.arguments.contains("--selftest") {
            precondition(prChange("1/0/0", "2/1/0") == "코멘트 +1, 승인 +1" && prChange("1/1/0", "1/1/0") == nil && prChange("2/1/0", "1/1/0") == nil)
            precondition(workChange("abc|release/5.0.0", "abc|master,release/5.0.0") == "→ master 반영됨")
            precondition(workChange(nil, "abc|develop/1.4.0") == "→ develop/1.4.0 병합됨" && workChange(nil, "abc|") == nil)
            precondition(workChange("abc|develop/1.4.0", "def|develop/1.4.0") == "브랜치에 새 커밋" && workChange("abc|x", "abc|x") == nil)
            precondition(stageLine(["develop/feature-2.3.6-to-develop-1.4.0"], ["develop/1.4.0", "master"]) == "develop ✗ → master ✗")
            precondition("develop/1.4.0-studio".range(of: mainline, options: .regularExpression) != nil && "develop/feature-x".range(of: mainline, options: .regularExpression) == nil)
            precondition(ticket("me/ABC-123-fix") == "ABC-123" && ticket("feature/2.3.1") == nil)
            precondition(webURL(remote: "https://user@bitbucket.org/team/my-app.git", branch: "me/ABC-1") == "https://bitbucket.org/team/my-app/branch/me/ABC-1")
            precondition(webURL(remote: "git@github.com:someone/my-app.git", branch: "main") == "https://github.com/someone/my-app/tree/main")
            precondition(splitRepo("my-app/me/ABC-1", ["my-app"]) == "my-app:me/ABC-1" && splitRepo("me/ABC-1", ["my-app"]) == nil)
            precondition(site("company.atlassian.net/") == "https://company.atlassian.net" && site("https://x.y") == "https://x.y" && site("") == "")
            precondition(stageLine(["release/5.0.0"], ["master", "release/4.0.0", "release/5.0.0"]) == "release/5.0.0 ✓ → master ✗")
            precondition(stageLine(["develop/1.4.0", "develop/2.4.0"], ["develop/2.4.0", "release/1.4.0", "master"]) == "develop/2.4.0 ✓ → release ✗ → master ✗")
            precondition(invader.joined().allSatisfy { $0.count == invader[0][0].count } && invader.allSatisfy { $0.count == invader[0].count })
            precondition(str(["a": ["b": 3]], "a.b") == "3" && str([:] as [String: Any], "x.y") == "")
            print("selftest ok"); exit(0)
        }
        // build.sh 가 앱 아이콘 만들 때 씀: HugWatch --icon AppIcon.icns
        if let i = CommandLine.arguments.firstIndex(of: "--icon"), i + 1 < CommandLine.arguments.count {
            let img = NSImage(size: NSSize(width: 1024, height: 1024), flipped: true) { r in
                NSColor(red: 0.95, green: 0.94, blue: 0.91, alpha: 1).setFill()
                NSBezierPath(roundedRect: r.insetBy(dx: 100, dy: 100), xRadius: 185, yRadius: 185).fill()
                spriteImage(px: 52).draw(in: NSRect(x: 174, y: 148, width: 676, height: 624), from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
                return true
            }
            let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1024, pixelsHigh: 1024, bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
            img.draw(in: NSRect(x: 0, y: 0, width: 1024, height: 1024))
            NSGraphicsContext.restoreGraphicsState()
            // icns = "icns" + 전체길이 + [타입 "ic10"(1024 png) + 항목길이 + png]
            let png = rep.representation(using: .png, properties: [:])!
            func be(_ n: Int) -> Data { withUnsafeBytes(of: UInt32(n).bigEndian) { Data($0) } }
            let icns = Data("icns".utf8) + be(png.count + 16) + Data("ic10".utf8) + be(png.count + 8) + png
            try? icns.write(to: URL(fileURLWithPath: CommandLine.arguments[i + 1]))
            exit(0)
        }
        UNUserNotificationCenter.current().delegate = self
    }
    func userNotificationCenter(_ c: UNUserNotificationCenter, willPresent n: UNNotification) async -> UNNotificationPresentationOptions { [.banner, .sound] }
    func userNotificationCenter(_ c: UNUserNotificationCenter, didReceive r: UNNotificationResponse) async {
        if let s = r.notification.request.content.userInfo["url"] as? String, let u = URL(string: s) { NSWorkspace.shared.open(u) }
    }
}

@main struct HugWatchApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) var delegate
    @StateObject private var w = Watcher()
    var body: some Scene {
        MenuBarExtra {
            Panel(w: w)
        } label: {
            Image(nsImage: w.icon).id(w.tick)  // id 안 바꾸면 메뉴바가 새 이미지로 안 그려줌
        }
        .menuBarExtraStyle(.window)
    }
}
