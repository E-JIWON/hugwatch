// HugWatch for Windows — 모델 · 설정 · 저장 · 자격 증명 · 감시 로직 (맥 HugWatch.swift 와 같은 동작)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HugWatch;

// ───────── 모델 ─────────

public record Person(string Name, string Avatar, string State);  // State: ok 승인 / ask 수정요청 / wait 대기

public class Item
{
    public string Id = "", Title = "", Sub = "", Url = "", Fp = "";
    public string Group = "";                       // 병합 탭: 티켓 키로 묶기
    public bool?[] Dots = Array.Empty<bool?>();     // develop·release·master 에 들어갔나 (저장소에 없는 단계는 null)
    public bool NotifyNew;                          // 처음 보여도 알림 (방금 PR 병합된 내 브랜치)
    public string Tag = "", Tone = "";              // 오른쪽 상태 배지 (Jira 상태)
    public string Detail = "";                      // 세 번째 줄 (PR: 누가 승인·코멘트)
    public List<Person> People = new();
}

public class Section { public string Id = ""; public List<Item> Items = new(); public string? Error; }

public class Event
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public string Sub { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTime Date { get; set; } = DateTime.Now;
    public bool Read { get; set; }
}

public class Todo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Text { get; set; } = "";
    public bool Done { get; set; }
    public DateTime? DoneAt { get; set; }
    public DateTime? Snooze { get; set; }   // 이 날까지 미룸
    public bool Someday { get; set; }       // 나중에
    public bool IsActive => !Done && !Someday && (Snooze ?? DateTime.MinValue) <= DateTime.Now;
    public bool IsLater => !Done && !IsActive;
}

// 설정 — %APPDATA%\HugWatch\settings.json (이 PC 에만). 토큰·비밀번호는 자격 증명 관리자
public class Settings
{
    public string Email { get; set; } = "";        // Atlassian 계정 (Jira·Bitbucket 공통)
    public string JiraSite { get; set; } = "";
    public string BbWorkspace { get; set; } = "";
    public string DaouUrl { get; set; } = "";      // 다우오피스 (선택)
    public string DaouId { get; set; } = "";
    public string RepoRoots { get; set; } = "";    // 쉼표로 여러 개
    public double Zoom { get; set; } = 1.0;
    public bool OnTop { get; set; } = true;
    public bool AutoStart { get; set; }

    public string Jira => Logic.Site(JiraSite);
    public string Daou => Logic.Site(DaouUrl);
    public IEnumerable<string> Roots =>
        (string.IsNullOrWhiteSpace(RepoRoots) ? @"%USERPROFILE%\work, %USERPROFILE%\source\repos, %USERPROFILE%\github, %USERPROFILE%\projects" : RepoRoots)
        .Split(',').Select(r => Environment.ExpandEnvironmentVariables(r.Trim().Replace("~", "%USERPROFILE%"))).Where(r => r.Length > 0);
}

// 감시 기준값·알림 기록·할 일 등 — %APPDATA%\HugWatch\state.json
public class State
{
    public Dictionary<string, Dictionary<string, string>> Fp { get; set; } = new();
    public List<Event> Events { get; set; } = new();
    public List<Todo> Todos { get; set; } = new();
    public HashSet<string> Hidden { get; set; } = new();
    public List<string> Branches { get; set; } = new();
    public Dictionary<string, string> Summaries { get; set; } = new();
    public Dictionary<string, string> Shas { get; set; } = new();
}

public static class Store
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HugWatch");
    static readonly JsonSerializerOptions Opt = new() { WriteIndented = true };

    public static T Load<T>(string name) where T : new()
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(Dir, name))) ?? new T(); }
        catch { return new T(); }
    }

    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Opt));  // 쓰다 꺼져도 안 깨지게
        File.Move(path + ".tmp", path, true);
    }
}

