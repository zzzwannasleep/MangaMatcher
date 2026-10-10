using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KomgaScraper.Core;
using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Mylar;
using KomgaScraper.Core.Scraping;
using KomgaScraper.Core.Sources;
using KomgaScraper.Core.Text;

namespace KomgaScraper.App;

/// <summary>
/// 主界面（Avalonia 跨平台版）：左侧固定宽度操作栏 + 右侧弹性内容区（列表/日志）。
/// 业务编排从 WinForms 版 1:1 移植；Core 层零改动。
/// </summary>
public sealed partial class MainWindow : Window
{
    private AppSettings _settings = AppSettings.Load();

    private KomgaClient? _komga;
    private BangumiClient? _bangumi;
    private ScrapeService? _scrape;
    private BatchMatcher? _batch;
    private MylarExporter? _mylar;
    private FolderTagger? _tagger;
    private AiClient? _ai;
    private BookWalkerIndex? _bwIndex;
    private MoxClient? _mox;
    private BaikeClient? _baike;
    private MangaDexClient? _mdex;
    private ScrapeStateStore? _state;

    private List<LibraryDto> _libraries = [];
    private readonly Dictionary<string, SeriesDto> _seriesCache = new();
    private bool _busy;
    private bool _saveErrorShown;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _coverSweepCts;
    private bool _logFileFailed;

    // 托盘：点 X / 最小化 = 挂后台，任务照常跑
    private TrayIcon? _tray;
    private bool _trayExitPending;

    // 外部封面下载（BW/Mox 候选封面，浏览器 UA + 代理，懒创建）
    private HttpClient? _externalImageHttp;
    private readonly object _externalImageHttpGate = new();

    // 右键命中的系列（右键列表项时用光标位置命中，不用焦点行）
    private SeriesDto? _contextSeries;

    // ---- 绑定集合 ----
    private readonly ObservableCollection<SeriesRow> _rows = [];
    private readonly ObservableCollection<LogLine> _logLines = [];

    // ---- 改动留痕：并入日志框，状态栏统计条数 ----
    private int _previewCount;

