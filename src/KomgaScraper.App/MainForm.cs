using System.Runtime.InteropServices;
using System.Text;
using KomgaScraper.Core;
using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Mylar;
using KomgaScraper.Core.Scraping;
using KomgaScraper.Core.Sources;
using KomgaScraper.Core.Text;
using KomgaScraper.App.Dialogs;

namespace KomgaScraper.App;

/// <summary>
/// 主界面：左侧固定宽度操作栏（分组竖排，永不挤压）+ 右侧弹性内容区（列表/日志）。
/// </summary>
public sealed class MainForm : Form
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
    private readonly float _dpi = 1f;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _coverSweepCts;   // 后台封面状态检查（换库/退出时取消）
    private bool _logFileFailed;
    private NotifyIcon _tray = null!;        // 托盘：点 X / 最小化 = 挂后台，任务照常跑
    private bool _trayBalloonShown;
    private bool _trayExitPending;           // 托盘菜单"退出程序"才允许真正关闭

    // 外部封面下载（BW/Mox 候选封面，浏览器 UA + 代理，懒创建）
    private HttpClient? _externalImageHttp;
    private readonly object _externalImageHttpGate = new();

    // ---- 控件 ----
    private TextBox _txtUrl = null!;
    private TextBox _txtApiKey = null!;
    private ComboBox _cmbLibrary = null!;
    private Button _btnTest = null!;
    private Button _btnSettings = null!;
    private Button _btnLoad = null!;
    private CheckBox _chkDryRun = null!;
    private Button _btnToggleAll = null!;
    private ComboBox _cmbFilter = null!;
    private CheckBox _chkAuto = null!;
    private Button _btnScrapeMeta = null!;
    private Button _btnScrapeAll = null!;
    private Button _btnManualMatch = null!;
    private Button _btnCoverScrape = null!;
    private Button _btnRematch = null!;
    private Button _btnFolderTag = null!;
    private Button _btnMylarExport = null!;
    private Button _btnOpenData = null!;
    private Button _btnExportLog = null!;
    private Button _btnCancel = null!;
    private TextBox _txtFilter = null!;
    private TextBox _txtSearch = null!;      // 全库搜索栏（跨库检索某部漫画 → 编辑）
    private Button _btnSearchSeries = null!;
    private Button _btnEditSeries = null!;
    private ListView _lvSeries = null!;
    private TextBox _txtLog = null!;
    private ToolStripProgressBar _progress = null!;
    private ToolStripStatusLabel _status = null!;

    // ---- 改动留痕：并入日志框（Dry-run "将…" / 真实 "已…"），状态栏统计条数 ----
    private ToolStripStatusLabel _statusChanges = null!;
    private int _previewCount;

    public MainForm()
    {
        Text = "KomgaScraper — Komga 漫画元数据刮削（Bangumi）";
        Font = new Font("Microsoft YaHei UI", 9F);
        _dpi = DeviceDpi / 96f;
        var work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        MinimumSize = new Size(780, 560);
        Size = new Size(1240, 820);
        StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        LoadSettingsToUi();
        RebuildClients();

        // 高 DPI（如 150%）：字体由 GDI 自动放大，布局手动等比放大，保持设计比例不拥挤
        if (_dpi > 1.01f) Scale(new SizeF(_dpi, _dpi));

        // 缩放后再夹进工作区：窗口（含右侧列表/日志）永远完整落在屏幕内，不会被切掉
        int maxW = Math.Max(900, work.Width - 30);
        int maxH = Math.Max(620, work.Height - 30);
        MinimumSize = new Size(Math.Min((int)Math.Round(780 * _dpi), maxW),
                               Math.Min((int)Math.Round(560 * _dpi), maxH));
        Size = new Size(Math.Min((int)Math.Round(1240 * _dpi), maxW),
                        Math.Min((int)Math.Round(820 * _dpi), maxH));

        // 首次运行：立刻生成一份默认 settings.json，用户能看到设置文件的确切位置
        if (!File.Exists(AppSettings.SettingsFile)) SaveSettings();
        Log("INFO", $"就绪。设置文件: {AppSettings.SettingsFile}");
        if (AppSettings.LastError != null) Log("ERR ", AppSettings.LastError);
        Log(_settings.DryRun ? "WARN" : "INFO", DryRunText);
    }

    /// <summary>保存设置；失败时把原因写进日志（不再静默吞掉，否则会出现"设置没保存"却无提示）。</summary>
    private void SaveSettings()
    {
        if (_settings.Save()) return;
        var msg = AppSettings.LastError ?? "保存设置失败";
        if (_saveErrorShown) return;
        _saveErrorShown = true;
        Log("ERR ", msg);
        Log("ERR ", "设置无法写入程序目录，请把程序解压到有写权限的文件夹（不要直接在压缩包里运行）");
        MessageBox.Show($"{msg}\n\n请把 KomgaScraper 解压到有写权限的文件夹后再运行" +
                        "（不要在压缩包内直接双击运行）。", "设置无法保存",
            MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    private string DryRunText => _settings.DryRun
        ? "当前为 Dry-run 预览模式（不实际修改 Komga），取消勾选后执行真实更新"
        : "当前为真实更新模式：将直接修改 Komga 元数据";

    // ================= UI 构建 =================
    // 布局原则（响应式）：所有操作控件放进左侧固定宽度操作栏，竖排分组 + 两列按钮网格，
    // 窗口拉多窄都不会把按钮挤没；右侧列表/日志占满剩余空间，列宽按窗口比例缩放。

    /// <summary>列表列名与宽度权重（权重和 = 1）。封面/简介都是 series 级状态（不看每卷）。</summary>
    private static readonly (string Name, double Weight)[] ColSpecs =
    [
        ("标题", 0.31), ("文件夹名", 0.19), ("册数", 0.06), ("状态", 0.07),
        ("链接", 0.06), ("封面", 0.05), ("简介", 0.05), ("BGM ID", 0.21),
    ];

    /// <summary>单列堆叠容器（行高自适应，宽度撑满父容器）。</summary>
    private static TableLayoutPanel Stack()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            Margin = new Padding(0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private static void AddRow(TableLayoutPanel t, Control c)
    {
        t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        t.Controls.Add(c, 0, t.RowCount - 1);
    }

    /// <summary>两个控件并排（各占一半，对称布局）。</summary>
    private static TableLayoutPanel Pair(Control a, Control b)
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        t.RowCount = 1;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        a.Margin = new Padding(0, 2, 3, 2);
        b.Margin = new Padding(3, 2, 0, 2);
        t.Controls.Add(a, 0, 0);
        t.Controls.Add(b, 1, 0);
        return t;
    }

    /// <summary>分组框：标题 + 单列内容（内容随窗口宽度自动撑满）。</summary>
    private static GroupBox Group(string title, params Control[] rows)
    {
        var inner = Stack();
        inner.Margin = new Padding(6, 0, 2, 2);
        foreach (var r in rows) AddRow(inner, r);
        var gb = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(6, 1, 8, 3),
            Margin = new Padding(2, 2, 2, 3),
        };
        gb.Controls.Add(inner);
        return gb;
    }

    /// <summary>输入框上方的字段标签。</summary>
    private static Label Field(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(2, 6, 0, 0),
    };

    /// <summary>操作栏按钮：横向撑满所在格子，固定高度 30。</summary>
    private static Button SideBtn(string text) => new()
    {
        Text = text,
        Height = 30,
        Anchor = AnchorStyles.Left | AnchorStyles.Right,
        Margin = new Padding(1, 2, 1, 2),
        UseVisualStyleBackColor = true,
    };

    /// <summary>让输入类控件横向撑满所在格子。</summary>
    private static T FullWidth<T>(T c) where T : Control
    {
        c.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        c.Margin = new Padding(1, 3, 1, 3);
        return c;
    }

    /// <summary>按 ColSpecs 权重缩放列表列宽（随窗口拉伸），最后一列吃满剩余宽度。
    /// 总宽严格小于可视宽度 → 永远不出现横向滚动条（横向滚动会把"标题"列推出视野）。</summary>
    private void ResizeColumns()
    {
        if (_lvSeries.Columns.Count != ColSpecs.Length) return;
        int avail = _lvSeries.ClientSize.Width - 2;
        if (avail < 240) return;
        int used = 0;
        for (int i = 0; i < ColSpecs.Length; i++)
        {
            int cw = i == ColSpecs.Length - 1
                ? Math.Max(70, avail - used)
                : Math.Max(70, (int)Math.Round(avail * ColSpecs[i].Weight));
            _lvSeries.Columns[i].Width = cw;
            used += cw;
        }
        ResetHorizontalScroll();
    }

    private const int WM_HSCROLL = 0x0114;
    private const int SB_LEFT = 6;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>列表曾经出现过横向滚动条时，原生控件可能残留横向偏移（表现为最左列被推出视野），强制回到最左。</summary>
    private void ResetHorizontalScroll()
    {
        if (!_lvSeries.IsHandleCreated) return;
        SendMessage(_lvSeries.Handle, WM_HSCROLL, (IntPtr)SB_LEFT, IntPtr.Zero);
    }

        private void BuildUi()
    {
        // ---- 左侧操作栏（固定宽度，分组竖排，内容超高时自动滚动，永不挤压） ----
        var sidebarHost = new Panel { Dock = DockStyle.Left, Width = 320, AutoScroll = true, Padding = new Padding(6, 6, 2, 6) };
        var side = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        side.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _txtUrl = FullWidth(new TextBox { Text = "http://localhost:25600" });
        _txtApiKey = FullWidth(new TextBox());
        _btnTest = SideBtn("测试连接");
        _btnSettings = SideBtn("⚙ 设置");

        _cmbLibrary = FullWidth(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
        _btnLoad = SideBtn("加载系列");
        _chkDryRun = new CheckBox
        {
            Text = "Dry-run（不实际修改）",
            AutoSize = true,
            Margin = new Padding(1, 8, 1, 0),
            Checked = true,
        };
        _btnToggleAll = SideBtn("全选");
        _cmbFilter = FullWidth(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
        _cmbFilter.Items.AddRange(["全部", "未刮削（无链接）", "已刮削（有链接）",
            "未刮封面", "未刮简介", "待补全（封面或简介缺）", "已完整（封面+简介）"]);
        _cmbFilter.SelectedIndex = 0;
        _txtFilter = FullWidth(new TextBox { PlaceholderText = "过滤标题…" });
        _txtSearch = FullWidth(new TextBox { PlaceholderText = "全库搜索：输入漫画名回车…" });
        _btnSearchSeries = SideBtn("搜索全库");
        _btnEditSeries = SideBtn("编辑单部");

        _chkAuto = new CheckBox
        {
            Text = "全自动（不弹候选框）",
            AutoSize = true,
            Margin = new Padding(1, 8, 1, 0),
        };
        _btnScrapeMeta = SideBtn("刮削元数据");
        _btnScrapeAll = SideBtn("元数据+封面");
        _btnCoverScrape = SideBtn("刮削封面");
        _btnManualMatch = SideBtn("纠正匹配");
        _btnRematch = SideBtn("覆盖匹配");

        _btnFolderTag = SideBtn("打标");
        _btnMylarExport = SideBtn("导出 series.json");
        _btnOpenData = SideBtn("打开数据目录");
        _btnExportLog = SideBtn("导出日志");
        _btnCancel = SideBtn("取消当前任务");
        _btnCancel.Enabled = false;

        AddRow(side, Group("① 连接 Komga",
            Field("Komga 地址"), _txtUrl,
            Field("API Key"), _txtApiKey,
            Pair(_btnTest, _btnSettings)));
        AddRow(side, Group("② 加载与列表",
            Field("库"), _cmbLibrary,
            _btnLoad,
            _chkDryRun,
            _btnToggleAll,
            Field("筛选"), _cmbFilter,
            Field("过滤"), _txtFilter,
            Field("全库搜索"), _txtSearch,
            Pair(_btnSearchSeries, _btnEditSeries)));
        AddRow(side, Group("③ 刮削",
            _chkAuto,
            Pair(_btnScrapeMeta, _btnScrapeAll),
            Pair(_btnCoverScrape, _btnManualMatch),
            _btnRematch));
        AddRow(side, Group("④ 工具",
            _btnFolderTag,
            _btnMylarExport,
            Pair(_btnOpenData, _btnExportLog),
            _btnCancel));
        sidebarHost.Controls.Add(side);

        // ---- 右侧内容区：信息框（列表，上）+ 日志框（下），中间缝隙可拖动调两框占比 ----
        var splitMain = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 5,                     // 缝隙就是拖动把手：鼠标放上去按住上下拖
            FixedPanel = FixedPanel.None,          // 缩放窗口时两块按比例跟随
            BackColor = SystemColors.ControlDark,  // 缝隙颜色（比面板深一点，看得见摸得着）
            Margin = new Padding(0),
        };
        _lvSeries = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
        };
        foreach (var (name, _) in ColSpecs) _lvSeries.Columns.Add(name, 80);
        splitMain.Panel1.Controls.Add(_lvSeries);

        // 日志框：普通日志 + 改动留痕（Dry-run "将…" / 真实 "已…"）都在这里，不再单独占一个预览框
        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(28, 28, 30),
            ForeColor = Color.FromArgb(190, 255, 190),
            Font = new Font("Consolas", 9F),
            WordWrap = true,   // 日志自动换行，永远不出现横向滚动
        };
        splitMain.Panel2.Controls.Add(_txtLog);

        // ---- 状态栏 ----
        var statusStrip = new StatusStrip { SizingGrip = false };
        _progress = new ToolStripProgressBar { Visible = false, Width = 260 };
        _status = new ToolStripStatusLabel { Text = "未连接" };
        _statusChanges = new ToolStripStatusLabel { Text = "改动 0" };
        statusStrip.Items.AddRange(new ToolStripItem[] { _status, new ToolStripStatusLabel { Spring = true }, _statusChanges, _progress });

        // ---- 关键：Dock 布局按 z 序"从后往前"排，最后添加的先占位置 ----
        // 先加 Fill 的内容区，后加 Left 的操作栏，内容区才会拿到操作栏之外的全部空间；
        // 顺序反了内容区会铺满整窗、被操作栏压住左侧（曾导致"标题"列与日志左半截看不见）。
        Controls.Add(splitMain);      // Fill：最后参与布局 → 拿到剩余空间（内部分上信息框/下日志框，缝隙可拖）
        Controls.Add(sidebarHost);    // Left：先占住 320 宽
        Controls.Add(statusStrip);    // Bottom：最先占住底部

        // ---- 响应式：窗口尺寸变化 → 列表列宽自动跟随 ----
        Shown += (_, _) =>
        {
            // 再排一次（等最终布局稳定），并设信息框/日志框初始占比（拖动中间缝隙可随时调整）
            BeginInvoke(() =>
            {
                ResizeColumns();
                try { splitMain.SplitterDistance = (int)Math.Round(splitMain.Height * 0.62); }
                catch { /* 极小窗口时 SplitContainer 有最小值保护 */ }
            });
            _txtLog.SelectionStart = _txtLog.TextLength;
            _txtLog.ScrollToCaret();
            // 布局风暴可能留下未重绘的旧像素，强制全窗口重绘一次
            Invalidate(true);
            Update();
        };
        _lvSeries.Resize += (_, _) => ResizeColumns();

        // 事件
        _btnTest.Click += async (_, _) => await TestConnectionAsync();
        _btnSettings.Click += async (_, _) => await OpenSettingsAsync();
        _btnLoad.Click += async (_, _) => await LoadSeriesAsync();
        _btnScrapeMeta.Click += async (_, _) => await ScrapeCheckedAsync(SyncType.Meta);
        _btnScrapeAll.Click += async (_, _) => await ScrapeCheckedAsync(SyncType.All);
        _btnManualMatch.Click += async (_, _) => await CorrectMatchAsync();
        _btnCoverScrape.Click += async (_, _) => await CoverScrapeAsync();
        _btnFolderTag.Click += async (_, _) => await FolderTagAsync();
        _btnMylarExport.Click += async (_, _) => await MylarExportAsync();
        _btnOpenData.Click += (_, _) => OpenDataDir();
        _btnExportLog.Click += (_, _) => ExportLog();
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        _btnRematch.Click += async (_, _) => await RematchAsync();
        _btnToggleAll.Click += (_, _) => ToggleAll();
        _cmbFilter.SelectedIndexChanged += (_, _) => ApplyFilter();
        _txtFilter.TextChanged += (_, _) => ApplyFilter();
        _btnSearchSeries.Click += async (_, _) => await SearchAllLibrariesAsync();
        _txtSearch.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = SearchAllLibrariesAsync(); } };
        _btnEditSeries.Click += async (_, _) => await EditSeriesEntryAsync();
        _lvSeries.DoubleClick += async (_, _) => await EditSeriesEntryAsync();
        _lvSeries.ColumnClick += (_, e) => ListViewColumnSorter.OnColumnClick(_lvSeries, e.Column);
        // ---- 鼠标左键拖拽框选：按住左键拖出方框，方框碰到的行全部勾选（批处理按钮只认勾选） ----
        _lvSeries.MouseDown += SeriesListBandMouseDown;
        _lvSeries.MouseMove += SeriesListBandMouseMove;
        _lvSeries.MouseUp += SeriesListBandMouseUp;
        // ---- 托盘后台：点 X / 最小化 = 挂后台继续跑任务；真正退出走托盘右键"退出程序" ----
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* 取不到就用默认图标 */ }
        _tray = new NotifyIcon
        {
            Icon = Icon,
            Text = "KomgaScraper",
            Visible = true,
        };
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示主窗口", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("退出程序", null, (_, _) => ExitFromTray());
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) HideToTray("已最小化到托盘，任务在后台继续"); };
        FormClosing += (_, e) =>
        {
            if (!_trayExitPending)
            {
                // 点 X 不再退出：任务运行中照样挂后台（不再弹"请先取消"），空闲时也常驻托盘
                e.Cancel = true;
                HideToTray(_busy ? "任务正在后台执行，可从托盘图标随时回来" : "程序已挂到后台");
                return;
            }
            CancelCoverSweep();
            _state?.SaveNow();   // 封面/简介状态落盘（平时是防抖延迟写，退出时强制一次）
            _tray.Visible = false;
            _tray.Dispose();
        };
        _chkDryRun.CheckedChanged += (_, _) => { _settings.DryRun = _chkDryRun.Checked; SaveSettings(); };
        _txtUrl.LostFocus += (_, _) =>
        {
            var v = _txtUrl.Text.Trim();
            if (v == _settings.KomgaUrl) return;             // 值没变不重建（旧逻辑每次失焦都重建，纯浪费且危险）
            _settings.KomgaUrl = v;
            SaveSettings();
            ScheduleRebuild();
        };
        _txtApiKey.LostFocus += (_, _) =>
        {
            var v = _txtApiKey.Text.Trim();
            if (v == _settings.KomgaApiKey) return;
            _settings.KomgaApiKey = v;
            SaveSettings();
            ScheduleRebuild();
        };

        // 右键菜单：编辑单部 / 纠正匹配 / 覆盖匹配 / 打开 Bangumi 条目 / 复制 subject ID
        var listMenu = new ContextMenuStrip();
        var miCorrect = new ToolStripMenuItem("纠正匹配");
        var miRematch = new ToolStripMenuItem("覆盖匹配");
        var miEdit = new ToolStripMenuItem("编辑单部");
        var miOpenBtv = new ToolStripMenuItem("打开 Bangumi 条目页");
        var miCopyId = new ToolStripMenuItem("复制 Bangumi subject ID");
        listMenu.Items.AddRange(new ToolStripItem[] { miEdit, new ToolStripSeparator(), miCorrect, miRematch, new ToolStripSeparator(), miOpenBtv, miCopyId });
        _lvSeries.ContextMenuStrip = listMenu;
        listMenu.Opening += (_, e) =>
        {
            var s = ClickedSeries();
            miOpenBtv.Enabled = s != null && HasBtvLink(s);
            miCopyId.Enabled = s != null && GetBtvSubjectId(s).HasValue;
        };
        miCorrect.Click += async (_, _) => await CorrectMatchAsync();
        miRematch.Click += async (_, _) => await RematchAsync();
        miEdit.Click += async (_, _) => await EditSeriesEntryAsync();
        miOpenBtv.Click += (_, _) => OpenBtvPage();
        miCopyId.Click += (_, _) => CopyBtvId();
    }

    /// <summary>最小化到托盘：窗口隐藏、任务照跑；气泡只提示一次。</summary>
    private void HideToTray(string tip)
    {
        Hide();
        if (!_trayBalloonShown)
        {
            _trayBalloonShown = true;
            _tray.ShowBalloonTip(4000, "KomgaScraper", tip, ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    /// <summary>托盘菜单"退出程序"：任务运行中先取消并等收尾，再真正退出。</summary>
    private void ExitFromTray()
    {
        if (_busy)
        {
            _cts?.Cancel();
            for (var i = 0; _busy && i < 50; i++)
            {
                Application.DoEvents();
                Thread.Sleep(100);
            }
        }
        _trayExitPending = true;
        Close();
    }

    /// <summary>打开便携数据目录（设置文件 / BookWalker 索引都在这里）。
    /// 走 Shell COM（打开并定位该文件夹）——不再直接派生 explorer.exe 子进程，
    /// 避免"应用程序无法正常启动(0xc0000142)"弹窗（explorer 继承了宿主的不完整环境块）。</summary>
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
                // COM 失败回退 ShellExecute（走 shell 的正常目录打开路径）
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
            try { Clipboard.SetText(AppSettings.DataDir); } catch { /* 剪贴板被占用就算了 */ }
            Log("ERR ", $"打开数据目录失败：{e.Message}（路径已复制到剪贴板：{AppSettings.DataDir}）");
        }
    }

    /// <summary>开发自检用：以非模态方式打开设置对话框（--dev-settings 启动参数）。</summary>
    internal void ShowSettingsDev()
    {
        var dlg = new SettingsDialog(_settings);
        dlg.Show(this);
    }

    private void LoadSettingsToUi()
    {
        _txtUrl.Text = _settings.KomgaUrl;
        _txtApiKey.Text = _settings.KomgaApiKey;
        _chkDryRun.Checked = _settings.DryRun;
    }

    private bool _pendingRebuild;

    /// <summary>
    /// 客户端重建（安全版）：任务运行中绝不重建——RebuildClients 会 Dispose 正在飞行的 HttpClient，
    /// 在飞请求当场集体 "A task was canceled"、批次流产（这正是"任务自己中断"的根因）。
    /// 运行中只挂起标志，任务收尾时补做。
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

    private void RebuildClients()
    {
        _komga?.Dispose();
        _bangumi?.Dispose();
        _ai?.Dispose();
        CancelCoverSweep();
        _state ??= new ScrapeStateStore(Path.Combine(AppSettings.DataDir, "scrape-state.json"));
        // Komga 走不走代理由设置开关决定（本机/内网部署默认直连；境外/跨网段部署走统一代理）
        var komgaProxy = _settings.KomgaProxyEnabled ? _settings.Proxy : ProxyConfig.None;
        _komga = new KomgaClient(_settings.KomgaUrl, _settings.KomgaApiKey,
            _settings.KomgaUsername, _settings.KomgaPassword, komgaProxy);
        if (_settings.KomgaProxyEnabled && komgaProxy.IsSet)
            Log("INFO", $"Komga 连接走代理：{komgaProxy.Display}");
        else if (_settings.KomgaProxyEnabled)
            Log("WARN", "已开启 Komga 代理，但未配置代理地址——当前仍直连，请到设置里补代理");
        _bangumi = new BangumiClient(_settings.BangumiAccessToken,
            string.IsNullOrWhiteSpace(_settings.BangumiApiBase) ? null : _settings.BangumiApiBase,
            _settings.ProxyUrl, proxyUser: _settings.ProxyUser, proxyPassword: _settings.ProxyPassword);
        var logger = new UiLogger(Log, SetProgress);
        _scrape = new ScrapeService(_komga, _bangumi, logger)
        {
            Proxy = _settings.Proxy,
            State = _state,
        };
        _batch = new BatchMatcher(_komga, _bangumi, logger)
        {
            MatchTypePlatform = _settings.MatchType,
            Proxy = _settings.Proxy,
            State = _state,
        };
        _mylar = new MylarExporter(_komga, logger);
        _tagger = new FolderTagger(_komga, logger);

        // 改动留痕：三条写入管线（单系列刮削 / 批量匹配 / 打标）都汇到同一个日志框
        _scrape.ChangeObserved += OnScrapeChange;
        _batch.ChangeObserved += OnScrapeChange;
        _tagger.ChangeObserved += OnScrapeChange;

        // AI（可选）：匹配选择 + 简介翻译，两者共用同一个接口配置
        _ai?.Dispose();
        _ai = null;
        _batch.Ai = null;
        _batch.TranslateSummary = false;
        _scrape.Ai = null;
        _scrape.TranslateSummary = false;
        var needAi = !string.IsNullOrWhiteSpace(_settings.AiApiKey)
                     && (_settings.AiEnabled || _settings.AiTranslateSummary);
        if (needAi)
        {
            // AI 中转站走统一代理：实测部分中转站直连路由 500、代理 200（2026-10-10 api.yjs.im）；
            // 代理未配置时 Proxy=None 自动退回直连，行为不变。
            var aiProxy = _settings.Proxy.IsSet ? _settings.Proxy : ProxyConfig.None;
            _ai = new AiClient(_settings.AiBaseUrl, _settings.AiApiKey, _settings.AiModel, aiProxy, logger);
            if (_settings.AiEnabled)
            {
                _batch.Ai = _ai;
                Log("INFO", $"AI 匹配已启用（{_settings.AiModel} @ {_settings.AiBaseUrl}，{(aiProxy.IsSet ? "走代理" : "直连")}）");
            }
            if (_settings.AiTranslateSummary)
            {
                _scrape.Ai = _ai;
                _scrape.TranslateSummary = true;
                _batch.TranslateSummary = true;   // 关键：批量链路（精确/覆盖匹配）的卷简介翻译也生效
                Log("INFO", $"AI 简介翻译已启用（{_settings.AiModel}）：非中文简介将自动翻译成中文");
            }
        }
        else if (_settings.AiEnabled || _settings.AiTranslateSummary)
        {
            Log("WARN", "AI 功能已勾选但未填 API Key，本次不会生效（设置 → AI 匹配）");
        }

        // 刮削行为 / BookWalker 推断字段
        _scrape.BwWriteTags = _settings.BwWriteTags;
        _batch.FetchVolumeData = _settings.FetchVolumeData;
        _batch.BwWriteTags = _settings.BwWriteTags;
        _batch.SeriesConcurrency = _settings.ScrapeConcurrency;

        // 版本标记黑白名单（设置 → 版本标记；三个写入管线共用同一套名单）
        _scrape.MarkerWhitelist = _settings.TagWhitelist.Count > 0 ? _settings.TagWhitelist : null;
        _scrape.MarkerBlacklist = _settings.TagBlacklist.Count > 0 ? _settings.TagBlacklist : null;
        _batch.MarkerWhitelist = _scrape.MarkerWhitelist;
        _batch.MarkerBlacklist = _scrape.MarkerBlacklist;
        _tagger.MarkerWhitelist = _scrape.MarkerWhitelist;
        _tagger.MarkerBlacklist = _scrape.MarkerBlacklist;

        // 代理：Bangumi API / 外部封面 / BookWalker 下载共用（Komga 请求始终直连）
        Log("INFO", _settings.Proxy.IsSet
            ? $"代理已启用: {_settings.Proxy.Display}（Bangumi API + 封面下载 + BookWalker 共用）"
            : "未使用代理：Bangumi API 与封面下载将直连（网络受限时请在 设置 → 代理 配置）");

        // BookWalker 本地索引（可选，设置里下载构建）
        var bwPath = AppSettings.BkwkIndexPath;
        _bwIndex = null;
        _batch.BwIndex = null;
        _scrape.BwIndex = null;
        if (File.Exists(bwPath))
        {
            try
            {
                _bwIndex = new BookWalkerIndex(bwPath);
                _batch.BwIndex = _bwIndex;
                _scrape.BwIndex = _bwIndex;
                Log("INFO", $"BookWalker 索引已加载（{_bwIndex.Count:N0} 个系列，{_bwIndex.VolumeTotal:N0} 卷，" +
                           $"格式 v{_bwIndex.FormatVersion}）");
                if (_bwIndex.IsLegacyFormat)
                    Log("WARN", "BookWalker 索引是旧格式（没有每卷/类型题材数据），" +
                                "请到设置里重新『导入本地文件…』或『直接下载建索引』");
            }
            catch (Exception e)
            {
                _bwIndex = null;
                _batch.BwIndex = null;
                _scrape.BwIndex = null;
                Log("WARN", $"BookWalker 索引加载失败: {e.Message}（可在设置里重建）");
            }
        }

        // Mox（bookof.moe）中文漫画库第三源：Bangumi/BookWalker 都没命中时按中文名兜底
        // （书名/简介/作者/状态/题材本身是中文，每卷封面来自其帧页；请求走代理与封面同路）
        _mox?.Dispose();
        _mox = new MoxClient(_settings.ProxyUrl, proxyUser: _settings.ProxyUser, proxyPassword: _settings.ProxyPassword);
        _scrape.Mox = _mox;
        _batch.Mox = _mox;

        // 百度百科（最终兜底源）：冷门漫画在 bgm/目录站都没有时按中文名查词条（国内直连，不走代理）
        _baike?.Dispose();
        _baike = null;
        _scrape.Baike = null;
        _batch.Baike = null;
        if (_settings.BaikeEnabled)
        {
            _baike = new BaikeClient();
            _scrape.Baike = _baike;
            _batch.Baike = _baike;
            Log("INFO", "百度百科兜底源已启用（所有源无结果时自动回退）");
        }

        // MangaDex（英文圈图源，免密钥公开 API）：百科兜底前的英文区回退（走代理，与 Bangumi 同路）
        _mdex?.Dispose();
        _mdex = null;
        OriginFilter.JapaneseOnly = _settings.JapaneseOnly;   // 静态来源过滤器随设置同步
        if (_settings.MangaDexEnabled)
        {
            _mdex = new MangaDexClient(proxy: _settings.Proxy, log: null);
            _batch.MangaDex = _mdex;
            Log("INFO", "MangaDex 源已启用（英文区回退，百科兜底之前）");
        }
        Log("INFO", _settings.JapaneseOnly
            ? "日漫限定：韩漫/美漫/国漫候选一律剔除"
            : "日漫限定已关闭：不再过滤非日漫候选");

        // OpenCC 繁简引擎后台预热（首次初始化加载词典 ~1s，别卡在第一次刮削里）
        _ = Task.Run(() =>
        {
            try
            {
                var s = CJK.T2S("繁簡轉換預熱");
                Log("INFO", string.Equals(s, "繁简转换预热", StringComparison.Ordinal)
                    ? "OpenCC 简繁引擎已就绪（词汇级）"
                    : "OpenCC 初始化降级：使用内置单字映射");
            }
            catch { /* 降级映射兜底，无需提示 */ }
        });
    }

    private void ToggleAll()
    {
        if (_lvSeries.Items.Count == 0) return;
        var allChecked = _lvSeries.Items.Cast<ListViewItem>().All(i => i.Checked);
        var target = !allChecked;
        foreach (ListViewItem item in _lvSeries.Items) item.Checked = target;
        _btnToggleAll.Text = target ? "全不选" : "全选";
    }

    // ================= 鼠标框选（按住左键拖出方框，碰到的行全勾选） =================
    // 批处理按钮（刮削元数据/全部/封面/纠正匹配…）只认 GetCheckedSeries() 的勾选状态，
    // 所以框选直接写 item.Checked；同时置 Selected 给视觉反馈。6px 位移阈值以内仍是普通点击。

    private bool _bandMaybe;      // 左键已按下、还没超阈值
    private bool _bandActive;     // 框选进行中
    private Point _bandStart;
    private Rectangle _bandRect;  // 列表客户区坐标

    private void SeriesListBandMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _bandMaybe = true;
        _bandStart = e.Location;
        _bandRect = Rectangle.Empty;
    }

    private void SeriesListBandMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_bandMaybe || e.Button != MouseButtons.Left) return;
        if (!_bandActive &&
            Math.Abs(e.X - _bandStart.X) + Math.Abs(e.Y - _bandStart.Y) < 6) return;   // 普通点击不动它

        if (!_bandActive) { _bandActive = true; }
        // 擦旧框（ReversibleRectangle 再画一遍即消失）、画新框（屏幕坐标）
        if (!_bandRect.IsEmpty)
            ControlPaint.FillReversibleRectangle(_lvSeries.RectangleToScreen(_bandRect), Color.Black);
        _bandRect = Rectangle.FromLTRB(
            Math.Min(_bandStart.X, e.X), Math.Min(_bandStart.Y, e.Y),
            Math.Max(_bandStart.X, e.X), Math.Max(_bandStart.Y, e.Y));
        ControlPaint.FillReversibleRectangle(_lvSeries.RectangleToScreen(_bandRect), Color.Black);

        SelectSeriesByBand();
    }

    private void SeriesListBandMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_bandActive) { _bandMaybe = false; return; }   // 没拖出框 = 普通点击，交回原行为
        _bandMaybe = false;
        _bandActive = false;
        if (!_bandRect.IsEmpty)
            ControlPaint.FillReversibleRectangle(_lvSeries.RectangleToScreen(_bandRect), Color.Black);   // 擦框
        _bandRect = Rectangle.Empty;
        // 收尾重算要排在 ListView 默认 MouseUp 处理（比如松手正落在复选框上会把勾又切回去）之后
        BeginInvoke(SelectSeriesByBand);
    }

    private void SelectSeriesByBand()
    {
        if (_bandRect.IsEmpty) return;
        foreach (ListViewItem item in _lvSeries.Items)
        {
            var row = item.Bounds;   // Details 视图 = 整行（含子项）
            if (!row.IntersectsWith(_bandRect)) continue;
            if (!item.Selected) item.Selected = true;
            if (!item.Checked) item.Checked = true;
        }
    }

    // ================= 工具 =================

    private void Log(string level, string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(level, message));
        }
        else
        {
            AppendLog(level, message);
        }
    }

    private void AppendLog(string level, string message)
    {
        var time = DateTime.Now.ToString("HH:mm:ss");
        _txtLog.AppendText($"[{time}][{level}] {message}{Environment.NewLine}");

        // 同时落到 data\logs\log-日期.log：程序崩了/关了也能拿到全文，方便直接把文件发给别人排查
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
            _logFileFailed = true;   // 目录只读等情况：放弃文件日志，界面日志不受影响
        }
    }

    /// <summary>
    /// 导出日志：不再弹保存对话框（实测换盘/换目录都可能被权限问题卡住），
    /// 直接把最近 3 天的文件日志（没有文件日志时用界面日志兜底）写进下载目录，
    /// 并在资源管理器里定位到该文件 —— 全程不经过任何对话框，不挑权限。
    /// </summary>
    private void ExportLog()
    {
        try
        {
            var logsDir = Path.Combine(AppSettings.DataDir, "logs");
            var sb = new System.Text.StringBuilder();
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
                sb.AppendLine(_txtLog.Text);
            }
            if (sb.Length == 0) sb.AppendLine("(日志为空)");

            var targetDir = GetWritableExportDir();
            var path = Path.Combine(targetDir, $"KomgaScraper-日志-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Log("INFO", $"日志已导出：{path}");
            // Shell COM 定位：把已运行的 shell 进程当导出通道，不新起 explorer 进程树。
            // COM 失败也不再回退 Process.Start("explorer.exe") —— 那正是「应用程序无法正常启动
            // (0xc0000142)」红窗的唯一来源（宿主拉起时环境块不完整）。定位不了就把路径留在日志里。
            try
            {
                OpenFolderAndSelectFile(path);
            }
            catch (Exception e)
            {
                Log("INFO", $"资源管理器定位失败（{e.Message}），文件路径见上一行日志");
            }
            // 不再弹模态框（成功蓝框/失败红框都算打扰）：托盘气泡 + 日志框留痕，导出全程零弹窗
            try
            {
                _tray?.ShowBalloonTip(3000, "导出日志", $"已导出：{Path.GetFileName(path)}", ToolTipIcon.Info);
            }
            catch { /* 托盘不可用就算了，日志已有路径 */ }
        }
        catch (Exception e)
        {
            Log("ERR ", $"导出日志失败: {e.Message}");
            try
            {
                _tray?.ShowBalloonTip(4000, "导出日志失败", e.Message, ToolTipIcon.Error);
            }
            catch { /* 托盘不可用就算了 */ }
        }
    }

    /// <summary>
    /// 资源管理器定位文件：走 Shell COM 的 SHOpenFolderAndSelectItems（资源管理器自身的导出接口）。
    /// 直接派生 "explorer.exe /select,…" 子进程会继承本进程的环境块——本程序由外部工具/服务类
    /// 宿主拉起时环境块不完整，explorer 初始化失败，每次都弹「应用程序无法正常启动(0xc0000142)」。
    /// COM 调用交给已运行的 shell 进程完成，不再新起 explorer 进程树，弹窗消失。
    /// </summary>
    private static void OpenFolderAndSelectFile(string path)
    {
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

    /// <summary>导出目标目录：下载 → 桌面 → 数据目录，用探测文件验证可写，第一个成功的生效。</summary>
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

    private void SetProgress(int current, int total, string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetProgressCore(current, total, message));
        }
        else
        {
            SetProgressCore(current, total, message);
        }
    }

    private void SetProgressCore(int current, int total, string message)
    {
        _progress.Visible = true;
        _progress.Maximum = Math.Max(1, total);
        _progress.Value = Math.Clamp(current, 0, _progress.Maximum);
        _status.Text = $"[{current}/{total}] {message}";
        if (_tray != null)
        {
            var tip = $"[{current}/{total}] {message}";
            if (tip.Length > 63)
            {
                tip = tip[..63];                                  // 托盘悬浮提示上限 63 字符
                while (tip.Length > 0 && char.IsHighSurrogate(tip[^1])) tip = tip[..^1];
            }
            _tray.Text = tip;
        }
    }

    private bool GuardBusy()
    {
        if (_busy) { MessageBox.Show("有任务正在执行，请等待或取消"); return false; }
        if (_komga == null || _scrape == null || _batch == null || _mylar == null || _tagger == null)
        { MessageBox.Show("客户端未初始化"); return false; }
        return true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _btnTest.Enabled = !busy;
        _btnLoad.Enabled = !busy;
        _btnSettings.Enabled = !busy;
        _btnScrapeMeta.Enabled = !busy;
        _btnScrapeAll.Enabled = !busy;
        _btnManualMatch.Enabled = !busy;
        _btnCoverScrape.Enabled = !busy;
        _btnRematch.Enabled = !busy;
        _btnFolderTag.Enabled = !busy;
        _btnMylarExport.Enabled = !busy;
        _btnExportLog.Enabled = !busy;
        _btnCancel.Enabled = busy;
        if (!busy)
        {
            _progress.Visible = false;
            _status.Text = "空闲";
            if (_tray != null)
            {
                _tray.Text = "KomgaScraper";
                if (!Visible)
                    _tray.ShowBalloonTip(2500, "KomgaScraper", "后台任务已结束，点托盘图标查看结果", ToolTipIcon.Info);
            }
        }
        else if (_tray != null)
        {
            _tray.Text = "KomgaScraper（任务执行中…）";
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
            // 带上异常信息：HttpClient 超时也抛 TaskCanceledException，不带原因的"任务已取消"
            // 曾把"100 秒超时"误报成用户手动取消（历史事故：整批莫名"已取消"）。
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

    private List<SeriesDto> GetCheckedSeries()
    {
        return _lvSeries.Items.Cast<ListViewItem>()
            .Where(i => i.Checked)
            .Select(i => (SeriesDto)i.Tag!)
            .ToList();
    }

    // ================= 事件处理 =================

    private async Task TestConnectionAsync()
    {
        if (_komga == null) return;
        _settings.KomgaUrl = _txtUrl.Text.Trim();
        _settings.KomgaApiKey = _txtApiKey.Text.Trim();
        SaveSettings();
        RebuildClients();
        try
        {
            SetBusy(true);
            _cts = new CancellationTokenSource();
            var libs = await Task.Run(() => _komga!.GetLibrariesAsync(_cts.Token));
            FillLibraryCombo(libs);
            _status.Text = $"连接成功，共 {libs.Count} 个库";
            Log("INFO", $"连接成功：{_settings.KomgaUrl}，共 {libs.Count} 个库");
        }
        catch (Exception e)
        {
            _status.Text = "连接失败";
            Log("ERR ", $"连接失败: {e.Message}");
            MessageBox.Show($"连接失败：{e.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            TryDeferredRebuild();
        }
    }

    private async Task OpenSettingsAsync()
    {
        using var dlg = new SettingsDialog(_settings);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _settings = dlg.EditedSettings;
            SaveSettings();
            ScheduleRebuild();
            Log("INFO", $"设置已保存：{AppSettings.SettingsFile}");
        }
    }

    private void FillLibraryCombo(List<LibraryDto> libs)
    {
        _libraries = libs;
        _cmbLibrary.Items.Clear();
        foreach (var lib in libs) _cmbLibrary.Items.Add(lib.Name);
        var idx = libs.FindIndex(l => l.Id == _settings.LastLibraryId);
        if (idx >= 0) _cmbLibrary.SelectedIndex = idx;
        else if (libs.Count > 0) _cmbLibrary.SelectedIndex = 0;
    }

    private async Task LoadSeriesAsync()
    {
        if (!GuardBusy()) return;
        if (_cmbLibrary.SelectedIndex < 0)
        {
            // 先尝试拉库
            try
            {
                var libs = await RunBusyAsync(ct => _komga!.GetLibrariesAsync(ct));
                FillLibraryCombo(libs);
            }
            catch (Exception e)
            {
                Log("ERR ", $"获取库列表失败: {e.Message}");
                MessageBox.Show($"获取库列表失败：{e.Message}\n请先点击\"测试连接\"检查地址与密钥");
                return;
            }
            if (_cmbLibrary.SelectedIndex < 0) { MessageBox.Show("请先选择库"); return; }
        }

        var library = _libraries[_cmbLibrary.SelectedIndex];
        try
        {
            var series = await RunBusyAsync(ct => _komga!.ListSeriesInLibraryAsync(library.Id, ct));
            _seriesCache.Clear();
            _lvSeries.BeginUpdate();
            _lvSeries.Items.Clear();
            foreach (var s in series)
            {
                _seriesCache[s.Id] = s;
                var item = new ListViewItem(string.IsNullOrWhiteSpace(s.Metadata?.Title) ? s.Name : s.Metadata!.Title) { Tag = s };
                item.SubItems.Add(Path.GetFileName((s.Url ?? "").TrimEnd('/')));
                item.SubItems.Add(s.BooksCount.ToString());
                item.SubItems.Add(s.Metadata?.Status ?? "");
                item.SubItems.Add(HasBtvLink(s) ? "Btv" : "");
                item.SubItems.Add(CoverCell(s.Id));
                item.SubItems.Add(string.IsNullOrEmpty(s.Metadata?.Summary) ? "✗" : "✓");
                var btvId = GetBtvSubjectId(s);
                item.SubItems.Add(btvId?.ToString() ?? "");
                _lvSeries.Items.Add(item);
            }
            _lvSeries.EndUpdate();
            _settings.LastLibraryId = library.Id;
            SaveSettings();
            _status.Text = $"库 \"{library.Name}\"：{series.Count} 个系列";
            Log("INFO", $"已加载库 \"{library.Name}\" 的 {series.Count} 个系列");
            if (_komga!.LastSeriesTotalElements > 0 && _komga.LastSeriesTotalElements != series.Count)
                Log("WARN", $"注意：服务器报告总数 {_komga.LastSeriesTotalElements}，实际加载 {series.Count}，" +
                            "可能存在分页异常，请检查日志");
            if (_seriesCache.Count == 0)
                Log("WARN", "该库没有系列（或全部被过滤）");
            // 后台补查封面状态（只查状态未知的系列，结果写进状态库并刷新行）
            StartCoverSweep(series);
        }
        catch (OperationCanceledException)
        {
            Log("WARN", "加载已取消");
        }
        catch (Exception e)
        {
            Log("ERR ", $"加载系列失败: {e.Message}");
            MessageBox.Show($"加载系列失败：{e.Message}");
        }
    }

    private static string ExtractSubjectIdText(SeriesDto s) =>
        s.Metadata?.Links?.FirstOrDefault(l => l.Url.Contains("/subject/", StringComparison.Ordinal))?.Url ?? "";

    private static bool HasBtvLink(SeriesDto s) => ExtractSubjectIdText(s).Length > 0;

    private static int? GetBtvSubjectId(SeriesDto s)
    {
        var url = ExtractSubjectIdText(s);
        var m = RegexCache.SubjectIdRegex().Match(url);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>封面列文本：✓ 已刮 / ✗ 确认未刮 / … 还没检查过（后台检查逐步补齐）。</summary>
    private string CoverCell(string seriesId) => _state?.GetCover(seriesId) switch
    {
        true => "✓",
        false => "✗",
        _ => "…",
    };

    /// <summary>服务器上该系列是否有用户上传的封面（本工具或其它途径刮的都算）；查失败返回 null（保持未知）。</summary>
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

    /// <summary>
    /// 后台封面状态检查：逐个 GET /series/{id}/thumbnails 把「封面是否已刮」补进状态库并刷新行。
    /// 只查状态未知的系列（结果会持久化，下次启动不重查）；并发 3，对象是自己的 Komga 服务器。
    /// </summary>
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
                        QueueCoverCellUpdate(id);
                    }).ConfigureAwait(false);
                Log("INFO", $"封面状态检查完成：已刮 {hasCover}，未刮 {noCover}");
            }
            catch (OperationCanceledException) { Log("INFO", "封面状态检查已取消"); }
            catch (Exception e) { Log("WARN", $"封面状态检查失败: {e.Message}"); }
        }, cts.Token);
    }

    // ---- 封面列的合并刷新：后台扫描一次命中上千系列，逐个 BeginInvoke 会排出一千多个 UI 消息，
    // 每个还全表扫描一遍列表——这里把要刷新的 id 攒起来，排一个"泵"一次刷完（UI 消息数从 ~N 降到 ~几）。
    private readonly object _pendingCoverGate = new();
    private readonly HashSet<string> _pendingCoverUpdates = [];
    private bool _coverPumpQueued;

    /// <summary>后台线程调用：登记一个待刷新的封面列，并保证 UI 队列里有一个泵在等着消化它们。</summary>
    private void QueueCoverCellUpdate(string seriesId)
    {
        lock (_pendingCoverGate)
        {
            _pendingCoverUpdates.Add(seriesId);
            if (_coverPumpQueued) return;
            _coverPumpQueued = true;
        }
        try
        {
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(PumpCoverUpdates);
            else
                ReleaseCoverPump();
        }
        catch (ObjectDisposedException) { ReleaseCoverPump(); }
        catch (InvalidOperationException) { ReleaseCoverPump(); }   // 句柄正在销毁的竞态窗口
    }

    private void ReleaseCoverPump()
    {
        lock (_pendingCoverGate) { _coverPumpQueued = false; _pendingCoverUpdates.Clear(); }
    }

    /// <summary>UI 线程：把攒下的封面列刷新一次性做完（按 id 建索引，不全表反复扫）。</summary>
    private void PumpCoverUpdates()
    {
        string[] ids;
        lock (_pendingCoverGate)
        {
            ids = [.. _pendingCoverUpdates];
            _pendingCoverUpdates.Clear();
            _coverPumpQueued = false;
        }
        if (ids.Length == 0 || _lvSeries.IsDisposed) return;
        var byId = new Dictionary<string, ListViewItem>(ids.Length, StringComparer.Ordinal);
        foreach (ListViewItem item in _lvSeries.Items)
            if (item.Tag is SeriesDto s)
                byId[s.Id] = item;
        foreach (var id in ids)
        {
            if (!byId.TryGetValue(id, out var item) || item.SubItems.Count <= 5) continue;
            item.SubItems[5].Text = CoverCell(id);
        }
    }

    /// <summary>单个系列封面列立即刷新（刮削完成等同步场景用）。</summary>
    private void UpdateCoverCell(string seriesId)
    {
        foreach (ListViewItem item in _lvSeries.Items)
        {
            if (item.Tag is SeriesDto s && s.Id == seriesId && item.SubItems.Count > 5)
                item.SubItems[5].Text = CoverCell(seriesId);
        }
    }

    private void CancelCoverSweep()
    {
        var old = _coverSweepCts;
        _coverSweepCts = null;
        if (old == null) return;
        try { old.Cancel(); } catch { }
        // 不立刻 Dispose：扫描循环可能还在读 token（Dispose 后访问会抛 ObjectDisposedException），
        // 延迟释放，回收动作与正在退出的循环错开
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(3000); old.Dispose(); } catch { }
        });
    }

    // ================= 改动留痕（并入日志框） =================

    /// <summary>Core 写入事件 → 日志行（事件来自线程池线程，这里切回 UI 线程）。</summary>
    private void OnScrapeChange(ScrapeChange c)
    {
        if (IsDisposed) return;
        try
        {
            if (IsHandleCreated) BeginInvoke(() => AppendPreviewRow(c));
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }   // 句柄正在销毁的竞态窗口
    }

    /// <summary>改动行直接写进日志框（不再单独占一个预览面板）：
    /// Dry-run 显示"将…"，真实模式显示"已…"；状态栏统计条数；文件日志同步留痕。</summary>
    private void AppendPreviewRow(ScrapeChange c)
    {
        if (_txtLog.IsDisposed) return;
        _previewCount++;
        if (_statusChanges != null) _statusChanges.Text = $"改动 {_previewCount}";
        var detail = string.IsNullOrEmpty(c.Detail) ? "" : $"；{c.Detail}";
        Log("改动", $"{ScrapeChangeKindText.Of(c.Kind)} {c.Target}：{c.Description}{detail}");
    }

    private void ClearPreview()
    {
        _previewCount = 0;
        if (_statusChanges != null) _statusChanges.Text = "改动 0";
        Log("改动", "──── 改动计数已清零（新任务从头统计）────");
    }

    // ================= 搜索全库 + 编辑单部 =================

    /// <summary>
    /// 跨库搜索：不经"加载系列"也能直接按名检索某部漫画（GET /series?search=…），
    /// 结果直接替换列表显示（含各库），双击行即可打开编辑器。
    /// </summary>
    private async Task SearchAllLibrariesAsync()
    {
        var kw = _txtSearch.Text.Trim();
        if (kw.Length == 0) { Log("WARN", "先在搜索框输入漫画名"); return; }
        if (_komga == null) { Log("WARN", "请先测试连接（Komga 未就绪）"); return; }

        try
        {
            SetBusy(true);
            SetProgress(0, 0, $"搜索「{kw}」…");
            var hits = await _komga.SearchSeriesAsync(kw, 60, CancellationToken.None);
            _seriesCache.Clear();
            _lvSeries.BeginUpdate();
            _lvSeries.Items.Clear();
            foreach (var s in hits)
            {
                _seriesCache[s.Id] = s;
                var item = new ListViewItem(string.IsNullOrWhiteSpace(s.Metadata?.Title) ? s.Name : s.Metadata!.Title) { Tag = s };
                item.SubItems.Add(Path.GetFileName((s.Url ?? "").TrimEnd('/')));
                item.SubItems.Add(s.BooksCount.ToString());
                item.SubItems.Add(s.Metadata?.Status ?? "");
                item.SubItems.Add(HasBtvLink(s) ? "Btv" : "");
                item.SubItems.Add(CoverCell(s.Id));
                item.SubItems.Add(string.IsNullOrEmpty(s.Metadata?.Summary) ? "✗" : "✓");
                var btvId = GetBtvSubjectId(s);
                item.SubItems.Add(btvId?.ToString() ?? "");
                _lvSeries.Items.Add(item);
            }
            _lvSeries.EndUpdate();
            Log("INFO", $"全库搜索「{kw}」命中 {hits.Count} 个系列（双击行可编辑）");
            SetProgress(0, 0, $"搜索「{kw}」命中 {hits.Count}");
        }
        catch (OperationCanceledException) { Log("INFO", "搜索已取消"); }
        catch (Exception e) { Log("ERR ", $"搜索失败: {e.Message}"); }
        finally { SetBusy(false); TryDeferredRebuild(); }
    }

    /// <summary>编辑入口：优先右键/双击所在系列，其次第一个勾选的系列。</summary>
    private async Task EditSeriesEntryAsync()
    {
        if (_komga == null) { Log("WARN", "请先测试连接（Komga 未就绪）"); return; }
        var s = ClickedSeries() ?? GetCheckedSeries().FirstOrDefault();
        if (s == null) { MessageBox.Show("先在列表里选中（或右键点在）要编辑的系列；\n也可以用上方搜索栏检索后再双击。", "编辑单部"); return; }
        await OpenSeriesEditorAsync(s.Id);
    }

    private async Task OpenSeriesEditorAsync(string seriesId)
    {
        var series = _seriesCache.TryGetValue(seriesId, out var s0) ? s0 : null;
        var subjectId = series != null ? GetBtvSubjectId(series) : null;

        async Task RescrapeVolumesAsync(bool dry, CancellationToken ct)
        {
            if (subjectId == null)
                throw new InvalidOperationException("该系列没有 Bangumi（Btv）来源链接");
            var opts = new ScrapeOptions
            {
                SyncType = SyncType.All,
                FetchVolumeData = true,
                DryRun = dry,
            };
            var r = await _scrape!.ScrapeAsync(seriesId, subjectId.Value, opts, null, null, ct);
            if (!r.Success) throw new InvalidOperationException(r.Error ?? "重刮失败");
            // 日志框/列表已在 _scrape.ChangeObserved 里实时更新；这里把行刷新一下
            await RefreshRowsFromServerAsync([seriesId]);
        }

        using var dlg = new SeriesEditorDialog(
            _komga!, seriesId, Log,
            () => _chkDryRun.Checked,
            RescrapeVolumesAsync);
        dlg.ChangeObserved += OnScrapeChange;   // 编辑器写入也汇入日志框
        dlg.ShowDialog(this);
        dlg.ChangeObserved -= OnScrapeChange;
        // 编辑器里改了库 → 刷新该行（封面列/标签等来自服务器）
        await RefreshRowsFromServerAsync([seriesId]);
    }

    private void ApplyFilter()
    {
        var kw = _txtFilter.Text.Trim();
        var mode = _cmbFilter.SelectedIndex; // 0=全部 1=未刮削 2=已刮削 3=未刮封面 4=未刮简介 5=待补全 6=已完整
        // 保留勾选状态重建列表（ListViewItem 无隐藏属性）
        var checkedIds = new HashSet<string>(
            _lvSeries.Items.Cast<ListViewItem>().Where(i => i.Checked).Select(i => ((SeriesDto)i.Tag!).Id));
        _lvSeries.BeginUpdate();
        _lvSeries.Items.Clear();
        foreach (var s in _seriesCache.Values)
        {
            var title = string.IsNullOrWhiteSpace(s.Metadata?.Title) ? s.Name : s.Metadata!.Title;
            if (kw.Length > 0 && !title.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                !s.Name.Contains(kw, StringComparison.OrdinalIgnoreCase))
                continue;
            var hasLink = HasBtvLink(s);
            if (mode == 1 && hasLink) continue;
            if (mode == 2 && !hasLink) continue;
            // 3/4/5/6：按 series 级「封面/简介」状态筛（不只是看链接）
            var coverOk = _state?.GetCover(s.Id);
            var summaryOk = !string.IsNullOrEmpty(s.Metadata?.Summary);
            if (mode == 3 && coverOk == true) continue;                       // 未刮封面（含未检查）
            if (mode == 4 && summaryOk) continue;                             // 未刮简介
            if (mode == 5 && coverOk == true && summaryOk) continue;          // 待补全：封面或简介缺
            if (mode == 6 && (coverOk != true || !summaryOk)) continue;       // 已完整
            var item = new ListViewItem(title) { Tag = s, Checked = checkedIds.Contains(s.Id) };
            item.SubItems.Add(Path.GetFileName((s.Url ?? "").TrimEnd('/')));
            item.SubItems.Add(s.BooksCount.ToString());
            item.SubItems.Add(s.Metadata?.Status ?? "");
            item.SubItems.Add(hasLink ? "Btv" : "");
            item.SubItems.Add(CoverCell(s.Id));
            item.SubItems.Add(summaryOk ? "✓" : "✗");
            var btvId = GetBtvSubjectId(s);
            item.SubItems.Add(btvId?.ToString() ?? "");
            _lvSeries.Items.Add(item);
        }
        _lvSeries.EndUpdate();
    }

    // ---- 刮削（勾选系列，弹候选面板） ----

    private async Task ScrapeCheckedAsync(SyncType syncType)
    {
        if (!GuardBusy()) return;
        var targets = GetCheckedSeries();
        if (targets.Count == 0) { MessageBox.Show("请先勾选要刮削的系列"); return; }

        // 全自动模式：走批量精确匹配管线（含 BookWalker/AI 回退），不弹候选框
        if (_chkAuto.Checked)
        {
            await RunBatchMatchAsync(targets, "勾选系列");
            RefreshCheckedRows();
            return;
        }

        var label = syncType == SyncType.All ? "元数据+封面" : "元数据";
        var dry = _chkDryRun.Checked;
        var confirm = MessageBox.Show(
            $"将对 {targets.Count} 个勾选系列刮削{label}。\n模式：{(dry ? "Dry-run 预览" : "真实更新")}\n继续？",
            "确认", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;
        ClearPreview();   // 每次写入任务的预览从头统计（上一次的行会混进来）

        int done = 0, ok = 0;
        foreach (var series in targets)
        {
            try
            {
                var name = string.IsNullOrWhiteSpace(series.Metadata?.Title) ? series.Name : series.Metadata!.Title;
                SetProgress(++done, targets.Count, $"搜索《{name}》");
                var term = TitleParser.ExtractSeriesTitles(name, 1).FirstOrDefault() ?? name;
                // 四源候选全量合并：bgm 返回噪声候选时，百科/BW/Mox 候选也要进弹窗（冷门漫画只有百科有）。
                var candidates = new List<SearchCandidate>();
                try
                {
                    candidates.AddRange(await _bangumi!.SearchSubjectsAsync(term, _settings.MatchType, 8, _cts?.Token ?? CancellationToken.None));
                }
                catch (Exception e)
                {
                    Log("ERR ", $"《{name}》Bangumi 搜索失败: {e.Message}");
                }

                // BookWalker 本地索引补候选
                if (_bwIndex != null)
                {
                    var bwList = _bwIndex.SearchFuzzy(term, 8);
                    if (bwList.Count > 0)
                    {
                        candidates.AddRange(bwList.Select(b => _bwIndex.ToCandidate(b)));
                        Log("INFO", $"《{name}》BookWalker 本地候选 {bwList.Count} 条");
                    }
                }
                // Mox（bookof.moe）中文漫画库按中文名搜（第三源）
                if (_mox != null)
                {
                    try
                    {
                        var moxList = await _mox.SearchAsync(term, 8, _cts?.Token ?? CancellationToken.None);
                        if (moxList.Count > 0)
                        {
                            candidates.AddRange(moxList.Select(m => new SearchCandidate(
                                0, m.Title, m.Title, m.Author, "", m.CoverUrl, BatchMatcher.SourceMox, null, m.Id)));
                            Log("INFO", $"《{name}》Mox 候选 {moxList.Count} 条");
                        }
                    }
                    catch (Exception e)
                    {
                        Log("WARN", $"《{name}》Mox 搜索失败: {e.Message}");
                    }
                }
                // MangaDex（英文圈图源，免密钥）：候选进弹窗
                if (_mdex != null)
                {
                    try
                    {
                        var mdList = await _mdex.SearchAsync(term, 8, _cts?.Token ?? CancellationToken.None);
                        if (mdList.Count > 0)
                        {
                            candidates.AddRange(mdList.Select(m => new SearchCandidate(
                                0, m.Title, m.OriTitle, "", string.Join(" / ", m.AltTitles.Take(3)),
                                m.CoverUrl, BatchMatcher.SourceMangaDex, MdId: m.Id)));
                            Log("INFO", $"《{name}》MangaDex 候选 {mdList.Count} 条");
                        }
                    }
                    catch (Exception e)
                    {
                        Log("WARN", $"《{name}》MangaDex 搜索失败: {e.Message}");
                    }
                }
                // 百度百科词条（冷门漫画其它源都没有；作为候选进弹窗，不绕过）
                if (_baike != null)
                {
                    try
                    {
                        var bkCard = await _baike.SearchCardAsync(term, _cts?.Token ?? CancellationToken.None);
                        if (bkCard != null)
                        {
                            candidates.Add(new SearchCandidate(0, bkCard.Title, bkCard.Field("原版名称"),
                                bkCard.Field("作者") is { Length: > 0 } ba ? ba : bkCard.Field("作画"),
                                "", bkCard.ImageUrl.Length > 0 ? BaikeClient.JpegCoverUrl(bkCard.ImageUrl) : null,
                                BatchMatcher.SourceBaike));
                            Log("INFO", $"《{name}》百度百科候选：《{bkCard.Title}》");
                        }
                    }
                    catch (Exception e)
                    {
                        Log("WARN", $"《{name}》百度百科搜索失败: {e.Message}");
                    }
                }
                if (candidates.Count == 0)
                {
                    Log("WARN", $"《{name}》搜索\"{term}\"无结果，跳过");
                    continue;
                }

                // UI 线程弹候选框（右侧详情面板会显示所选候选的封面与简介）
                SearchCandidate? chosen = null;
                Invoke(() =>
                {
                    using var dlg = new MatchDialog(name, term, candidates, _bangumi!, _mox, _bwIndex, _baike, _mdex, DownloadExternalCoverAsync);
                    if (dlg.ShowDialog(this) == DialogResult.OK) chosen = dlg.SelectedCandidate;
                });
                if (chosen == null)
                {
                    Log("INFO", $"《{name}》已跳过（未选择候选）");
                    continue;
                }

                ScrapeResult result;
                if (chosen.Source == BatchMatcher.SourceMox && chosen.MoxId is { Length: > 0 } moxId && _mox != null)
                {
                    var mox = await _mox.GetSeriesAsync(moxId, _cts?.Token ?? CancellationToken.None);
                    SetProgress(done, targets.Count, $"刮削《{name}》← Mox 《{mox.Title}》");
                    result = await _scrape!.ScrapeFromMoxAsync(series.Id, mox,
                        new ScrapeOptions
                        {
                            SyncType = syncType,
                            FetchVolumeData = _settings.FetchVolumeData,
                            DryRun = dry,
                        }, series, null, _cts?.Token ?? CancellationToken.None);
                }
                else if (chosen.Source == BatchMatcher.SourceMangaDex && chosen.MdId is { Length: > 0 } mdId && _mdex != null)
                {
                    var md = await _mdex.GetAsync(mdId, _cts?.Token ?? CancellationToken.None);
                    if (md == null) { Log("ERR ", $"《{name}》MangaDex 条目取回失败"); continue; }
                    SetProgress(done, targets.Count, $"刮削《{name}》← MangaDex 《{md.Title}》");
                    result = await _scrape!.ScrapeFromMangaDexAsync(series.Id, md,
                        new ScrapeOptions
                        {
                            SyncType = syncType,
                            FetchVolumeData = _settings.FetchVolumeData,
                            DryRun = dry,
                        }, series, null, _cts?.Token ?? CancellationToken.None);
                }
                else if (chosen.Source == BatchMatcher.SourceBaike && _baike != null)
                {
                    var bkCard = await _baike.SearchCardAsync(chosen.Title, _cts?.Token ?? CancellationToken.None);
                    if (bkCard == null) { Log("ERR ", $"《{name}》百度百科词条取回失败"); continue; }
                    SetProgress(done, targets.Count, $"刮削《{name}》← 百度百科 《{bkCard.Title}》");
                    result = await _scrape!.ScrapeFromBaikeAsync(series.Id, bkCard,
                        new ScrapeOptions
                        {
                            SyncType = syncType,
                            FetchVolumeData = _settings.FetchVolumeData,
                            DryRun = dry,
                        }, series, null, _cts?.Token ?? CancellationToken.None);
                }
                else if (chosen.Source == BookWalkerIndex.SourceBw)
                {
                    var bw = _bwIndex?.FindById(chosen.BwId ?? "");
                    if (bw == null) { Log("ERR ", $"《{name}》BookWalker 条目丢失"); continue; }
                    SetProgress(done, targets.Count, $"刮削《{name}》← BookWalker 《{bw.Title}》");
                    result = await _scrape!.ScrapeFromBookWalkerAsync(series.Id, bw,
                        new ScrapeOptions
                        {
                            SyncType = syncType,
                            FetchVolumeData = _settings.FetchVolumeData,
                            DryRun = dry,
                        }, series, null, _cts?.Token ?? CancellationToken.None);
                }
                else
                {
                    SetProgress(done, targets.Count, $"刮削《{name}》← subject {chosen.Id}");
                    result = await _scrape!.ScrapeAsync(series.Id, chosen.Id,
                        new ScrapeOptions
                        {
                            SyncType = syncType,
                            FetchVolumeData = _settings.FetchVolumeData,
                            DryRun = dry,
                        }, series, null, _cts?.Token ?? CancellationToken.None);
                }
                if (result.Success) ok++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                Log("ERR ", $"《{series.Name}》刮削异常: {e.Message}");
            }
        }
        Log("INFO", $"刮削完成：{ok}/{targets.Count} 成功");
        RefreshCheckedRows();
        // 真实更新后行内状态要反映新链接/简介 —— 目标少时直接从服务器重拉（多的场景走批量管线）
        if (!dry && targets.Count <= 50)
            await RefreshRowsFromServerAsync(targets.Select(s => s.Id).ToList());
    }

    /// <summary>从服务器重拉系列数据并刷新行显示（覆盖匹配/纠正匹配后让新链接、新状态立即出现在列表里）。</summary>
    private async Task RefreshRowsFromServerAsync(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                var s = await _komga!.GetSeriesAsync(id, _cts?.Token ?? CancellationToken.None);
                _seriesCache[id] = s;
                // 刷新封面状态（本工具刮成功的封面 ScrapeService 已记入状态库；这里兜底查一次服务器）
                if (_state?.GetCover(id) == null)
                {
                    var has = await SeriesHasUserCoverAsync(id, _cts?.Token ?? CancellationToken.None);
                    if (has != null) _state!.SetCover(id, has == true);
                }
                var coverText = CoverCell(id);
                foreach (ListViewItem item in _lvSeries.Items)
                {
                    if (item.Tag is not SeriesDto old || old.Id != id) continue;
                    item.Tag = s;
                    item.Text = string.IsNullOrWhiteSpace(s.Metadata?.Title) ? s.Name : s.Metadata!.Title;
                    item.SubItems[1].Text = Path.GetFileName((s.Url ?? "").TrimEnd('/'));
                    item.SubItems[2].Text = s.BooksCount.ToString();
                    item.SubItems[3].Text = s.Metadata?.Status ?? "";
                    item.SubItems[4].Text = HasBtvLink(s) ? "Btv" : "";
                    item.SubItems[5].Text = coverText;
                    item.SubItems[6].Text = string.IsNullOrEmpty(s.Metadata?.Summary) ? "✗" : "✓";
                    item.SubItems[7].Text = GetBtvSubjectId(s)?.ToString() ?? "";
                    break;
                }
            }
            catch (Exception e)
            {
                Log("WARN", $"刷新系列 {id} 显示失败: {e.Message}");
            }
        }
    }

    private void RefreshCheckedRows()
    {
        // 刮削后行内状态可能变化，简单重载一遍元数据视图
        foreach (ListViewItem item in _lvSeries.Items)
        {
            if (item.Tag is SeriesDto s && _seriesCache.ContainsKey(s.Id))
            {
                var btvId = GetBtvSubjectId(s);
                item.SubItems[4].Text = btvId.HasValue ? "Btv" : "";
                item.SubItems[5].Text = CoverCell(s.Id);
                item.SubItems[7].Text = btvId?.ToString() ?? "";
            }
        }
    }

    // ---- 覆盖匹配（全自动重走匹配流程：不看旧链接/旧元数据，新匹配覆盖旧数据）----

    /// <summary>
    /// 覆盖匹配：对勾选的系列（无勾选则用右键所在的系列）重走一遍自动匹配全链路
    /// （与批量精确匹配同一套源回退：Bangumi 精确 → BookWalker → AI 原名重搜 → Mox → MangaDex → 百度百科），
    /// 但完全无视旧的匹配结果：不跳过已有链接、搜索词只用文件夹名（不看被旧匹配污染的元数据标题），
    /// 命中即以 ReplaceMatch 覆盖写入。全程无弹窗，不再手动选候选；没匹配到的系列保持原样。
    /// </summary>
    private async Task RematchAsync()
    {
        if (!GuardBusy()) return;
        var targets = GetCheckedSeries();
        if (targets.Count == 0)
        {
            var clicked = ClickedSeries();
            if (clicked == null) { MessageBox.Show("请先勾选要覆盖的系列，或右键点在要覆盖的系列上"); return; }
            targets = [clicked];
        }

        var dry = _chkDryRun.Checked;
        var confirm = MessageBox.Show(
            $"对 {targets.Count} 个系列重走自动匹配：完全不看旧链接和旧元数据（按文件夹名搜索），\n" +
            "匹配到的新条目覆盖旧的错误匹配（旧链接剔除、被锁字段解锁重写、全部卷重抓详情）；\n" +
            $"没匹配到的系列保持原样。\n模式：{(dry ? "Dry-run 预览" : "真实更新")}\n继续？",
            "覆盖匹配", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;
        ClearPreview();   // 每次写入任务的预览从头统计

        _batch!.MatchTypePlatform = _settings.MatchType;
        _batch!.DryRun = dry;
        try
        {
            var stats = await RunBusyAsync(ct => _batch!.ReMatchAsync(targets, SyncType.All, ct));
            Log("INFO", $"覆盖匹配完成{(dry ? "（Dry-run 预览，未修改 Komga）" : "")}：{stats}");
            MessageBox.Show($"覆盖匹配完成{(dry ? "（Dry-run 预览）" : "")}：\n" +
                $"覆盖成功 {stats.SuccessCount}，未命中 {stats.FailureCount}，共 {stats.Total}",
                "覆盖匹配", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!dry && stats.SuccessCount > 0)
                await RefreshRowsFromServerAsync(targets.Select(t => t.Id).ToList());
        }
        catch (OperationCanceledException)
        {
            Log("WARN", "覆盖匹配已取消");
        }
    }

    // ---- 纠正匹配（重选候选：候选面板里选条目，或手动输入 Bangumi 链接/ID；新数据覆盖旧匹配）----

    /// <summary>
    /// 纠正匹配：对勾选的系列（无勾选则用右键所在的系列）逐个弹候选框，
    /// 面板里选一个正确条目（或「手动输入 Bangumi 链接/ID…」——手动输入是纠正的子集），
    /// 选中后用「元数据+封面 + ReplaceMatch」刮削 —— 新链接/标题/简介等会覆盖旧的错误匹配。
    /// </summary>
    private async Task CorrectMatchAsync()
    {
        if (!GuardBusy()) return;
        var targets = GetCheckedSeries();
        if (targets.Count == 0)
        {
            var clicked = ClickedSeries();
            if (clicked == null) { MessageBox.Show("请先勾选系列，或右键点在要覆盖的系列上"); return; }
            targets = [clicked];
        }

        var dry = _chkDryRun.Checked;
        var confirm = MessageBox.Show(
            $"为 {targets.Count} 个系列重新选择正确条目并刮削（元数据+封面），用新数据覆盖旧的错误匹配。\n" +
            $"候选面板里也可以「手动输入 Bangumi 链接/ID」。\n" +
            $"模式：{(dry ? "Dry-run 预览" : "真实更新")}\n继续？",
            "纠正匹配", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;
        ClearPreview();   // 每次写入任务的预览从头统计

        SetBusy(true);
        _cts = new CancellationTokenSource();
        var ok = 0;
        var refreshed = new List<string>();
        try
        {
            var done = 0;
            foreach (var series in targets)
            {
                try
                {
                    // 纠正匹配：搜索词优先取文件夹名。文件夹名是用户自己的原始命名（含正确中文名），
                    // 元数据标题可能已被上一次错误匹配污染（比如刮成《ZERO》）——用它去搜会搜回旧条目，选了又按旧链接刮。
                    var name = series.Name;
                    var metaTitle = string.IsNullOrWhiteSpace(series.Metadata?.Title) ? series.Name : series.Metadata!.Title;
                    SetProgress(++done, targets.Count, $"纠正《{name}》");
                    var term = TitleParser.ExtractSeriesTitles(name, 1).FirstOrDefault()
                            ?? TitleParser.ExtractSeriesTitles(metaTitle, 1).FirstOrDefault()
                            ?? metaTitle;
                    if (!string.Equals(term, metaTitle, StringComparison.Ordinal))
                        Log("INFO", $"纠正匹配：按文件夹名搜索\"{term}\"（元数据标题《{metaTitle}》已被旧匹配污染，不作为搜索词）");
                    // 四源候选全量合并：纠正匹配要看到全部来源——bgm 返回噪声候选时，
                    // 百度百科候选也得进弹窗（冷门漫画只有百科有，「空了才查下一个」的短路会把百科挡在门外）。
                    var candidates = new List<SearchCandidate>();
                    try
                    {
                        candidates.AddRange(await _bangumi!.SearchSubjectsAsync(term, _settings.MatchType, 8, _cts.Token));
                    }
                    catch (Exception e) { Log("ERR ", $"《{name}》Bangumi 搜索失败: {e.Message}"); }

                    if (_bwIndex != null)
                    {
                        var bwList = _bwIndex.SearchFuzzy(term, 8);
                        if (bwList.Count > 0)
                        {
                            candidates.AddRange(bwList.Select(b => _bwIndex.ToCandidate(b)));
                            Log("INFO", $"《{name}》BookWalker 本地候选 {bwList.Count} 条");
                        }
                    }
                    if (_mox != null)
                    {
                        try
                        {
                            var moxList = await _mox.SearchAsync(term, 8, _cts.Token);
                            if (moxList.Count > 0)
                            {
                                candidates.AddRange(moxList.Select(m => new SearchCandidate(
                                    0, m.Title, m.Title, m.Author, "", m.CoverUrl, BatchMatcher.SourceMox, null, m.Id)));
                                Log("INFO", $"《{name}》Mox 候选 {moxList.Count} 条");
                            }
                        }
                        catch (Exception e) { Log("WARN", $"《{name}》Mox 搜索失败: {e.Message}"); }
                    }
                    // MangaDex（英文圈图源，免密钥）：候选进弹窗
                    if (_mdex != null)
                    {
                        try
                        {
                            var mdList = await _mdex.SearchAsync(term, 8, _cts.Token);
                            if (mdList.Count > 0)
                            {
                                candidates.AddRange(mdList.Select(m => new SearchCandidate(
                                    0, m.Title, m.OriTitle, "", string.Join(" / ", m.AltTitles.Take(3)),
                                    m.CoverUrl, BatchMatcher.SourceMangaDex, MdId: m.Id)));
                                Log("INFO", $"《{name}》MangaDex 候选 {mdList.Count} 条");
                            }
                        }
                        catch (Exception e) { Log("WARN", $"《{name}》MangaDex 搜索失败: {e.Message}"); }
                    }
                    if (_baike != null)
                    {
                        try
                        {
                            var bkCard = await _baike.SearchCardAsync(term, _cts.Token);
                            if (bkCard != null)
                            {
                                candidates.Add(new SearchCandidate(0, bkCard.Title, bkCard.Field("原版名称"),
                                    bkCard.Field("作者") is { Length: > 0 } ba ? ba : bkCard.Field("作画"),
                                    "", bkCard.ImageUrl.Length > 0 ? BaikeClient.JpegCoverUrl(bkCard.ImageUrl) : null,
                                    BatchMatcher.SourceBaike));
                                Log("INFO", $"《{name}》百度百科候选：《{bkCard.Title}》");
                            }
                        }
                        catch (Exception e) { Log("WARN", $"《{name}》百度百科搜索失败: {e.Message}"); }
                    }
                    if (candidates.Count == 0)
                        Log("INFO", $"《{name}》搜索\"{term}\"无候选，可用面板里的「手动输入 Bangumi 链接/ID…」直接指定条目");

                    SearchCandidate? chosen = null;
                    Invoke(() =>
                    {
                        using var dlg = new MatchDialog(name, term, candidates, _bangumi!, _mox, _bwIndex, _baike, _mdex, DownloadExternalCoverAsync);
                        if (dlg.ShowDialog(this) == DialogResult.OK) chosen = dlg.SelectedCandidate;
                    });
                    if (chosen == null)
                    {
                        Log("INFO", $"《{name}》已跳过（未选择候选）");
                        continue;
                    }

                    ScrapeOptions Opts() => new()
                    {
                        SyncType = SyncType.All,
                        FetchVolumeData = _settings.FetchVolumeData,
                        DryRun = dry,
                        ReplaceMatch = true,   // 覆盖：新匹配顶替旧匹配
                    };
                    ScrapeResult result;
                    if (chosen.Source == BatchMatcher.SourceMox && chosen.MoxId is { Length: > 0 } moxId && _mox != null)
                    {
                        var mox = await _mox.GetSeriesAsync(moxId, _cts.Token);
                        SetProgress(done, targets.Count, $"纠正《{name}》← Mox 《{mox.Title}》");
                        result = await _scrape!.ScrapeFromMoxAsync(series.Id, mox, Opts(), series, null, _cts.Token);
                    }
                    else if (chosen.Source == BatchMatcher.SourceMangaDex && chosen.MdId is { Length: > 0 } mdId && _mdex != null)
                    {
                        var md = await _mdex.GetAsync(mdId, _cts.Token);
                        if (md == null) { Log("ERR ", $"《{name}》MangaDex 条目取回失败"); continue; }
                        SetProgress(done, targets.Count, $"纠正《{name}》← MangaDex 《{md.Title}》");
                        result = await _scrape!.ScrapeFromMangaDexAsync(series.Id, md, Opts(), series, null, _cts.Token);
                    }
                    else if (chosen.Source == BatchMatcher.SourceBaike && _baike != null)
                    {
                        var bkCard = await _baike.SearchCardAsync(chosen.Title, _cts.Token)
                                     ?? await _baike.SearchCardAsync(term, _cts.Token);
                        if (bkCard == null) { Log("ERR ", $"《{name}》百度百科词条取回失败"); continue; }
                        SetProgress(done, targets.Count, $"纠正《{name}》← 百度百科 《{bkCard.Title}》");
                        result = await _scrape!.ScrapeFromBaikeAsync(series.Id, bkCard, Opts(), series, null, _cts.Token);
                    }
                    else if (chosen.Source == BookWalkerIndex.SourceBw && chosen.BwId is { Length: > 0 } bwId && _bwIndex != null)
                    {
                        var bw = _bwIndex.FindById(bwId);
                        if (bw == null) { Log("ERR ", $"《{name}》BookWalker 条目丢失"); continue; }
                        SetProgress(done, targets.Count, $"纠正《{name}》← BookWalker 《{bw.Title}》");
                        result = await _scrape!.ScrapeFromBookWalkerAsync(series.Id, bw, Opts(), series, null, _cts.Token);
                    }
                    else
                    {
                        SetProgress(done, targets.Count, $"纠正《{name}》← subject {chosen.Id}");
                        result = await _scrape!.ScrapeAsync(series.Id, chosen.Id, Opts(), series, null, _cts.Token);
                    }
                    if (result.Success) { ok++; refreshed.Add(series.Id); }
                    else Log("ERR ", $"《{name}》纠正失败: {result.Error}");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    Log("ERR ", $"《{series.Name}》纠正匹配异常: {e.Message}");
                }
            }
            Log("INFO", $"纠正匹配完成：{ok}/{targets.Count} 成功");
            if (!dry && refreshed.Count > 0)
                await RefreshRowsFromServerAsync(refreshed);
            else
                RefreshCheckedRows();
        }
        catch (OperationCanceledException)
        {
            Log("WARN", "纠正匹配已取消");
        }
        finally
        {
            SetBusy(false);
            _cts.Dispose();
            _cts = null;
            TryDeferredRebuild();
        }
    }

    /// <summary>右键所在的行对应的系列（右键列表项时 Focus 可能还在旧行上，用光标位置命中）。</summary>
    private SeriesDto? ClickedSeries()
    {
        var p = _lvSeries.PointToClient(Cursor.Position);
        return (_lvSeries.GetItemAt(p.X, p.Y)?.Tag as SeriesDto) ?? (_lvSeries.FocusedItem?.Tag as SeriesDto);
    }

    private void OpenBtvPage()
    {
        var s = ClickedSeries();
        var url = ExtractSubjectIdText(s!);
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

    private void CopyBtvId()
    {
        var s = ClickedSeries();
        var id = GetBtvSubjectId(s!);
        if (id == null) return;
        try { Clipboard.SetText(id.Value.ToString()); Log("INFO", $"已复制 subject ID：{id}"); }
        catch (Exception e) { Log("ERR ", $"复制失败: {e.Message}"); }
    }

    /// <summary>BookWalker/Mox 候选封面下载（浏览器 UA + 代理，与刮削封面同路；失败返回 null 占位）。</summary>
    private async Task<byte[]?> DownloadExternalCoverAsync(string url, string? _)
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

    // ---- 刮削封面（独立封面覆盖：复用覆盖匹配全链路，只写封面不动元数据）----

    /// <summary>
    /// 刮削封面：对勾选的系列（无勾选 = 整库）按文件夹名重走自动匹配，
    /// 但只上传系列封面 + 卷封面，系列/卷的元数据一个字段都不写。
    /// 用于百科封面 400 等封面失败后的单独补救——元数据已齐，别再动。
    /// </summary>
    private async Task CoverScrapeAsync()
    {
        if (!GuardBusy()) return;
        var targets = GetCheckedSeries();
        var scope = "勾选系列";
        if (targets.Count == 0)
        {
            targets = _lvSeries.Items.Cast<ListViewItem>().Select(i => (SeriesDto)i.Tag!).ToList();
            scope = "整库全部系列";
        }
        if (targets.Count == 0) { MessageBox.Show("没有可刮削的系列"); return; }

        var dry = _chkDryRun.Checked;
        var confirm = MessageBox.Show(
            $"对{scope}共 {targets.Count} 个系列重新匹配并只刮封面（系列封面 + 卷封面）。\n" +
            "元数据一个字段都不会动（已刮好的简介/卷信息保持原样）。\n" +
            $"模式：{(dry ? "Dry-run 预览" : "真实更新")}\n继续？",
            "刮削封面", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;
        ClearPreview();

        _batch!.MatchTypePlatform = _settings.MatchType;
        _batch!.DryRun = dry;
        try
        {
            var stats = await RunBusyAsync(ct => _batch!.ReMatchCoverOnlyAsync(targets, ct));
            Log("INFO", $"封面独立刮削完成{(dry ? "（Dry-run 预览，未修改 Komga）" : "")}：{stats}");
            MessageBox.Show($"封面刮削完成{(dry ? "（Dry-run 预览）" : "")}：\n" +
                $"成功 {stats.SuccessCount}，未命中 {stats.FailureCount}，共 {stats.Total}",
                "刮削封面", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (!dry && stats.SuccessCount > 0)
                await RefreshRowsFromServerAsync(targets.Select(t => t.Id).ToList());
        }
        catch (OperationCanceledException)
        {
            Log("WARN", "封面刮削已取消");
        }
    }

    private async Task RunBatchMatchAsync(List<SeriesDto> targets, string scope)
    {
        var dry = _chkDryRun.Checked;
        var confirm = MessageBox.Show(
            $"对{scope}共 {targets.Count} 个系列自动匹配刮削（Bangumi + 各源自动回退" +
            (_baike != null ? "，含百度百科" : "") + "）。\n" +
            $"模式：{(dry ? "Dry-run 预览" : "真实更新")}\n" +
            "已有 Btv 链接的跳过，失败的加入\"手动匹配\"收藏夹。\n继续？",
            "自动匹配刮削", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;
        ClearPreview();   // 每次写入任务的预览从头统计

        _batch!.MatchTypePlatform = _settings.MatchType;
        _batch!.DryRun = dry;   // 关键：批量匹配以前漏传 Dry-run，勾了预览也会真写库
        var stats = await RunBusyAsync(ct => _batch!.BatchMatchAsync(targets,
            SyncType.All, _settings.ForceRefreshBtv, ct));
        Log("INFO", $"自动匹配刮削完成{(dry ? "（Dry-run 预览，未修改 Komga）" : "")}！{stats}");
        MessageBox.Show($"自动匹配完成{(dry ? "（Dry-run 预览）" : "")}：{stats}", "完成",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---- 文件夹名打标签 ----

    private async Task FolderTagAsync()
    {
        if (!GuardBusy()) return;
        var all = _lvSeries.Items.Cast<ListViewItem>().Select(i => (SeriesDto)i.Tag!).ToList();
        if (all.Count == 0) { MessageBox.Show("请先加载系列"); return; }

        var dry = _chkDryRun.Checked;
        var confirm = MessageBox.Show(
            $"对当前库 {all.Count} 个系列打标：文件夹名标记 → 标签+标题后缀，并补 MACHINE 标签。\n" +
            $"模式：{(dry ? "Dry-run 预览（不修改）" : "真实更新")}\n继续？",
            "打标", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;
        ClearPreview();   // 每次写入任务的预览从头统计

        var count = await RunBusyAsync(ct => _tagger!.TagAsync(all, dry, ct));
        MessageBox.Show($"打标完成：{count} 个系列更新", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---- Mylar 导出 ----

    private async Task MylarExportAsync()
    {
        if (!GuardBusy()) return;
        if (_cmbLibrary.SelectedIndex < 0) { MessageBox.Show("请先测试连接并选择库"); return; }
        var library = _libraries[_cmbLibrary.SelectedIndex];

        using var folderDlg = new FolderBrowserDialog
        {
            Description = "选择 series.json 导出目录（导出时保持库内相对目录结构）",
            ShowNewFolderButton = true,
        };
        if (!string.IsNullOrEmpty(_settings.MylarOutputDir) && Directory.Exists(_settings.MylarOutputDir))
            folderDlg.SelectedPath = _settings.MylarOutputDir;
        if (folderDlg.ShowDialog(this) != DialogResult.OK) return;

        var coverChoice = MessageBox.Show(
            "是否同时下载系列封面（cover.jpg）？\n是 = 下载封面\n否 = 仅导出 JSON",
            "封面", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (coverChoice == DialogResult.Cancel) return;
        var downloadCovers = coverChoice == DialogResult.Yes;

        _settings.MylarOutputDir = folderDlg.SelectedPath;
        SaveSettings();

        var libraryRoot = library.Root;
        var outputDir = folderDlg.SelectedPath;
        var libId = library.Id;
        var covers = downloadCovers;
        await RunBusyAsync(ct => _mylar!.ExportAsync(libId, libraryRoot, covers, outputDir, ct));
        MessageBox.Show("Mylar 导出完成，详见日志", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}

/// <summary>常用正则缓存。</summary>
internal static partial class RegexCache
{
    [System.Text.RegularExpressions.GeneratedRegex(@"subject/(\d+)")]
    public static partial System.Text.RegularExpressions.Regex SubjectIdRegex();
}

/// <summary>ListView 简单列排序。</summary>
internal static class ListViewColumnSorter
{
    private static int _lastColumn = -1;
    private static bool _ascending = true;

    public static void OnColumnClick(ListView lv, int column)
    {
        if (_lastColumn == column) _ascending = !_ascending;
        else { _lastColumn = column; _ascending = true; }
        lv.ListViewItemSorter = new Sorter(column, _ascending);
    }

    private sealed class Sorter(int column, bool ascending) : System.Collections.IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not ListViewItem a || y is not ListViewItem b) return 0;
            var ta = a.SubItems.Count > column ? a.SubItems[column].Text : "";
            var tb = b.SubItems.Count > column ? b.SubItems[column].Text : "";
            if (column == 2 && int.TryParse(ta, out var na) && int.TryParse(tb, out var nb))
                return ascending ? na.CompareTo(nb) : nb.CompareTo(na);
            return ascending ? string.Compare(ta, tb, StringComparison.CurrentCulture) :
                               string.Compare(tb, ta, StringComparison.CurrentCulture);
        }
    }
}