// 비밀값: 윈도우 자격 증명 관리자 (일반 자격 증명 "HugWatch:<key>")
public static class Creds
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags, Type;
        public string TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredReadW(string target, int type, int flags, out IntPtr cred);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredWriteW(ref CREDENTIAL cred, int flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool CredDeleteW(string target, int type, int flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr cred);
    const int Generic = 1, PersistLocalMachine = 2;

    public static string? Get(string key)
    {
        if (!CredReadW("HugWatch:" + key, Generic, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            return c.CredentialBlobSize == 0 ? null : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
        }
        finally { CredFree(p); }
    }

    public static void Set(string key, string value)
    {
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var c = new CREDENTIAL
            {
                Type = Generic, TargetName = "HugWatch:" + key, CredentialBlobSize = bytes.Length, CredentialBlob = blob,
                Persist = PersistLocalMachine, UserName = "hugwatch", Comment = "", TargetAlias = null!,
            };
            CredWriteW(ref c, 0);
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public static void Delete(string key) => CredDeleteW("HugWatch:" + key, Generic, 0);
    public static bool Has(string key) => !string.IsNullOrEmpty(Get(key));
    public static string Need(string key) => Get(key) ?? throw new Exception($"설정에서 {key} 를 넣어주세요");
}

// ───────── 순수 로직 (셀프체크 대상) ─────────

public static class Logic
{
    public const string MyJql = "assignee = currentUser() AND statusCategory != Done ORDER BY created DESC";

    public const string TicketRe = @"[A-Z][A-Z0-9]+-[0-9]+";
    // 단계 브랜치 = develop · develop/1.4.0(-studio) · release/3.6.1 · master · main. develop/feature-… 같은 임시 동기화 브랜치는 제외
    public const string Mainline = @"^((develop|release)(/[0-9][^/]*)?|master|main)$";

    public static string Site(string s)  // "company.atlassian.net" → "https://company.atlassian.net"
    {
        var t = (s ?? "").Trim().Trim('/');
        return t.Length == 0 || t.StartsWith("http") ? t : "https://" + t;
    }

    public static string? Ticket(string s) { var m = Regex.Match(s, TicketRe); return m.Success ? m.Value : null; }

    // PR fp "코멘트/승인/수정요청" 이 늘어난 것 → "코멘트 +1, 승인 +1"
    public static string? PrChange(string old, string @new)
    {
        int[] o = old.Split('/').Select(x => int.TryParse(x, out var v) ? v : -1).ToArray();
        int[] n = @new.Split('/').Select(x => int.TryParse(x, out var v) ? v : -1).ToArray();
        if (o.Length != 3 || n.Length != 3 || o.Contains(-1) || n.Contains(-1)) return null;
        var labels = new[] { "코멘트", "승인", "수정 요청" };
        var parts = Enumerable.Range(0, 3).Where(k => n[k] > o[k]).Select(k => $"{labels[k]} +{n[k] - o[k]}").ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    // 작업 fp "sha|들어간 브랜치들" 변화 → "→ master 반영됨" / "→ develop/1.4.0 병합됨" / "브랜치에 새 커밋"
    public static string? WorkChange(string? old, string @new)
    {
        static (string sha, HashSet<string> into)? Parse(string? f)
        {
            if (f == null) return null;
            var p = f.Split('|');
            return p.Length == 2 ? (p[0], p[1].Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet()) : null;
        }
        var n = Parse(@new);
        if (n == null) return null;
        var o = Parse(old);
        static int Rank(string b) => b.StartsWith("master") || b.StartsWith("main") ? 3 : b.StartsWith("release") ? 2 : 1;
        var added = n.Value.into.Except(o?.into ?? new HashSet<string>()).ToList();
        if (added.Count > 0)
        {
            var best = added.OrderBy(Rank).ThenBy(b => b, NaturalComparer.Instance).Last();
            return Rank(best) == 3 ? $"→ {best} 반영됨" : $"→ {best} 병합됨";
        }
        if (o != null && o.Value.sha != n.Value.sha) return "브랜치에 새 커밋";
        return null;
    }

    // 저장소에 있는 단계마다 내 커밋이 들어간 브랜치 (없으면 null)
    public static List<(string Stage, string? Hit)> Stages(IEnumerable<string> into, IEnumerable<string> remote)
    {
        var res = new List<(string, string?)>();
        foreach (var st in new[] { "develop", "release", "master|main" })
        {
            var re = new Regex($"^({st})(/[0-9][^/]*)?$");
            if (!remote.Any(r => re.IsMatch(r))) continue;
            var hit = into.Where(b => re.IsMatch(b)).OrderBy(b => b, NaturalComparer.Instance).LastOrDefault();
            res.Add((st.Split('|')[0], hit));
        }
        return res;
    }

    public static string StageLine(IEnumerable<string> into, IEnumerable<string> remote) =>
        string.Join(" → ", Stages(into, remote).Select(s => s.Hit != null ? $"{s.Hit} ✓" : $"{s.Stage} ✗"));

    // "my-app/me/ABC-1" → "my-app:me/ABC-1" (맨 앞이 실제 저장소 이름일 때만)
    public static string? SplitRepo(string input, ICollection<string> repoNames)
    {
        var p = input.Split('/', 2);
        return p.Length == 2 && repoNames.Contains(p[0]) ? $"{p[0]}:{p[1]}" : null;
    }

    public static string RepoWeb(string remote)
    {
        var s = Regex.Replace(remote.Trim(), @"^(https://([^@/]+@)?|git@)", "https://");
        s = Regex.Replace(s, @"(\.com|\.org):", "$1/");
        return Regex.Replace(s, @"\.git$", "");
    }

    public static string WebUrl(string remote, string branch)
    {
        var b = RepoWeb(remote);
        return b.Contains("github.com") ? $"{b}/tree/{branch}" : $"{b}/branch/{branch}";
    }
}

// "release/1.10.0" 이 "release/1.9.0" 보다 뒤로 가게 (숫자 덩어리는 숫자로 비교)
public class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();
    public int Compare(string? a, string? b)
    {
        var x = Regex.Split(a ?? "", @"(\d+)"); var y = Regex.Split(b ?? "", @"(\d+)");
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            int c = long.TryParse(x[i], out var nx) && long.TryParse(y[i], out var ny) ? nx.CompareTo(ny) : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return c;
        }
        return x.Length.CompareTo(y.Length);
    }
}

// ───────── 바깥 세계: HTTP · git ─────────

public static class Net
{
    static readonly CookieContainer Cookies = new();  // 다우오피스 로그인 세션
    public static readonly HttpClient Http = new(new HttpClientHandler { CookieContainer = Cookies }) { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<JsonNode> Json(string url, string? basic = null, object? post = null)
    {
        using var req = new HttpRequestMessage(post == null ? HttpMethod.Get : HttpMethod.Post, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (basic != null) req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basic)));
        if (post != null) req.Content = new StringContent(JsonSerializer.Serialize(post), Encoding.UTF8, "application/json");
        using var res = await Http.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        if (res.StatusCode != HttpStatusCode.OK) throw new Exception($"HTTP {(int)res.StatusCode} · {new Uri(url).Host}{new Uri(url).AbsolutePath}");
        return JsonNode.Parse(body) ?? throw new Exception($"응답이 JSON 이 아님 · {new Uri(url).Host}");
    }

    // "a.b.c" 경로로 꺼내서 문자열로 (bool 은 맥 버전처럼 "1"/"0")
    public static string Str(JsonNode? n, string path)
    {
        foreach (var k in path.Split('.')) n = n is JsonObject o ? o[k] : null;
        if (n is not JsonValue v) return "";
        if (v.TryGetValue<bool>(out var b)) return b ? "1" : "0";
        return v.ToString();
    }

    public static IEnumerable<JsonNode> Arr(JsonNode? n, string path)
    {
        foreach (var k in path.Split('.')) n = n is JsonObject o ? o[k] : null;
        return n is JsonArray a ? a.OfType<JsonNode>() : Enumerable.Empty<JsonNode>();
    }
}

