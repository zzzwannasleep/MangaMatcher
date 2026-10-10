namespace KomgaScraper.Core;

/// <summary>日志/进度回调接口，由 UI 注入实现；Core 内不引用任何 UI 类型。</summary>
public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    void Progress(int current, int total, string message);
}

/// <summary>空实现（用于无 UI 场景）。</summary>
public sealed class NullLogger : IAppLogger
{
    public static readonly NullLogger Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Progress(int current, int total, string message) { }
}
