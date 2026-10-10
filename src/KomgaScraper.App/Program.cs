using Avalonia;
using System;

namespace KomgaScraper.App;

internal static class Program
{
    /// <summary>未处理异常写进文件日志（格式对齐界面日志，导出日志时一并带走）。</summary>
    private static void TryLogUnhandled(Exception ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data", "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"log-{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}][ERR ] 未处理异常：{ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}\r\n");
        }
        catch { /* 日志写不了就算了，别在异常处理器里再炸 */ }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) TryLogUnhandled(ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => TryLogUnhandled(e.Exception);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            TryLogUnhandled(ex);
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