public static class Git
{
    // 창 안 띄우고 실행. 실패하면 null
    public static string? Run(string repo, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(repo);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120_000);
            return p.ExitCode == 0 ? output.Trim() : null;
        }
        catch { return null; }
    }

    public static IEnumerable<string> Lines(string? s) => (s ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim());
}

// ───────── 감시 대상 ─────────

public static class Sources
{
    public static async Task<List<Item>> MyPRs(Settings s)
    {
        var auth = $"{s.Email}:{Creds.Need("bitbucket-token")}";
        var uuid = Net.Str(await Net.Json("https://api.bitbucket.org/2.0/user", auth), "uuid");
        var j = await Net.Json($"https://api.bitbucket.org/2.0/workspaces/{Uri.EscapeDataString(s.BbWorkspace)}/pullrequests/{Uri.EscapeDataString(uuid)}?state=OPEN&pagelen=50&fields=%2Bvalues.participants", auth);
        var outList = new List<Item>();
        foreach (var pr in Net.Arr(j, "values"))
        {
            var ps = Net.Arr(pr, "participants").ToList();
            var ok = ps.Where(p => Net.Str(p, "approved") == "1").Select(p => Net.Str(p, "user.display_name")).ToList();
            var ask = ps.Where(p => Net.Str(p, "state") == "changes_requested").Select(p => Net.Str(p, "user.display_name")).ToList();
            var waiting = ps.Count(p => Net.Str(p, "role") == "REVIEWER" && Net.Str(p, "approved") != "1" && Net.Str(p, "state") != "changes_requested");
            var order = new Dictionary<string, int> { ["ok"] = 0, ["ask"] = 1, ["wait"] = 2 };
            var people = ps.Select(p => new Person(Net.Str(p, "user.display_name"), Net.Str(p, "user.links.avatar.href"),
                Net.Str(p, "approved") == "1" ? "ok" : Net.Str(p, "state") == "changes_requested" ? "ask" : "wait")).OrderBy(p => order[p.State]).ToList();
            // 코멘트 작성자별 개수 (내 코멘트·삭제 제외). 못 가져오면 전체 개수
            var by = new Dictionary<string, int>();
            JsonNode? cj = null;
            try { cj = await Net.Json(Net.Str(pr, "links.comments.href") + "?pagelen=100", auth); } catch { }
            foreach (var c in Net.Arr(cj, "values"))
                if (Net.Str(c, "deleted") != "1" && Net.Str(c, "user.uuid") != uuid) { var who = Net.Str(c, "user.display_name"); by[who] = by.GetValueOrDefault(who) + 1; }
            int cm = cj == null ? (int.TryParse(Net.Str(pr, "comment_count"), out var cc) ? cc : 0) : by.Values.Sum();
            var repo = Net.Str(pr, "destination.repository.name");
            var parts = new List<string>();
            if (ok.Count > 0) parts.Add("승인 " + string.Join(", ", ok));
            if (ask.Count > 0) parts.Add("수정요청 " + string.Join(", ", ask));
            if (by.Count > 0) parts.Add("코멘트 " + string.Join(", ", by.OrderByDescending(x => x.Value).Select(x => $"{x.Key} {x.Value}")));
            if (waiting > 0) parts.Add($"대기 {waiting}명");
            outList.Add(new Item
            {
                Id = $"{repo}#{Net.Str(pr, "id")}", Title = Net.Str(pr, "title"),
                Sub = $"{repo} · 코멘트 {cm} · 승인 {ok.Count}" + (ask.Count > 0 ? $" · 수정요청 {ask.Count}" : ""),
                Url = Net.Str(pr, "links.html.href"), Fp = $"{cm}/{ok.Count}/{ask.Count}", Detail = string.Join(" · ", parts), People = people,
            });
        }
        return outList;
    }

