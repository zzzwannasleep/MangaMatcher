using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
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
/// 单部漫画编辑器（Avalonia 版）：三个页签——系列信息 / 标签·别名 / 每卷。
/// 保存 = 只 PATCH 改过的字段；系列保存自动补 MACHINE 标签；锁字段原样回传。
/// 编辑器的写入也汇入主界面的日志框（ChangeObserved 事件）。
/// </summary>
public sealed class SeriesEditorWindow : Window
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

    // ---- 控件 ----
    private readonly Image _picSeriesCover = new() { Stretch = Stretch.Uniform };
    private readonly Button _btnPickSeriesCover = new() { Content = "选图更换封面…", MinHeight = 30, Classes = { "app" } };
    private readonly Button _btnResetSeriesCover = new() { Content = "恢复服务器封面", MinHeight = 26, Classes = { "app" } };
    private readonly Button _btnRetryCover = new() { Content = "重试封面（网络失败时点我）", MinHeight = 26, Classes = { "app" } };
    private readonly TextBox _txtTitle = new(), _txtTitleSort = new(), _txtPublisher = new(), _txtLanguage = new(),
        _txtReleaseDate = new(), _txtTotalBooks = new(), _txtAgeRating = new(), _txtSummary = new();
    private readonly ComboBox _cmbStatus = new(), _cmbDirection = new();
    private readonly TextBox _txtTags = new(), _txtGenres = new(), _txtAuthors = new(), _txtAltTitles = new(), _txtLinks = new();
    private readonly DataGrid _lvBooks = new();
    private readonly TextBox _txtBookTitle = new(), _txtBookNumber = new(), _txtBookDate = new(), _txtBookIsbn = new(), _txtBookSummary = new();
    private readonly Image _picBookCover = new() { Stretch = Stretch.Uniform };
    private readonly Button _btnSaveBook = new() { Content = "保存该卷", Classes = { "primary" }, MinHeight = 30 };
    private readonly Button _btnPickBookCover = new() { Content = "选图换卷封面…", MinHeight = 26, Classes = { "app" } };
    private readonly Button _btnRescrapeVolumes = new() { Content = "重刮每卷（按卷号对齐）", Classes = { "app" }, MinHeight = 30 };
    private readonly Button _btnSaveSeries = new() { Content = "保存系列信息", Classes = { "primary" }, MinHeight = 32 };
    private readonly TextBlock _statusText = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
    private readonly ObservableCollection<BookRow> _bookRows = [];

    /// <summary>编辑器对库的写入 → 主界面日志框。</summary>
    public event Action<ScrapeChange>? ChangeObserved;

    private void Emit(ScrapeChangeKind kind, string target, string desc, string detail = "") =>
        ChangeObserved?.Invoke(new ScrapeChange(kind, _seriesId, target, desc, detail));

    public SeriesEditorWindow(KomgaClient komga, string seriesId, Action<string, string> log,
        Func<bool> dryRunProvider, Func<bool, CancellationToken, Task>? rescrapeVolumes)
    {
        _komga = komga;
        _seriesId = seriesId;
        _log = log;
        _dryRunProvider = dryRunProvider;
        _rescrapeVolumes = rescrapeVolumes;

        Title = "编辑单部漫画";
        Width = 1000;
        Height = 780;
        MinWidth = 860;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI, PingFang SC, Segoe UI");

        BuildUi();
        Opened += async (_, _) => await LoadAllAsync();
    }

    // ================= UI 构建 =================

    private static TextBlock Lbl(string t, bool right = false) => new()
    {
        Text = t,
        FontSize = 12,
        Foreground = Brushes.DimGray,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        Margin = new Thickness(1, 5, 6, 2),
    };

    private void BuildUi()
    {
        var tabs = new TabControl();

        // ---------- 页签 1：系列信息 ----------
        var coverPanel = new Grid { RowDefinitions = new RowDefinitions("*,30,26,26") };
        var coverBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F5F5F5")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D8D8E0")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = _picSeriesCover,
        };
        Grid.SetColumn(coverBorder, 0);
        Grid.SetRow(coverBorder, 0);
        coverPanel.Children.Add(coverBorder);
        Grid.SetRow(_btnPickSeriesCover, 1);
        Grid.SetRow(_btnResetSeriesCover, 2);
        Grid.SetRow(_btnRetryCover, 3);
        coverPanel.Children.Add(_btnPickSeriesCover);
        coverPanel.Children.Add(_btnResetSeriesCover);
        coverPanel.Children.Add(_btnRetryCover);

        var infoFields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,50*,Auto,50*"),
        };
        int r = 0;
        void InfoRow(Control? l1, Control? f1, Control? l2, Control? f2, bool tall = false)
        {
            infoFields.RowDefinitions.Add(new RowDefinition(tall ? new GridLength(120) : GridLength.Auto));
            if (l1 != null) { Grid.SetColumn(l1, 0); Grid.SetRow(l1, r); infoFields.Children.Add(l1); }
            if (f1 != null)
            {
                Grid.SetColumn(f1, 1); Grid.SetRow(f1, r);
                f1.HorizontalAlignment = HorizontalAlignment.Stretch; f1.Margin = new Thickness(0, 2, 8, 2);
                infoFields.Children.Add(f1);
            }
            if (l2 != null) { Grid.SetColumn(l2, 2); Grid.SetRow(l2, r); infoFields.Children.Add(l2); }
            if (f2 != null)
            {
                Grid.SetColumn(f2, 3); Grid.SetRow(f2, r);
                f2.HorizontalAlignment = HorizontalAlignment.Stretch; f2.Margin = new Thickness(0, 2, 0, 2);
                infoFields.Children.Add(f2);
            }
            r++;
        }
        void Span2(Control f, Control label)
        {
            infoFields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(label, 0); Grid.SetRow(label, r); infoFields.Children.Add(label);
            Grid.SetColumn(f, 1); Grid.SetColumnSpan(f, 3); Grid.SetRow(f, r);
            f.HorizontalAlignment = HorizontalAlignment.Stretch; f.Margin = new Thickness(0, 2, 0, 2);
            infoFields.Children.Add(f);
            r++;
        }

        _cmbStatus.ItemsSource = new[] { "ONGOING", "ENDED", "ABANDONED", "HIATUS" };
        _cmbDirection.ItemsSource = new[] { "RIGHT_TO_LEFT", "LEFT_TO_RIGHT" };
        _txtReleaseDate.Watermark = "YYYY-MM-DD";
        _txtAgeRating.Watermark = "空=无分级；18=成人";
        _txtSummary.MinHeight = 110;
        _txtSummary.AcceptsReturn = true;
        _txtSummary.TextWrapping = TextWrapping.Wrap;
        _txtSummary.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);

        InfoRow(Lbl("标题"), _txtTitle, Lbl("排序标题", true), _txtTitleSort);
        InfoRow(Lbl("状态"), _cmbStatus, Lbl("阅读方向", true), _cmbDirection);
        InfoRow(Lbl("出版社"), _txtPublisher, Lbl("语言", true), _txtLanguage);
        InfoRow(Lbl("发售日期"), _txtReleaseDate, Lbl("总卷数", true), _txtTotalBooks);
        Span2(_txtAgeRating, Lbl("分级"));
        Span2(_txtSummary, Lbl("简介"));
        Span2(new TextBlock
        {
            Text = "清空文本框并保存 = 清空该字段",
            Foreground = Brushes.Gray,
            FontSize = 11,
        }, new TextBlock { Text = "" });

        var tabInfo = new TabItem
        {
            Header = "系列信息",
            Content = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("250,*"),
                Children =
                {
                    Panel(coverPanel, 0),
                    Scroll(Panel(infoFields, 1)),
                },
            },
        };
        static Panel Panel(Control c, int col) { Grid.SetColumn(c, col); return new Panel { Children = { c } }; }
        static ScrollViewer Scroll(Control c) => new()
        {
            Content = new Border { Padding = new Thickness(8, 2, 2, 2), Child = c },
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // ---------- 页签 2：标签/题材/作者/别名/链接 ----------
        var tagsGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        void TagRow(string label, Control c, int row, double height = 0)
        {
            tagsGrid.RowDefinitions.Add(new RowDefinition(height > 0 ? new GridLength(height) : GridLength.Auto));
            var l = Lbl(label);
            Grid.SetColumn(l, 0); Grid.SetRow(l, row); tagsGrid.Children.Add(l);
            Grid.SetColumn(c, 1); Grid.SetRow(c, row);
            c.HorizontalAlignment = HorizontalAlignment.Stretch;
            tagsGrid.Children.Add(c);
        }
        _txtTags.Watermark = "逗号分隔，例：战斗,恋爱,MACHINE";
        _txtGenres.Watermark = "逗号分隔，例：喜剧,幻想";
        _txtAuthors.AcceptsReturn = true;
        _txtAuthors.Watermark = "每行一个：名字 或 名字(writer)";
        _txtAltTitles.AcceptsReturn = true;
        _txtAltTitles.Watermark = "每行一个：标题 或 标题|标签";
        _txtLinks.AcceptsReturn = true;
        _txtLinks.Watermark = "每行一个：标签|URL，如 Btv|https://…";
        TagRow("标签", _txtTags, 0, 30);
        TagRow("题材", _txtGenres, 1, 30);
        TagRow("作者", _txtAuthors, 2, 64);
        TagRow("别名", _txtAltTitles, 3, 64);
        TagRow("来源链接", _txtLinks, 4, 64);
        tagsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var machineHint = new TextBlock
        {
            Text = "MACHINE 标签保存时自动补，无需手写。",
            Foreground = Brushes.Gray,
            FontSize = 11,
            Margin = new Thickness(1, 8, 0, 0),
        };
        Grid.SetColumn(machineHint, 1);
        Grid.SetRow(machineHint, 5);
        tagsGrid.Children.Add(machineHint);
        var tabTags = new TabItem
        {
            Header = "标签 / 作者 / 别名",
            Content = new Border { Padding = new Thickness(12, 10, 14, 6), Child = tagsGrid },
        };

        // ---------- 页签 3：每卷 ----------
        _lvBooks.AutoGenerateColumns = false;
        _lvBooks.HeadersVisibility = DataGridHeadersVisibility.Column;
        _lvBooks.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        _lvBooks.HorizontalGridLinesBrush = new SolidColorBrush(Color.Parse("#ECECF2"));
        _lvBooks.Background = Brushes.White;
        _lvBooks.BorderThickness = new Thickness(1);
        _lvBooks.BorderBrush = new SolidColorBrush(Color.Parse("#D8D8E0"));
        _lvBooks.CornerRadius = new CornerRadius(6);
        _lvBooks.SelectionMode = DataGridSelectionMode.Single;
        _lvBooks.ItemsSource = _bookRows;
        _lvBooks.Columns.Add(new DataGridTextColumn { Header = "卷号", Binding = new Avalonia.Data.Binding(nameof(BookRow.Number)), Width = new DataGridLength(60), IsReadOnly = true });
        _lvBooks.Columns.Add(new DataGridTextColumn { Header = "标题", Binding = new Avalonia.Data.Binding(nameof(BookRow.Title)), Width = new DataGridLength(220), IsReadOnly = true });
        _lvBooks.Columns.Add(new DataGridTextColumn { Header = "发售日", Binding = new Avalonia.Data.Binding(nameof(BookRow.ReleaseDate)), Width = new DataGridLength(100), IsReadOnly = true });
        _lvBooks.Columns.Add(new DataGridTextColumn { Header = "ISBN", Binding = new Avalonia.Data.Binding(nameof(BookRow.Isbn)), Width = new DataGridLength(130), IsReadOnly = true });
        var thumbCol = new DataGridTemplateColumn { Header = "封面", Width = new DataGridLength(90), IsReadOnly = true };
        thumbCol.CellTemplate = new FuncDataTemplate<BookRow>((v, _) =>
            new Image { Width = 60, Height = 84, Stretch = Stretch.Uniform, [!Image.SourceProperty] = new Avalonia.Data.Binding(nameof(BookRow.ThumbImage)) });
        _lvBooks.Columns.Add(thumbCol);
        _lvBooks.Columns.Add(new DataGridTextColumn { Header = "简介", Binding = new Avalonia.Data.Binding(nameof(BookRow.SummaryBrief)), Width = new DataGridLength(300), IsReadOnly = true });
        _lvBooks.SelectionChanged += async (_, _) => await OnBookSelectedAsync();

        var bookTools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bookTools.Children.Add(_btnRescrapeVolumes);
        bookTools.Children.Add(new TextBlock
        {
            Text = "遵循主界面 Dry-run 开关",
            Foreground = Brushes.Gray,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var bookCoverPanel = new Grid { RowDefinitions = new RowDefinitions("*,30") };
        var bookCoverBorder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#F5F5F5")),
            BorderBrush = new SolidColorBrush(Color.Parse("#D8D8E0")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = _picBookCover,
        };
        bookCoverPanel.Children.Add(bookCoverBorder);
        Grid.SetRow(_btnPickBookCover, 1);
        bookCoverPanel.Children.Add(_btnPickBookCover);

        var bookFields = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,50*,Auto,50*"),
        };
        int br = 0;
        void BookRow2(Control? l1, Control? f1, Control? l2, Control? f2)
        {
            bookFields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            if (l1 != null) { Grid.SetColumn(l1, 0); Grid.SetRow(l1, br); bookFields.Children.Add(l1); }
            if (f1 != null)
            {
                Grid.SetColumn(f1, 1); Grid.SetRow(f1, br);
                f1.HorizontalAlignment = HorizontalAlignment.Stretch; f1.Margin = new Thickness(0, 2, 8, 2);
                bookFields.Children.Add(f1);
            }
            if (l2 != null) { Grid.SetColumn(l2, 2); Grid.SetRow(l2, br); bookFields.Children.Add(l2); }
            if (f2 != null)
            {
                Grid.SetColumn(f2, 3); Grid.SetRow(f2, br);
                f2.HorizontalAlignment = HorizontalAlignment.Stretch; f2.Margin = new Thickness(0, 2, 0, 2);
                bookFields.Children.Add(f2);
            }
            br++;
        }
        _txtBookDate.Watermark = "YYYY-MM-DD";
        _txtBookSummary.AcceptsReturn = true;
        _txtBookSummary.MinHeight = 70;
        _txtBookSummary.TextWrapping = TextWrapping.Wrap;
        BookRow2(Lbl("标题"), _txtBookTitle, Lbl("卷号", true), _txtBookNumber);
        BookRow2(Lbl("发售日"), _txtBookDate, Lbl("ISBN", true), _txtBookIsbn);
        bookFields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var bl = Lbl("简介");
        Grid.SetColumn(bl, 0); Grid.SetRow(bl, br); bookFields.Children.Add(bl);
        Grid.SetColumn(_txtBookSummary, 1); Grid.SetColumnSpan(_txtBookSummary, 3); Grid.SetRow(_txtBookSummary, br);
        _txtBookSummary.HorizontalAlignment = HorizontalAlignment.Stretch;
        bookFields.Children.Add(_txtBookSummary);
        br++;
        bookFields.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Grid.SetColumn(_btnSaveBook, 0); Grid.SetColumnSpan(_btnSaveBook, 4); Grid.SetRow(_btnSaveBook, br);
        _btnSaveBook.HorizontalAlignment = HorizontalAlignment.Left;
        bookFields.Children.Add(_btnSaveBook);

        var tabBooks = new TabItem
        {
            Header = "每卷",
            Content = new Border
            {
                Padding = new Thickness(12, 10, 14, 10),
                Child = new Grid
                {
                    RowDefinitions = new RowDefinitions("Auto,*,230"),
                    Children =
                    {
                        Row(bookTools, 0),
                        Row(_lvBooks, 1),
                        Row(new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("190,*"),
                            Children = { ColPanel(bookCoverPanel, 0), ColPanel(bookFields, 1) },
                        }, 2),
                    },
                },
            },
        };
        static Panel Row(Control c, int row) { Grid.SetRow(c, row); return new Panel { Children = { c } }; }
        static Panel ColPanel(Control c, int col) { Grid.SetColumn(c, col); return new Panel { Children = { c } }; }

        tabs.Items.Add(tabInfo);
        tabs.Items.Add(tabTags);
        tabs.Items.Add(tabBooks);
        tabs.SelectionChanged += async (_, _) =>
        {
            if (tabs.SelectedItem is TabItem t && t.Header?.ToString() == "每卷" && !_booksLoaded)
                await LoadBooksAsync();
        };

        var btnClose = new Button { Content = "关闭", Classes = { "app" }, MinHeight = 32, MinWidth = 88 };
        btnClose.Click += (_, _) => Close();
        var bottom = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            Margin = new Thickness(12, 6, 12, 10),
        };
        bottom.Children.Add(Panel(btnClose, 0));
        _btnSaveSeries.SetValue(Grid.ColumnProperty, 1);
        bottom.Children.Add(Panel(_btnSaveSeries, 1));
        _statusText.SetValue(Grid.ColumnProperty, 3);
        bottom.Children.Add(Panel(_statusText, 3));

        Content = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        var tabsHost = Panel(tabs, 0);
        ((Grid)Content).Children.Add(tabsHost);
        var bottomHost = Panel(bottom, 1);
        ((Grid)Content).Children.Add(bottomHost);

        _btnPickSeriesCover.Click += async (_, _) => await PickSeriesCoverAsync();
        _btnResetSeriesCover.Click += (_, _) => ResetSeriesCover();
        _btnRetryCover.Click += async (_, _) => await RetryCoversAsync();
        _btnPickBookCover.Click += async (_, _) => await PickBookCoverAsync();
        _btnSaveSeries.Click += async (_, _) => await SaveSeriesAsync();
        _btnSaveBook.Click += async (_, _) => await SaveSelectedBookAsync();
        _btnRescrapeVolumes.Click += async (_, _) => await RescrapeVolumesAsync();
    }

    /// <summary>封面网络加载失败后的重试入口：系列封面 + 每卷缩略图一起重拉。</summary>
    private async Task RetryCoversAsync()
    {
        SetStatus("重新加载封面…", false);
        _btnRetryCover.IsEnabled = false;
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
            _btnRetryCover.IsEnabled = true;
        }
    }

    // ================= 装载 =================

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
        _txtTitle.Text = _meta.Title ?? "";
        _txtTitleSort.Text = _meta.TitleSort ?? "";
        _cmbStatus.SelectedItem = string.IsNullOrEmpty(_meta.Status) ? null : _meta.Status;
        _cmbDirection.SelectedItem = string.IsNullOrEmpty(_meta.ReadingDirection) ? null : _meta.ReadingDirection;
        _txtPublisher.Text = _meta.Publisher ?? "";
        _txtLanguage.Text = _meta.Language ?? "";
        _txtReleaseDate.Text = _meta.ReleaseDate ?? "";
        _txtTotalBooks.Text = _meta.TotalBookCount?.ToString() ?? "";
        _txtAgeRating.Text = _meta.AgeRating?.ToString() ?? "";
        _txtSummary.Text = _meta.Summary ?? "";
        _txtTags.Text = string.Join("，", _meta.Tags ?? []);
        _txtGenres.Text = string.Join("，", _meta.Genres ?? []);
        _txtAuthors.Text = string.Join(Environment.NewLine,
            (_meta.Authors ?? []).Select(a => a.Role.Length > 0 ? $"{a.Name}({a.Role})" : a.Name));
        _txtAltTitles.Text = string.Join(Environment.NewLine,
            (_meta.AlternateTitles ?? []).Select(t => string.IsNullOrEmpty(t.Label) ? t.Title : $"{t.Title}|{t.Label}"));
        _txtLinks.Text = string.Join(Environment.NewLine,
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
        if (_lvBooks.SelectedItem is not BookRow row) return;
        var b = row.Book;
        _selectedBookId = b.Id;
        var m = b.Metadata;
        _txtBookTitle.Text = m?.Title ?? "";
        _txtBookNumber.Text = m?.Number ?? "";
        _txtBookDate.Text = m?.ReleaseDate ?? "";
        _txtBookIsbn.Text = m?.Isbn ?? "";
        _txtBookSummary.Text = m?.Summary ?? "";
        _pendingBookCover = null;
        if (_bookThumbBytes.TryGetValue(b.Id, out var bytes))
            ShowBookCover(bytes);
        else
        {
            _picBookCover.Source = null;
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
            _btnSaveSeries.IsEnabled = false;
            SetStatus("保存中…", false);
            var input = new SeriesEditInput
            {
                Title = _txtTitle.Text?.Trim() ?? "",
                TitleSort = _txtTitleSort.Text?.Trim() ?? "",
                Status = _cmbStatus.SelectedItem as string,
                ReadingDirection = _cmbDirection.SelectedItem as string,
                Publisher = _txtPublisher.Text?.Trim() ?? "",
                Language = _txtLanguage.Text?.Trim() ?? "",
                ReleaseDate = _txtReleaseDate.Text?.Trim() ?? "",
                Summary = _txtSummary.Text ?? "",
                AgeRating = int.TryParse(_txtAgeRating.Text?.Trim(), out var ar) ? ar : 0,
                TotalBookCount = int.TryParse(_txtTotalBooks.Text?.Trim(), out var tc) && tc > 0 ? tc : null,
                Tags = SplitList(_txtTags.Text ?? ""),
                Genres = SplitList(_txtGenres.Text ?? ""),
                Authors = ParseLines(_txtAuthors.Text ?? "").Select(ParseAuthor).ToList(),
                AlternateTitles = ParseLines(_txtAltTitles.Text ?? "").Select(ParseAlt).ToList(),
                Links = ParseLines(_txtLinks.Text ?? "").Select(ParseLink).ToList(),
            };
            var body = SeriesEditorPatch.BuildSeriesPatch(_meta, input);
            if (body.Count > 0)
            {
                await _komga.PatchSeriesMetadataRawAsync(_seriesId, body);
                _log("INFO", $"《{_txtTitle.Text}》系列信息已更新（编辑器：{string.Join("、", body.Keys)}）");
                Emit(ScrapeChangeKind.SeriesMeta, $"《{_txtTitle.Text}》", "已编辑系列元数据（编辑器）",
                    string.Join("；", body.Keys.Select(k => $"改 {k}")));
            }
            else
            {
                _log("INFO", "系列信息没有改动，跳过 PATCH");
            }
            if (_pendingSeriesCover != null)
            {
                await _komga.UploadSeriesCoverAsync(_seriesId, _pendingSeriesCover, "cover.jpg");
                _log("INFO", $"《{_txtTitle.Text}》系列封面已更换（{(_pendingSeriesCover.Length / 1024)}KB）");
                Emit(ScrapeChangeKind.SeriesCover, $"《{_txtTitle.Text}》",
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
            _btnSaveSeries.IsEnabled = true;
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
            _btnSaveBook.IsEnabled = false;
            SetStatus("保存该卷…", false);
            var input = new BookEditInput
            {
                Title = _txtBookTitle.Text?.Trim() ?? "",
                Number = _txtBookNumber.Text?.Trim() ?? "",
                ReleaseDate = _txtBookDate.Text?.Trim() ?? "",
                Isbn = _txtBookIsbn.Text?.Trim() ?? "",
                Summary = _txtBookSummary.Text ?? "",
            };
            var body = SeriesEditorPatch.BuildBookPatch(b.Metadata ?? new BookMetadataDto(), input);
            if (body.Count > 0)
            {
                await _komga.PatchBookMetadataRawAsync(b.Id, body);
                _log("INFO", $"《{_meta.Title ?? _series.Name}》卷 {_txtBookNumber.Text} 信息已更新（编辑器：{string.Join("、", body.Keys)}）");
                Emit(ScrapeChangeKind.BookMeta, $"《{_meta.Title ?? _series.Name}》卷 {_txtBookNumber.Text}",
                    "已编辑卷信息（编辑器）", string.Join("；", body.Keys.Select(k => $"改 {k}")));
            }
            if (_pendingBookCover != null)
            {
                await _komga.UploadBookCoverAsync(b.Id, _pendingBookCover, "cover.jpg");
                _log("INFO", $"《{_meta.Title ?? _series.Name}》卷 {_txtBookNumber.Text} 封面已更换（{_pendingBookCover.Length / 1024}KB）");
                Emit(ScrapeChangeKind.BookCover, $"《{_meta.Title ?? _series.Name}》卷 {_txtBookNumber.Text}",
                    $"已更换卷封面（编辑器，{_pendingBookCover.Length / 1024}KB）");
                _pendingBookCover = null;
            }
            SetStatus("该卷已保存", false);
            await ReloadBooksAsync();
            _lvBooks.SelectedItem = null;
        }
        catch (Exception e)
        {
            _log("ERR ", $"保存卷信息失败: {e.Message}");
            SetStatus($"保存失败: {e.Message}", true);
        }
        finally
        {
            _btnSaveBook.IsEnabled = true;
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
            _btnRescrapeVolumes.IsEnabled = false;
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
            _btnRescrapeVolumes.IsEnabled = true;
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
        _picSeriesCover.Source = img;
        return true;
    }

    private void ShowBookCover(byte[] bytes)
    {
        var img = ImageFromBytes(bytes);
        if (img != null) _picBookCover.Source = img;
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
        _statusText.Text = text;
        _statusText.Foreground = new SolidColorBrush(Color.Parse(error ? "#B22222" : "#808080"));
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