using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using KomgaScraper.Core;
using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Scraping;

namespace KomgaScraper.App;

/// <summary>每卷编辑行（封面缩略图异步填充）。</summary>
public sealed class BookRow : INotifyPropertyChanged
{
    private string _thumb = "";
    private IImage? _thumbImage;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public BookDto Book { get; }
    public string Number { get; }
    public string Title { get; }
    public string ReleaseDate { get; }
    public string Isbn { get; }
    public string SummaryBrief { get; }

    public string Thumb { get => _thumb; set { if (_thumb != value) { _thumb = value; Raise(nameof(Thumb)); } } }
    public IImage? ThumbImage { get => _thumbImage; set { if (!ReferenceEquals(_thumbImage, value)) { _thumbImage = value; Raise(nameof(ThumbImage)); } } }

    public BookRow(BookDto b)
    {
        Book = b;
        Number = b.Metadata?.Number ?? "";
        Title = string.IsNullOrWhiteSpace(b.Metadata?.Title) ? b.Name : b.Metadata!.Title!;
        ReleaseDate = b.Metadata?.ReleaseDate ?? "";
        Isbn = b.Metadata?.Isbn ?? "";
        SummaryBrief = Brief(b.Metadata?.Summary);
    }

    private static string Brief(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 40 ? s : s[..40] + "…";
    }
}

/// <summary>
/// 单部漫画编辑器：AXAML 声明式布局（见 SeriesEditorWindow.axaml），
/// 三个页签——系列信息 / 标签·别名 / 每卷。
/// 保存 = 只 PATCH 改过的字段；系列保存自动补 MACHINE 标签；锁字段原样回传。
/// 编辑器的写入也汇入主界面的日志框（ChangeObserved 事件）。
/// </summary>
public sealed partial class SeriesEditorWindow : Window
{
    private readonly KomgaClient _komga;
    private readonly string _seriesId;
    private readonly Action<string, string> _log;
    private readonly Func<bool> _dryRunProvider;
    private readonly Func<bool, CancellationToken, Task>? _rescrapeVolumes;

    private SeriesDto _series = new();
    private SeriesMetadataDto _meta = new();
    private List<BookDto> _books = [];
    private bool _booksLoaded;

    private readonly Dictionary<string, byte[]> _bookThumbBytes = new();
    private byte[]? _pendingSeriesCover;
    private byte[]? _pendingBookCover;
    private string _selectedBookId = "";

    private readonly ObservableCollection<BookRow> _bookRows = [];

    /// <summary>编辑器对库的写入 → 主界面日志框。</summary>
    public event Action<ScrapeChange>? ChangeObserved;

    private void Emit(ScrapeChangeKind kind, string target, string desc, string detail = "") =>
        ChangeObserved?.Invoke(new ScrapeChange(kind, _seriesId, target, desc, detail));

    public SeriesEditorWindow(KomgaClient komga, string seriesId, Action<string, string> log,
        Func<bool> dryRunProvider, Func<bool, CancellationToken, Task>? rescrapeVolumes)
    {
        InitializeComponent();
        _komga = komga;
        _seriesId = seriesId;
        _log = log;
        _dryRunProvider = dryRunProvider;
        _rescrapeVolumes = rescrapeVolumes;

        CmbStatus.ItemsSource = new[] { "ONGOING", "ENDED", "ABANDONED", "HIATUS" };
        CmbDirection.ItemsSource = new[] { "RIGHT_TO_LEFT", "LEFT_TO_RIGHT" };
        BooksGrid.ItemsSource = _bookRows;

        BtnPickSeriesCover.Click += async (_, _) => await PickSeriesCoverAsync();
        BtnResetSeriesCover.Click += (_, _) => ResetSeriesCover();
        BtnRetryCover.Click += async (_, _) => await RetryCoversAsync();
        BtnPickBookCover.Click += async (_, _) => await PickBookCoverAsync();
        BtnSaveSeries.Click += async (_, _) => await SaveSeriesAsync();
        BtnSaveBook.Click += async (_, _) => await SaveSelectedBookAsync();
        BtnRescrapeVolumes.Click += async (_, _) => await RescrapeVolumesAsync();
        BtnClose.Click += (_, _) => Close();
        BooksGrid.SelectionChanged += async (_, _) => await OnBookSelectedAsync();

        Opened += async (_, _) => await LoadAllAsync();
    }

    // ================= 装载 =================

