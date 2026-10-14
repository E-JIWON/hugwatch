// 상태 · 10분 감시 · 알림 판단 (맥 Watcher 와 같은 규칙)
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace HugWatch;

public class Watcher
{
    public static readonly Watcher I = new();

    public Settings S = Store.Load<Settings>("settings.json");
    public State St = Store.Load<State>("state.json");
    public List<Section> Sections = new();
    public DateTime? LastCheck;
    public bool Busy;
    public bool Badge { get; private set; }  // 새 소식 생기면 창 열 때까지 트레이 인베이더가 폴짝
    // 병합 탭 티켓들의 Jira 상태. release 에서 먼저 배포하는 팀은 M 만으론 배포 여부를 몰라서 같이 보여줌
    public readonly Dictionary<string, (string Name, bool Done)> JiraStatus = new();

    public event Action? Changed;                       // 화면 다시 그리기
    public event Action<string, string, string>? Notify;  // 제목, 내용, 주소

    DispatcherTimer? timer;

    Watcher()
    {
        // 완료한 지 7일 지난 할 일은 정리
        St.Todos.RemoveAll(t => t.Done && t.DoneAt < DateTime.Now.AddDays(-7));
    }

    public void Start()
    {
        timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
        timer.Tick += async (_, _) => await Check();
        timer.Start();
        _ = Check();
    }

    public void SaveState() => Store.Save("state.json", St);
    public void SaveSettings() => Store.Save("settings.json", S);
    public void Touch() { SaveState(); Changed?.Invoke(); }

    public void SetBadge(bool on) { if (Badge == on) return; Badge = on; Changed?.Invoke(); }

    public List<Item> All(string src) => Sections.FirstOrDefault(x => x.Id == src)?.Items ?? new();
    public List<Item> Items(string src) => All(src).Where(i => !St.Hidden.Contains($"{src}/{i.Id}")).ToList();
    public List<Item> HiddenItems(string src) => All(src).Where(i => St.Hidden.Contains($"{src}/{i.Id}")).ToList();
    public string? Error(string src) => Sections.FirstOrDefault(x => x.Id == src)?.Error;
    public bool Has(string src) => Sections.Any(x => x.Id == src);
    public string? Summary(string key) => St.Summaries.TryGetValue(key, out var v) && v != "" ? v : null;

    public void MarkRead(string src)
    {
        if (!St.Events.Any(e => !e.Read && e.Source == src)) return;
        foreach (var e in St.Events.Where(e => e.Source == src)) e.Read = true;
        Touch();
    }

    public void AddBranch(string input)
    {
        var b = Sources.ResolveBranch(S, input);
        if (b == "" || St.Branches.Contains(b)) return;
        St.Branches.Add(b);
        Touch();
        _ = Check();
    }

    public async Task Check()
    {
        if (Busy) return;
        Busy = true;
        Changed?.Invoke();
        var s = S;
        var manual = St.Branches.ToList();
        // 설정된 출처만 (설정 안 한 탭은 안 보임)
        var sources = new List<(string Name, Func<Task<List<Item>>> Fetch, Func<string?, Item, (string, string)?> Describe)>();
        if (s.Email != "" && s.BbWorkspace != "" && Creds.Has("bitbucket-token"))
            sources.Add(("PR 리뷰", () => Sources.MyPRs(s),
                (old, i) => old != null && Logic.PrChange(old, i.Fp) is { } c ? ($"{Logic.Ticket(i.Title) ?? "PR"} · {c}", i.Detail == "" ? i.Title : i.Detail) : null));
        if (s.Daou != "" && s.DaouId != "" && Creds.Has("daou-password"))
        {
            // 내 PR 의 Bitbucket 알림 메일은 PR 탭이 이미 알려줘서 알림 안 함 (목록엔 남김)
            var prs = All("PR 리뷰").Select(p => p.Title).Where(t => t != "").ToList();
            sources.Add(("메일", () => Sources.Mail(s), (old, i) => old == null && !prs.Any(t => i.Title.Contains(t)) ? ($"새 메일 · {i.Sub}", i.Title) : null));
        }
        if (s.Email != "" && s.Jira != "" && Creds.Has("jira-token"))
            sources.Add(("Jira 할당", () => Sources.JiraAssigned(s), (old, i) => old == null ? ($"{i.Id} 새로 할당됨", i.Title) : null));
        sources.Add(("내 작업 병합", async () =>
            {
                var items = await Task.Run(() => Sources.WorkStatus(s, St, manual));  // git 은 느려서 백그라운드 (화면 안 멈추게)
                await LoadSummaries(items.Select(x => Logic.Ticket(x.Group)).OfType<string>().ToHashSet());
                return items;
            },
            (old, i) => (old != null || i.NotifyNew) && Logic.WorkChange(old, i.Fp) is { } c ? ($"{i.Group} {c}", Summary(i.Group) ?? i.Title) : null));

        var result = new List<Section>();
        foreach (var (name, fetch, describe) in sources)
        {
            var sec = new Section { Id = name };
            try { sec.Items = await fetch(); Diff(name, sec.Items, describe); }
            catch (Exception e) { sec.Error = e.Message; }
            result.Add(sec);
        }
        Sections = result;
        LastCheck = DateTime.Now;
        Busy = false;
        SaveState();
        Changed?.Invoke();
    }

    void Diff(string name, List<Item> items, Func<string?, Item, (string, string)?> describe)
    {
        St.Fp.TryGetValue(name, out var old);
        St.Fp[name] = items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First().Fp);
        if (old == null) return;  // 처음엔 기준값만 저장
        var sent = new HashSet<string>();  // 같은 티켓 브랜치 여러 개 → 같은 알림 한 번만
        foreach (var i in items)
        {
            if (describe(old.GetValueOrDefault(i.Id), i) is not { } d || !sent.Add(d.Item1)) continue;
            Badge = true;
            St.Events.Insert(0, new Event { Source = name, Title = d.Item1, Sub = d.Item2, Url = i.Url });
            if (St.Events.Count > 50) St.Events.RemoveRange(50, St.Events.Count - 50);
            Notify?.Invoke(d.Item1, d.Item2, i.Url);
        }
    }

    // 병합 탭 티켓들의 Jira 제목·상태
    async Task LoadSummaries(HashSet<string> keys)
    {
        var s = S;
        if (keys.Count == 0 || s.Email == "" || s.Jira == "" || Creds.Get("jira-token") is not { } token) return;
        foreach (var k in keys)
        {
            if (St.Summaries.TryGetValue(k, out var known) && known == "") continue;  // "" = Jira 에 없는 키
            try
            {
                var j = await Net.Json($"{s.Jira}/rest/api/3/issue/{k}?fields=summary,status", $"{s.Email}:{token}");
                St.Summaries[k] = Net.Str(j, "fields.summary");
                JiraStatus[k] = (Net.Str(j, "fields.status.name"), Net.Str(j, "fields.status.statusCategory.key") == "done");
            }
            catch { St.Summaries[k] = ""; }
        }
    }
}
