using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Scraping;

namespace KomgaScraper.App.Dialogs;

/// <summary>
/// 单部漫画编辑器：检索到某部漫画后，直接编辑这个 series 的封面/标题/简介/标签/作者/别名/链接，
/// 以及每卷（book）的封面/标题/卷号/简介/发售日/ISBN。
/// 保存 = 只 PATCH 改过的字段；系列保存自动补 MACHINE 标签（凡程序写入必打标，不是选择是必须）；
/// 锁字段原样回传避免翻转。编辑器的写入也汇入主界面的日志框（ChangeObserved 事件）。
/// </summary>
public sealed class SeriesEditorDialog : Form
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

    private readonly Dictionary<string, byte[]> _bookThumbBytes = new();   // bookId → 当前封面字节
    private byte[]? _pendingSeriesCover;                                  // 选了图还没保存
    private byte[]? _pendingBookCover;                                    // 选中卷选了图还没保存
    private string _selectedBookId = "";

    private TabControl _tabs = null!;
    private TextBox _txtTitle = null!, _txtTitleSort = null!, _txtPublisher = null!, _txtLanguage = null!,
        _txtReleaseDate = null!, _txtTotalBooks = null!, _txtAgeRating = null!, _txtSummary = null!;
    private ComboBox _cmbStatus = null!, _cmbDirection = null!;
    private TextBox _txtTags = null!, _txtGenres = null!, _txtAuthors = null!, _txtAltTitles = null!, _txtLinks = null!;
    private PictureBox _picSeriesCover = null!;
    private Button _btnPickSeriesCover = null!, _btnSaveSeries = null!, _btnResetSeriesCover = null!, _btnRetryCover = null!;
    private ListView _lvBooks = null!;
    private TextBox _txtBookTitle = null!, _txtBookNumber = null!, _txtBookDate = null!, _txtBookIsbn = null!, _txtBookSummary = null!;
    private PictureBox _picBookCover = null!;
    private Button _btnSaveBook = null!, _btnPickBookCover = null!, _btnRescrapeVolumes = null!;
    private Label _statusText = null!;
    private ImageList _bookThumbs = null!;

    /// <summary>编辑器对库的写入 → 主界面日志框（同 ScrapeService.ChangeObserved 结构）。</summary>
    public event Action<ScrapeChange>? ChangeObserved;

    private void Emit(ScrapeChangeKind kind, string target, string desc, string detail = "") =>
        ChangeObserved?.Invoke(new ScrapeChange(kind, _seriesId, target, desc, detail));

    public SeriesEditorDialog(KomgaClient komga, string seriesId, Action<string, string> log,
        Func<bool> dryRunProvider, Func<bool, CancellationToken, Task>? rescrapeVolumes)
    {
        _komga = komga;
        _seriesId = seriesId;
        _log = log;
        _dryRunProvider = dryRunProvider;
        _rescrapeVolumes = rescrapeVolumes;

        Text = "编辑单部漫画";
        Font = new Font("Microsoft YaHei UI", 9F);
        Size = new Size(1000, 760);
        MinimumSize = new Size(860, 620);
        StartPosition = FormStartPosition.CenterParent;
        BuildUi();
        Shown += async (_, _) => await LoadAllAsync();
    }

    private void BuildUi()
    {
        _tabs = new TabControl { Dock = DockStyle.Fill };

        // ---------- 页签 1：系列信息 ----------
        var tabInfo = new TabPage("系列信息");
        var infoGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
        infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        infoGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 左：系列封面（大图预览，加载失败可见可重试——不再静默吞网络错误）
        var coverPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        coverPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        coverPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        coverPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coverPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        coverPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _picSeriesCover = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(245, 245, 245),
        };
        _btnPickSeriesCover = new Button { Text = "选图更换封面…", Height = 30, Dock = DockStyle.Fill, UseVisualStyleBackColor = true };
        _btnResetSeriesCover = new Button { Text = "恢复服务器封面", Height = 26, Dock = DockStyle.Fill, UseVisualStyleBackColor = true };
        _btnRetryCover = new Button { Text = "重试封面（网络失败时点我）", Height = 26, Dock = DockStyle.Fill, UseVisualStyleBackColor = true };
        coverPanel.Controls.Add(_picSeriesCover, 0, 0);
        coverPanel.Controls.Add(_btnPickSeriesCover, 0, 1);
        coverPanel.Controls.Add(_btnResetSeriesCover, 0, 2);
        coverPanel.Controls.Add(_btnRetryCover, 0, 3);
        infoGrid.Controls.Add(coverPanel, 0, 0);

        // 右：字段
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 4, Padding = new Padding(8, 2, 2, 2) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        static T FieldFill<T>(T c) where T : Control
        {
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(1, 2, 6, 2);
            return c;
        }
        static Label Lbl(string t) => new() { Text = t, AutoSize = true, Margin = new Padding(1, 6, 4, 2) };

        _txtTitle = FieldFill(new TextBox());
        _txtTitleSort = FieldFill(new TextBox());
        _cmbStatus = FieldFill(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Items = { "ONGOING", "ENDED", "ABANDONED", "HIATUS" } });
        _cmbDirection = FieldFill(new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Items = { "RIGHT_TO_LEFT", "LEFT_TO_RIGHT" } });
        _txtPublisher = FieldFill(new TextBox());
        _txtLanguage = FieldFill(new TextBox());
        _txtReleaseDate = FieldFill(new TextBox { PlaceholderText = "YYYY-MM-DD" });
        _txtTotalBooks = FieldFill(new TextBox());
        _txtAgeRating = FieldFill(new TextBox { PlaceholderText = "空=无分级；18=成人" });
        _txtSummary = FieldFill(new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, MinimumSize = new Size(0, 120) });

        fields.Controls.Add(Lbl("标题"), 0, 0);
        fields.Controls.Add(_txtTitle, 1, 0);
        fields.Controls.Add(Lbl("排序标题"), 2, 0);
        fields.Controls.Add(_txtTitleSort, 3, 0);
        fields.Controls.Add(Lbl("状态"), 0, 1);
        fields.Controls.Add(_cmbStatus, 1, 1);
        fields.Controls.Add(Lbl("阅读方向"), 2, 1);
        fields.Controls.Add(_cmbDirection, 3, 1);
        fields.Controls.Add(Lbl("出版社"), 0, 2);
        fields.Controls.Add(_txtPublisher, 1, 2);
        fields.Controls.Add(Lbl("语言"), 2, 2);
        fields.Controls.Add(_txtLanguage, 3, 2);
        fields.Controls.Add(Lbl("发售日期"), 0, 3);
        fields.Controls.Add(_txtReleaseDate, 1, 3);
        fields.Controls.Add(Lbl("总卷数"), 2, 3);
        fields.Controls.Add(_txtTotalBooks, 3, 3);
        fields.Controls.Add(Lbl("分级"), 0, 4);
        fields.Controls.Add(_txtAgeRating, 1, 4);
        fields.SetColumnSpan(_txtAgeRating, 3);
        fields.Controls.Add(Lbl("简介"), 0, 5);
        fields.Controls.Add(_txtSummary, 1, 5);
        fields.SetColumnSpan(_txtSummary, 3);
        var summaryHint = new Label
        {
            Text = "清空文本框并保存 = 清空该字段",
            AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(1, 2, 0, 0),
        };
        fields.Controls.Add(summaryHint, 1, 6);
        fields.SetColumnSpan(summaryHint, 3);
        infoGrid.Controls.Add(fields, 1, 0);
        tabInfo.Controls.Add(infoGrid);   // 关键修复：布局网格必须挂到页签上，否则整页空白（曾导致编辑器打不开）
        _tabs.TabPages.Add(tabInfo);

        // ---------- 页签 2：标签/题材/作者/别名/链接 ----------
        var tabTags = new TabPage("标签 / 作者 / 别名");
        var tagsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        tagsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        tagsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void TagRow(string label, Control c, int row, int height = 0)
        {
            tagsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            tagsGrid.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(1, 6, 6, 2) }, 0, row);
            tagsGrid.Controls.Add(c, 1, row);
            if (height > 0)
            {
                tagsGrid.RowStyles[row] = new RowStyle(SizeType.Absolute, height);
                c.Dock = DockStyle.Fill;
            }
        }
        _txtTags = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "逗号分隔，例：战斗,恋爱,MACHINE" };
        _txtGenres = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "逗号分隔，例：喜剧,幻想" };
        _txtAuthors = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 60, PlaceholderText = "每行一个：名字 或 名字(writer)" };
        _txtAltTitles = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 60, PlaceholderText = "每行一个：标题 或 标题|标签" };
        _txtLinks = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 60, PlaceholderText = "每行一个：标签|URL，如 Btv|https://…" };
        TagRow("标签", _txtTags, 0, 28);
        TagRow("题材", _txtGenres, 1, 28);
        TagRow("作者", _txtAuthors, 2, 64);
        TagRow("别名", _txtAltTitles, 3, 64);
        TagRow("来源链接", _txtLinks, 4, 64);
        tagsGrid.Controls.Add(new Label
        {
            Text = "MACHINE 标签保存时自动补，无需手写。",
            AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(1, 8, 0, 0),
        }, 1, 5);
        tabTags.Controls.Add(tagsGrid);   // 同上：网格挂到页签
        _tabs.TabPages.Add(tabTags);

        // ---------- 页签 3：每卷（封面/简介/ISBN） ----------
        var tabBooks = new TabPage("每卷");
        var booksGrid = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
        booksGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        booksGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        booksGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        booksGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));

        var booksTools = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _btnRescrapeVolumes = new Button
        {
            Text = "重刮每卷（按卷号对齐）",
            AutoSize = true,
            UseVisualStyleBackColor = true,
        };
        _btnRescrapeVolumes.Click += async (_, _) => await RescrapeVolumesAsync();
        booksTools.Controls.Add(_btnRescrapeVolumes);
        booksTools.Controls.Add(new Label
        {
            Text = "  遵循主界面 Dry-run 开关",
            AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(4, 8, 0, 0),
        });
        booksGrid.Controls.Add(booksTools, 0, 0);

        _bookThumbs = new ImageList { ImageSize = new Size(84, 118), ColorDepth = ColorDepth.Depth32Bit };
        _lvBooks = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            SmallImageList = _bookThumbs,
        };
        _lvBooks.Columns.Add("卷号", 60);
        _lvBooks.Columns.Add("标题", 220);
        _lvBooks.Columns.Add("发售日", 100);
        _lvBooks.Columns.Add("ISBN", 130);
        _lvBooks.Columns.Add("封面", 90);
        _lvBooks.Columns.Add("简介", 300);
        _lvBooks.SelectedIndexChanged += async (_, _) => await OnBookSelectedAsync();
        booksGrid.Controls.Add(_lvBooks, 0, 1);

        // 底部：选中卷编辑区
        var bookEdit = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(6, 4, 6, 6) };
        bookEdit.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        bookEdit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var bookCoverPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        bookCoverPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bookCoverPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bookCoverPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _picBookCover = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.FromArgb(245, 245, 245),
        };
        _btnPickBookCover = new Button { Text = "选图换卷封面…", Dock = DockStyle.Fill, Height = 26, UseVisualStyleBackColor = true };
        bookCoverPanel.Controls.Add(_picBookCover, 0, 0);
        bookCoverPanel.Controls.Add(_btnPickBookCover, 0, 1);
        bookEdit.Controls.Add(bookCoverPanel, 0, 0);

        var bookFields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
        bookFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bookFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        bookFields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bookFields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _txtBookTitle = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(1, 2, 6, 2) };
        _txtBookNumber = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(1, 2, 6, 2) };
        _txtBookDate = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(1, 2, 6, 2), PlaceholderText = "YYYY-MM-DD" };
        _txtBookIsbn = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(1, 2, 6, 2) };
        _txtBookSummary = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Vertical, Margin = new Padding(1, 2, 6, 2) };
        bookFields.Controls.Add(new Label { Text = "标题", AutoSize = true, Margin = new Padding(1, 6, 4, 2) }, 0, 0);
        bookFields.Controls.Add(_txtBookTitle, 1, 0);
        bookFields.Controls.Add(new Label { Text = "卷号", AutoSize = true, Margin = new Padding(1, 6, 4, 2) }, 2, 0);
        bookFields.Controls.Add(_txtBookNumber, 3, 0);
        bookFields.Controls.Add(new Label { Text = "发售日", AutoSize = true, Margin = new Padding(1, 6, 4, 2) }, 0, 1);
        bookFields.Controls.Add(_txtBookDate, 1, 1);
        bookFields.Controls.Add(new Label { Text = "ISBN", AutoSize = true, Margin = new Padding(1, 6, 4, 2) }, 2, 1);
        bookFields.Controls.Add(_txtBookIsbn, 3, 1);
        bookFields.Controls.Add(new Label { Text = "简介", AutoSize = true, Margin = new Padding(1, 6, 4, 2) }, 0, 2);
        bookFields.Controls.Add(_txtBookSummary, 1, 2);
        bookFields.SetColumnSpan(_txtBookSummary, 3);
        var bookBtns = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        _btnSaveBook = new Button { Text = "保存该卷", AutoSize = true, UseVisualStyleBackColor = true };
        _btnSaveBook.Click += async (_, _) => await SaveSelectedBookAsync();
        bookBtns.Controls.Add(_btnSaveBook);
        bookFields.Controls.Add(bookBtns, 0, 3);
        bookFields.SetColumnSpan(bookBtns, 4);
        bookEdit.Controls.Add(bookFields, 1, 0);
        booksGrid.Controls.Add(bookEdit, 0, 2);
        tabBooks.Controls.Add(booksGrid);   // 同上：网格挂到页签
        _tabs.TabPages.Add(tabBooks);

        // ---------- 底部：保存系列 / 关闭 ----------
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var btnClose = new Button { Text = "关闭", AutoSize = true, UseVisualStyleBackColor = true };
        btnClose.Click += (_, _) => Close();
        _btnSaveSeries = new Button { Text = "保存系列信息", AutoSize = true, UseVisualStyleBackColor = true };
        _btnSaveSeries.Click += async (_, _) => await SaveSeriesAsync();
        bottom.Controls.Add(btnClose);
        bottom.Controls.Add(_btnSaveSeries);
        _statusText = new Label
        {
            Text = "就绪",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            ForeColor = Color.Gray,
            Margin = new Padding(4, 10, 8, 0),
        };
        bottom.Controls.Add(_statusText);

        Controls.Add(_tabs);
        Controls.Add(bottom);
        _tabs.SelectedIndexChanged += async (_, _) => { if (_tabs.SelectedTab == tabBooks && !_booksLoaded) await LoadBooksAsync(); };

        _btnPickSeriesCover.Click += PickSeriesCover;
        _btnResetSeriesCover.Click += ResetSeriesCover;
        _btnPickBookCover.Click += PickBookCover;
        _btnRetryCover.Click += async (_, _) => await RetryCoversAsync();
    }

    /// <summary>封面网络加载失败后的重试入口：系列封面 + 每卷缩略图一起重拉。</summary>
    private async Task RetryCoversAsync()
    {
        SetStatus("重新加载封面…", false);
        _btnRetryCover.Enabled = false;
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
            }
            _bookThumbs.Images.Clear();
            _bookThumbBytes.Clear();
            foreach (ListViewItem it in _lvBooks.Items) { it.ImageKey = ""; it.SubItems[4].Text = ""; }
            await LoadBookThumbnailsAsync();
            if (_statusText.ForeColor != Color.Firebrick) SetStatus("封面已重新加载", false);
        }
        finally
        {
            _btnRetryCover.Enabled = true;
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
            Text = $"编辑单部漫画 — 《{title}》";
            PopulateSeries();
            SetStatus($"已载入《{title}》", false);
            // 封面：加载失败必须可见（曾经静默 catch → 用户只见文字不见封面，还以为程序坏了）
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
            // 默认先装好每卷页（常用页）
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
            _bookThumbs.Images.Clear();
            _bookThumbBytes.Clear();
            _lvBooks.BeginUpdate();
            _lvBooks.Items.Clear();
            foreach (var b in _books)
            {
                var num = b.Metadata?.Number ?? "";
                var title = string.IsNullOrWhiteSpace(b.Metadata?.Title) ? b.Name : b.Metadata!.Title!;
                var item = new ListViewItem(num) { Tag = b };
                item.SubItems.Add(title);
                item.SubItems.Add(b.Metadata?.ReleaseDate ?? "");
                item.SubItems.Add(b.Metadata?.Isbn ?? "");
                item.SubItems.Add("");    // 封面列：缩略图加载后填尺寸
                item.SubItems.Add(SummaryBrief(b.Metadata?.Summary));
                _lvBooks.Items.Add(item);
            }
            _lvBooks.EndUpdate();
            SetStatus($"每卷 {_books.Count} 册", false);
            _ = LoadBookThumbnailsAsync();
        }
        catch (Exception e)
        {
            _log("ERR ", $"读取每卷数据失败: {e.Message}");
            SetStatus($"读取每卷失败: {e.Message}", true);
        }
    }

    private static string SummaryBrief(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length <= 40 ? s : s[..40] + "…";
    }

    /// <summary>逐册抓当前封面缩略图（并发 4，图放到 ImageList + 字节缓存；失败计数，加载完汇报）。</summary>
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
                    BeginInvoke(() =>
                    {
                        try
                        {
                            if (IsDisposed) return;
                            using var ms = new MemoryStream(bytes);
                            using var img = Image.FromStream(ms);
                            _bookThumbs.Images.Add(b.Id, new Bitmap(img, _bookThumbs.ImageSize));
                            var item = _lvBooks.Items.Cast<ListViewItem>().FirstOrDefault(i => ReferenceEquals(i.Tag, b));
                            if (item != null)
                            {
                                item.ImageKey = b.Id;
                                item.SubItems[4].Text = $"{bytes.Length / 1024}KB";
                            }
                        }
                        catch { /* 图片解码失败就跳过 */ }
                    });
                }
                catch
                {
                    Interlocked.Increment(ref failed);   // 网络失败不再静默：完成后在状态栏明示
                }
            });
        if (failed > 0 && !IsDisposed)
            BeginInvoke(() => SetStatus($"每卷 {_books.Count} 册（{failed} 张封面加载失败——网络抖动可点「重试封面」重拉）", true));
    }

    private async Task OnBookSelectedAsync()
    {
        if (_lvBooks.SelectedItems.Count == 0) return;
        var b = (BookDto)_lvBooks.SelectedItems[0].Tag!;
        _selectedBookId = b.Id;
        var m = b.Metadata;
        _txtBookTitle.Text = m?.Title ?? "";
        _txtBookNumber.Text = m?.Number ?? "";
        _txtBookDate.Text = m?.ReleaseDate ?? "";
        _txtBookIsbn.Text = m?.Isbn ?? "";
        _txtBookSummary.Text = m?.Summary ?? "";
        _pendingBookCover = null;
        // 大图预览：优先未保存的选图，其次已缓存的服务器原图
        if (_bookThumbBytes.TryGetValue(b.Id, out var bytes))
            ShowBookCover(bytes);
        else
        {
            _picBookCover.Image = null;
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
            _btnSaveSeries.Enabled = false;
            SetStatus("保存中…", false);
            var input = new SeriesEditInput
            {
                Title = _txtTitle.Text.Trim(),
                TitleSort = _txtTitleSort.Text.Trim(),
                Status = _cmbStatus.SelectedItem as string,
                ReadingDirection = _cmbDirection.SelectedItem as string,
                Publisher = _txtPublisher.Text.Trim(),
                Language = _txtLanguage.Text.Trim(),
                ReleaseDate = _txtReleaseDate.Text.Trim(),
                Summary = _txtSummary.Text,
                AgeRating = int.TryParse(_txtAgeRating.Text.Trim(), out var ar) ? ar : 0,
                TotalBookCount = int.TryParse(_txtTotalBooks.Text.Trim(), out var tc) && tc > 0 ? tc : null,
                Tags = SplitList(_txtTags.Text),
                Genres = SplitList(_txtGenres.Text),
                Authors = ParseLines(_txtAuthors.Text).Select(ParseAuthor).ToList(),
                AlternateTitles = ParseLines(_txtAltTitles.Text).Select(ParseAlt).ToList(),
                Links = ParseLines(_txtLinks.Text).Select(ParseLink).ToList(),
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
            // 保存后重新拉一次服务器值（PATCH 后的标签/标题以服务器为准）
            _meta = (await _komga.GetSeriesAsync(_seriesId)).Metadata ?? new SeriesMetadataDto();
            PopulateSeries();
            if (_booksLoaded) await ReloadBooksAsync();   // 卷标题/卷号可能也变了（books 列的标题从书上来，不受影响，但保险重载）
        }
        catch (Exception e)
        {
            _log("ERR ", $"保存系列信息失败: {e.Message}");
            SetStatus($"保存失败: {e.Message}", true);
        }
        finally
        {
            _btnSaveSeries.Enabled = true;
        }
    }

    private void PickSeriesCover(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择新封面（JPG/PNG）",
            Filter = "图片 (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            if (ShowSeriesCover(bytes))
            {
                _pendingSeriesCover = bytes;
                SetStatus($"已选新封面 {bytes.Length / 1024}KB（点「保存系列信息」生效）", false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取图片失败: {ex.Message}");
        }
    }

    private void ResetSeriesCover(object? sender, EventArgs e)
    {
        _pendingSeriesCover = null;
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await _komga.GetSeriesThumbnailAsync(_seriesId);
                BeginInvoke(() => { if (!IsDisposed) ShowSeriesCover(bytes); });
            }
            catch { }
        });
    }

    // ================= 保存：单卷 =================

    private async Task SaveSelectedBookAsync()
    {
        if (_selectedBookId.Length == 0) { MessageBox.Show(this, "先在列表里选中一卷"); return; }
        var b = _books.FirstOrDefault(x => x.Id == _selectedBookId);
        if (b == null) return;
        try
        {
            _btnSaveBook.Enabled = false;
            SetStatus("保存该卷…", false);
            var input = new BookEditInput
            {
                Title = _txtBookTitle.Text.Trim(),
                Number = _txtBookNumber.Text.Trim(),
                ReleaseDate = _txtBookDate.Text.Trim(),
                Isbn = _txtBookIsbn.Text.Trim(),
                Summary = _txtBookSummary.Text,
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
            // 就地刷新列表行 + 缩略图
            await ReloadBooksAsync();
            var item2 = _lvBooks.Items.Cast<ListViewItem>().FirstOrDefault(i => ((BookDto)i.Tag!).Id == b.Id);
            if (item2 != null) _lvBooks.SelectedItems.Clear();
        }
        catch (Exception e)
        {
            _log("ERR ", $"保存卷信息失败: {e.Message}");
            SetStatus($"保存失败: {e.Message}", true);
        }
        finally
        {
            _btnSaveBook.Enabled = true;
        }
    }

    private void PickBookCover(object? sender, EventArgs e)
    {
        if (_selectedBookId.Length == 0) { MessageBox.Show(this, "先在列表里选中一卷"); return; }
        using var dlg = new OpenFileDialog
        {
            Title = "选择该卷新封面（JPG/PNG）",
            Filter = "图片 (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var bytes = File.ReadAllBytes(dlg.FileName);
            ShowBookCover(bytes);
            _pendingBookCover = bytes;
            SetStatus($"已选新卷封面 {bytes.Length / 1024}KB（点「保存该卷」生效）", false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取图片失败: {ex.Message}");
        }
    }

    private async Task RescrapeVolumesAsync()
    {
        if (_rescrapeVolumes == null)
        {
            MessageBox.Show(this, "没有 Btv 链接，无法重刮每卷。\n请先用主界面「覆盖匹配」或「纠正匹配」选定正确条目。");
            return;
        }
        var dry = _dryRunProvider();
        var confirm = MessageBox.Show(this,
            $"按 Bangumi 卷号对齐每卷封面/简介/ISBN。\n模式：{(dry ? "Dry-run 预览" : "真实更新")}\n继续？",
            "重刮每卷", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;
        try
        {
            _btnRescrapeVolumes.Enabled = false;
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
            _btnRescrapeVolumes.Enabled = true;
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
        try
        {
            var img = ImageFromBytes(bytes);
            if (img == null) return false;
            var old = _picSeriesCover.Image;
            _picSeriesCover.Image = img;
            old?.Dispose();
            return true;
        }
        catch { return false; }
    }

    private void ShowBookCover(byte[] bytes)
    {
        try
        {
            var img = ImageFromBytes(bytes);
            if (img == null) return;
            var old = _picBookCover.Image;
            _picBookCover.Image = img;
            old?.Dispose();
        }
        catch { }
    }

    private static Image? ImageFromBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        var ms = new MemoryStream(bytes);   // Image 持有流 → 不 dispose
        try { return Image.FromStream(ms); }
        catch { ms.Dispose(); return null; }
    }

    private void SetStatus(string text, bool error)
    {
        if (IsDisposed) return;
        _statusText.Text = text;
        _statusText.ForeColor = error ? Color.Firebrick : Color.Gray;
    }

    /// <summary>"a，b、c , d" → ["a","b","c","d"]（中英逗号/顿号都算分隔符；空段丢弃）。</summary>
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