    // 列表列名（排序用）
    private static readonly string[] ColNames = ["Checked", "标题", "文件夹名", "册数", "状态", "链接", "封面", "简介", "BGM ID"];

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://KomgaScraper/App.ico")));
        }
        catch { /* 取不到就用默认图标 */ }

        // 绑定日志集合 + 自动滚动到底（新日志永远可见）
        LogList.ItemsSource = _logLines;
        _logLines.CollectionChanged += (_, _) => ScrollLogToBottom();

        // 列表视图：直接绑定 ObservableCollection；筛选/排序见 ApplyFilter / OnSeriesSorting
        SeriesGrid.ItemsSource = _rows;

        // 空态提示：行集合变化时联动（无数据=引导加载；有数据无匹配=提示调整筛选）
        _rows.CollectionChanged += (_, _) => UpdateEmptyHint();

        WireEvents();
        SetupTray();

        LoadSettingsToUi();
        ApplyTheme(_settings.Theme == "dark", persist: false);
        RebuildClients();

        // 首次运行：立刻生成一份默认 settings.json，用户能看到设置文件的确切位置
        if (!File.Exists(AppSettings.SettingsFile)) SaveSettings();
        Log("INFO", $"就绪。设置文件: {AppSettings.SettingsFile}");
        if (AppSettings.LastError != null) Log("ERR ", AppSettings.LastError);
        Log(_settings.DryRun ? "WARN" : "INFO", DryRunText);
    }

    private string DryRunText => _settings.DryRun
        ? "当前为 Dry-run 预览模式（不实际修改 Komga），取消勾选后执行真实更新"
        : "当前为真实更新模式：将直接修改 Komga 元数据";

    // ================= 主题（深色/浅色） =================

    /// <summary>切换全局主题变体并持久化。深浅切换即时生效（所有 DynamicResource 跟随）。</summary>
    private void ApplyTheme(bool dark, bool persist)
    {
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        BtnTheme.Content = dark ? "☀" : "🌙";
        ToolTip.SetTip(BtnTheme, dark ? "切换到浅色模式" : "切换到深色模式");
        _settings.Theme = dark ? "dark" : "light";
        if (persist) SaveSettings();
    }

    /// <summary>空态提示文案：无库数据给引导，有数据无匹配给筛选提示。</summary>
    private void UpdateEmptyHint()
    {
        EmptyHint.IsVisible = _rows.Count == 0;
        EmptyHintDetail.Text = _seriesCache.Count == 0
            ? "测试连接后选择库，点「加载系列」"
            : "当前筛选/过滤条件没有匹配的系列";
    }

    // ================= 事件装配 =================

    private void WireEvents()
    {
        BtnTest.Click += async (_, _) => await TestConnectionAsync();
        BtnSettings.Click += async (_, _) => await OpenSettingsAsync();
        BtnLoad.Click += async (_, _) => await LoadSeriesAsync();
        BtnScrapeMeta.Click += async (_, _) => await ScrapeCheckedAsync(SyncType.Meta);
        BtnScrapeAll.Click += async (_, _) => await ScrapeCheckedAsync(SyncType.All);
        BtnManualMatch.Click += async (_, _) => await CorrectMatchAsync();
        BtnCoverScrape.Click += async (_, _) => await CoverScrapeAsync();
        BtnFolderTag.Click += async (_, _) => await FolderTagAsync();
        BtnMylarExport.Click += async (_, _) => await MylarExportAsync();
        BtnOpenData.Click += (_, _) => OpenDataDir();
        BtnExportLog.Click += (_, _) => ExportLog();
        BtnCancel.Click += (_, _) => _cts?.Cancel();
        BtnRematch.Click += async (_, _) => await RematchAsync();
        BtnToggleAll.Click += (_, _) => ToggleAll();
        CmbFilter.SelectionChanged += (_, _) => ApplyFilter();
        TxtFilter.PropertyChanged += (_, e) => { if (e.Property == TextBox.TextProperty) ApplyFilter(); };
        BtnSearchSeries.Click += async (_, _) => await SearchAllLibrariesAsync();
        TxtSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { _ = SearchAllLibrariesAsync(); e.Handled = true; }
        };
        BtnEditSeries.Click += async (_, _) => await EditSeriesEntryAsync();
        BtnTheme.Click += (_, _) =>
        {
            var dark = Application.Current!.RequestedThemeVariant == ThemeVariant.Dark;
            ApplyTheme(!dark, persist: true);
        };

        ChkDryRun.PropertyChanged += (_, e) =>
        {
            if (e.Property == CheckBox.IsCheckedProperty)
            {
                _settings.DryRun = ChkDryRun.IsChecked == true;
                SaveSettings();
            }
        };
        TxtUrl.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) ScheduleUrlSave();
        };
        TxtApiKey.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) ScheduleApiKeySave();
        };

        // 右键菜单（代码构建：资源区 x:Name 不生成字段）
        var miEdit = new MenuItem { Header = "编辑单部" };
        var miCorrect = new MenuItem { Header = "纠正匹配" };
        var miRematch = new MenuItem { Header = "覆盖匹配" };
        var miOpenBtv = new MenuItem { Header = "打开 Bangumi 条目页" };
        var miCopyId = new MenuItem { Header = "复制 Bangumi subject ID" };
        miEdit.Click += async (_, _) => await EditSeriesEntryAsync();
        miCorrect.Click += async (_, _) => await CorrectMatchAsync();
        miRematch.Click += async (_, _) => await RematchAsync();
        miOpenBtv.Click += (_, _) => OpenBtvPage();
        miCopyId.Click += (_, _) => CopyBtvId();
        var seriesMenu = new ContextMenu
        {
            Items = { miEdit, new Separator(), miCorrect, miRematch, new Separator(), miOpenBtv, miCopyId },
        };
        seriesMenu.Opening += (_, _) =>
        {
            _contextSeries ??= (SeriesGrid.SelectedItem as SeriesRow)?.Series;
            var s = _contextSeries;
            miOpenBtv.IsEnabled = s != null && HasBtvLink(s);
            miCopyId.IsEnabled = s != null && GetBtvSubjectId(s).HasValue;
        };
        SeriesGrid.ContextMenu = seriesMenu;
        // 指针处理：右键命中行 + 左键拖拽框选（橡皮筋，跨平台一致）
        SeriesGrid.AddHandler(PointerPressedEvent, OnSeriesPointerPressed, RoutingStrategies.Tunnel);
        SeriesGrid.AddHandler(PointerMovedEvent, OnSeriesPointerMoved, RoutingStrategies.Tunnel);
        SeriesGrid.AddHandler(PointerReleasedEvent, OnSeriesPointerReleased, RoutingStrategies.Tunnel);
    }

    // ================= 拖拽框选（按住左键拖出方框，碰到的行全部勾选；6px 阈值内仍是普通点击） =================

    private bool _bandMaybe;      // 左键已按下、还没超阈值
    private bool _bandActive;     // 框选进行中
    private Point _bandStart;

    private void OnSeriesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pt = e.GetCurrentPoint(SeriesGrid);
        if (pt.Properties.IsRightButtonPressed)
        {
            // 右键命中行：沿可视树找 DataGridRow，命中即选中并记忆（供右键菜单用）
            var el = e.Source as Visual;
            while (el is not null && el is not DataGridRow) el = el.Parent as Visual;
            if (el is DataGridRow row && row.DataContext is SeriesRow sr)
            {
                _contextSeries = sr.Series;
                SeriesGrid.SelectedItem = sr;
            }
            return;
        }
        if (!pt.Properties.IsLeftButtonPressed) return;
        // 只在行上启用框选拦截：列头（含列宽拖块）与滚动条绝不抢捕获——
        // 否则 DataGrid 原生拖拽失去鼠标捕获，列宽调节会退化成"点一下-滑动-再点一下"
        var src = e.Source as Visual;
        while (src is not null && src is not DataGridRow
               && src is not Avalonia.Controls.Primitives.ScrollBar)
            src = src.Parent as Visual;
        if (src is not DataGridRow) return;
        _bandMaybe = true;
        _bandStart = pt.Position;
        e.Pointer.Capture(SeriesGrid);   // 统一捕获：拖出列表边界也能继续收 moved/released
    }

    private void OnSeriesPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_bandMaybe) return;
        var pt = e.GetCurrentPoint(SeriesGrid);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var pos = pt.Position;
        if (!_bandActive &&
            Math.Abs(pos.X - _bandStart.X) + Math.Abs(pos.Y - _bandStart.Y) < 6) return;   // 普通点击不动它

        if (!_bandActive) _bandActive = true;
        DrawBandRect(_bandStart, pos);
        SelectSeriesByBand(_bandStart, pos);
    }

    private void OnSeriesPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_bandMaybe) return;
        _bandMaybe = false;
        if (!_bandActive) return;   // 没拖出框 = 普通点击，交回原行为
        _bandActive = false;
        BandRect.IsVisible = false;
    }

    /// <summary>把方框画到覆盖层 Canvas 上（列表坐标系）。</summary>
    private void DrawBandRect(Point a, Point b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var w = Math.Abs(a.X - b.X);
        var h = Math.Abs(a.Y - b.Y);
        Canvas.SetLeft(BandRect, x);
        Canvas.SetTop(BandRect, y);
        BandRect.Width = w;
        BandRect.Height = h;
        BandRect.IsVisible = true;
    }

    /// <summary>方框碰到的行：勾选 + 选中（可见行遍历；虚拟化下未滚出的行不在树里，滚动后再框即可）。</summary>
    private void SelectSeriesByBand(Point a, Point b)
    {
        var rect = new Rect(
            Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
            Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        foreach (var row in SeriesGrid.GetVisualDescendants().OfType<DataGridRow>())
        {
            if (row.DataContext is not SeriesRow sr) continue;
            var topLeft = row.TranslatePoint(new Point(0, 0), SeriesGrid);
            if (topLeft is not { } tl) continue;
            var rowRect = new Rect(tl, row.Bounds.Size);
            if (!rowRect.Intersects(rect)) continue;
            if (!sr.Checked) sr.Checked = true;
            if (!SeriesGrid.SelectedItems.Contains(sr)) SeriesGrid.SelectedItems.Add(sr);
        }
    }

    /// <summary>双击行打开编辑。只在行上响应——双击列头/空白不再误触发"编辑单部"提示。</summary>
    private void OnSeriesDoubleTapped(object? sender, TappedEventArgs e)
    {
        var el = e.Source as Visual;
        while (el is not null && el is not DataGridRow) el = el.Parent as Visual;
        if (el is DataGridRow row && row.DataContext is SeriesRow sr)
        {
            _contextSeries = sr.Series;
            SeriesGrid.SelectedItem = sr;
            _ = EditSeriesEntryAsync();
        }
    }

    // ================= 托盘后台 =================

    private void SetupTray()
    {
        try
        {
            var miShow = new NativeMenuItem("显示主窗口");
            miShow.Click += (_, _) => ShowFromTray();
            var miExit = new NativeMenuItem("退出程序");
            miExit.Click += (_, _) => ExitFromTray();
            var menu = new NativeMenu();
            menu.Items.Add(miShow);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(miExit);
            _tray = new TrayIcon
            {
                Icon = Icon,
                ToolTipText = "KomgaScraper",
                IsVisible = true,
                Menu = menu,
            };
            // TrayIcon 通过 Window 的附加属性注册（跨平台托盘）
            SetValue(TrayIcon.IconsProperty, new TrayIcons { _tray });
        }
        catch { /* 托盘初始化失败就不挂托盘，主窗口照常工作 */ }

        // 点 X / 最小化 = 挂后台，任务照跑；真正退出走托盘菜单"退出程序"
        Closing += (_, e) =>
        {
            if (!_trayExitPending)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            CancelCoverSweep();
            _state?.SaveNow();   // 封面/简介状态落盘（平时是防抖延迟写，退出时强制一次）
        };
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _trayExitPending = true;
        Close();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    // ================= 保存设置（失败写日志，不再静默吞掉） =================

    private void SaveSettings()
    {
        if (_settings.Save()) return;
        var msg = AppSettings.LastError ?? "保存设置失败";
        if (_saveErrorShown) return;
        _saveErrorShown = true;
        Log("ERR ", msg);
        Log("ERR ", "设置无法写入程序目录，请把程序解压到有写权限的文件夹（不要直接在压缩包里运行）");
        _ = Ui.ErrorAsync(this, $"{msg}\n\n请把 KomgaScraper 解压到有写权限的文件夹后再运行（不要在压缩包内直接双击运行）。", "设置无法保存");
    }

    // ---- 地址/密钥输入防抖保存 + 延迟重建（值没变不重建） ----

    private DispatcherTimer? _urlDebounce;
    private DispatcherTimer? _apiKeyDebounce;
    private bool _pendingRebuild;

    private void ScheduleUrlSave()
    {
        _urlDebounce ??= NewDebounce(() =>
        {
            var v = TxtUrl.Text?.Trim() ?? "";
            if (v == _settings.KomgaUrl) return;
            _settings.KomgaUrl = v;
            SaveSettings();
            ScheduleRebuild();
        });
        RestartDebounce(_urlDebounce);
    }

    private void ScheduleApiKeySave()
    {
        _apiKeyDebounce ??= NewDebounce(() =>
        {
            var v = TxtApiKey.Text?.Trim() ?? "";
            if (v == _settings.KomgaApiKey) return;
            _settings.KomgaApiKey = v;
            SaveSettings();
            ScheduleRebuild();
        });
        RestartDebounce(_apiKeyDebounce);
    }

    private DispatcherTimer NewDebounce(Action action)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            action();
        };
        return t;
    }

    private void RestartDebounce(DispatcherTimer t)
    {
        t.Stop();
        t.Start();
    }

    /// <summary>
    /// 客户端重建（安全版）：任务运行中绝不重建——RebuildClients 会 Dispose 正在飞行的 HttpClient，
    /// 在飞请求当场集体 "A task was canceled"、批次流产。运行中只挂起标志，任务收尾时补做。
    /// </summary>
    private void ScheduleRebuild()
    {
        if (!_busy) RebuildClients();
        else
        {
            _pendingRebuild = true;
            Log("WARN", "客户端设置有变更；任务执行中不重建连接，将在任务结束后生效");
        }
    }

    private void TryDeferredRebuild()
    {
        if (!_pendingRebuild || _busy) return;
        _pendingRebuild = false;
        RebuildClients();
    }

    // ================= 日志 =================

    private void Log(string level, string message)
    {
        if (Dispatcher.UIThread.CheckAccess()) AppendLog(level, message);
        else Dispatcher.UIThread.Post(() => AppendLog(level, message), DispatcherPriority.Background);
    }

    private void AppendLog(string level, string message)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        _logLines.Add(new LogLine(time, level, message));
        while (_logLines.Count > 4000) _logLines.RemoveAt(0);   // 防极端长任务内存膨胀

        // 同时落到 data\logs\log-日期.log：程序崩了/关了也能拿到全文
        if (_logFileFailed) return;
        try
        {
            var dir = Path.Combine(AppSettings.DataDir, "logs");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, $"log-{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}][{level}] {message}{Environment.NewLine}");
        }
        catch
        {
            _logFileFailed = true;
        }
    }

    private void ScrollLogToBottom()
    {
        if (_logLines.Count == 0) return;
        try { LogList.ScrollIntoView(_logLines[^1]); }
        catch
        {
            var sv = LogList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            sv?.ScrollToEnd();
        }
    }

    /// <summary>导出日志：直接把最近 3 天文件日志写进下载目录并定位，全程零弹窗。</summary>
    private void ExportLog()
    {
        try
        {
            var logsDir = Path.Combine(AppSettings.DataDir, "logs");
            var sb = new StringBuilder();
            var files = new[] { 0, 1, 2 }
                .Select(o => DateTime.Today.AddDays(-o).ToString("yyyyMMdd"))
                .Select(d => Path.Combine(logsDir, $"log-{d}.log"))
                .Where(File.Exists)
                .ToList();
            foreach (var f in files)
            {
                sb.AppendLine($"===== {Path.GetFileName(f)} =====");
                try { sb.AppendLine(File.ReadAllText(f)); }
                catch (Exception e) { sb.AppendLine($"（该文件读取失败：{e.Message}）"); }
            }
            if (files.Count == 0)
            {
                sb.AppendLine("===== 界面日志（文件日志不存在时的兜底） =====");
                foreach (var l in _logLines) sb.AppendLine($"[{l.Time}][{l.Level}] {l.Message}");
            }
            if (sb.Length == 0) sb.AppendLine("(日志为空)");

            var targetDir = GetWritableExportDir();
            var path = Path.Combine(targetDir, $"KomgaScraper-日志-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Log("INFO", $"日志已导出：{path}");
            try { OpenFolderAndSelectFile(path); }
            catch (Exception e) { Log("INFO", $"资源管理器定位失败（{e.Message}），文件路径见上一行日志"); }
            Ui.Toast(this, $"日志已导出：{Path.GetFileName(path)}");
        }
        catch (Exception e)
        {
            Log("ERR ", $"导出日志失败: {e.Message}");
            Ui.Toast(this, $"导出日志失败：{e.Message}", error: true);
        }
    }

    /// <summary>资源管理器定位文件：走 Shell COM 的 SHOpenFolderAndSelectItems（仅 Windows；其余平台静默跳过）。</summary>
    private static void OpenFolderAndSelectFile(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hr = SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, IntPtr.Zero);
        if (hr != 0 || pidl == IntPtr.Zero)
            throw new InvalidOperationException($"SHParseDisplayName 失败: 0x{hr:X8}");
        try
        {
            hr = SHOpenFolderAndSelectItems(pidl, 0, IntPtr.Zero, 0);
            if (hr != 0) throw new InvalidOperationException($"SHOpenFolderAndSelectItems 失败: 0x{hr:X8}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName, IntPtr pbc,
        out IntPtr ppidl, uint sfgaoIn, IntPtr psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlFolder, uint cidl, IntPtr apidl, uint dwFlags);

    /// <summary>导出目标目录：下载 → 桌面 → 数据目录，用探测文件验证可写。</summary>
    private static string GetWritableExportDir()
    {
        foreach (var dir in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                     Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                     AppSettings.DataDir,
                 })
        {
            try
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, $".komga-scraper-probe-{Guid.NewGuid():N}.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return dir;
            }
            catch { /* 没权限就试下一个 */ }
        }
        return Path.Combine(AppSettings.DataDir, "logs");
    }

    /// <summary>打开便携数据目录（设置文件 / BookWalker 索引都在这里）。</summary>
    private void OpenDataDir()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DataDir);
            try
            {
                OpenFolderAndSelectFile(AppSettings.DataDir);
            }
            catch
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppSettings.DataDir,
                    UseShellExecute = true,
                });
            }
            Log("INFO", $"已打开数据目录：{AppSettings.DataDir}");
        }
        catch (Exception e)
        {
            try { TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(AppSettings.DataDir); } catch { }
            Log("ERR ", $"打开数据目录失败：{e.Message}（路径已复制到剪贴板：{AppSettings.DataDir}）");
        }
    }

    // ================= 进度 / 忙碌 =================

    private void SetProgress(int current, int total, string message)
    {
        if (Dispatcher.UIThread.CheckAccess()) SetProgressCore(current, total, message);
        else Dispatcher.UIThread.Post(() => SetProgressCore(current, total, message), DispatcherPriority.Background);
    }

    private void SetProgressCore(int current, int total, string message)
    {
        Progress.IsVisible = true;
        if (total > 0)
        {
            Progress.IsIndeterminate = false;
            Progress.Maximum = Math.Max(1, total);
            Progress.Value = Math.Clamp(current, 0, Progress.Maximum);
        }
        else
        {
            Progress.IsIndeterminate = true;   // 总数未知（搜索中）：转圈
        }
        StatusText.Text = total > 0 ? $"[{current}/{total}] {message}" : message;
    }

    private bool GuardBusy()
    {
        if (_busy) { _ = Ui.MessageAsync(this, "有任务正在执行，请等待或取消"); return false; }
        if (_komga == null || _scrape == null || _batch == null || _mylar == null || _tagger == null)
        { _ = Ui.MessageAsync(this, "客户端未初始化", "错误", okOnly: true, error: true); return false; }
        return true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (var b in new[] { BtnTest, BtnLoad, BtnSettings, BtnScrapeMeta, BtnScrapeAll, BtnManualMatch,
                     BtnCoverScrape, BtnRematch, BtnFolderTag, BtnMylarExport, BtnExportLog })
            b.IsEnabled = !busy;
        BtnCancel.IsEnabled = busy;
        BtnCancel.Opacity = busy ? 1 : 0.55;
        if (busy)
        {
            // 样式动画（IterationCount=Infinite + FillMode=Forward）在 class 移除后值会粘住，
            // 所以关闭时显式归零 Opacity 并直接隐藏，开态重新交给动画接管
            BusyOverlay.Classes.Add("open");
            BusyOverlay.IsVisible = true;
        }
        else
        {
            BusyOverlay.Classes.Remove("open");
            BusyOverlay.Opacity = 0;
            BusyOverlay.IsVisible = false;
        }
        if (!busy)
        {
            Progress.IsVisible = false;
            StatusText.Text = "空闲";
        }
    }

    private async Task<T> RunBusyAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            return await Task.Run(() => work(_cts.Token));
        }
        catch (OperationCanceledException oce)
        {
            Log("WARN", string.IsNullOrWhiteSpace(oce.Message) || oce.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                ? "任务已取消"
                : $"任务已取消/超时: {oce.Message}");
            throw;
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
            TryDeferredRebuild();
        }
    }

    private async Task RunBusyAsync(Func<CancellationToken, Task> work)
    {
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            await Task.Run(() => work(_cts.Token));
        }
        catch (OperationCanceledException oce)
        {
            Log("WARN", string.IsNullOrWhiteSpace(oce.Message) || oce.Message.Contains("canceled", StringComparison.OrdinalIgnoreCase)
                ? "任务已取消"
                : $"任务已取消/超时: {oce.Message}");
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
            TryDeferredRebuild();
        }
    }

    // ================= 列表操作 =================

    private List<SeriesDto> GetCheckedSeries()
        => _rows.Where(r => r.Checked).Select(r => r.Series).ToList();

    private void ToggleAll()
    {
        if (_rows.Count == 0) return;
        var target = !_rows.All(r => r.Checked);
        foreach (var r in _rows) r.Checked = target;
        BtnToggleAll.Content = target ? "全不选" : "全选";
    }

    /// <summary>筛选（下拉模式 + 关键字）。重建可见行集合，勾选状态按 id 保留。</summary>
    private void ApplyFilter()
    {
        var kw = (TxtFilter.Text ?? "").Trim();
        var mode = CmbFilter.SelectedIndex; // 0=全部 1=未刮削 2=已刮削 3=未刮封面 4=未刮简介 5=待补全 6=已完整
        // 重建前记住勾选与排序；重建后恢复
        var checkedIds = new HashSet<string>(_rows.Where(r => r.Checked).Select(r => r.Series.Id));
        _lastSortColumn = -1;   // 筛选重置排序（保持实现简单）
        _rows.Clear();
        foreach (var s in _seriesCache.Values)
        {
            if (!FilterRow(s, kw, mode)) continue;
            var row = new SeriesRow(s, () => CoverCell(s.Id)) { Checked = checkedIds.Contains(s.Id) };
            _rows.Add(row);
        }
    }

    private static bool FilterRow(SeriesDto s, string kw, int mode)
    {
        var title = SeriesRow.TitleOf(s);
        if (kw.Length > 0 && !title.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
            !s.Name.Contains(kw, StringComparison.OrdinalIgnoreCase))
            return false;
        var hasLink = HasBtvLink(s);
        if (mode == 1 && hasLink) return false;
        if (mode == 2 && !hasLink) return false;
        var coverOk = _stateGetCover(s.Id);
        var summaryOk = !string.IsNullOrEmpty(s.Metadata?.Summary);
        if (mode == 3 && coverOk == true) return false;
        if (mode == 4 && summaryOk) return false;
        if (mode == 5 && coverOk == true && summaryOk) return false;
        if (mode == 6 && (coverOk != true || !summaryOk)) return false;
        return true;
    }

    // state 访问（FilterRow 是静态的，走闭包太啰嗦——用静态转发）
    private static Func<string, bool?>? _stateCoverAccessor;
    private static bool? _stateGetCover(string id) => _stateCoverAccessor?.Invoke(id);

    /// <summary>列头点击排序（册数按数值，其余按字符串；同列再点反向）——就地重排源集合。</summary>
    private void OnSeriesSorting(object? sender, DataGridColumnEventArgs e)
    {
        var colIndex = Array.IndexOf(ColNames, e.Column.Header?.ToString());
        if (colIndex < 0) return;
        _lastSortAsc = _lastSortColumn == colIndex ? !_lastSortAsc : true;
        _lastSortColumn = colIndex;
        var ascending = _lastSortAsc;

        var sorted = colIndex == 3
            ? _rows.OrderBy(r => int.TryParse(r.Count, out var n) ? n : int.MaxValue).ToList()
            : _rows.OrderBy(r => r.GetCell(colIndex), StringComparer.CurrentCulture).ToList();
        if (!ascending) sorted.Reverse();

        // 注意：不能用 ObservableCollection.Move 就地重排——Avalonia DataGrid 的
        // DataConnection 只处理 Add/Remove/Replace/Reset，Move 通知被静默忽略，
        // 行序永远不会刷新。改为整体重建（触发 Reset，DataGrid 原生支持）；
        // 复用同一批 SeriesRow 对象，勾选状态自然保留。
        var selected = SeriesGrid.SelectedItem as SeriesRow;
        _rows.Clear();
        foreach (var r in sorted) _rows.Add(r);
        if (selected != null) SeriesGrid.SelectedItem = selected;
        e.Handled = true;
    }

    private int _lastSortColumn = -1;
    private bool _lastSortAsc = true;

    // ================= 封面状态后台扫描 =================

    /// <summary>封面列文本：✓ 已刮 / ✗ 未刮 / … 未知。</summary>
    private string CoverCell(string seriesId) => _state?.GetCover(seriesId) switch
    {
        true => "✓",
        false => "✗",
        _ => "…",
    };

    /// <summary>服务器上该系列是否有用户上传的封面；查失败返回 null（保持未知）。</summary>
    private async Task<bool?> SeriesHasUserCoverAsync(string seriesId, CancellationToken ct)
    {
        try
        {
            var thumbs = await _komga!.GetSeriesThumbnailsAsync(seriesId, ct);
            return thumbs.Any(t => t.Type == "USER_UPLOADED");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>后台封面状态检查：只查状态未知的系列（结果持久化，下次启动不重查）；并发 3。</summary>
    private void StartCoverSweep(IEnumerable<SeriesDto> series)
    {
        CancelCoverSweep();
        if (_state == null) return;
        var unknown = _state.UnknownCoverIds(series.Select(s => s.Id));
        if (unknown.Count == 0) return;
        _coverSweepCts = new CancellationTokenSource();
        var cts = _coverSweepCts;
        Log("INFO", $"后台检查封面状态：{unknown.Count} 个系列待查（并发 3）…");
        var hasCover = 0;
        var noCover = 0;
        _ = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(unknown,
                    new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cts.Token },
                    async (id, token) =>
                    {
                        var has = await SeriesHasUserCoverAsync(id, token);
                        if (has == null) return;
                        if (has == true) Interlocked.Increment(ref hasCover);
                        else Interlocked.Increment(ref noCover);
                        _state!.SetCover(id, has == true);
                        var cell = CoverCell(id);
                        Dispatcher.UIThread.Post(() => UpdateCoverCell(id, cell), DispatcherPriority.Background);
                    }).ConfigureAwait(false);
                Log("INFO", $"封面状态检查完成：已刮 {hasCover}，未刮 {noCover}");
            }
            catch (OperationCanceledException) { Log("INFO", "封面状态检查已取消"); }
            catch (Exception e) { Log("WARN", $"封面状态检查失败: {e.Message}"); }
        }, cts.Token);
    }

    private void UpdateCoverCell(string seriesId, string cell)
    {
        foreach (var r in _rows)
        {
            if (r.Series.Id == seriesId) { r.Cover = cell; break; }
        }
    }

    private void CancelCoverSweep()
    {
        var old = _coverSweepCts;
        _coverSweepCts = null;
        if (old == null) return;
        try { old.Cancel(); } catch { }
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(3000); old.Dispose(); } catch { }
        });
    }

    // ================= 改动留痕（并入日志框） =================

    private void OnScrapeChange(ScrapeChange c)
    {
        Dispatcher.UIThread.Post(() => AppendPreviewRow(c), DispatcherPriority.Background);
    }

    private void AppendPreviewRow(ScrapeChange c)
    {
        _previewCount++;
        StatusChanges.Text = $"改动 {_previewCount}";
        var detail = string.IsNullOrEmpty(c.Detail) ? "" : $"；{c.Detail}";
        Log("改动", $"{ScrapeChangeKindText.Of(c.Kind)} {c.Target}：{c.Description}{detail}");
    }

    private void ClearPreview()
    {
        _previewCount = 0;
        StatusChanges.Text = "改动 0";
        Log("改动", "──── 改动计数已清零（新任务从头统计）────");
    }

    // ================= 链接工具（供 ViewModels 复用） =================

    internal static string ExtractSubjectIdText(SeriesDto s) =>
        s.Metadata?.Links?.FirstOrDefault(l => l.Url.Contains("/subject/", StringComparison.Ordinal))?.Url ?? "";

    internal static bool HasBtvLink(SeriesDto s) => ExtractSubjectIdText(s).Length > 0;

    internal static int? GetBtvSubjectId(SeriesDto s)
    {
        var url = ExtractSubjectIdText(s);
        var m = SubjectIdRegex().Match(url);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    [GeneratedRegex(@"subject/(\d+)")]
    private static partial System.Text.RegularExpressions.Regex SubjectIdRegex();

    private void LoadSettingsToUi()
    {
        TxtUrl.Text = _settings.KomgaUrl;
        TxtApiKey.Text = _settings.KomgaApiKey;
        ChkDryRun.IsChecked = _settings.DryRun;
        _stateCoverAccessor = id => _state?.GetCover(id);
    }

    private void RefreshCheckedRows()
    {
        foreach (var r in _rows)
        {
            if (_seriesCache.TryGetValue(r.Series.Id, out var s))
            {
                var btvId = GetBtvSubjectId(s);
                r.Link = btvId.HasValue ? "Btv" : "";
                r.Cover = CoverCell(s.Id);
                r.BgmId = btvId?.ToString() ?? "";
            }
        }
    }

    // ---- 右键工具 ----

    private void OpenBtvPage()
    {
        var s = _contextSeries;
        var url = s != null ? ExtractSubjectIdText(s) : "";
        if (s == null || url.Length == 0) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            Log("ERR ", $"打开 Bangumi 页面失败: {e.Message}");
        }
    }

    private async void CopyBtvId()
    {
        var s = _contextSeries;
        var id = s != null ? GetBtvSubjectId(s) : null;
        if (id == null) return;
        try
        {
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(id.Value.ToString());
            Log("INFO", $"已复制 subject ID：{id}");
        }
        catch (Exception e)
        {
            Log("ERR ", $"复制失败: {e.Message}");
        }
        await Task.CompletedTask;
    }

    /// <summary>外部封面下载（BW/Mox 候选封面，浏览器 UA + 代理，懒创建）。</summary>
    internal async Task<byte[]?> DownloadExternalCoverAsync(string url, string? _)
    {
        try
        {
            if (_externalImageHttp == null)
            {
                lock (_externalImageHttpGate)
                {
                    _externalImageHttp ??= HttpFactory.Create(_settings.Proxy, TimeSpan.FromSeconds(30),
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                }
            }
            return await _externalImageHttp!.GetByteArrayAsync(url);
        }
        catch
        {
            return null;
        }
    }
}