    /// <summary>封面网络加载失败后的重试入口：系列封面 + 每卷缩略图一起重拉。</summary>
    private async Task RetryCoversAsync()
    {
        SetStatus("重新加载封面…", false);
        BtnRetryCover.IsEnabled = false;
        var failed = false;
        try
        {
            try
            {
                var bytes = await _komga.GetSeriesThumbnailAsync(_seriesId);
                ShowSeriesCover(bytes);
            }
            catch (Exception e)
            {
                SetStatus($"系列封面仍加载失败：{e.Message}", true);
                failed = true;
            }
            _bookThumbBytes.Clear();
            await ReloadBooksAsync();
            if (!failed) SetStatus("封面已重新加载", false);
        }
        finally
        {
            BtnRetryCover.IsEnabled = true;
        }
    }

    private async Task LoadAllAsync()
    {
        try
        {
            SetStatus("读取系列与元数据…", false);
            _series = await _komga.GetSeriesAsync(_seriesId);
            _meta = _series.Metadata ?? new SeriesMetadataDto();
            var title = string.IsNullOrWhiteSpace(_meta.Title) ? _series.Name : _meta.Title!;
            Title = $"编辑单部漫画 — 《{title}》";
            PopulateSeries();
            SetStatus($"已载入《{title}》", false);
            try
            {
                var bytes = await _komga.GetSeriesThumbnailAsync(_seriesId);
                ShowSeriesCover(bytes);
            }
            catch (Exception e)
            {
                SetStatus($"封面加载失败（{e.Message}）—— 可点左侧「重试封面」", true);
                _log("WARN", $"《{title}》封面加载失败: {e.Message}");
            }
            await LoadBooksAsync();
        }
        catch (Exception e)
        {
            _log("ERR ", $"读取系列失败: {e.Message}");
            SetStatus($"读取失败: {e.Message}", true);
        }
    }

    private void PopulateSeries()
    {
        TxtTitle.Text = _meta.Title ?? "";
        TxtTitleSort.Text = _meta.TitleSort ?? "";
        CmbStatus.SelectedItem = string.IsNullOrEmpty(_meta.Status) ? null : _meta.Status;
        CmbDirection.SelectedItem = string.IsNullOrEmpty(_meta.ReadingDirection) ? null : _meta.ReadingDirection;
        TxtPublisher.Text = _meta.Publisher ?? "";
        TxtLanguage.Text = _meta.Language ?? "";
        TxtReleaseDate.Text = _meta.ReleaseDate ?? "";
        TxtTotalBooks.Text = _meta.TotalBookCount?.ToString() ?? "";
        TxtAgeRating.Text = _meta.AgeRating?.ToString() ?? "";
        TxtSummary.Text = _meta.Summary ?? "";
        TxtTags.Text = string.Join("，", _meta.Tags ?? []);
        TxtGenres.Text = string.Join("，", _meta.Genres ?? []);
        TxtAuthors.Text = string.Join(Environment.NewLine,
            (_meta.Authors ?? []).Select(a => a.Role.Length > 0 ? $"{a.Name}({a.Role})" : a.Name));
        TxtAltTitles.Text = string.Join(Environment.NewLine,
            (_meta.AlternateTitles ?? []).Select(t => string.IsNullOrEmpty(t.Label) ? t.Title : $"{t.Title}|{t.Label}"));
        TxtLinks.Text = string.Join(Environment.NewLine,
            (_meta.Links ?? []).Select(l => $"{l.Label}|{l.Url}"));
    }

    private async Task LoadBooksAsync()
    {
        if (_booksLoaded) return;
        _booksLoaded = true;
        try
        {
            SetStatus("读取每卷数据…", false);
            _books = await _komga.ListBooksInSeriesAsync(_seriesId);
            _bookThumbBytes.Clear();
            _bookRows.Clear();
            foreach (var b in _books) _bookRows.Add(new BookRow(b));
            SetStatus($"每卷 {_books.Count} 册", false);
            _ = LoadBookThumbnailsAsync();
        }
        catch (Exception e)
        {
            _log("ERR ", $"读取每卷数据失败: {e.Message}");
            SetStatus($"读取每卷失败: {e.Message}", true);
        }
    }

