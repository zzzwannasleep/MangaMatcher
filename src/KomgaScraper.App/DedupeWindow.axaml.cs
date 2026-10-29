using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KomgaScraper.Core;
using KomgaScraper.Core.Komga;

namespace KomgaScraper.App;

/// <summary>重复清理列表行（UI 绑定用；勾选状态可变）。</summary>
public sealed class DedupeRowVm : INotifyPropertyChanged
{
    private bool _checked;
    public event PropertyChangedEventHandler? PropertyChanged;

    public DedupeCandidate Candidate { get; }

    public DedupeRowVm(DedupeCandidate candidate, bool isChecked)
    {
        Candidate = candidate;
        _checked = isChecked && candidate.Selectable;
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Checked)));
        }
    }

    public bool Selectable => Candidate.Selectable;
    public string KindText => Candidate.KindText;
    public string Title => Candidate.Title;
    public string Folder => Candidate.Folder;
    public string Books => Candidate.Kind switch
    {
        DedupeKind.Series => Candidate.BookCount.ToString(),
        DedupeKind.PartialSeries => $"{Candidate.MatchedBooks}/{Candidate.BookCount}",
        _ => "1",
    };
    public string KeepTitles => Candidate.KeepTitles;
    public string SizeText => Candidate.SizeText;
    public string Evidence => Candidate.Evidence;
}

/// <summary>
/// 重复清理窗口：选「保留库 / 清理库」→ 扫描同尺寸重复 → 勾选 → 删除清理库里的重复。
/// 保留库只读；删除走 Komga 官方 DELETE 接口（不可恢复），Dry-run 只打预览日志。
/// </summary>
public sealed partial class DedupeWindow : Window
{
    private readonly KomgaClient _komga;
    private readonly Action<string, string> _log;
    private readonly IAppLogger _logger;
    private readonly ObservableCollection<DedupeRowVm> _rows = [];
    private readonly HashSet<string> _deletedSeriesIds = new(StringComparer.Ordinal);
    private DuplicateCleaner? _cleaner;
    private LibraryDto? _planKeep;    // 本次扫描对应的两个库（执行时只认它们，防止改下拉框后删错库）
    private LibraryDto? _planClean;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _ready;

    /// <summary>窗口关闭后由主界面读取：记住上次选择 + 刷新库视图。</summary>
    public string LastKeepLibraryId { get; private set; } = "";
    public string LastCleanLibraryId { get; private set; } = "";
    public string LastMode { get; private set; } = "series";
    public string LastMatch { get; private set; } = "pages";
    /// <summary>真实删除成功的项数（>0 时主界面做缓存整理/后台对齐）。</summary>
    public int DeletedCount { get; private set; }
    /// <summary>真实删除成功的整部系列 id（主界面立即从本地缓存剔除，避免列表残留）。</summary>
    public IReadOnlyCollection<string> DeletedSeriesIds => _deletedSeriesIds;

    public DedupeWindow(KomgaClient komga, IReadOnlyList<LibraryDto> libraries, Action<string, string> log,
        bool defaultDryRun, string keepId, string cleanId, string mode, string match)
    {
        InitializeComponent();
        _komga = komga;
        _log = log;
        _logger = new WindowLogger(log);

        CmbKeep.ItemsSource = libraries;
        CmbClean.ItemsSource = libraries;
        // 只在记得的库还存在时预选；旧配置里没有就留空（避免"随便预选两个库"被一键扫描+删除）
        CmbKeep.SelectedIndex = FindLibraryIndex(libraries, keepId);
        CmbClean.SelectedIndex = FindLibraryIndex(libraries, cleanId);

        CmbMode.ItemsSource = new[] { "整部系列（全册都重复才删，推荐）", "逐册（只删重复的册）" };
        CmbMode.SelectedIndex = mode == "book" ? 1 : 0;
        CmbMatch.ItemsSource = new[]
        {
            "大小 + 页数（推荐，有哈希时按哈希）",
            "仅文件大小",
            "大小 + 页数 + 哈希（要求双方都有）",
        };
        CmbMatch.SelectedIndex = match switch { "size" => 1, "hash" => 2, _ => 0 };

        ChkDryRun.IsChecked = defaultDryRun;
        RowsGrid.ItemsSource = _rows;

        BtnScan.Click += async (_, _) => await ScanAsync();
        BtnExecute.Click += async (_, _) => await ExecuteAsync();
        BtnClose.Click += (_, _) => Close();
        BtnSelectAll.Click += (_, _) => SetAllChecked(true);
        BtnSelectNone.Click += (_, _) => SetAllChecked(false);
        RowsGrid.AddHandler(PointerPressedEvent, OnRowsPointerPressed, RoutingStrategies.Tunnel);
        CmbMode.SelectionChanged += (_, _) => OnOptionChanged();
        CmbMatch.SelectionChanged += (_, _) => OnOptionChanged();
        CmbKeep.SelectionChanged += (_, _) => OnOptionChanged();
        CmbClean.SelectionChanged += (_, _) => OnOptionChanged();
        Closing += (_, _) =>
        {
            _cts?.Cancel();
            RememberChoices();
        };

        _ready = true;
        UpdateSelection();
    }

