using System.ComponentModel;
using System.Runtime.CompilerServices;
using KomgaScraper.Core.Komga;

namespace KomgaScraper.App;

/// <summary>INPC 基类：一行代码实现属性通知。</summary>
public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>系列列表行（DataGrid 绑定模型；勾选状态随 VM 常驻，筛选不丢勾选）。</summary>
public sealed class SeriesRow : NotifyBase
{
    private SeriesDto _series;
    private bool _checked;
    private string _title = "";
    private string _cover = "…";
    private string _summary = "✗";
    private string _status;
    private string _link;
    private string _bgmId;
    private string _count;
    private readonly Func<string> _coverCell;

    public SeriesRow(SeriesDto s, Func<string> coverCell)
    {
        _series = s;
        _coverCell = coverCell;
        _title = TitleOf(s);
        _status = s.Metadata?.Status ?? "";
        _link = HasBtvLink(s) ? "Btv" : "";
        _count = s.BooksCount.ToString();
        _bgmId = GetBtvSubjectId(s)?.ToString() ?? "";
        _cover = _coverCell();
        _summary = string.IsNullOrEmpty(s.Metadata?.Summary) ? "✗" : "✓";
    }

    public SeriesDto Series { get => _series; private set => Set(ref _series, value); }
    public bool Checked { get => _checked; set => Set(ref _checked, value); }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Cover { get => _cover; set => Set(ref _cover, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Link { get => _link; set => Set(ref _link, value); }
    public string Count { get => _count; set => Set(ref _count, value); }
    public string BgmId { get => _bgmId; set => Set(ref _bgmId, value); }
    public string Folder => Path.GetFileName((Series.Url ?? "").TrimEnd('/'));

    /// <summary>按列索引取单元格文本（排序用；0=勾选列返回空）。</summary>
    public string GetCell(int col) => col switch
    {
        1 => Title,
        2 => Folder,
        3 => Count,
        4 => Status,
        5 => Link,
        6 => Cover,
        7 => Summary,
        8 => BgmId,
        _ => "",
    };

    public static string TitleOf(SeriesDto s)
        => string.IsNullOrWhiteSpace(s.Metadata?.Title) ? s.Name : s.Metadata!.Title!;

    /// <summary>服务器数据更新后原地替换 DTO（勾选状态保留），全部列属性刷新。</summary>
    public void Replace(SeriesDto s, string coverCell)
    {
        _series = s;
        Title = TitleOf(s);
        Status = s.Metadata?.Status ?? "";
        Link = HasBtvLink(s) ? "Btv" : "";
        Count = s.BooksCount.ToString();
        BgmId = GetBtvSubjectId(s)?.ToString() ?? "";
        Cover = _coverCell();
        Summary = string.IsNullOrEmpty(s.Metadata?.Summary) ? "✗" : "✓";
    }

    // 链接工具（自含实现，不依赖 MainForm，避免 UI 编译错误级联）
    private static readonly System.Text.RegularExpressions.Regex SubjectIdRegex =
        new(@"subject/(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static string ExtractSubjectIdText(SeriesDto s) =>
        s.Metadata?.Links?.FirstOrDefault(l => l.Url.Contains("/subject/", StringComparison.Ordinal))?.Url ?? "";

    internal static bool HasBtvLink(SeriesDto s) => ExtractSubjectIdText(s).Length > 0;

    internal static int? GetBtvSubjectId(SeriesDto s)
    {
        var url = ExtractSubjectIdText(s);
        var m = SubjectIdRegex.Match(url);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }
}

/// <summary>日志行（ListBox 虚拟化绑定；新行滑入动效由 ItemsControl 项容器样式实现）。</summary>
public sealed class LogLine
{
    public string Time { get; }
    public string Level { get; }
    public string Message { get; }
    public string LevelColor { get; }
    public string LevelForeground => LevelColor;

    public LogLine(string time, string level, string message)
    {
        Time = time;
        Level = level;
        Message = message;
        LevelColor = level switch
        {
            "INFO" => "#9FE8A9",
            "WARN" => "#FFD37E",
            "ERR " => "#FF8E8E",
            _ => "#8FC9FF",   // 改动行：蓝绿
        };
    }
}
