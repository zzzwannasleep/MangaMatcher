using Avalonia.Threading;
using KomgaScraper.Core;

namespace KomgaScraper.App;

/// <summary>把 Core 的日志回调转发到 Avalonia UI（线程安全，自动切回 UI 线程）。</summary>
internal sealed class UiLogger(Action<string, string> appendLog, Action<int, int, string> progress) : IAppLogger
{
    public void Info(string message) => Post(() => appendLog("INFO", message));
    public void Warn(string message) => Post(() => appendLog("WARN", message));
    public void Error(string message) => Post(() => appendLog("ERR ", message));
    public void Progress(int current, int total, string message) => Post(() => progress(current, total, message));

    private static void Post(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a, DispatcherPriority.Background);
    }
}
