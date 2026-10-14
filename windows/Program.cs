// 진입점: HugWatch.exe (트레이 앱) · HugWatch.exe --selftest (로직 검사, 종료 코드로 결과)
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace HugWatch;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run();

        using var single = new Mutex(true, @"Local\HugWatch.Single", out var first);  // 하나만 실행
        if (!first) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // 예상 못 한 오류로 트레이 앱이 꺼지지 않게: 기록만 남기고 계속
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HugWatch");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTime.Now:s} {e.Exception}\n");
            }
            catch { }
        };
        app.Startup += (_, _) =>
        {
            Tray.I = new Tray();
            Watcher.I.Start();
            if (Watcher.I.S.Email == "" && Watcher.I.S.DaouUrl == "") SettingsWindow.Open();  // 처음 켰을 때
        };
        app.Run();
        return 0;
    }
}
