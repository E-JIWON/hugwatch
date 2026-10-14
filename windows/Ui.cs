// 팝업 창 (트레이 누르면 오른쪽 아래) · 띄워두기 · 설정 창 — XAML 없이 코드로 그림
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace HugWatch;

// ───────── 색 (윈도우 라이트/다크 따라감) ─────────
public static class P
{
    public static readonly bool Dark = IsDark();
    static bool IsDark()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"); return k?.GetValue("AppsUseLightTheme") is int v && v == 0; }
        catch { return false; }
    }
    public static SolidColorBrush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    public static readonly SolidColorBrush Bg = B(Dark ? "#F21E1D1B" : "#F7F7F6F3");
    public static readonly SolidColorBrush Text = B(Dark ? "#DED9D1" : "#141210");
    public static readonly SolidColorBrush Sub = B(Dark ? "#9A9285" : "#6B6558");
    public static readonly SolidColorBrush Faint = B(Dark ? "#7A7365" : "#9C9588");
    public static readonly SolidColorBrush Line = B(Dark ? "#33FFFFFF" : "#1F141210");
    public static readonly SolidColorBrush ChipBg = B(Dark ? "#2E2C29" : "#EAE8E3");
    public static readonly SolidColorBrush ChipFaint = B(Dark ? "#252421" : "#F0EEEA");
    public static readonly SolidColorBrush Hover = B(Dark ? "#14FFFFFF" : "#0A141210");
    public static readonly SolidColorBrush Accent = B("#7854FF");
    public static readonly SolidColorBrush AccentSoft = B("#227854FF");
    public static readonly SolidColorBrush Green = B("#5A8268");  // bongchil-design-system --color-primary
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // 출처별 아이콘 글자·색
    public static (string Glyph, string Color) Look(string src) => src switch
    {
        "PR 리뷰" => ("", "#8E5BD9"),
        "메일" => ("", "#3A7BD5"),
        "Jira 할당" => ("", "#4C9A52"),
        "내 작업 병합" => ("", "#E08A2E"),
        _ => ("", "#888888"),
    };

    public static string Rel(DateTime d)
    {
        var s = DateTime.Now - d;
        return s.TotalMinutes < 1 ? "방금" : s.TotalHours < 1 ? $"{(int)s.TotalMinutes}분 전" : s.TotalDays < 1 ? $"{(int)s.TotalHours}시간 전" : s.TotalDays < 2 ? "어제" : $"{(int)s.TotalDays}일 전";
    }
}

// ───────── 작은 부품들 ─────────
public static class UiKit
{
    public static TextBlock T(string text, double size = 12, SolidColorBrush? color = null, bool bold = false, int lines = 1) => new()
    {
        Text = text, FontSize = size, Foreground = color ?? P.Text, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = lines > 1 ? TextWrapping.Wrap : TextWrapping.NoWrap,
        MaxHeight = lines > 1 ? size * 1.45 * lines : double.PositiveInfinity,
    };

    public static TextBlock Glyph(string g, double size = 13, Brush? color = null) => new() { Text = g, FontFamily = P.Icons, FontSize = size, Foreground = color ?? P.Sub, VerticalAlignment = VerticalAlignment.Center };