    // ================= 选项 → 枚举 =================

    private DedupeMode Mode => CmbMode.SelectedIndex == 1 ? DedupeMode.PerBook : DedupeMode.WholeSeries;

    private DedupeMatch Match => CmbMatch.SelectedIndex switch
    {
        1 => DedupeMatch.SizeOnly,
        2 => DedupeMatch.SizePagesAndHash,
        _ => DedupeMatch.SizeAndPages,
    };

    private string ModeKey => Mode == DedupeMode.PerBook ? "book" : "series";

    private string MatchKey => CmbMatch.SelectedIndex switch
    {
        1 => "size",
        2 => "hash",
        _ => "pages",
    };

    /// <summary>只在记得的库 id 命中时返回下标；否则 -1（下拉框留空，由用户自己选）。</summary>
    private static int FindLibraryIndex(IReadOnlyList<LibraryDto> libraries, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return -1;
        for (var i = 0; i < libraries.Count; i++)
            if (libraries[i].Id == id) return i;
        return -1;
    }

    private void RememberChoices()
    {
        if (CmbKeep.SelectedItem is LibraryDto keep && CmbClean.SelectedItem is LibraryDto clean)
        {
            LastKeepLibraryId = keep.Id;
            LastCleanLibraryId = clean.Id;
        }
        LastMode = ModeKey;
        LastMatch = MatchKey;
    }

    /// <summary>扫描后改粒度/判定强度/库 → 旧结果作废，必须重扫（避免用旧清单删错东西）。</summary>
    private void OnOptionChanged()
    {
        if (!_ready || _busy || _rows.Count == 0) return;
        _rows.Clear();
        TxtSummary.Text = "选项已改变，请重新「扫描重复」";
        UpdateSelection();
    }

    // ================= 扫描 =================