    public static async Task<List<Item>> JiraAssigned(Settings s)
    {
        var auth = $"{s.Email}:{Creds.Need("jira-token")}";
        // 인증 실패해도 검색은 익명으로 200 + 빈 목록을 줘서, 로그인부터 확인
        try { await Net.Json($"{s.Jira}/rest/api/3/myself", auth); } catch { throw new Exception("Jira 인증 실패 — 토큰 다시 넣기"); }
        var jql = Uri.EscapeDataString(Logic.MyJql);
        var j = await Net.Json($"{s.Jira}/rest/api/3/search/jql?jql={jql}&fields=summary,status&maxResults=50", auth);
        return Net.Arr(j, "issues").Select(i =>
        {
            var key = Net.Str(i, "key"); var status = Net.Str(i, "fields.status.name");
            var tone = status.Contains("완료") ? "green" : Net.Str(i, "fields.status.statusCategory.key") == "indeterminate" ? "blue" : "gray";
            return new Item { Id = key, Title = Net.Str(i, "fields.summary"), Sub = key, Url = $"{s.Jira}/browse/{key}", Fp = "1", Tag = status, Tone = tone };
        }).ToList();
    }

    // 로그인 실패하면 앱 재시작 전까지 재시도 안 함 — 틀린 비번 반복해서 계정 잠기는 것 방지
    public static string? DaouFail;