    // 납작한 클릭 영역 (기본 버튼 테두리 없이)
    public static Border Click(UIElement child, Action onClick, string? tip = null, Thickness? pad = null)
    {
        var b = new Border { Child = child, Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Padding = pad ?? new Thickness(4, 2, 4, 2), Cursor = Cursors.Hand, ToolTip = tip };
        b.MouseEnter += (_, _) => b.Background = P.Hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    public static Border Pill(string text, string tone)
    {
        var c = (Color)ColorConverter.ConvertFromString(tone == "green" ? "#4C9A52" : tone == "blue" ? "#3A7BD5" : "#888888");
        return new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Color.FromArgb(38, c.R, c.G, c.B)), Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(c) },
        };
    }

    // D·R·M 칸: 들어갔으면 보라로 채움, 저장소에 없는 단계는 점선
    public static UIElement Stages(bool?[] dots)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        string[] names = { "develop", "release", "master" };
        for (int k = 0; k < 3; k++)
        {
            bool? on = k < dots.Length ? dots[k] : null;
            var g = new Grid { Width = 16, Height = 16, Margin = new Thickness(3, 0, 0, 0), ToolTip = names[k] + (on == null ? ": 이 저장소엔 없음" : on.Value ? ": 들어감" : ": 아직") };
            g.Children.Add(new Rectangle { RadiusX = 4, RadiusY = 4, Fill = on == true ? P.Accent : Brushes.Transparent, Stroke = on == true ? null : (on == null ? P.Line : P.Faint), StrokeThickness = 1, StrokeDashArray = on == null ? new DoubleCollection { 2, 2 } : null });
            g.Children.Add(new TextBlock { Text = "DRM"[k].ToString(), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = on == true ? Brushes.White : on == false ? P.Sub : P.Line, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(g);
        }
        return row;
    }

    // 리뷰어 프로필: 승인 초록 점, 수정요청 주황 점, 대기는 흐리게
    public static UIElement Avatars(List<Person> people)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        foreach (var p in people)
        {
            var g = new Grid { Width = 18, Height = 18, Margin = new Thickness(0, 0, 3, 0), ToolTip = $"{p.Name} · {(p.State == "ok" ? "승인" : p.State == "ask" ? "수정 요청" : "대기")}" };
            Brush fill = P.ChipBg;
            try { if (p.Avatar != "") fill = new ImageBrush(new BitmapImage(new Uri(p.Avatar))) { Stretch = Stretch.UniformToFill }; } catch { }
            g.Children.Add(new Ellipse { Fill = fill, Opacity = p.State == "wait" ? 0.4 : 1 });
            if (p.State != "wait")
                g.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = P.B(p.State == "ok" ? "#34C759" : "#FF9500"), Stroke = P.Bg, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2) });
            row.Children.Add(g);
        }
        return row;
    }

    // 디자인시스템 TodoCheckbox: 안 함 = 점선, 함 = 초록 채움 + 체크
    public static UIElement TodoCheck(bool done, Action toggle)
    {
        var g = new Grid { Width = 16, Height = 16, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        g.Children.Add(new Rectangle { RadiusX = 5, RadiusY = 5, Fill = done ? P.Green : Brushes.Transparent, Stroke = done ? null : P.Faint, StrokeThickness = 1.5, StrokeDashArray = done ? null : new DoubleCollection { 2, 1.5 } });
        if (done) g.Children.Add(new Path { Data = Geometry.Parse("M 3.5 8 L 6.5 11 L 12.5 5"), Stroke = Brushes.White, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round });
        g.MouseLeftButtonUp += (_, e) => { e.Handled = true; toggle(); };
        return g;
    }

    public static UIElement Dancer(string text)  // 빈 화면: 인베이더 + 문구
    {
        var sp = new StackPanel { Margin = new Thickness(0, 14, 0, 14), HorizontalAlignment = HorizontalAlignment.Center };
        var bmp = Invader.Draw(0, 0, Invader.Violet, 32);
        var h = bmp.GetHbitmap();
        var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(h, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        DeleteObject(h); bmp.Dispose();
        var img = new Image { Source = src, Width = 32, Height = 32 };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        sp.Children.Add(img);
        sp.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = P.Sub, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0) });
        return sp;
    }
    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
}

// ───────── 팝업 ─────────
public class PopupWindow : Window
{
    static PopupWindow? inst;
    public static void Toggle()
    {
        inst ??= new PopupWindow();
        if (inst.IsVisible && !inst.pinned) inst.Hide(); else inst.Present();
    }

    static Watcher W => Watcher.I;
    readonly StackPanel root = new() { Width = 340 };
    readonly ScaleTransform scale;
    bool pinned;  // 띄워두기 (안 닫히고 끌어서 옮김)
    string tab = "새 소식";
    bool showHidden, showPast, showLater, showDone, adding, picking;
    List<(string Id, string Note)> candidates = new();

