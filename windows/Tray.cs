// 작업 표시줄 트레이: 도트 인베이더 아이콘 (새 소식 있으면 팔 흔들며 폴짝 + 무지개), 윈도우 알림
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace HugWatch;

// 원작 스페이스 인베이더(게). 두 프레임 번갈아 → 행진
public static class Invader
{
    public static readonly string[][] Frames =
    {
        new[] { "..#.....#..", "...#...#...", "..#######..", ".##.###.##.", "###########", "#.#######.#", "#.#.....#.#", "...##.##..." },
        new[] { "..#.....#..", "#..#...#..#", "#.#######.#", "###.###.###", "###########", ".#########.", "..#.....#..", ".#.......#." },
    };
    public static readonly int[] Hop = { 0, 1, 2, 2, 1, 0, 1, 2, 2, 1, 0, 0, 0, 0 };  // 폴짝폴짝 + 쉼

    // 테두리 포함 칸: 0 빈칸 · 1 몸 · 2 테두리 (몸에 닿은 빈칸)
    public static int[,] Cells(int frame)
    {
        var raw = Frames[frame % Frames.Length];
        int h = raw.Length + 2, w = raw[0].Length + 2;
        var c = new int[h, w];
        for (int y = 0; y < raw.Length; y++)
            for (int x = 0; x < raw[0].Length; x++)
                if (raw[y][x] == '#') c[y + 1, x + 1] = 1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (c[y, x] == 0 && new[] { (1, 0), (-1, 0), (0, 1), (0, -1) }.Any(d => y + d.Item2 >= 0 && y + d.Item2 < h && x + d.Item1 >= 0 && x + d.Item1 < w && c[y + d.Item2, x + d.Item1] == 1))
                    c[y, x] = 2;
        return c;
    }

    public static readonly Color Violet = Color.FromArgb(120, 84, 255);
    public static readonly Color Outline = Color.FromArgb(33, 23, 84);

    public static Color Hue(double h)  // 무지개 (채도 0.75, 명도 1)
    {
        double s = 0.75, v = 1, c = v * s, x = c * (1 - Math.Abs(h * 6 % 2 - 1)), m = v - c;
        var (r, g, b) = (int)(h * 6) switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    // 32px 트레이 아이콘 (칸당 2px). dy = 폴짝 높이(칸)
    public static Bitmap Draw(int frame, int dy, Color body, int size = 32)
    {
        var cells = Cells(frame);
        int h = cells.GetLength(0), w = cells.GetLength(1), px = size / 16;
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        using var b1 = new SolidBrush(body);
        using var b2 = new SolidBrush(Outline);
        int ox = (size - w * px) / 2, oy = (size - h * px) / 2 + px * 2 - dy * px;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (cells[y, x] != 0) g.FillRectangle(cells[y, x] == 1 ? b1 : b2, ox + x * px, oy + y * px, px, px);
        return bmp;
    }
}

public class Tray
{
    public static Tray? I;

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    readonly WinForms.NotifyIcon icon;
    readonly Icon idle;
    readonly Icon[] anim;  // 프레임을 미리 만들어 둠 (매번 그리면 GDI 핸들이 새고 무거움)
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(90) };
    int tick;
    string? lastUrl;

    static Icon ToIcon(Bitmap b) { var h = b.GetHicon(); var i = (Icon)Icon.FromHandle(h).Clone(); DestroyIcon(h); b.Dispose(); return i; }

    public Tray()
    {
        idle = ToIcon(Invader.Draw(0, 0, Invader.Violet));
        anim = Enumerable.Range(0, Invader.Hop.Length * 2).Select(t => ToIcon(Invader.Draw(t / 2, Invader.Hop[t % Invader.Hop.Length], Invader.Hue(t % 14 / 14.0)))).ToArray();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("열기", null, (_, _) => PopupWindow.Toggle());
        menu.Items.Add("설정", null, (_, _) => SettingsWindow.Open());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Quit());

        icon = new WinForms.NotifyIcon { Icon = idle, Visible = true, Text = "HugWatch", ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) PopupWindow.Toggle(); };
        icon.BalloonTipClicked += (_, _) => { if (!string.IsNullOrEmpty(lastUrl)) Open(lastUrl); };

        timer.Tick += (_, _) => { tick++; icon.Icon = anim[tick % anim.Length]; };
        Watcher.I.Changed += Sync;
        Watcher.I.Notify += (title, body, url) => { lastUrl = url; icon.ShowBalloonTip(6000, title, body, WinForms.ToolTipIcon.None); };
    }

    void Sync()
    {
        if (Watcher.I.Badge && !timer.IsEnabled) timer.Start();
        else if (!Watcher.I.Badge && timer.IsEnabled) { timer.Stop(); icon.Icon = idle; }
    }

    public static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public static void Quit()
    {
        if (I != null) { I.icon.Visible = false; I.icon.Dispose(); }
        System.Windows.Application.Current.Shutdown();
    }
}