    /// <summary>逐册抓当前封面缩略图（并发 4；失败计数，加载完汇报）。</summary>
    private async Task LoadBookThumbnailsAsync()
    {
        var books = _books;
        var failed = 0;
        await Parallel.ForEachAsync(books,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = CancellationToken.None },
            async (b, token) =>
            {
                try
                {
                    var bytes = await _komga.GetBookThumbnailBytesAsync(b.Id, token);
                    if (bytes == null || bytes.Length == 0) return;
                    _bookThumbBytes[b.Id] = bytes;
                    var img = ImageFromBytes(bytes, 84, 118);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var row = _bookRows.FirstOrDefault(x => x.Book.Id == b.Id);
                        if (row != null)
                        {
                            row.ThumbImage = img;
                            row.Thumb = $"{bytes.Length / 1024}KB";
                        }
                    });
                }
                catch
                {
                    Interlocked.Increment(ref failed);
                }
            });
        if (failed > 0)
            SetStatus($"每卷 {_books.Count} 册（{failed} 张封面加载失败——网络抖动可点「重试封面」重拉）", true);
    }

    private async Task OnBookSelectedAsync()
    {
        if (BooksGrid.SelectedItem is not BookRow row) return;
        var b = row.Book;
        _selectedBookId = b.Id;
        var m = b.Metadata;
        TxtBookTitle.Text = m?.Title ?? "";
        TxtBookNumber.Text = m?.Number ?? "";
        TxtBookDate.Text = m?.ReleaseDate ?? "";
        TxtBookIsbn.Text = m?.Isbn ?? "";
        TxtBookSummary.Text = m?.Summary ?? "";
        _pendingBookCover = null;
        if (_bookThumbBytes.TryGetValue(b.Id, out var bytes))
            ShowBookCover(bytes);
        else
        {
            PicBookCover.Source = null;
            try
            {
                var big = await _komga.GetBookThumbnailBytesAsync(b.Id);
                if (big != null && b.Id == _selectedBookId) ShowBookCover(big);
            }
            catch { }
        }
    }

    // ================= 保存：系列 =================

    private async Task SaveSeriesAsync()
    {
        try
        {
            BtnSaveSeries.IsEnabled = false;
            SetStatus("保存中…", false);
            var input = new SeriesEditInput
            {
                Title = TxtTitle.Text?.Trim() ?? "",
                TitleSort = TxtTitleSort.Text?.Trim() ?? "",
                Status = CmbStatus.SelectedItem as string,
                ReadingDirection = CmbDirection.SelectedItem as string,
                Publisher = TxtPublisher.Text?.Trim() ?? "",
                Language = TxtLanguage.Text?.Trim() ?? "",
                ReleaseDate = TxtReleaseDate.Text?.Trim() ?? "",
                Summary = TxtSummary.Text ?? "",
                AgeRating = int.TryParse(TxtAgeRating.Text?.Trim(), out var ar) ? ar : 0,
                TotalBookCount = int.TryParse(TxtTotalBooks.Text?.Trim(), out var tc) && tc > 0 ? tc : null,
                Tags = SplitList(TxtTags.Text ?? ""),
                Genres = SplitList(TxtGenres.Text ?? ""),
                Authors = ParseLines(TxtAuthors.Text ?? "").Select(ParseAuthor).ToList(),
                AlternateTitles = ParseLines(TxtAltTitles.Text ?? "").Select(ParseAlt).ToList(),
                Links = ParseLines(TxtLinks.Text ?? "").Select(ParseLink).ToList(),
            };
            var body = SeriesEditorPatch.BuildSeriesPatch(_meta, input);
            if (body.Count > 0)
            {
                await _komga.PatchSeriesMetadataRawAsync(_seriesId, body);
                _log("INFO", $"《{TxtTitle.Text}》系列信息已更新（编辑器：{string.Join("、", body.Keys)}）");
                Emit(ScrapeChangeKind.SeriesMeta, $"《{TxtTitle.Text}》", "已编辑系列元数据（编辑器）",
                    string.Join("；", body.Keys.Select(k => $"改 {k}")));
            }
            else
            {
                _log("INFO", "系列信息没有改动，跳过 PATCH");
            }
            if (_pendingSeriesCover != null)
            {
                await _komga.UploadSeriesCoverAsync(_seriesId, _pendingSeriesCover, "cover.jpg");
                _log("INFO", $"《{TxtTitle.Text}》系列封面已更换（{(_pendingSeriesCover.Length / 1024)}KB）");
                Emit(ScrapeChangeKind.SeriesCover, $"《{TxtTitle.Text}》",
                    $"已更换系列封面（编辑器，{_pendingSeriesCover.Length / 1024}KB）");
                _pendingSeriesCover = null;
            }
            SetStatus("已保存", false);
            _meta = (await _komga.GetSeriesAsync(_seriesId)).Metadata ?? new SeriesMetadataDto();
            PopulateSeries();
            if (_booksLoaded) await ReloadBooksAsync();
        }
        catch (Exception e)
        {
            _log("ERR ", $"保存系列信息失败: {e.Message}");
            SetStatus($"保存失败: {e.Message}", true);
        }
        finally
        {
            BtnSaveSeries.IsEnabled = true;
        }
    }

    private async Task PickSeriesCoverAsync()
    {
        var file = await Ui.PickFileAsync(this, "选择新封面（JPG/PNG）", ["*.jpg", "*.jpeg", "*.png"]);
        if (file == null) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(file);
            if (ShowSeriesCover(bytes))
            {
                _pendingSeriesCover = bytes;
                SetStatus($"已选新封面 {bytes.Length / 1024}KB（点「保存系列信息」生效）", false);
            }
        }
        catch (Exception ex)
        {
            await Ui.ErrorAsync(this, $"读取图片失败: {ex.Message}");
        }
    }

    private void ResetSeriesCover()
    {
        _pendingSeriesCover = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await _komga.GetSeriesThumbnailAsync(_seriesId);
                await Dispatcher.UIThread.InvokeAsync(() => ShowSeriesCover(bytes));
            }
            catch { }
        });
    }

    // ================= 保存：单卷 =================

    private async Task SaveSelectedBookAsync()
    {
        if (_selectedBookId.Length == 0)
        {
            await Ui.MessageAsync(this, "先在列表里选中一卷", "提示", okOnly: true);
            return;
        }
        var b = _books.FirstOrDefault(x => x.Id == _selectedBookId);
        if (b == null) return;
        try
        {
            BtnSaveBook.IsEnabled = false;
            SetStatus("保存该卷…", false);
            var input = new BookEditInput
            {
                Title = TxtBookTitle.Text?.Trim() ?? "",
                Number = TxtBookNumber.Text?.Trim() ?? "",
                ReleaseDate = TxtBookDate.Text?.Trim() ?? "",
                Isbn = TxtBookIsbn.Text?.Trim() ?? "",
                Summary = TxtBookSummary.Text ?? "",
            };
            var body = SeriesEditorPatch.BuildBookPatch(b.Metadata ?? new BookMetadataDto(), input);
            if (body.Count > 0)
            {
                await _komga.PatchBookMetadataRawAsync(b.Id, body);
                _log("INFO", $"《{_meta.Title ?? _series.Name}》卷 {TxtBookNumber.Text} 信息已更新（编辑器：{string.Join("、", body.Keys)}）");
                Emit(ScrapeChangeKind.BookMeta, $"《{_meta.Title ?? _series.Name}》卷 {TxtBookNumber.Text}",
                    "已编辑卷信息（编辑器）", string.Join("；", body.Keys.Select(k => $"改 {k}")));
            }
            if (_pendingBookCover != null)
            {
                await _komga.UploadBookCoverAsync(b.Id, _pendingBookCover, "cover.jpg");
                _log("INFO", $"《{_meta.Title ?? _series.Name}》卷 {TxtBookNumber.Text} 封面已更换（{_pendingBookCover.Length / 1024}KB）");
                Emit(ScrapeChangeKind.BookCover, $"《{_meta.Title ?? _series.Name}》卷 {TxtBookNumber.Text}",
                    $"已更换卷封面（编辑器，{_pendingBookCover.Length / 1024}KB）");
                _pendingBookCover = null;
            }
            SetStatus("该卷已保存", false);
            await ReloadBooksAsync();
            BooksGrid.SelectedItem = null;
        }
        catch (Exception e)
        {
            _log("ERR ", $"保存卷信息失败: {e.Message}");
            SetStatus($"保存失败: {e.Message}", true);
        }
        finally
        {
            BtnSaveBook.IsEnabled = true;
        }
    }

    private async Task PickBookCoverAsync()
    {
        if (_selectedBookId.Length == 0)
        {
            await Ui.MessageAsync(this, "先在列表里选中一卷", "提示", okOnly: true);
            return;
        }
        var file = await Ui.PickFileAsync(this, "选择该卷新封面（JPG/PNG）", ["*.jpg", "*.jpeg", "*.png"]);
        if (file == null) return;
        try
        {
            var bytes = await File.ReadAllBytesAsync(file);
            ShowBookCover(bytes);
            _pendingBookCover = bytes;
            SetStatus($"已选新卷封面 {bytes.Length / 1024}KB（点「保存该卷」生效）", false);
        }
        catch (Exception ex)
        {
            await Ui.ErrorAsync(this, $"读取图片失败: {ex.Message}");
        }
    }

    private async Task RescrapeVolumesAsync()
    {
        if (_rescrapeVolumes == null)
        {
            await Ui.MessageAsync(this,
                "没有 Btv 链接，无法重刮每卷。\n请先用主界面「覆盖匹配」或「纠正匹配」选定正确条目。",
                "提示", okOnly: true);
            return;
        }
        var dry = _dryRunProvider();
        var okGo = await Ui.MessageAsync(this,
            $"按 Bangumi 卷号对齐每卷封面/简介/ISBN。\n模式：{(dry ? "Dry-run 预览" : "真实更新")}\n继续？",
            "重刮每卷");
        if (!okGo) return;
        try
        {
            BtnRescrapeVolumes.IsEnabled = false;
            SetStatus("重刮每卷中…（看主界面日志框）", false);
            await _rescrapeVolumes(dry, CancellationToken.None);
            SetStatus("重刮完成（主界面已刷新可关闭本窗口）", false);
        }
        catch (Exception e)
        {
            _log("ERR ", $"重刮每卷失败: {e.Message}");
            SetStatus($"重刮失败: {e.Message}", true);
        }
        finally
        {
            BtnRescrapeVolumes.IsEnabled = true;
        }
    }

    // ================= 杂项 =================

    private async Task ReloadBooksAsync()
    {
        _booksLoaded = false;
        await LoadBooksAsync();
    }

    private bool ShowSeriesCover(byte[] bytes)
    {
        var img = ImageFromBytes(bytes);
        if (img == null) return false;
        PicSeriesCover.Source = img;
        return true;
    }

    private void ShowBookCover(byte[] bytes)
    {
        var img = ImageFromBytes(bytes);
        if (img != null) PicBookCover.Source = img;
    }

    private static Bitmap? ImageFromBytes(byte[] bytes, int? w = null, int? h = null)
    {
        if (bytes.Length == 0) return null;
        try
        {
            var bmp = new Bitmap(new MemoryStream(bytes));
            if (w.HasValue && h.HasValue && (bmp.PixelSize.Width > w || bmp.PixelSize.Height > h))
            {
                var scaled = bmp.CreateScaledBitmap(new Avalonia.PixelSize(w.Value, h.Value),
                    Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
                bmp.Dispose();
                return scaled;
            }
            return bmp;
        }
        catch { return null; }
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        var app = Application.Current;
        IBrush? fg = null;
        if (app != null && app.Resources.TryGetResource(
                error ? "ErrorBrush" : "TextSecondaryBrush", app.ActualThemeVariant, out var res))
            fg = res as IBrush;
        StatusText.Foreground = fg ?? Brushes.Gray;
    }

    /// <summary>"a，b、c , d" → ["a","b","c","d"]。</summary>
    private static List<string> SplitList(string text)
    {
        var parts = text.Split(['，', '、', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. parts.Where(p => p.Length > 0)];
    }

    private static List<string> ParseLines(string text)
    {
        var parts = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. parts.Where(p => p.Length > 0)];
    }

    /// <summary>"名字(角色)" → (名字, 角色)；没有括号 → (名字, writer)。</summary>
    private static AuthorDto ParseAuthor(string line)
    {
        var i = line.IndexOf('(');
        if (i > 0 && line.EndsWith(")"))
        {
            var role = line[(i + 1)..^1].Trim();
            return new AuthorDto { Name = line[..i].Trim(), Role = role.Length > 0 ? role : "writer" };
        }
        return new AuthorDto { Name = line, Role = "writer" };
    }

    /// <summary>"标题|标签" → (标题, 标签)；无 | → (标题, "")。</summary>
    private static AlternateTitleDto ParseAlt(string line)
    {
        var i = line.IndexOf('|');
        return i > 0
            ? new AlternateTitleDto { Title = line[..i].Trim(), Label = line[(i + 1)..].Trim() }
            : new AlternateTitleDto { Title = line, Label = "" };
    }

    /// <summary>"标签|URL" → (标签, URL)；无 | → ("Btv", URL)。</summary>
    private static LinkDto ParseLink(string line)
    {
        var i = line.IndexOf('|');
        return i > 0
            ? new LinkDto { Label = line[..i].Trim(), Url = line[(i + 1)..].Trim() }
            : new LinkDto { Label = "Btv", Url = line.Trim() };
    }
}