    public PopupWindow()
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        scale = new ScaleTransform(W.S.Zoom, W.S.Zoom);
        Content = new Border
        {
            Margin = new Thickness(14), Padding = new Thickness(14, 12, 14, 10), CornerRadius = new CornerRadius(12),
            Background = P.Bg, BorderBrush = P.Line, BorderThickness = new Thickness(1), Child = root, LayoutTransform = scale,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = P.Dark ? 0.5 : 0.22 },
        };
        Deactivated += (_, _) => { if (!pinned) Hide(); };
        MouseLeftButtonDown += (_, e) => { if (pinned && e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        SizeChanged += (_, _) => { if (!pinned) Anchor(); };
        PreviewKeyDown += OnKey;
        W.Changed += () => { if (IsVisible) Render(); };
    }

    void Anchor() { var wa = SystemParameters.WorkArea; Left = wa.Right - ActualWidth; Top = wa.Bottom - ActualHeight; }

    void Present()
    {
        Render();
        Show();
        if (!pinned) { UpdateLayout(); Anchor(); }
        Activate();
        W.SetBadge(false);
    }

    // Ctrl+= / Ctrl++ 크게, Ctrl+- 작게, Ctrl+0 원래대로 (WPF 는 벡터로 다시 그려서 선명)
    void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        double z = W.S.Zoom;
        if (e.Key is Key.OemPlus or Key.Add) z = Math.Min(1.8, Math.Round(z + 0.1, 1));
        else if (e.Key is Key.OemMinus or Key.Subtract) z = Math.Max(0.8, Math.Round(z - 0.1, 1));
        else if (e.Key is Key.D0 or Key.NumPad0) z = 1;
        else return;
        e.Handled = true;
        W.S.Zoom = z; scale.ScaleX = scale.ScaleY = z; W.SaveSettings();
    }

    // ── 그리기 (데이터 바뀔 때마다 통째로 다시 — 몇 십 줄이라 빠름) ──
    void Render()
    {
        root.Children.Clear();
        root.Children.Add(Header());
        root.Children.Add(Chips());
        var body = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        switch (tab)
        {
            case "새 소식": Feed(body); break;
            case "할 일": Todos(body); break;
            case "병합": Work(body); break;
            default: List(body, Src(tab)); break;
        }
        root.Children.Add(body);
        root.Children.Add(Footer());
    }

    static string Src(string t) => t switch { "PR" => "PR 리뷰", "메일" => "메일", "Jira" => "Jira 할당", "병합" => "내 작업 병합", _ => t };

    UIElement Header()
    {
        var g = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 8) };
        g.Children.Add(UiKit.T("HugWatch", 15, bold: true));
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(right, Dock.Right);
        right.Children.Add(UiKit.T(W.Busy ? "확인 중…" : W.LastCheck is { } t ? $"{P.Rel(t)} 확인" : "", 11, P.Sub));
        right.Children.Add(UiKit.Click(UiKit.Glyph(""), () => _ = W.Check(), "지금 확인"));
        if (pinned)
        {
            right.Children.Add(UiKit.Click(UiKit.Glyph(W.S.OnTop ? "" : "", 13, W.S.OnTop ? P.Accent : P.Sub), () => { W.S.OnTop = !W.S.OnTop; Topmost = W.S.OnTop; W.SaveSettings(); Render(); }, W.S.OnTop ? "맨 위 고정 끄기" : "맨 위 고정"));
            right.Children.Add(UiKit.Click(UiKit.Glyph(""), () => { pinned = false; Topmost = true; Hide(); }, "닫기"));
        }
        else right.Children.Add(UiKit.Click(UiKit.Glyph(""), () => { pinned = true; Topmost = W.S.OnTop; Render(); }, "화면에 띄워두기 (끌어서 옮기기)"));
        g.Children.Add(right);
        return g;
    }

    // 칩: 보라 하나만. 선택 = 진하게 · 새 알림 = 연보라 + 점 · 항목 있음 = 회색 · 비었음 = 흐리게
    UIElement Chips()
    {
        var wrap = new WrapPanel();
        var active = Groups().Count(x => !x.Done);
        var list = new List<(string Tab, int N)> { ("새 소식", W.St.Events.Count(e => !e.Read)), ("할 일", W.St.Todos.Count(t => t.IsActive)) };
        if (W.Has("PR 리뷰")) list.Add(("PR", W.Items("PR 리뷰").Count));
        if (W.Has("메일")) list.Add(("메일", W.Items("메일").Count));
        if (W.Has("Jira 할당")) list.Add(("Jira", W.Items("Jira 할당").Count));
        list.Add(("병합", active));
        foreach (var (t, n) in list)
        {
            bool on = tab == t, isNew = W.St.Events.Any(e => !e.Read && (t == "새 소식" || e.Source == Src(t)));
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            var fg = on ? P.Bg : isNew ? P.Accent : n > 0 ? P.Text : P.Sub;
            label.Children.Add(new TextBlock { Text = t, FontSize = 11.5, Foreground = fg });
            if (n > 0) label.Children.Add(new TextBlock { Text = " " + n, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = fg });
            var chip = new Border
            {
                Child = label, CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand,
                Background = on ? P.Text : isNew ? P.AccentSoft : n > 0 ? P.ChipBg : P.ChipFaint,
            };
            var holder = new Grid();
            holder.Children.Add(chip);
            if (isNew && !on) holder.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = P.Accent, Stroke = P.Bg, StrokeThickness = 1.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, 2, 0) });
            chip.MouseLeftButtonUp += (_, _) => { tab = t; showHidden = false; if (t != "새 소식" && t != "할 일") W.MarkRead(Src(t)); Render(); };
            wrap.Children.Add(holder);
        }
        return wrap;
    }

    // 한 줄: 아이콘 · 제목/내용/세 번째 줄 · 오른쪽
    UIElement Row(string src, string title, string sub, Action onClick, string detail = "", UIElement? trailing = null, List<Person>? people = null, int titleLines = 1)
    {
        var (glyph, color) = P.Look(src);
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var c = (Color)ColorConverter.ConvertFromString(color);
        var icon = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(38, c.R, c.G, c.B)), VerticalAlignment = VerticalAlignment.Top, Child = new TextBlock { Text = glyph, FontFamily = P.Icons, FontSize = 12, Foreground = new SolidColorBrush(c), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        g.Children.Add(icon);
        var mid = new StackPanel { Margin = new Thickness(10, 0, 6, 0) };
        mid.Children.Add(UiKit.T(title, 13, bold: true, lines: titleLines));
        if (sub != "") mid.Children.Add(UiKit.T(sub, 11, P.Sub));
        if (detail != "") mid.Children.Add(UiKit.T(detail, 11, P.Sub, lines: 2));
        if (people is { Count: > 0 }) mid.Children.Add(UiKit.Avatars(people));
        Grid.SetColumn(mid, 1); g.Children.Add(mid);
        if (trailing != null) { Grid.SetColumn(trailing, 2); g.Children.Add(trailing); }
        return UiKit.Click(g, onClick, pad: new Thickness(2, 1, 2, 1));
    }

    void Empty(Panel body, string text) => body.Children.Add(UiKit.Dancer(W.Sections.Count == 0 && W.Busy ? "불러오는 중…" : text));

    UIElement Fold(string label, Action flip) => UiKit.Click(UiKit.T(label, 11, P.Sub), flip, pad: new Thickness(2, 4, 2, 2));

    // 새 소식: 안 읽은 것만. 읽은 건 '지난 알림'으로 접기
    void Feed(Panel body)
    {
        var unread = W.St.Events.Where(e => !e.Read).ToList();
        var past = W.St.Events.Where(e => e.Read).ToList();
        if (unread.Count == 0) Empty(body, "새 소식 없음 · 평화로워요");
        foreach (var e in unread.Take(8)) body.Children.Add(EventRow(e));
        if (past.Count > 0)
        {
            body.Children.Add(Fold(showPast ? "지난 알림 접기" : $"지난 알림 {past.Count}개", () => { showPast = !showPast; Render(); }));
            if (showPast) foreach (var e in past.Take(8)) { var r = EventRow(e); r.Opacity = 0.6; body.Children.Add(r); }
        }
    }

    UIElement EventRow(Event e)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        right.Children.Add(UiKit.T(P.Rel(e.Date), 10, P.Faint));
        right.Children.Add(new Ellipse { Width = 7, Height = 7, Margin = new Thickness(6, 0, 0, 0), Fill = e.Read ? Brushes.Transparent : P.Accent });
        return Row(e.Source, e.Title, e.Sub, () => { Tray.Open(e.Url); e.Read = true; W.Touch(); }, trailing: right, titleLines: 2);
    }

    // PR · 메일 · Jira 목록
    void List(Panel body, string src)
    {
        var items = W.Items(src);
        if (W.Error(src) is { } err) body.Children.Add(UiKit.T(err, 11, P.B("#E5484D"), lines: 3));
        else if (items.Count == 0) Empty(body, src switch { "PR 리뷰" => "열린 PR 없음", "메일" => "안 읽은 메일 없음", "Jira 할당" => "할당된 일 없음", _ => "없음" });
        foreach (var i in items.Take(8))
        {
            var row = Row(src, i.Title, i.Sub, () => Tray.Open(i.Url), i.Detail, i.Tag != "" ? UiKit.Pill(i.Tag, i.Tone) : null, i.People);
            ((FrameworkElement)row).ContextMenu = Menu(("목록에서 숨기기", () => { W.St.Hidden.Add($"{src}/{i.Id}"); W.Touch(); }));
            body.Children.Add(row);
        }
        if (items.Count > 8) body.Children.Add(UiKit.T($"외 {items.Count - 8}개", 11, P.Sub));
        HiddenSection(body, src);
        // 탭 아래 바로가기: 웹에서 전체 보기
        (string Label, string Url)? go = src switch
        {
            "PR 리뷰" => ("Bitbucket 내 PR", "https://bitbucket.org/dashboard/pullrequests"),
            "메일" when W.S.Daou != "" => ("다우오피스 메일함", $"{W.S.Daou}/app/mail"),
            "Jira 할당" when W.S.Jira != "" => ("Jira 내게 할당된 일", $"{W.S.Jira}/issues/?jql={Uri.EscapeDataString(Logic.MyJql)}"),
            _ => null,
        };
        if (go is { } g)
        {
            var link = UiKit.Click(UiKit.T(g.Label + " ↗", 11, P.Sub), () => Tray.Open(g.Url), pad: new Thickness(2, 4, 2, 2));
            link.HorizontalAlignment = HorizontalAlignment.Right;
            body.Children.Add(link);
        }
    }

    void HiddenSection(Panel body, string src)
    {
        var hid = W.HiddenItems(src);
        if (hid.Count == 0) return;
        body.Children.Add(Fold(showHidden ? "숨긴 항목 접기" : $"숨긴 항목 {hid.Count}개", () => { showHidden = !showHidden; Render(); }));
        if (!showHidden) return;
        foreach (var i in hid)
        {
            var row = (FrameworkElement)Row(src, i.Title, i.Sub, () => Tray.Open(i.Url));
            row.Opacity = 0.55;
            row.ContextMenu = Menu(("다시 보이기", () => { W.St.Hidden.Remove($"{src}/{i.Id}"); W.Touch(); }));
            body.Children.Add(row);
        }
    }

    static ContextMenu Menu(params (string Label, Action Do)[] items)
    {
        var m = new ContextMenu();
        foreach (var (label, act) in items)
        {
            if (label == "-") { m.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = label };
            mi.Click += (_, _) => act();
            m.Items.Add(mi);
        }
        return m;
    }

    // ── 할 일 ──
    void Todos(Panel body)
    {
        // 입력: 테두리 없이 연한 바탕 + ＋, Enter 로 추가
        var box = new TextBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = P.Text, CaretBrush = P.Text, FontSize = 13, VerticalContentAlignment = VerticalAlignment.Center };
        var hint = new TextBlock { Text = "할 일 추가", Foreground = P.Faint, FontSize = 13, IsHitTestVisible = false, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        box.TextChanged += (_, _) => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || box.Text.Trim() == "") return;
            W.St.Todos.Insert(0, new Todo { Text = box.Text.Trim() });
            W.Touch();
            Dispatcher.BeginInvoke(new Action(() => LastBox?.Focus()));
        };
        var inner = new Grid();
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inner.ColumnDefinitions.Add(new ColumnDefinition());
        inner.Children.Add(UiKit.Glyph("", 11, P.Faint));
        var field = new Grid { Margin = new Thickness(8, 0, 0, 0) };
        field.Children.Add(hint); field.Children.Add(box);
        Grid.SetColumn(field, 1); inner.Children.Add(field);
        body.Children.Add(new Border { Background = P.ChipFaint, CornerRadius = new CornerRadius(8), Padding = new Thickness(9, 6, 9, 6), Margin = new Thickness(0, 0, 0, 4), Child = inner });
        LastBox = box;

        var active = W.St.Todos.Where(t => t.IsActive).ToList();
        var later = W.St.Todos.Where(t => t.IsLater).ToList();
        var done = W.St.Todos.Where(t => t.Done).OrderByDescending(t => t.DoneAt).ToList();
        if (active.Count == 0) body.Children.Add(UiKit.Dancer("오늘 할 일 없음 · 여유롭네요"));
        foreach (var t in active) body.Children.Add(TodoRow(t));
        if (later.Count > 0)
        {
            body.Children.Add(Fold(showLater ? "나중에 접기" : $"나중에 {later.Count}개", () => { showLater = !showLater; Render(); }));
            if (showLater) foreach (var t in later) { var r = TodoRow(t); r.Opacity = 0.6; body.Children.Add(r); }
        }
        if (done.Count > 0)
        {
            body.Children.Add(Fold(showDone ? "완료 접기" : $"완료 {done.Count}개", () => { showDone = !showDone; Render(); }));
            if (showDone) foreach (var t in done) body.Children.Add(TodoRow(t));
        }
    }
    TextBox? LastBox;

    FrameworkElement TodoRow(Todo t)
    {
        void Flip() { t.Done = !t.Done; t.DoneAt = t.Done ? DateTime.Now : null; W.Touch(); }
        var g = new Grid { Margin = new Thickness(3, 4, 3, 4), Background = Brushes.Transparent };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(UiKit.TodoCheck(t.Done, Flip));
        // 티켓 키는 누르면 Jira 로 가는 링크
        var text = new TextBlock { FontSize = 13, Foreground = t.Done ? P.Faint : P.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(9, 0, 6, 0), TextDecorations = t.Done ? TextDecorations.Strikethrough : null };
        var m = Regex.Match(t.Text, Logic.TicketRe);
        if (m.Success && W.S.Jira != "")
        {
            text.Inlines.Add(new Run(t.Text[..m.Index]));
            var link = new Hyperlink(new Run(m.Value)) { Foreground = P.Green, TextDecorations = null, Cursor = Cursors.Hand };
            link.Click += (_, _) => Tray.Open($"{W.S.Jira}/browse/{m.Value}");
            text.Inlines.Add(link);
            text.Inlines.Add(new Run(t.Text[(m.Index + m.Length)..]));
        }
        else text.Text = t.Text;
        Grid.SetColumn(text, 1); g.Children.Add(text);
        if (t.Someday || t.Snooze > DateTime.Now)
        {
            var pill = UiKit.Pill(t.Someday ? "나중에" : t.Snooze!.Value.Date == DateTime.Today.AddDays(1) ? "내일" : t.Snooze!.Value.ToString("M/d"), "gray");
            Grid.SetColumn(pill, 2); g.Children.Add(pill);
        }
        var items = new List<(string, Action)> { (t.Done ? "완료 취소" : "완료", Flip) };
        if (!t.Done)
        {
            items.Add(("내일로 미루기", () => { t.Snooze = DateTime.Today.AddDays(1); t.Someday = false; W.Touch(); }));
            items.Add(("나중에", () => { t.Someday = true; t.Snooze = null; W.Touch(); }));
            if (t.IsLater) items.Add(("오늘로", () => { t.Someday = false; t.Snooze = null; W.Touch(); }));
        }
        items.Add(("-", () => { }));
        items.Add(("삭제", () => { W.St.Todos.Remove(t); W.Touch(); }));
        g.ContextMenu = Menu(items.ToArray());
        return g;
    }

    // ── 병합 ──
    record WorkGroup(string Ticket, string Repo, List<Item> Items)
    {
        public bool?[] Dots => Enumerable.Range(0, 3).Select(k => { var v = Items.Where(i => i.Dots.Length > k && i.Dots[k] != null).Select(i => i.Dots[k]!.Value).ToList(); return v.Count == 0 ? (bool?)null : v.Contains(true); }).ToArray();
        public Item Furthest => Items.OrderBy(i => i.Dots.Count(d => d == true)).Last();
        public bool Done => Dots.Length == 3 && Dots[2] == true;
    }

    List<WorkGroup> Groups() => W.Items("내 작업 병합").GroupBy(i => (i.Group, Repo: i.Title.Split(" · ")[0]))
        .Select(g => new WorkGroup(g.Key.Group, g.Key.Repo, g.ToList())).ToList();

    void Work(Panel body)
    {
        body.Children.Add(UiKit.T("내 PR이 어디까지 갔나 · 보라 칸 = 들어감 · 초록 = Jira 완료", 10.5, P.Sub));
        var gs = Groups();
        // Jira 완료(= 대개 배포 끝)는 아래로
        var active = gs.Where(g => !g.Done).OrderBy(g => W.JiraStatus.TryGetValue(g.Ticket, out var st) && st.Done).ToList();
        if (W.Error("내 작업 병합") is { } err) body.Children.Add(UiKit.T(err, 11, P.B("#E5484D"), lines: 3));
        else if (active.Count == 0) Empty(body, "진행 중인 작업 없음");
        foreach (var g in active.Take(8))
        {
            var f = g.Furthest;
            var summary = W.Summary(g.Ticket);
            var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            right.Children.Add(UiKit.Stages(g.Dots));
            if (W.JiraStatus.TryGetValue(g.Ticket, out var st)) right.Children.Add(UiKit.Pill(st.Name, st.Done ? "green" : "blue"));
            var row = (FrameworkElement)Row("내 작업 병합", summary ?? g.Ticket,
                (summary == null ? "" : $"{g.Ticket} · ") + $"{g.Repo} · {f.Sub}" + (g.Items.Count > 1 ? $" · 브랜치 {g.Items.Count}개" : ""), () => Tray.Open(f.Url), trailing: right);
            var menu = new List<(string, Action)> { ("목록에서 숨기기", () => { foreach (var i in g.Items) W.St.Hidden.Add($"내 작업 병합/{i.Id}"); W.Touch(); }) };
            foreach (var i in g.Items.Where(i => W.St.Branches.Contains(i.Id))) menu.Add(($"감시 해제: {i.Title}", () => { W.St.Branches.Remove(i.Id); W.Touch(); }));
            row.ContextMenu = Menu(menu.ToArray());
            body.Children.Add(row);
        }
        if (active.Count > 8) body.Children.Add(UiKit.T($"외 {active.Count - 8}개", 11, P.Sub));
        HiddenSection(body, "내 작업 병합");

        var bar = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var doneCount = gs.Count - gs.Count(g => !g.Done);
        bar.Children.Add(UiKit.T("D develop · R release · M master" + (doneCount > 0 ? $" · 끝난 {doneCount}개 숨김" : ""), 10.5, P.Sub));
        var add = UiKit.Click(UiKit.T(adding ? "닫기" : "+ 브랜치", 11, P.Sub), () => { adding = !adding; Render(); });
        DockPanel.SetDock(add, Dock.Right); bar.Children.Insert(0, add);
        body.Children.Add(bar);
        if (!adding) return;

        var line = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var pick = new Button { Content = picking ? "완료" : "고르기", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2) };
        pick.Click += async (_, _) =>
        {
            picking = !picking;
            if (picking) { var s = W.S; candidates = await Task.Run(() => Sources.RecentBranches(s)); } else _ = W.Check();
            Render();
        };
        DockPanel.SetDock(pick, Dock.Right); line.Children.Add(pick);
        var input = new TextBox { Padding = new Thickness(4, 2, 4, 2), ToolTip = "브랜치 이름 또는 저장소/브랜치 · Enter" };
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter && input.Text.Trim() != "") W.AddBranch(input.Text); };
        line.Children.Add(input);
        body.Children.Add(line);
        if (!picking) return;
        var scroll = new ScrollViewer { MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 0) };
        var list = new StackPanel();
        if (candidates.Count == 0) list.Children.Add(UiKit.T("최근 30일 안에 작업한 브랜치 없음", 11, P.Sub));
        foreach (var c in candidates)
        {
            var cb = new CheckBox { Content = c.Id.Replace(":", " · ") + (c.Note == "" ? "" : $"  ({c.Note})"), IsChecked = W.St.Branches.Contains(c.Id), Foreground = P.Text, FontSize = 11, Margin = new Thickness(0, 1, 0, 1) };
            cb.Checked += (_, _) => { if (!W.St.Branches.Contains(c.Id)) W.St.Branches.Add(c.Id); W.SaveState(); };
            cb.Unchecked += (_, _) => { W.St.Branches.Remove(c.Id); W.SaveState(); };
            list.Children.Add(cb);
        }
        scroll.Content = list;
        body.Children.Add(scroll);
    }

    UIElement Footer()
    {
        var d = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        var line = new Border { Height = 1, Background = P.Line, Margin = new Thickness(0, 0, 0, 6) };
        var wrap = new StackPanel();
        wrap.Children.Add(line);
        var settings = new StackPanel { Orientation = Orientation.Horizontal };
        settings.Children.Add(UiKit.Glyph("", 12)); settings.Children.Add(UiKit.T(" 설정", 11, P.Sub));
        d.Children.Add(UiKit.Click(settings, SettingsWindow.Open));
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(right, Dock.Right);
        if (W.St.Events.Any(e => !e.Read)) right.Children.Add(UiKit.Click(UiKit.T("모두 읽음", 11, P.Sub), () => { foreach (var e in W.St.Events) e.Read = true; W.Touch(); }));
        right.Children.Add(UiKit.Click(UiKit.T("종료", 11, P.Sub), Tray.Quit));
        d.Children.Add(right);
        wrap.Children.Add(d);
        return wrap;
    }
}