    static async Task DaouLogin(Settings s)
    {
        if (DaouFail != null) throw new Exception(DaouFail);
        try
        {
            var body = new { username = s.DaouId, password = Creds.Need("daou-password"), captcha = "", returnUrl = "" };
            using var res = await Net.Http.PostAsync($"{s.Daou}/api/login", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            var j = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            if (res.StatusCode != HttpStatusCode.OK) throw new Exception($"메일 로그인 실패({(int)res.StatusCode}): {Net.Str(j, "message")}");
            if (new[] { "normal", "regist" }.Contains(Net.Str(j, "data.certType"))) throw new Exception("메일 서버에 2차 인증이 켜져 있어 자동 로그인 불가");
        }
        catch (Exception e) { DaouFail = e.Message + " (앱 재시작 시 재시도)"; throw; }
    }

    // 다우오피스 웹의 '안 읽은 메일' 보기와 같은 요청 (최신 20통)
    public static async Task<List<Item>> Mail(Settings s)
    {
        var url = $"{s.Daou}/api/mail/message/list";
        var body = new { folder = "all", extFolder = "on", extType = "unseen", flag = "U" };
        JsonNode? j = null;
        try { j = await Net.Json(url, post: body); } catch { }
        if (j == null || Net.Str(j, "code") != "200") { await DaouLogin(s); j = await Net.Json(url, post: body); }
        return Net.Arr(j, "data.messageList").Select(m => new Item
        {
            Id = $"{Net.Str(m, "folderName")}/{Net.Str(m, "id")}", Title = Net.Str(m, "subject"), Sub = Net.Str(m, "fromToSimple"), Url = $"{s.Daou}/app/mail", Fp = "1",
        }).ToList();
    }

    // ── 병합 추적 (로컬 git) ──

    public static Dictionary<string, string> LocalRepos(Settings s)
    {
        var res = new Dictionary<string, string>();
        foreach (var root in s.Roots.Where(Directory.Exists))
            // 저장소 폴더 자체를 넣었으면 그것만, 아니면 바로 아래 저장소들
            foreach (var d in Directory.Exists(Path.Combine(root, ".git")) ? new[] { root } : Directory.GetDirectories(root))
                if (Directory.Exists(Path.Combine(d, ".git")) && Path.GetFileName(d.TrimEnd('\\', '/')) is var n && !res.ContainsKey(n)) res[n] = d;
        return res;
    }

    // Bitbucket 병합 커밋 "Merged in <브랜치> (pull request #N)": 두 번째 부모 = 브랜치 마지막 커밋, N = PR 번호
    public static (string Head, string? Pr)? MergeInfo(string path, string branch)
    {
        var line = Git.Run(path, "log", "--all", "--merges", "-1", "-F", $"--grep=Merged in {branch} (pull request", "--format=%P|%s") ?? "";
        var parts = line.Split('|', 2);
        var parents = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parents.Length < 2) return null;
        var m = Regex.Match(parts[1], @"pull request #([0-9]+)");
        return (parents[1], m.Success ? m.Groups[1].Value : null);
    }