    private async Task ScanAsync()
    {
        if (_busy) return;
        if (CmbKeep.SelectedItem is not LibraryDto keep || CmbClean.SelectedItem is not LibraryDto clean)
        {
            await Ui.MessageAsync(this, "请先选择「保留库」和「清理库」", "重复清理", okOnly: true);
            return;
        }
        if (keep.Id == clean.Id)
        {
            await Ui.MessageAsync(this, "保留库和清理库不能是同一个库（否则会把唯一的一份也删掉）", "重复清理", okOnly: true);
            return;
        }

        var options = new DedupeOptions { Mode = Mode, Match = Match };
        SetBusy(true, "扫描中：读取两个库的书籍与大小…");
        _cts = new CancellationTokenSource();
        try
        {
            _cleaner = new DuplicateCleaner(_komga, _logger);
            var plan = await _cleaner.ScanAsync(keep, clean, options, _cts.Token);
            if (!IsVisible) return;

            foreach (var vm in _rows) vm.PropertyChanged -= OnRowChanged;
            _rows.Clear();
            foreach (var c in plan.Candidates)
            {
                // 默认不勾选：扫描只负责列清单，真要删请点「全选可删」再逐行确认
                var vm = new DedupeRowVm(c, isChecked: false);
                vm.PropertyChanged += OnRowChanged;
                _rows.Add(vm);
            }

            TxtSummary.Text = plan.Summary;
            if (plan.Candidates.Count == 0 && !plan.CleanAllSizesUnknown)
                TxtSummary.Text += "（两个库之间没有同尺寸的书）";

            _planKeep = keep;
            _planClean = clean;
            LastKeepLibraryId = keep.Id;
            LastCleanLibraryId = clean.Id;
            LastMode = ModeKey;
            LastMatch = MatchKey;

            if (plan.CleanAllSizesUnknown)
                await Ui.ErrorAsync(this, plan.Summary, "无法比较");
        }
        catch (OperationCanceledException)
        {
            TxtSummary.Text = "扫描已取消";
        }
        catch (Exception e)
        {
            TxtSummary.Text = $"扫描失败：{e.Message}";
            _log("ERR ", $"[重复清理] 扫描失败：{e.Message}");
            if (IsVisible) await Ui.ErrorAsync(this, e.Message, "扫描失败");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false, null);
        }
        UpdateSelection();
    }

    // ================= 执行删除 =================

    private async Task ExecuteAsync()
    {
        if (_busy || _cleaner == null) return;
        var selected = _rows.Where(r => r.Checked && r.Selectable).ToList();
        if (selected.Count == 0) return;

        var dryRun = ChkDryRun.IsChecked == true;
        var keep = _planKeep;
        var clean = _planClean;
        if (keep == null || clean == null) return;
        var books = selected.Sum(r => r.Candidate.BookCount);
        var bytes = selected.Sum(r => r.Candidate.Bytes);
        var what = Mode == DedupeMode.PerBook
            ? $"{selected.Count} 册（{DuplicateCleaner.FormatBytes(bytes)}）"
            : $"{selected.Count} 部系列 / {books} 册（{DuplicateCleaner.FormatBytes(bytes)}）";

        var msg = dryRun
            ? $"Dry-run 预览：逐条列出「将要删除」的目标，不会真的删文件。\n\n" +
              $"清理库「{clean.Name}」：{what}\n保留库「{keep.Name}」不受影响。\n\n继续？"
            : $"即将真实删除：\n\n" +
              $"清理库「{clean.Name}」：{what}\n保留库「{keep.Name}」不受影响。\n\n" +
              "删除后文件无法恢复（Komga 直接从磁盘移除），确定继续？";

        if (!await Ui.MessageAsync(this, msg, dryRun ? "Dry-run 预览" : "确认删除", error: !dryRun)) return;

        SetBusy(true, dryRun ? "预览中…" : "删除中…");
        _cts = new CancellationTokenSource();
        try
        {
            var done = await _cleaner.ExecuteAsync(selected.Select(r => r.Candidate), dryRun, _cts.Token);
            var failed = selected.Count - done;

            if (!dryRun)
            {
                DeletedCount += done;
                CollectDeleted(_cleaner.LastSucceeded);
                RemoveRows(_cleaner.LastSucceeded);   // 只移除真正删成功的行
            }

            TxtSummary.Text = dryRun
                ? $"Dry-run 预览完成：{done} 项（详见主窗口日志，未删除任何文件）"
                : failed > 0
                    ? $"已删除 {done} 项，{failed} 项失败（失败项仍保留在列表里，详见日志）"
                    : $"已删除 {done} 项（详见主窗口日志）";

            if (!IsVisible) return;
            await Ui.InfoAsync(this,
                dryRun
                    ? $"Dry-run 完成：预览 {done} 项，未删除任何文件。\n详见主窗口日志。"
                    : $"删除完成：{done} 项。\n" +
                      (failed > 0 ? $"\n{failed} 项失败（多为权限/网络问题），失败项仍保留在列表，可重试。\n" : "") +
                      "\nKomga 的删除是后台任务，删完若库里还显示空系列，等任务结束在该库菜单" +
                      "执行「清空回收站 / Empty trash」即可从列表里清掉。",
                dryRun ? "预览完成" : "完成");
        }
        catch (OperationCanceledException)
        {
            // 取消时已经删掉的部分也要计入，主界面关闭后仍会整理缓存
            if (!dryRun)
            {
                DeletedCount += _cleaner.LastRunCount;
                CollectDeleted(_cleaner.LastSucceeded);
                RemoveRows(_cleaner.LastSucceeded);
            }
            TxtSummary.Text = $"已取消（已处理 {_cleaner.LastRunCount} 项）";
        }
        catch (Exception e)
        {
            TxtSummary.Text = $"删除失败：{e.Message}";
            _log("ERR ", $"[重复清理] 删除失败：{e.Message}");
            if (IsVisible) await Ui.ErrorAsync(this, e.Message, "删除失败");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false, null);
        }
        UpdateSelection();
    }

    private void CollectDeleted(IEnumerable<DedupeCandidate> succeeded)
    {
        foreach (var c in succeeded)
            if (c.Kind == DedupeKind.Series) _deletedSeriesIds.Add(c.DeleteId);
    }

    private void RemoveRows(IEnumerable<DedupeCandidate> succeeded)
    {
        var set = new HashSet<DedupeCandidate>(succeeded);
        if (set.Count == 0) return;
        foreach (var vm in _rows.Where(r => set.Contains(r.Candidate)).ToList())
        {
            vm.PropertyChanged -= OnRowChanged;
            _rows.Remove(vm);
        }
    }

    // ================= 勾选与状态 =================

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DedupeRowVm.Checked)) UpdateSelection();
    }

    private void SetAllChecked(bool value)
    {
        foreach (var row in _rows)
        {
            if (row.Selectable) row.Checked = value;
        }
        UpdateSelection();
    }

    /// <summary>
    /// 勾选列点击：DataGrid 单元格会吃掉内容区的按压，复选框经常收不到点击（老问题，
    /// 主列表同样处理）——这里在隧道阶段按行坐标命中首列，手动翻转。
    /// </summary>
    private void OnRowsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_busy || RowsGrid.Columns.Count == 0) return;
        var pt = e.GetCurrentPoint(RowsGrid);
        if (!pt.Properties.IsLeftButtonPressed) return;

        var el = e.Source as Visual;
        while (el is not null && el is not DataGridRow) el = el.Parent as Visual;
        if (el is not DataGridRow row || row.DataContext is not DedupeRowVm vm) return;
        if (e.GetPosition(row).X > RowsGrid.Columns[0].ActualWidth) return;
        if (!vm.Selectable) return;

        vm.Checked = !vm.Checked;
        UpdateSelection();
        e.Handled = true;
    }

    private void UpdateSelection()
    {
        var selected = _rows.Where(r => r.Checked && r.Selectable).ToList();
        var bytes = selected.Sum(r => r.Candidate.Bytes);
        var books = selected.Sum(r => r.Candidate.BookCount);

        BtnExecute.Content = Mode == DedupeMode.PerBook ? "删除选中册" : "删除选中系列";
        BtnExecute.IsEnabled = !_busy && selected.Count > 0;
        BtnSelectAll.IsEnabled = !_busy && _rows.Any(r => r.Selectable);
        BtnSelectNone.IsEnabled = !_busy && _rows.Count > 0;

        if (selected.Count == 0)
        {
            var partial = _rows.Count(r => r.Candidate.Kind == DedupeKind.PartialSeries);
            TxtSelected.Text = _rows.Count == 0
                ? "未选择任何项"
                : partial > 0 && Mode == DedupeMode.WholeSeries
                    ? $"未选择任何项（{partial} 部只重复了一部分/册数不完整，整部模式不处理；切「逐册」可逐册删）"
                    : "未选择任何项（勾选左侧方框，或点「全选可删」）";
        }
        else
        {
            TxtSelected.Text = $"已选 {selected.Count} 项 · {books} 册 · {DuplicateCleaner.FormatBytes(bytes)}";
        }
    }

    private void SetBusy(bool busy, string? summary)
    {
        _busy = busy;
        BusyBar.IsVisible = busy;
        CmbKeep.IsEnabled = CmbClean.IsEnabled = CmbMode.IsEnabled = CmbMatch.IsEnabled = !busy;
        RowsGrid.IsEnabled = !busy;   // 扫描/删除中禁止改勾选
        BtnScan.IsEnabled = !busy;
        BtnSelectAll.IsEnabled = !busy && _rows.Any(r => r.Selectable);
        BtnSelectNone.IsEnabled = !busy && _rows.Count > 0;
        BtnClose.IsEnabled = true;   // 关闭随时可用：关闭 = 取消正在进行的扫描/删除
        if (summary != null) TxtSummary.Text = summary;
        if (busy) { BtnExecute.IsEnabled = false; }
    }

    /// <summary>把窗口日志接到主界面日志框（重复清理的每条动作都可追溯）。</summary>
    private sealed class WindowLogger(Action<string, string> log) : IAppLogger
    {
        public void Info(string message) => log("INFO", message);
        public void Warn(string message) => log("WARN", message);
        public void Error(string message) => log("ERR ", message);
        public void Progress(int current, int total, string message) { }
    }
}