// ───────── 설정 창 ─────────
public class SettingsWindow : Window
{
    static SettingsWindow? inst;
    public static void Open()
    {
        inst ??= new SettingsWindow();
        inst.Show(); inst.Activate();
    }

    static Watcher W => Watcher.I;
    readonly TextBox email = new(), jiraSite = new(), bbWs = new(), daouUrl = new(), daouId = new(), roots = new();
    readonly PasswordBox jiraToken = new(), bbToken = new(), daouPw = new();
    readonly CheckBox autoStart = new() { Content = "Windows 시작할 때 자동 실행" };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 8, 0, 0) };
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    SettingsWindow()
    {
        Title = "HugWatch 설정"; Width = 470; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Closing += (_, e) => { e.Cancel = true; Hide(); };  // 닫아도 다시 열 수 있게 숨기기만
        var sp = new StackPanel { Margin = new Thickness(18) };

        void Header(string t, string? note = null)
        {
            sp.Children.Add(new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 12, 0, 4) });
            if (note != null) sp.Children.Add(new TextBlock { Text = note, Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        }
        void Field(string label, Control box, string hint = "")
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            box.Padding = new Thickness(4, 3, 4, 3); box.ToolTip = hint == "" ? null : hint;
            Grid.SetColumn(box, 1); g.Children.Add(box);
            sp.Children.Add(g);
        }
        void Link(string text, string url)
        {
            var h = new Hyperlink(new Run(text)); h.Click += (_, _) => Tray.Open(url);
            sp.Children.Add(new TextBlock(h) { FontSize = 11, Margin = new Thickness(96, 2, 0, 0) });
        }
        const string tokenPage = "https://id.atlassian.com/manage-profile/security/api-tokens";

        Header("Atlassian 계정", "Jira·Bitbucket 로그인에 같이 써요. 회사 Atlassian 계정 이메일");
        Field("이메일", email, "name@company.com");
        Header("Jira · 나에게 할당된 일", "'API 토큰 만들기'(범위 없는 일반 토큰). 만료일은 최대 1년");
        Field("사이트 주소", jiraSite, "company.atlassian.net");
        Field("API 토큰", jiraToken, "붙여넣기 · 비워두면 그대로");
        Link("토큰 만들기 ↗", tokenPage);
        Header("Bitbucket · 내 PR 리뷰", "'범위가 있는 API 토큰 만들기' → 앱 Bitbucket → 권한 read:user, read:pullrequest");
        Field("워크스페이스", bbWs, "bitbucket.org/여기 부분");
        Field("API 토큰", bbToken, "붙여넣기 · 비워두면 그대로");
        Link("토큰 만들기 ↗", tokenPage);
        Header("다우오피스 메일 (선택)", "안 쓰면 비워두세요. 로그인에 2차 인증이 켜져 있으면 안 돼요");
        Field("주소", daouUrl, "groupware.company.com");
        Field("아이디", daouId);
        Field("비밀번호", daouPw, "비워두면 그대로");
        Header("병합 추적 · 저장소 폴더", "쉼표로 여러 개 (예: %USERPROFILE%\\work). 이 안의 git 저장소에서 내 PR 브랜치가 develop·release·master 까지 갔는지 봐요. Git for Windows 필요");
        Field("폴더", roots);
        sp.Children.Add(new Border { Height = 10 });
        sp.Children.Add(autoStart);

        var save = new Button { Content = "저장하고 확인", IsDefault = true, Padding = new Thickness(14, 4, 14, 4), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        save.Click += async (_, _) => await Save();
        sp.Children.Add(save);
        sp.Children.Add(status);
        sp.Children.Add(new TextBlock { Text = "토큰·비밀번호는 이 PC 의 자격 증명 관리자에만 저장돼요. 각 서비스 말고 다른 곳으로는 아무것도 보내지 않아요.", Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
        Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80 };
        Load();
    }

    void Load()
    {
        var s = W.S;
        email.Text = s.Email; jiraSite.Text = s.JiraSite; bbWs.Text = s.BbWorkspace; daouUrl.Text = s.DaouUrl; daouId.Text = s.DaouId; roots.Text = s.RepoRoots;
        autoStart.IsChecked = s.AutoStart;
        jiraToken.ToolTip = Creds.Has("jira-token") ? "저장됨 · 바꾸려면 새로 붙여넣기" : "붙여넣기";
        bbToken.ToolTip = Creds.Has("bitbucket-token") ? "저장됨 · 바꾸려면 새로 붙여넣기" : "붙여넣기";
        daouPw.ToolTip = Creds.Has("daou-password") ? "저장됨 · 바꾸려면 새로 입력" : "입력";
        status.Text = string.Join("  ", new[] { ("Jira 토큰", "jira-token"), ("Bitbucket 토큰", "bitbucket-token"), ("메일 비밀번호", "daou-password") }.Where(x => Creds.Has(x.Item2)).Select(x => $"{x.Item1} 저장됨 ✓"));
    }

    async Task Save()
    {
        var s = W.S;
        s.Email = email.Text.Trim(); s.JiraSite = jiraSite.Text.Trim(); s.BbWorkspace = bbWs.Text.Trim(); s.DaouUrl = daouUrl.Text.Trim(); s.DaouId = daouId.Text.Trim(); s.RepoRoots = roots.Text.Trim();
        s.AutoStart = autoStart.IsChecked == true;
        W.SaveSettings();
        foreach (var (key, box) in new[] { ("jira-token", jiraToken), ("bitbucket-token", bbToken), ("daou-password", daouPw) })
            if (box.Password.Trim() != "") { Creds.Set(key, box.Password.Trim()); box.Clear(); }
        Sources.DaouFail = null;  // 새 비밀번호면 다시 로그인 시도
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (s.AutoStart) k.SetValue("HugWatch", $"\"{Environment.ProcessPath}\""); else k.DeleteValue("HugWatch", false);
        }
        catch { }
        status.Text = "확인 중…";
        await W.Check();
        status.Text = string.Join("\n", new[] { ("PR 리뷰", "Bitbucket"), ("Jira 할당", "Jira"), ("메일", "메일"), ("내 작업 병합", "병합 추적") }
            .Where(x => W.Has(x.Item1)).Select(x => W.Error(x.Item1) is { } e ? $"{x.Item2} ✗ {e}" : $"{x.Item2} ✓ {W.All(x.Item1).Count}개"));
        Load();
    }
}