    // 최근 30일 안에 PR로 병합된 "내" 브랜치. 브랜치 마지막 커밋이 내가 쓴 일반 커밋일 때만 (병합 커밋이면 남의 묶음 브랜치)
    public static List<string> RecentMergedMine(string path, string fallbackEmail)
    {
        var me = Git.Run(path, "config", "user.email") ?? fallbackEmail;
        var merges = Git.Lines(Git.Run(path, "log", "--all", "--merges", "--since=30.days", "--grep=^Merged in ", "--format=%P|%s")).Select(l =>
        {
            var p = l.Split('|', 2); var parents = p[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var m = p.Length == 2 ? Regex.Match(p[1], @"^Merged in (\S+) \(pull request") : Match.Empty;
            return parents.Length >= 2 && m.Success ? (head: parents[1], branch: m.Groups[1].Value) : default;
        }).Where(x => x.head != null).ToList();
        if (merges.Count == 0) return new();
        var args = new List<string> { "log", "--no-walk", "--format=%H|%ae|%P" }; args.AddRange(merges.Select(x => x.head));
        var mine = Git.Lines(Git.Run(path, args.ToArray())).Select(l => l.Split('|')).Where(f => f.Length == 3 && f[1] == me && !f[2].Contains(' ')).Select(f => f[0]).ToHashSet();
        return merges.Where(x => mine.Contains(x.head) && !Regex.IsMatch(x.branch, Logic.Mainline)).Select(x => x.branch).Distinct().ToList();
    }

    // 고르기 후보: 저장소마다 최근 30일 로컬 브랜치
    public static List<(string Id, string Note)> RecentBranches(Settings s)
    {
        var cutoff = DateTimeOffset.Now.AddDays(-30).ToUnixTimeSeconds();
        var res = new List<(string, string)>();
        foreach (var (name, path) in LocalRepos(s).OrderBy(x => x.Key))
        {
            var cur = Git.Run(path, "branch", "--show-current") ?? "";
            foreach (var l in Git.Lines(Git.Run(path, "for-each-ref", "--sort=-committerdate", "--format=%(committerdate:unix) %(refname:short)", "refs/heads")))
            {
                var p = l.Split(' ', 2);
                if (p.Length == 2 && long.TryParse(p[0], out var t) && t > cutoff && !Regex.IsMatch(p[1], Logic.Mainline)) res.Add(($"{name}:{p[1]}", p[1] == cur ? "지금" : ""));
            }
        }
        return res;
    }

    // repo:branch / repo/branch / 브랜치 이름만 → repo:branch
    public static string ResolveBranch(Settings s, string input)
    {
        input = input.Trim();
        if (input.Contains(':')) return input;
        var repos = LocalRepos(s);
        if (Logic.SplitRepo(input, repos.Keys) is { } r) return r;
        var hit = repos.OrderBy(x => x.Key).FirstOrDefault(x => Git.Run(x.Value, "rev-parse", "--verify", "--quiet", $"origin/{input}") != null);
        return hit.Key != null ? $"{hit.Key}:{input}" : input;
    }

    // 내 작업: 직접 고른 브랜치 + 최근 30일 PR로 병합된 내 브랜치(자동). 브랜치 마지막 커밋이 어느 단계까지 갔나
    public static List<Item> WorkStatus(Settings s, State st, List<string> manual)
    {
        var repos = LocalRepos(s);
        // 최근 30일에 내 커밋이 있는 저장소만 fetch·자동 탐색
        var active = repos.Where(r => !string.IsNullOrEmpty(Git.Run(r.Value, "log", "--all", "-1", "--since=30.days", $"--author={Git.Run(r.Value, "config", "user.email") ?? s.Email}", "--format=%h"))).ToList();
        var manualPaths = manual.Select(e => e.Split(':')[0]).Where(repos.ContainsKey).Select(n => repos[n]);
        foreach (var path in active.Select(a => a.Value).Union(manualPaths).Distinct()) Git.Run(path, "fetch", "--quiet", "--prune", "origin");
        var auto = active.OrderBy(a => a.Key).SelectMany(a => RecentMergedMine(a.Value, s.Email).Select(b => $"{a.Key}:{b}")).ToList();
        var entries = manual.Select(e => (e, false)).Concat(auto.Where(a => !manual.Contains(a)).Select(a => (a, true)));
        var remotes = new Dictionary<string, List<string>>();
        var items = new List<Item>();
        foreach (var (e, isAuto) in entries)
        {
            var parts = e.Split(':', 2);
            if (parts.Length != 2 || !repos.TryGetValue(parts[0], out var path)) { items.Add(new Item { Id = e, Title = e, Sub = "저장소를 못 찾음", Group = e }); continue; }
            var (repo, branch) = (parts[0], parts[1]);
            var group = Logic.Ticket(branch) ?? branch;
            var live = Git.Run(path, "rev-parse", $"origin/{branch}");
            var merged = MergeInfo(path, branch);
            var sha = live ?? st.Shas.GetValueOrDefault(e) ?? merged?.Head ?? "";
            // 링크: 병합된 PR > 살아있는 브랜치 > 커밋 (지워진 브랜치 주소는 404)
            var remoteUrl = Git.Run(path, "remote", "get-url", "origin") ?? "";
            var web = Logic.RepoWeb(remoteUrl); var gh = web.Contains("github.com");
            var url = merged?.Pr != null ? $"{web}/pull-requests/{merged.Value.Pr}" : live != null ? Logic.WebUrl(remoteUrl, branch) : $"{web}/{(gh ? "commit" : "commits")}/{sha}";
            if (sha == "") { items.Add(new Item { Id = e, Title = $"{repo} · {branch}", Sub = "원격 브랜치 없음", Url = url, Group = group }); continue; }
            st.Shas[e] = sha;
            if (!remotes.TryGetValue(path, out var remote)) remotes[path] = remote = Git.Lines(Git.Run(path, "branch", "-r", "--format=%(refname:lstrip=3)")).ToList();
            var into = Git.Lines(Git.Run(path, "branch", "-r", "--contains", sha, "--format=%(refname:lstrip=3)")).Where(b => b != branch && Regex.IsMatch(b, Logic.Mainline)).OrderBy(b => b, StringComparer.Ordinal).ToList();
            var stg = Logic.Stages(into, remote);
            items.Add(new Item
            {
                Id = e, Title = $"{repo} · {branch}", Sub = stg.LastOrDefault(x => x.Hit != null).Hit ?? "아직 병합 전", Url = url,
                Fp = $"{sha}|{string.Join(",", into)}", Group = group, NotifyNew = isAuto,
                Dots = new[] { "develop", "release", "master" }.Select(sname => stg.Any(x => x.Stage == sname) ? stg.First(x => x.Stage == sname).Hit != null : (bool?)null).ToArray(),
            });
        }
        return items;
    }
}

// ───────── 셀프체크: HugWatch.exe --selftest (CI 가 윈도우에서 돌림) ─────────

public static class SelfTest
{
    public static int Run()
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }
        Check(Logic.PrChange("1/0/0", "2/1/0") == "코멘트 +1, 승인 +1", "prChange 증가");
        Check(Logic.PrChange("1/1/0", "1/1/0") == null && Logic.PrChange("2/1/0", "1/1/0") == null, "prChange 변화 없음/감소");
        Check(Logic.WorkChange("abc|release/5.0.0", "abc|master,release/5.0.0") == "→ master 반영됨", "workChange master");
        Check(Logic.WorkChange(null, "abc|develop/1.4.0") == "→ develop/1.4.0 병합됨" && Logic.WorkChange(null, "abc|") == null, "workChange 처음");
        Check(Logic.WorkChange("abc|develop/1.4.0", "def|develop/1.4.0") == "브랜치에 새 커밋" && Logic.WorkChange("abc|x", "abc|x") == null, "workChange 새 커밋");
        Check(Logic.Ticket("me/ABC-123-fix") == "ABC-123" && Logic.Ticket("feature/2.3.1") == null, "ticket");
        Check(Logic.WebUrl("https://user@bitbucket.org/team/my-app.git", "me/ABC-1") == "https://bitbucket.org/team/my-app/branch/me/ABC-1", "webURL bitbucket");
        Check(Logic.WebUrl("git@github.com:someone/my-app.git", "main") == "https://github.com/someone/my-app/tree/main", "webURL github");
        Check(Logic.SplitRepo("my-app/me/ABC-1", new[] { "my-app" }) == "my-app:me/ABC-1" && Logic.SplitRepo("me/ABC-1", new[] { "my-app" }) == null, "splitRepo");
        Check(Logic.StageLine(new[] { "release/5.0.0" }, new[] { "master", "release/4.0.0", "release/5.0.0" }) == "release/5.0.0 ✓ → master ✗", "stageLine release");
        Check(Logic.StageLine(new[] { "develop/1.4.0", "develop/2.4.0" }, new[] { "develop/2.4.0", "release/1.4.0", "master" }) == "develop/2.4.0 ✓ → release ✗ → master ✗", "stageLine develop");
        Check(Logic.StageLine(new[] { "develop/feature-2.3.6-to-develop-1.4.0" }, new[] { "develop/1.4.0", "master" }) == "develop ✗ → master ✗", "stageLine 임시 브랜치 제외");
        Check(Regex.IsMatch("develop/1.4.0-studio", Logic.Mainline) && !Regex.IsMatch("develop/feature-x", Logic.Mainline), "mainline");
        Check(Logic.Site("company.atlassian.net/") == "https://company.atlassian.net" && Logic.Site("https://x.y") == "https://x.y" && Logic.Site("") == "", "site");
        Check(NaturalComparer.Instance.Compare("release/1.10.0", "release/1.9.0") > 0, "natural sort");
        Check(Net.Str(JsonNode.Parse("{\"a\":{\"b\":3,\"t\":true}}"), "a.b") == "3" && Net.Str(JsonNode.Parse("{\"a\":{\"t\":true}}"), "a.t") == "1" && Net.Str(JsonNode.Parse("{}"), "x.y") == "", "json str");
        Check(Invader.Frames.All(f => f.All(r => r.Length == Invader.Frames[0][0].Length)), "invader grid");
        var log = fails.Count == 0 ? "selftest ok" : "selftest FAIL:\n" + string.Join("\n", fails);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest.log"), log);
        return fails.Count == 0 ? 0 : 1;
    }
}
