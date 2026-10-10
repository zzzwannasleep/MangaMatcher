using KomgaScraper.Core;

namespace KomgaScraper.App;

/// <summary>把 Core 的日志回调转发到 WinForms 日志框与进度条（线程安全）。</summary>
internal sealed class UiLogger(Action<string, string> appendLog, Action<int, int, string> progress) : IAppLogger
{
    public void Info(string message) => appendLog("INFO", message);
    public void Warn(string message) => appendLog("WARN", message);
    public void Error(string message) => appendLog("ERR ", message);
    public void Progress(int current, int total, string message) => progress(current, total, message);
}