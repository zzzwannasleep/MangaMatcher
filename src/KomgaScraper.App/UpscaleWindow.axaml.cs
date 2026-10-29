using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KomgaScraper.Core;
using KomgaScraper.Core.Upscale;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using ISImage = SixLabors.ImageSharp.Image;

namespace KomgaScraper.App;

/// <summary>超分页列表的一行（页序可变，序号随移动重排）。</summary>
public sealed class UpscalePageVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ExtractedPage Page { get; }
    private int _index;

    public UpscalePageVm(ExtractedPage page, int index)
    {
        Page = page;
        _index = index;
    }

    public int DisplayIndex
    {
        get => _index;
        set
        {
            if (_index == value) return;
            _index = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IndexText)));
        }
    }

    public string IndexText => _index.ToString();
    public string Name => Page.DisplayName;
    public string SizeText => ComicArchiveReader.FormatBytes(Page.Bytes);
    public string DimsText => Page.Width > 0 ? $"{Page.Width}×{Page.Height}" : "—";
}

/// <summary>GPU 下拉项：Id=null 表示「自动（优先独显）」。</summary>
public sealed record GpuChoice(int? Id, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 图片超分窗口：导入压缩包 → 选模型/设置 → 核对页序 + 填标题 → GPU 超分 → WebP q95 → 带 ComicInfo 的 CBZ。
/// 引擎与模型不随软件分发，首次使用需一键下载（或手动导入 zip / 自备 ncnn 模型）。
/// </summary>
public sealed partial class UpscaleWindow : Window
{
    private static readonly (string Code, string Label)[] Languages =
    [
        ("zh", "中文（zh）"), ("ja", "日文（ja）"), ("en", "英文（en）"), ("zh-TW", "繁体中文（zh-TW）"),
    ];

    private static readonly string[] TileItems = ["0（自动）", "64", "128", "200", "256", "320"];

    private readonly AppSettings _settings;
    private readonly Action<string, string> _log;
    private readonly IAppLogger _logger;
    private readonly UpscaleRuntime _runtime = new();
    private readonly string? _initialArchive;
    private readonly ObservableCollection<UpscalePageVm> _pages = [];
    private readonly List<UpscaleModelInfo> _models = [];
    private readonly List<int> _scaleValues = [];
    private readonly List<GpuChoice> _gpuChoices = [];

    private GpuReport? _gpu;
    private IReadOnlyList<VulkanDevice> _devices = [];
    private int? _autoGpuIndex;
    private int _preferredScale;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _ready;
    private bool _gpuAcknowledged;
    private bool _closingConfirmed;
    private bool _closeInProgress;
    private bool _forceClose;
    private string? _sessionDir;
    private string _archivePath = "";
    private ImageSortMode _sortMode = ImageSortMode.NaturalName;
    private bool _manualOrder;
    private Bitmap? _preview;
    private string _previewKey = "";
    private CancellationTokenSource? _previewCts;

    // ---- 关闭后由主界面保存的偏好 ----
    public bool Changed { get; private set; }
    public string LastModelId { get; private set; }
    public int LastScale { get; private set; }
    public int LastTile { get; private set; }
    public bool LastTta { get; private set; }
    public int LastGpuId { get; private set; } = -1;
    public int LastMaxLongSide { get; private set; }
    public string LastOutputDir { get; private set; } = "";
    public bool LastSkipFailed { get; private set; }
    public string LastLanguage { get; private set; } = "zh";

    public UpscaleWindow(AppSettings settings, Action<string, string> log, string? initialArchive = null)
    {
        InitializeComponent();
        _settings = settings;
        _log = log;
        _logger = new WindowLogger(log);
        _initialArchive = string.IsNullOrWhiteSpace(initialArchive) ? null : initialArchive;

        LastModelId = settings.UpscaleModel;
        LastScale = settings.UpscaleScale;
        LastTile = settings.UpscaleTile;
        LastTta = settings.UpscaleTta;
        LastGpuId = settings.UpscaleGpuId;
        LastMaxLongSide = settings.UpscaleMaxLongSide;
        LastOutputDir = settings.UpscaleOutputDir;
        LastSkipFailed = settings.UpscaleSkipFailed;
        LastLanguage = settings.UpscaleLanguage;

        LstPages.ItemsSource = _pages;
        CmbTile.ItemsSource = TileItems;
        CmbTile.SelectedIndex = Math.Max(0, Array.IndexOf(TileItems, settings.UpscaleTile.ToString()));
        if (!TileItems.Contains(settings.UpscaleTile.ToString())) CmbTile.SelectedIndex = 0;

        CmbLanguage.ItemsSource = Languages.Select(l => l.Label).ToList();
        var langIndex = Array.FindIndex(Languages, l => l.Code == settings.UpscaleLanguage);
        CmbLanguage.SelectedIndex = langIndex >= 0 ? langIndex : 0;

        TxtMaxLongSide.Text = settings.UpscaleMaxLongSide > 0
            ? Math.Clamp(settings.UpscaleMaxLongSide, 1, WebpTranscoder.MaxWebpSide).ToString(CultureInfo.InvariantCulture)
            : "0";
        TxtOutputDir.Text = settings.UpscaleOutputDir;
        ChkTta.IsChecked = settings.UpscaleTta;
        ChkSkipFailed.IsChecked = settings.UpscaleSkipFailed;
        _preferredScale = settings.UpscaleScale;

        BtnPickArchive.Click += async (_, _) => await PickArchiveAsync();
        BtnDownload.Click += async (_, _) => await DownloadPackAsync();
        BtnDownloadPage.Click += (_, _) => OpenDownloadPage();
        BtnImportPack.Click += async (_, _) => await ImportPackAsync();
        BtnImportModel.Click += async (_, _) => await ImportModelAsync();
        BtnRemoveModel.Click += async (_, _) => await RemoveModelAsync();
        BtnModelDb.Click += (_, _) =>
        {
            if (!UpscaleRuntime.OpenUrl(UpscaleRuntime.ModelDatabaseUrl))
                _log("WARN", $"[超分] 无法打开浏览器，请手动访问：{UpscaleRuntime.ModelDatabaseUrl}");
        };
        BtnOutputDir.Click += async (_, _) => await PickOutputDirAsync();
        BtnRecheckGpu.Click += async (_, _) => await RecheckGpuAsync();
        BtnUp.Click += (_, _) => MoveSelected(-1);
        BtnDown.Click += (_, _) => MoveSelected(1);
        BtnReverse.Click += (_, _) => ReverseOrder();
        BtnSortName.Click += (_, _) => ApplySort(ImageSortMode.NaturalName);
        BtnSortArchive.Click += (_, _) => ApplySort(ImageSortMode.ArchiveOrder);
        BtnStart.Click += async (_, _) => await StartAsync();
        BtnCancelTask.Click += (_, _) => CancelTask();
        BtnClose.Click += (_, _) => Close();
        CmbModel.SelectionChanged += (_, _) => OnModelChanged();
        CmbScale.SelectionChanged += (_, _) => { _preferredScale = SelectedScale; UpdateEstimate(); RefreshModelState(); };
        LstPages.SelectionChanged += async (_, _) => await UpdatePreviewAsync();
        TxtTitle.TextChanged += (_, _) => UpdateStartState();
        TxtMaxLongSide.TextChanged += (_, _) => UpdateEstimate();
        Closing += OnClosing;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);

        _ready = true;
        RefreshModels(settings.UpscaleModel);
        SetBusy(false, indeterminate: false, status: null);
        UpdateStartState();
        Loaded += async (_, _) =>
        {
            await InitializeAsync();
            if (_initialArchive is { } startupArchive && File.Exists(startupArchive))
                await LoadArchiveAsync(startupArchive);
        };
    }

    // ================= 初始化 =================

    private async Task InitializeAsync()
    {
        try
        {
            await RefreshGpuAsync();
            await RefreshDevicesAsync();
            RefreshModelState();
            UpdateStartState();
        }
        catch (Exception e)
        {
            _log("WARN", $"[超分] 初始化检测失败：{e.Message}");
        }
    }

    private async Task RefreshGpuAsync()
    {
        TxtGpu.Text = "显卡：检测中…";
        var report = await Task.Run(GpuDetector.Detect);
        if (!IsVisible) return;
        _gpu = report;
        TxtGpu.Text = report.Summary;
        TxtGpuHint.Text = report.Advice;
        TxtGpuHint.IsVisible = report.NeedsWarning || report.Best == GpuClass.Unknown;
        _log("INFO", $"[超分] {report.Summary}");
        if (report.NeedsWarning) _log("WARN", "[超分] 未检测到独立显卡，超分会很慢。");
        RebuildGpuChoices();
    }

    private async Task RefreshDevicesAsync()
    {
        if (!_runtime.EngineReady)
        {
            _devices = [];
            RebuildGpuChoices();
            return;
        }

        var devices = await VulkanProbe.ProbeAsync(_runtime.ExePath);
        if (!IsVisible) return;
        _devices = devices;
        if (devices.Count == 0)
            _log("WARN", "[超分] 未能探测到 Vulkan 设备（驱动异常？将使用引擎默认显卡）。");
        else
            _log("INFO", "[超分] Vulkan 设备：" + string.Join("；", devices.Select(d => $"GPU {d.Index} {d.Name}")));
        RebuildGpuChoices();
    }

    private async Task RecheckGpuAsync()
    {
        if (_busy) return;
        await RefreshGpuAsync();
        await RefreshDevicesAsync();
        RefreshModelState();
    }

    private void RebuildGpuChoices()
    {
        var recommended = VulkanProbe.FindBestDevice(_devices, _gpu?.PreferredName);
        _autoGpuIndex = recommended ?? (_devices.Count > 0 ? _devices[0].Index : null);
        var previous = CmbGpu.SelectedItem as GpuChoice;

        _gpuChoices.Clear();
        var autoLabel = recommended is { } autoIndex
            ? $"自动（优先独显 GPU {autoIndex}）"
            : _devices.Count == 0
                ? "自动（引擎默认）"
                : $"自动（GPU {_autoGpuIndex}）";
        _gpuChoices.Add(new GpuChoice(null, autoLabel));
        foreach (var d in _devices)
            _gpuChoices.Add(new GpuChoice(d.Index, $"GPU {d.Index} · {d.Name}"));

        CmbGpu.ItemsSource = null;
        CmbGpu.ItemsSource = _gpuChoices;
        var index = 0;
        if (previous?.Id is { } previousId)
        {
            // 重新检测/重装引擎时保留用户本次手动选的设备，不要悄悄切回「自动」
            var found = _gpuChoices.FindIndex(c => c.Id == previousId);
            if (found >= 0) index = found;
        }
        else if (previous is null && _settings.UpscaleGpuId >= 0)
        {
            var found = _gpuChoices.FindIndex(c => c.Id == _settings.UpscaleGpuId);
            if (found >= 0) index = found;
        }
        CmbGpu.SelectedIndex = index;
    }

    // ================= 模型与设置 =================

    private UpscaleModelInfo? SelectedModel => CmbModel.SelectedItem as UpscaleModelInfo;

    private int SelectedScale =>
        CmbScale.SelectedItem is string s && int.TryParse(s.TrimEnd('x'), out var v)
            ? v
            : _scaleValues.Count > 0 ? _scaleValues[0] : 4;

    private int SelectedTile
    {
        get
        {
            if (CmbTile.SelectedItem is not string s) return 0;
            var digits = new string(s.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, out var v) ? v : 0;
        }
    }

    private int? SelectedGpuIndex
    {
        get
        {
            if (CmbGpu.SelectedItem is GpuChoice { Id: int id }) return id;
            return _autoGpuIndex;
        }
    }

    private string SelectedLanguageCode =>
        CmbLanguage.SelectedIndex >= 0 && CmbLanguage.SelectedIndex < Languages.Length
            ? Languages[CmbLanguage.SelectedIndex].Code
            : "zh";

    private void RefreshModels(string? selectId = null)
    {
        var wanted = selectId ?? SelectedModel?.Id ?? _settings.UpscaleModel;
        _models.Clear();
        _models.AddRange(_runtime.AllModels());
        CmbModel.ItemsSource = null;
        CmbModel.ItemsSource = _models;
        var index = _models.FindIndex(m => m.Id == wanted);
        CmbModel.SelectedIndex = index >= 0 ? index : 0;
        OnModelChanged();
    }

    private void OnModelChanged()
    {
        if (!_ready) return;
        var model = SelectedModel;
        if (model is null) return;

        _scaleValues.Clear();
        _scaleValues.AddRange(model.Scales);
        var wanted = _preferredScale;
        CmbScale.ItemsSource = null;
        CmbScale.ItemsSource = _scaleValues.Select(s => $"{s}x").ToList();
        var scaleIndex = _scaleValues.IndexOf(wanted);
        CmbScale.SelectedIndex = scaleIndex >= 0 ? scaleIndex : 0;

        TxtModelDesc.Text = model.Description +
                            $"\n原生倍率：{model.ScalesText} · 模型体积：{ComicArchiveReader.FormatBytes(model.ApproxBytes)}";
        BtnRemoveModel.IsVisible = model.Kind == UpscaleModelKind.Custom;
        BtnRemoveModel.IsEnabled = model.Kind == UpscaleModelKind.Custom;
        UpdateEstimate();
        RefreshModelState();
    }

    private void RefreshModelState()
    {
        if (!UpscaleRuntime.PlatformSupported)
        {
            TxtModelState.Text = "当前系统暂不支持图片超分（引擎仅有 Windows 便携版）；请使用 Windows 版。";
            TxtModelState.Foreground = ThemeBrush("WarnBrush") ?? TxtModelState.Foreground;
            BtnDownload.Content = "仅 Windows 支持";
            BtnDownload.IsEnabled = false;
            BtnImportPack.IsEnabled = false;
            BtnImportModel.IsEnabled = false;
            UpdateStartState();
            return;
        }

        var model = SelectedModel;
        var scale = SelectedScale;
        if (model is null)
        {
            TxtModelState.Text = "没有可用模型";
            TxtModelState.Foreground = ThemeBrush("ErrorBrush") ?? TxtModelState.Foreground;
            UpdateStartState();
            return;
        }

        var engine = _runtime.EngineReady;
        var engineText = engine ? $"引擎：已安装（{_runtime.InstalledVersion}）" : "引擎：未安装";
        string modelText;
        if (!engine) modelText = "模型：随引擎包一起下载";
        else if (!model.SupportsScale(scale)) modelText = $"模型：该模型不支持 {scale}x";
        else
        {
            var missing = _runtime.MissingModelFiles(model, scale);
            modelText = missing.Count == 0
                ? $"模型：已就绪（{scale}x）"
                : $"模型：缺少 {string.Join("、", missing)}";
        }

        TxtModelState.Text = $"{engineText}；{modelText}";
        TxtModelState.Foreground = ThemeBrush(engine && model.SupportsScale(scale) && _runtime.IsModelReady(model, scale)
            ? "TextSecondaryBrush"
            : "WarnBrush") ?? TxtModelState.Foreground;
        BtnDownload.Content = engine ? "重新下载引擎包" : "一键下载引擎 + 模型";
        // 已安装时把「重新下载」降为次级按钮，避免抢「开始超分」的视觉焦点
        BtnDownload.Classes.Clear();
        BtnDownload.Classes.Add(engine ? "app" : "primary");
        UpdateStartState();
    }

    /// <summary>取主题画刷；资源缺失时 FindResource 会返回 UnsetValue（不是 null），必须用 as 兜底。</summary>
    private Avalonia.Media.IBrush? ThemeBrush(string key) => this.FindResource(key) as Avalonia.Media.IBrush;

    private void UpdateEstimate()
    {
        if (_pages.Count == 0 || SelectedModel is null)
        {
            TxtSizeEstimate.Text = "";
            return;
        }

        var page = _pages[0].Page;
        var (w, h) = UpscaleJob.EffectiveOutputSize(page.Width, page.Height, SelectedScale, ParseInt(TxtMaxLongSide.Text));
        var warning = Math.Max(w, h) > WebpTranscoder.MaxWebpSide
            ? $"　⚠ 超过 WebP 单边上限 {WebpTranscoder.MaxWebpSide}，请设置长边上限"
            : "";
        TxtSizeEstimate.Text = page.Width > 0
            ? $"预计第 1 页输出：{w}×{h}（WebP q95）{warning}；整部体积取决于页数与画面复杂度"
            : "";
    }

    private void UpdateStartState()
    {
        var model = SelectedModel;
        var can = !_busy
                  && _pages.Count > 0
                  && model is not null
                  && _runtime.EngineReady
                  && model.SupportsScale(SelectedScale)
                  && _runtime.IsModelReady(model, SelectedScale)
                  && (TxtTitle.Text?.Trim().Length ?? 0) > 0;
        BtnStart.IsEnabled = can;
    }

    // ================= 导入压缩包 =================

    private async Task PickArchiveAsync()
    {
        if (_busy) return;
        var path = await Ui.PickFileAsync(this, "选择漫画压缩包",
            ["*.zip", "*.cbz", "*.rar", "*.cbr", "*.7z", "*.cb7", "*.tar"]);
        if (path is null) return;
        await LoadArchiveAsync(path);
    }

    private async Task LoadArchiveAsync(string path)
    {
        if (_busy) return;
        path = Path.GetFullPath(path);
        var dir = Path.Combine(Path.GetTempPath(), "KomgaScraper-upscale-session", Guid.NewGuid().ToString("N"));
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        // 每次导入都从「文件名自然排序」开始；解压时就按这个顺序，避免列表与解压顺序不一致
        _sortMode = ImageSortMode.NaturalName;
        _manualOrder = false;
        SetBusy(true, indeterminate: true, status: "正在解压压缩包…");
        Bar.IsIndeterminate = true;
        TxtArchiveInfo.Text = "解压中…";
        try
        {
            var pages = await ComicArchiveReader.ExtractAsync(path, dir, _sortMode, _logger, _cts.Token,
                (done, total, name) => Dispatcher.UIThread.Post(() =>
                {
                    if (_busy) TxtStatus.Text = $"解压中：{done}/{total}  {name}";
                }));

            if (!IsVisible)
            {
                TryDeleteDir(dir);
                return;
            }

            CleanupSession();
            _sessionDir = dir;
            _archivePath = path;

            _pages.Clear();
            for (var i = 0; i < pages.Count; i++) _pages.Add(new UpscalePageVm(pages[i], i + 1));

            TxtArchive.Text = path;
            var maxW = pages.Max(p => p.Width);
            var maxH = pages.Max(p => p.Height);
            TxtArchiveInfo.Text =
                $"{pages.Count} 页 · {ComicArchiveReader.FormatBytes(pages.Sum(p => p.Bytes))} · " +
                $"最大 {maxW}×{maxH} · 已按文件名自然排序（可改）";
            TxtPagesSummary.Text = $"③ 检查图片顺序（共 {pages.Count} 页）";

            // 每次导入都重置为本压缩包的标题并清空册级元信息，避免沿用上一本导致错档
            TxtTitle.Text = ImageOrder.SanitizeFileName(Path.GetFileNameWithoutExtension(path));
            TxtSeries.Text = "";
            TxtVolume.Text = "";
            TxtWriter.Text = "";
            TxtSummary.Text = "";

            LstPages.SelectedIndex = 0;
            UpdateOrderButtons(true);
            UpdateOrderHint();
            UpdateEstimate();
            _log("INFO", $"[超分] 已导入 {path}：{pages.Count} 页");
            TxtStatus.Text = $"已导入 {pages.Count} 页；核对页序与标题后点「开始超分」。";

            SetBusy(false, indeterminate: false, status: null);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDir(dir);
            ResetAfterImportFailure();
            TxtArchiveInfo.Text = "解压已取消";
        }
        catch (Exception e)
        {
            TryDeleteDir(dir);
            ResetAfterImportFailure();
            TxtArchiveInfo.Text = $"导入失败：{e.Message}";
            _log("ERR ", $"[超分] 导入失败：{e.Message}");
            if (IsVisible) await Ui.ErrorAsync(this, e.Message, "导入失败");
        }
        finally
        {
            Bar.IsIndeterminate = false;
            SetBusy(false, indeterminate: false, status: null);
            UpdateStartState();
        }
    }

    // ================= 页序 =================

    private void UpdateOrderButtons(bool hasPages)
    {
        var on = hasPages && !_busy;
        BtnUp.IsEnabled = on;
        BtnDown.IsEnabled = on;
        BtnReverse.IsEnabled = on;
        BtnSortName.IsEnabled = on;
        BtnSortArchive.IsEnabled = on;
    }

    private void UpdateOrderHint()
    {
        TxtOrderHint.Text = _manualOrder
            ? "当前：手动顺序（上移/下移调整，导出按这个顺序）。点「按文件名重排」可恢复自动排序。"
            : _sortMode == ImageSortMode.NaturalName
                ? "当前：按文件名自然排序（2 在 10 之前）。可上移/下移手动调整，或改成压缩包内原始顺序。"
                : "当前：按压缩包内原始顺序。若与阅读顺序不符，建议用「按文件名重排」。";
    }

    private void Renumber()
    {
        for (var i = 0; i < _pages.Count; i++) _pages[i].DisplayIndex = i + 1;
    }

    private void MoveSelected(int delta)
    {
        if (_busy) return;
        var from = LstPages.SelectedIndex;
        var to = from + delta;
        if (from < 0 || to < 0 || to >= _pages.Count) return;
        _pages.Move(from, to);
        Renumber();
        LstPages.SelectedIndex = to;
        _manualOrder = true;
        UpdateOrderHint();
    }

    private void ReverseOrder()
    {
        if (_busy || _pages.Count == 0) return;
        var current = (LstPages.SelectedItem as UpscalePageVm)?.Page.RelativePath;
        var reversed = _pages.Reverse().ToList();
        _pages.Clear();
        foreach (var vm in reversed) _pages.Add(vm);
        Renumber();
        _manualOrder = true;
        var index = reversed.FindIndex(v => v.Page.RelativePath == current);
        LstPages.SelectedIndex = index >= 0 ? index : 0;
        UpdateOrderHint();
    }

    private void ApplySort(ImageSortMode mode)
    {
        if (_busy || _pages.Count == 0) return;
        var current = (LstPages.SelectedItem as UpscalePageVm)?.Page.RelativePath;
        _sortMode = mode;
        var sorted = ImageOrder.Sort(_pages.Select(v => v.Page), p => p.RelativePath, mode);
        _pages.Clear();
        for (var i = 0; i < sorted.Count; i++) _pages.Add(new UpscalePageVm(sorted[i], i + 1));
        _manualOrder = false;
        var index = sorted.FindIndex(p => p.RelativePath == current);
        LstPages.SelectedIndex = index >= 0 ? index : 0;
        UpdateOrderHint();
    }

    // ================= 预览 =================

    private async Task UpdatePreviewAsync()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = new CancellationTokenSource();
        var token = _previewCts.Token;

        if (LstPages.SelectedItem is not UpscalePageVm vm)
        {
            ClearPreview();
            TxtPreviewNote.IsVisible = false;
            return;
        }

        var key = vm.Page.FilePath;
        if (key == _previewKey) return;
        _previewKey = key;

        // 超大图只解码缩略图也可能吃掉几百 MB：直接跳过预览，避免点一下就把内存打爆
        if ((long)vm.Page.Width * vm.Page.Height > 40_000_000)
        {
            ClearPreview();
            TxtPreviewNote.IsVisible = true;
            return;
        }
        TxtPreviewNote.IsVisible = false;

        Bitmap? bmp;
        try
        {
            bmp = await Task.Run(() => RenderPreview(key, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (token.IsCancellationRequested || !IsVisible || _previewKey != key)
        {
            bmp?.Dispose();
            return;
        }
        var old = _preview;
        _preview = bmp;
        PreviewImage.Source = bmp;
        old?.Dispose();
    }

    private void ClearPreview()
    {
        _previewKey = "";
        var old = _preview;
        _preview = null;
        PreviewImage.Source = null;
        old?.Dispose();
    }

    private static Bitmap? RenderPreview(string path, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var img = ISImage.Load(path);
            ct.ThrowIfCancellationRequested();
            img.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new SixLabors.ImageSharp.Size(280, 440),
                Sampler = KnownResamplers.Lanczos3,
            }));
            ct.ThrowIfCancellationRequested();
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            ms.Position = 0;
            return new Bitmap(ms);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>导入失败/取消后把界面恢复到「没有导入」状态，避免用户误导出上一本。</summary>
    private void ResetAfterImportFailure()
    {
        CleanupSession();
        ClearPreview();
        TxtPreviewNote.IsVisible = false;
        _pages.Clear();
        _archivePath = "";
        TxtArchive.Text = "";
        TxtPagesSummary.Text = "③ 检查图片顺序（尚未导入压缩包）";
        TxtStatus.Text = "导入失败，请换一个压缩包重试。";
        TxtSizeEstimate.Text = "";
        UpdateOrderButtons(false);
        UpdateStartState();
    }

    // ================= 引擎/模型安装 =================

    private void OpenDownloadPage()
    {
        var ok = UpscaleRuntime.OpenUrl(UpscaleRuntime.DownloadPageUrl);
        if (!ok)
            _log("WARN", $"[超分] 无法打开浏览器，请手动访问：{UpscaleRuntime.DownloadPageUrl}");
    }

    private async Task DownloadPackAsync()
    {
        if (_busy) return;
        if (!await Ui.MessageAsync(this,
                "将下载 Real-ESRGAN 超分引擎 + 3 个官方模型（约 43 MB）。\n\n" +
                $"模型不会打进软件安装包，下载后保存在：\n{_runtime.Root}\n\n继续？",
                "下载超分引擎")) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetBusy(true, indeterminate: false, status: "正在下载引擎包…");
        Bar.IsVisible = false;
        DownloadBar.IsVisible = true;
        DownloadBar.Value = 0;
        TxtDownload.Text = "0%";
        var importAfterFailure = false;
        try
        {
            var progress = new Progress<UpscaleDownloadProgress>(p =>
            {
                DownloadBar.Value = p.Fraction;
                TxtDownload.Text = $"{ComicArchiveReader.FormatBytes(p.Received)} / " +
                                   $"{ComicArchiveReader.FormatBytes(p.Total)}（{p.Fraction:P0}）";
            });
            await _runtime.DownloadAndInstallAsync(progress, _settings.Proxy, _logger, _cts.Token);
            TxtDownload.Text = "安装完成";
            await RefreshDevicesAsync();
            RefreshModelState();
            UpdateStartState();
            if (IsVisible) await Ui.InfoAsync(this, "超分引擎与 3 个官方模型已安装完成。", "下载完成");
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "下载已取消";
            _log("WARN", "[超分] 下载已取消");
        }
        catch (UpscalePackException e)
        {
            _log("ERR ", $"[超分] {e.Message}");
            TxtDownload.Text = "下载失败";
            if (!IsVisible) return;
            var choice = await Ui.ChoiceAsync(this,
                e.Message + "\n\n可以打开下载页手动下载 zip（约 43 MB），再回来点「导入引擎包」。",
                "下载失败",
                ("打开下载页", "page"), ("导入已下载的 zip", "import"), ("取消", "cancel"));
            if (choice == "page") OpenDownloadPage();
            else if (choice == "import") importAfterFailure = true;
        }
        catch (Exception e)
        {
            _log("ERR ", $"[超分] 下载失败：{e.Message}");
            if (IsVisible) await Ui.ErrorAsync(this, e.Message, "下载失败");
        }
        finally
        {
            DownloadBar.IsVisible = false;
            SetBusy(false, indeterminate: false, status: null);
            RefreshModelState();
            UpdateStartState();
        }

        // 放到 finally 之后再导入：此时 _busy 已复位，否则 ImportPackAsync 会直接 return
        if (importAfterFailure) await ImportPackAsync();
    }

    private async Task ImportPackAsync()
    {
        if (_busy) return;
        var zip = await Ui.PickFileAsync(this, "选择 Real-ESRGAN Windows 便携包 zip", ["*.zip"]);
        if (zip is null) return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetBusy(true, indeterminate: true, status: "正在安装引擎包…");
        Bar.IsVisible = false;
        try
        {
            await _runtime.InstallPackFromZipAsync(zip, _logger, _cts.Token);
            await RefreshDevicesAsync();
            RefreshModelState();
            if (IsVisible) await Ui.InfoAsync(this, "引擎包安装完成。", "安装完成");
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "安装已取消";
        }
        catch (Exception e)
        {
            _log("ERR ", $"[超分] 安装失败：{e.Message}");
            if (IsVisible) await Ui.ErrorAsync(this, e.Message, "安装失败");
        }
        finally
        {
            SetBusy(false, indeterminate: false, status: null);
            RefreshModelState();
            UpdateStartState();
        }
    }

    private async Task ImportModelAsync()
    {
        if (_busy) return;
        var param = await Ui.PickFileAsync(this, "选择 ncnn 模型的 .param 文件", ["*.param"]);
        if (param is null) return;
        var stem = Path.GetFileNameWithoutExtension(param);
        var bin = Path.Combine(Path.GetDirectoryName(param)!, stem + ".bin");
        if (!File.Exists(bin))
        {
            bin = await Ui.PickFileAsync(this, "找不到同名 .bin，请选择对应的权重文件", ["*.bin"]) ?? "";
            if (bin.Length == 0) return;
        }

        var scaleText = await Ui.ChoiceAsync(this,
            $"模型「{stem}」的**原生**放大倍率是多少？\n选错会导致输出尺寸不对（多数 Real-ESRGAN 模型是 4x）。",
            "模型倍率", ("2x", "2"), ("3x", "3"), ("4x", "4"), ("取消", "cancel"));
        if (scaleText is null or "cancel" || !int.TryParse(scaleText, out var scale)) return;

        try
        {
            var info = await _runtime.ImportCustomModelAsync(param, bin, stem, scale);
            _log("INFO", $"[超分] 已导入自定义模型：{info.DisplayName}（{info.Id}，{scale}x）");
            RefreshModels(info.Id);
            if (IsVisible) await Ui.InfoAsync(this,
                $"已导入模型「{info.DisplayName}」，可在模型下拉框里选择。", "导入成功");
        }
        catch (Exception e)
        {
            _log("ERR ", $"[超分] 导入模型失败：{e.Message}");
            if (IsVisible) await Ui.ErrorAsync(this, e.Message, "导入模型失败");
        }
    }

    private async Task RemoveModelAsync()
    {
        if (_busy) return;
        if (SelectedModel is not { Kind: UpscaleModelKind.Custom } model) return;
        if (!await Ui.MessageAsync(this, $"删除自定义模型「{model.DisplayName}」？\n只删模型文件，不影响已导出的 CBZ。",
                "删除模型", error: true)) return;
        if (!_runtime.RemoveCustomModel(model.Id))
        {
            await Ui.ErrorAsync(this, "删除失败（文件可能被占用）。", "删除模型失败");
            return;
        }
        _log("INFO", $"[超分] 已删除自定义模型：{model.DisplayName}");
        RefreshModels(UpscaleModelCatalog.SpecializedId);
    }

    private async Task PickOutputDirAsync()
    {
        if (_busy) return;
        var dir = await Ui.PickFolderAsync(this);
        if (dir is null) return;
        TxtOutputDir.Text = dir;
    }

    // ================= 执行超分 =================

    private async Task StartAsync()
    {
        if (_busy) return;
        if (!UpscaleRuntime.PlatformSupported)
        {
            await Ui.ErrorAsync(this, "图片超分目前只支持 Windows（引擎是 Real-ESRGAN 的 Windows 便携版）。", "无法开始");
            return;
        }
        var model = SelectedModel;
        if (model is null)
        {
            await Ui.ErrorAsync(this, "没有可用模型。", "无法开始");
            return;
        }
        if (!_runtime.EngineReady)
        {
            await Ui.MessageAsync(this,
                "超分引擎还没安装。请先点「一键下载引擎 + 模型」，或从下载页手动下载后点「导入引擎包」。",
                "无法开始", okOnly: true);
            return;
        }
        if (!_runtime.IsModelReady(model, SelectedScale))
        {
            await Ui.ErrorAsync(this,
                $"模型「{model.DisplayName}」文件缺失。请重新下载引擎包，或导入自己的模型。", "无法开始");
            return;
        }
        var title = TxtTitle.Text?.Trim() ?? "";
        if (title.Length == 0)
        {
            await Ui.MessageAsync(this, "请先填写标题（会写进 ComicInfo.xml，也用作文件名）。", "缺少标题", okOnly: true);
            TxtTitle.Focus();
            return;
        }

        // 核显/无显卡提醒：用户确认后才继续（若检测还没跑完，先补一次，绝不能悄悄跳过提醒）
        if (_gpu is null) await RefreshGpuAsync();
        if (_gpu is { NeedsWarning: true } && !_gpuAcknowledged)
        {
            if (!await Ui.MessageAsync(this, _gpu.Advice, "显卡提醒", error: true)) return;
            _gpuAcknowledged = true;
            _log("WARN", "[超分] 用户确认在无独显/核显环境继续超分。");
        }

        var outputDir = ResolveOutputDir();
        try
        {
            Directory.CreateDirectory(outputDir);
        }
        catch (Exception e)
        {
            await Ui.ErrorAsync(this, $"输出目录不可用：{e.Message}", "无法开始");
            return;
        }

        var targetName = ImageOrder.SanitizeFileName(title);
        var target = Path.Combine(outputDir, targetName + ".cbz");
        // 标题默认取自压缩包名，输出会和源文件同名 → 自动改成「xxx 超分.cbz」，避免用户误覆盖原档
        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(_archivePath), StringComparison.OrdinalIgnoreCase))
            target = Path.Combine(outputDir, targetName + " 超分.cbz");

        var overwrite = false;
        if (File.Exists(target))
        {
            var choice = await Ui.ChoiceAsync(this,
                $"输出文件已存在：\n{target}\n\n覆盖它，还是自动改名为「{Path.GetFileNameWithoutExtension(target)} (2).cbz」？",
                "文件已存在", ("覆盖", "overwrite"), ("自动改名", "rename"), ("取消", "cancel"));
            if (choice is null or "cancel") return;
            overwrite = choice == "overwrite";
        }

        var options = BuildOptions(overwrite, target);
        var inputs = _pages
            .Select(vm => new UpscalePageInput(vm.Page.FilePath, vm.Name, vm.Page.Bytes, vm.Page.Width, vm.Page.Height))
            .ToList();
        var meta = new UpscaleMetadata(
            title,
            TxtSeries.Text?.Trim() ?? "",
            TxtWriter.Text?.Trim() ?? "",
            TxtSummary.Text?.Trim() ?? "",
            SelectedLanguageCode,
            TxtVolume.Text?.Trim() ?? "");

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        SetBusy(true, indeterminate: false, status: "准备超分…");
        Bar.IsIndeterminate = false;
        Bar.Minimum = 0;
        Bar.Maximum = Math.Max(1, inputs.Count);
        Bar.Value = 0;
        TxtProgress.Text = $"0/{inputs.Count}";

        try
        {
            var job = new UpscaleJob(_runtime, new ProcessUpscalerRunner(), _logger);
            var progress = new Progress<UpscaleProgressInfo>(p =>
            {
                Bar.Maximum = Math.Max(1, p.Total);
                Bar.Value = Math.Min(p.Done, p.Total);
                TxtProgress.Text = $"{p.Done}/{p.Total}";
                TxtStatus.Text = p.Message + (p.Eta is { } eta ? $"　剩余约 {UpscaleJob.FormatDuration(eta)}" : "");
            });

            var result = await job.RunAsync(inputs, model, options, meta, outputDir, progress, _cts.Token);
            Bar.Value = inputs.Count;
            TxtProgress.Text = $"{inputs.Count}/{inputs.Count}";
            TxtStatus.Text = $"完成：{result.Pages} 页 → {result.OutputPath}";
            _log("INFO", $"[超分] 输出：{result.OutputPath}");

            if (!IsVisible) return;
            var choice = await Ui.ChoiceAsync(this,
                $"超分完成：{result.Pages} 页" + (result.Skipped > 0 ? $"（跳过 {result.Skipped} 页）" : "") + "\n" +
                $"{ComicArchiveReader.FormatBytes(result.InputBytes)} → {ComicArchiveReader.FormatBytes(result.OutputBytes)}\n" +
                $"用时 {UpscaleJob.FormatDuration(result.Elapsed)}\n\n{result.OutputPath}",
                "超分完成", ("打开输出文件夹", "open"), ("关闭", "close"));
            if (choice == "open") OpenContainingFolder(result.OutputPath);
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "超分已取消（已处理的临时文件会清理）";
            _log("WARN", "[超分] 用户取消");
        }
        catch (Exception e)
        {
            TxtStatus.Text = $"超分失败：{e.Message}";
            _log("ERR ", $"[超分] 失败：{e.Message}");
            if (IsVisible) await Ui.ErrorAsync(this, e.Message, "超分失败");
        }
        finally
        {
            Bar.IsIndeterminate = false;
            SetBusy(false, indeterminate: false, status: null);
            UpdateStartState();
        }
    }

    private UpscaleOptions BuildOptions(bool overwrite, string targetPath) => new()
    {
        Scale = SelectedScale,
        TileSize = SelectedTile,
        Tta = ChkTta.IsChecked == true,
        GpuIndex = SelectedGpuIndex,
        MaxLongSide = ParseInt(TxtMaxLongSide.Text),
        WebpQuality = WebpTranscoder.DefaultQuality,
        SkipFailedPages = ChkSkipFailed.IsChecked == true,
        Overwrite = overwrite,
        OutputPath = targetPath,
    };

    private string ResolveOutputDir()
    {
        var custom = TxtOutputDir.Text?.Trim() ?? "";
        if (custom.Length > 0) return custom;
        return Path.GetDirectoryName(_archivePath) ?? Environment.CurrentDirectory;
    }

    private void CancelTask()
    {
        if (!_busy) return;
        _cts?.Cancel();
        TxtStatus.Text = "正在取消…";
    }

    private void OpenContainingFolder(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            _log("WARN", $"[超分] 打开文件夹失败：{e.Message}");
        }
    }

    // ================= 状态与生命周期 =================

    private void SetBusy(bool busy, bool indeterminate, string? status)
    {
        _busy = busy;
        Bar.IsVisible = busy;
        Bar.IsIndeterminate = indeterminate;
        BtnCancelTask.IsEnabled = busy;

        var enable = !busy;
        BtnPickArchive.IsEnabled = enable;
        CmbModel.IsEnabled = enable;
        CmbScale.IsEnabled = enable;
        CmbGpu.IsEnabled = enable;
        CmbTile.IsEnabled = enable;
        BtnDownload.IsEnabled = enable;
        BtnDownloadPage.IsEnabled = enable;
        BtnImportPack.IsEnabled = enable;
        BtnImportModel.IsEnabled = enable;
        BtnModelDb.IsEnabled = enable;
        BtnRemoveModel.IsEnabled = enable && SelectedModel?.Kind == UpscaleModelKind.Custom;
        TxtMaxLongSide.IsEnabled = enable;
        ChkTta.IsEnabled = enable;
        ChkSkipFailed.IsEnabled = enable;
        TxtOutputDir.IsEnabled = enable;
        BtnOutputDir.IsEnabled = enable;
        TxtTitle.IsEnabled = enable;
        TxtSeries.IsEnabled = enable;
        TxtVolume.IsEnabled = enable;
        TxtWriter.IsEnabled = enable;
        CmbLanguage.IsEnabled = enable;
        TxtSummary.IsEnabled = enable;
        BtnRecheckGpu.IsEnabled = enable;
        LstPages.IsEnabled = enable;
        UpdateOrderButtons(_pages.Count > 0);
        if (!UpscaleRuntime.PlatformSupported)
        {
            BtnDownload.IsEnabled = false;
            BtnImportPack.IsEnabled = false;
            BtnImportModel.IsEnabled = false;
        }

        BtnClose.IsEnabled = true;
        if (status is not null) TxtStatus.Text = status;
        if (busy) BtnStart.IsEnabled = false;
        else UpdateStartState();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_busy && !_forceClose)
        {
            e.Cancel = true;
            _ = CancelThenCloseAsync();
            return;
        }
        if (_busy) _cts?.Cancel();
        SavePreferences();
        _previewCts?.Cancel();
        ClearPreview();
        if (!_busy) CleanupSession();
        else ScheduleSessionCleanup();
    }

    /// <summary>取消任务后等它真正退出（最多 15 秒）再关窗；避免边跑边删会话目录导致临时文件泄漏。</summary>
    private async Task CancelThenCloseAsync()
    {
        if (_closeInProgress) return;
        _closeInProgress = true;
        try
        {
            if (!_closingConfirmed)
            {
                var ok = await Ui.MessageAsync(this, "正在超分/下载/解压，关闭窗口会取消当前任务。确定关闭？",
                    "取消任务并关闭", error: true);
                if (!ok) return;
                _closingConfirmed = true;
            }

            _cts?.Cancel();
            for (var i = 0; i < 150 && _busy; i++) await Task.Delay(100);

            if (_busy)
            {
                _log("WARN", "[超分] 任务未在 15 秒内退出，强制关闭窗口；临时文件会在后台重试清理。");
                _forceClose = true;
            }
            Close();
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    /// <summary>任务还没完全停下就关窗时，后台延迟重试删除会话目录。</summary>
    private void ScheduleSessionCleanup()
    {
        var dir = _sessionDir;
        _sessionDir = null;
        if (dir is null) return;
        _ = Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.Delete(dir, true);
                    return;
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
            _log("WARN", $"[超分] 临时目录清理失败，可手动删除：{dir}");
        });
    }

    private void SavePreferences()
    {
        LastModelId = SelectedModel?.Id ?? _settings.UpscaleModel;
        LastScale = SelectedScale;
        LastTile = SelectedTile;
        LastTta = ChkTta.IsChecked == true;
        LastGpuId = (CmbGpu.SelectedItem as GpuChoice)?.Id ?? -1;
        LastMaxLongSide = ParseInt(TxtMaxLongSide.Text);
        LastOutputDir = TxtOutputDir.Text?.Trim() ?? "";
        LastSkipFailed = ChkSkipFailed.IsChecked == true;
        LastLanguage = SelectedLanguageCode;
        Changed = true;
    }

    private void CleanupSession()
    {
        if (_sessionDir is null) return;
        TryDeleteDir(_sessionDir);
        _sessionDir = null;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 忽略 */ }
    }

    private static int ParseInt(string? text)
    {
        var t = (text ?? "").Trim();
        var digits = new string(t.TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var v)) return 0;
        return Math.Clamp(v, 0, WebpTranscoder.MaxWebpSide);
    }

    // ================= 拖拽 =================

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_busy) return;
        var files = e.Data.GetFiles();
        var path = files?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
        if (path is null || !ComicArchiveReader.IsSupportedArchive(path))
        {
            TxtStatus.Text = "拖入的文件不是支持的压缩包（zip / cbz / rar / cbr / 7z / cb7 / tar）。";
            return;
        }
        await LoadArchiveAsync(path);
    }

    /// <summary>把窗口日志接到主界面日志框。</summary>
    private sealed class WindowLogger(Action<string, string> log) : IAppLogger
    {
        public void Info(string message) => log("INFO", message);
        public void Warn(string message) => log("WARN", message);
        public void Error(string message) => log("ERR ", message);
        public void Progress(int current, int total, string message) { }
    }
}
