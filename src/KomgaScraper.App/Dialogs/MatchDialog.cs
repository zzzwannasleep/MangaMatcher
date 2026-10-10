using KomgaScraper.Core.Scraping;
using KomgaScraper.Core.Sources;

namespace KomgaScraper.App.Dialogs;

/// <summary>
/// 刮削候选选择面板（对齐脚本 showBookSelectionPanel）：
/// 左侧候选卡片（封面+标题+作者，来源角标），右侧选中候选的详情
/// （大封面 + 中文名/原名/作者/别名/来源 + 完整简介 —— 选之前就能确认是不是这部）。
/// 「确定」按钮在右侧，封面加载前就能直接选卡片回车确认。
/// </summary>
internal sealed class MatchDialog : Form
{
    public SearchCandidate? SelectedCandidate { get; private set; }

    private readonly SearchCandidate[] _candidates;
    private readonly BangumiClient _bangumi;
    private readonly MoxClient? _mox;
    private readonly BookWalkerIndex? _bwIndex;
    private readonly BaikeClient? _baike;
    private readonly MangaDexClient? _mdex;
    private readonly Func<string, string?, Task<byte[]?>> _downloadCover;
    private readonly FlowLayoutPanel _cards = new()
    {
        Dock = DockStyle.Fill, AutoScroll = true,
        FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8),
        WrapContents = true,
    };
    private readonly PictureBox _picDetail;
    private readonly TextBox _txtMeta;
    private readonly TextBox _txtSummary;
    private readonly Button _btnOk;
    private SearchCandidate? _previewed;
    private int _detailGeneration;

    public MatchDialog(string seriesName, string searchTerm, List<SearchCandidate> candidates,
        BangumiClient bangumi, MoxClient? mox, BookWalkerIndex? bwIndex, BaikeClient? baike, MangaDexClient? mdex,
        Func<string, string?, Task<byte[]?>> downloadCover)
    {
        _candidates = [.. candidates];
        _bangumi = bangumi;
        _mox = mox;
        _bwIndex = bwIndex;
        _baike = baike;
        _mdex = mdex;
        _downloadCover = downloadCover;

        Text = $"为《{seriesName}》选择匹配条目（搜索词：{searchTerm}）";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(1180, 560);
        Font = new Font("Microsoft YaHei UI", 9F);

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(8),
        };
        var btnCancel = new Button { Text = "取消（不刮削）", DialogResult = DialogResult.Cancel, Width = 130 };
        var btnManual = new Button { Text = "手动输入 Bangumi 链接/ID…", Width = 190 };
        btnManual.Click += async (_, _) => await ManualInputAsync();
        _btnOk = new Button { Text = "确认匹配", DialogResult = DialogResult.None, Width = 110, Enabled = false };
        _btnOk.Click += (_, _) => { if (SelectedCandidate != null) DialogResult = DialogResult.OK; };
        btnPanel.Controls.Add(btnCancel);
        btnPanel.Controls.Add(btnManual);
        btnPanel.Controls.Add(_btnOk);

        // ---- 左：候选卡片流 ----
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 4, 0) };
        left.Controls.Add(_cards);
        _cards.Dock = DockStyle.Fill;

        // ---- 右：详情面板（固定 300 宽）----
        var right = new Panel { Dock = DockStyle.Right, Width = 300, Padding = new Padding(4, 0, 0, 0) };
        _picDetail = new PictureBox
        {
            Dock = DockStyle.Top, Height = 260, SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(242, 242, 242),
        };
        _txtMeta = new TextBox
        {
            Dock = DockStyle.Top, Multiline = true, ReadOnly = true, Height = 96,
            ScrollBars = ScrollBars.Vertical, BackColor = Color.White, WordWrap = true,
        };
        _txtSummary = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, BackColor = Color.White, WordWrap = true,
        };
        // Dock 按加入顺序逆序处理：pic 先占顶部，meta 其后，summary 吃剩余高度
        right.Controls.Add(_txtSummary);
        right.Controls.Add(_txtMeta);
        right.Controls.Add(_picDetail);

        Controls.Add(left);
        Controls.Add(right);
        Controls.Add(btnPanel);
        CancelButton = btnCancel;
        AcceptButton = _btnOk;

        // 卡片按源分组展示，配小节标题；没有候选的源整组不显示
        AddGroup("Bangumi", "Bgm");
        AddGroup("BookWalker", BookWalkerIndex.SourceBw);
        AddGroup("Mox bookof.moe", BatchMatcher.SourceMox);
        AddGroup("MangaDex", BatchMatcher.SourceMangaDex);
        AddGroup("百度百科", BatchMatcher.SourceBaike);
        // 兜底：不属于上面五组的来源
        var knownSources = new HashSet<string> { "Bgm", BookWalkerIndex.SourceBw, BatchMatcher.SourceMox, BatchMatcher.SourceMangaDex, BatchMatcher.SourceBaike };
        var rest = _candidates.Where(c => !knownSources.Contains(c.Source)).ToList();
        if (rest.Count > 0) AddRawGroup("其它", rest);

        void AddGroup(string label, string source)
        {
            var group = _candidates.Where(c => c.Source == source).ToList();
            if (group.Count > 0) AddRawGroup(label, group);
        }
        void AddRawGroup(string label, List<SearchCandidate> group)
        {
            _cards.Controls.Add(new Label
            {
                Text = $"{label}（{group.Count}）",
                Width = 840,
                Height = 22,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold),
                ForeColor = Color.DimGray,
                Margin = new Padding(2, 6, 2, 2),
            });
            foreach (var c in group) _cards.Controls.Add(BuildCard(c));
        }

        Shown += async (_, _) => await LoadCoversAsync();

        // 高 DPI：布局等比放大
        float dpi = DeviceDpi / 96f;
        if (dpi > 1.01f) Scale(new SizeF(dpi, dpi));
    }

    private Panel BuildCard(SearchCandidate c)
    {
        var card = new Panel
        {
            Size = new Size(122, 188),
            Margin = new Padding(5),
            BorderStyle = BorderStyle.FixedSingle,
            Cursor = Cursors.Hand,
            Tag = c,
        };
        var pic = new PictureBox
        {
            Size = new Size(118, 140), Location = new Point(1, 1),
            SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(240, 240, 240), Tag = "pic",
        };
        var title = new Label
        {
            Text = Truncate(SourceTag(c) + c.Title, 14),
            Location = new Point(0, 142), Size = new Size(120, 22),
            Font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true,
        };
        var author = new Label
        {
            Text = Truncate(c.Author, 12), Location = new Point(0, 164), Size = new Size(120, 18),
            ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true,
        };
        card.Controls.AddRange([pic, title, author]);
        card.Click += (_, _) => SelectCard(card);
        pic.Click += (_, _) => SelectCard(card);
        title.Click += (_, _) => SelectCard(card);
        author.Click += (_, _) => SelectCard(card);
        return card;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private static string SourceTag(SearchCandidate c) =>
        c.Source == BookWalkerIndex.SourceBw ? "[BW]" :
        c.Source == BatchMatcher.SourceMox ? "[MOX]" :
        c.Source == BatchMatcher.SourceMangaDex ? "[MD]" :
        c.Source == BatchMatcher.SourceBaike ? "[百科]" : "";

    private void SelectCard(Panel card)
    {
        foreach (Control other in _cards.Controls)
            if (other is Panel p) p.BorderStyle = BorderStyle.FixedSingle;
        card.BorderStyle = BorderStyles.Selected;
        SelectedCandidate = (SearchCandidate)card.Tag!;
        _btnOk.Enabled = true;
        _ = ShowDetailAsync(SelectedCandidate!);
    }

    /// <summary>
    /// 右侧详情：大封面 + 中文名/原名/作者/别名/来源 + 完整简介。
    /// 简介按候选来源现取：Bgm→Bangumi 详情接口（走全局节流），Bw→本地索引描述，Mox→详情页。
    /// </summary>
    private async Task ShowDetailAsync(SearchCandidate c)
    {
        if (_previewed == c) return;
        _previewed = c;
        var gen = ++_detailGeneration;

        // 1) 大封面（候选小封面同一个 URL，只是放更大）
        try
        {
            var bytes = await DownloadCoverAsync(c.CoverUrl, c.Source);
            if (gen != _detailGeneration || IsDisposed) return;
            if (bytes is { Length: > 0 })
            {
                using var ms = new MemoryStream(bytes);
                _picDetail.Image = Image.FromStream(ms);
            }
        }
        catch { /* 详情封面加载失败不阻断 */ }

        // 2) 元数据行 + 简介
        try
        {
            (string Meta, string? Summary) detail = c.Source switch
            {
                _ when c.Source == BookWalkerIndex.SourceBw => await BwDetailAsync(c),
                _ when c.Source == BatchMatcher.SourceMox => await MoxDetailAsync(c),
                _ when c.Source == BatchMatcher.SourceMangaDex => await MDDetailAsync(c),
                _ when c.Source == BatchMatcher.SourceBaike => await BaikeDetailAsync(c),
                _ => await BangumiDetailAsync(c),
            };
            if (gen != _detailGeneration || IsDisposed) return;
            _txtMeta.Text = detail.Meta;
            _txtSummary.Text = string.IsNullOrWhiteSpace(detail.Summary) ? "（该来源没有简介）" : detail.Summary;
            _txtSummary.SelectionStart = 0;
            _txtSummary.ScrollToCaret();
        }
        catch (Exception e)
        {
            if (gen != _detailGeneration || IsDisposed) return;
            _txtMeta.Text = SourceTag(c) + c.Title;
            _txtSummary.Text = $"（详情加载失败：{e.Message}）";
        }
    }

    private async Task<(string Meta, string? Summary)> BangumiDetailAsync(SearchCandidate c)
    {
        var s = await _bangumi.GetSubjectAsync(c.Id);
        var author = SubjectMapper.ParseInfobox(s.Infobox, "作者") ?? SubjectMapper.ParseInfobox(s.Infobox, "作画") ?? "";
        var aliases = SubjectMapper.ExtractAliases(s.Infobox);
        var meta = $"中文名：{FirstOf(s.NameCn, s.Name)}\n原名：{s.Name}\n" +
                   $"作者：{FirstOf(author, "未知")}\n" +
                   (aliases.Length > 0 ? $"别名：{Truncate(aliases, 80)}\n" : "") +
                   $"来源：Bangumi #{s.Id}{(s.Nsfw == true ? "（R18）" : "")}";
        return (meta, SubjectMapper.CleanMultiline(s.Summary));
    }

    private Task<(string Meta, string? Summary)> BwDetailAsync(SearchCandidate c)
    {
        var bw = _bwIndex?.FindById(c.BwId ?? "");
        if (bw == null) return Task.FromResult(($"来源：BookWalker {c.BwId}", (string?)"（索引里找不到该条目）"));
        var meta = $"标题：{bw.Title}\n" +
                   (bw.AltTitles.Count > 0 ? $"别名：{Truncate(string.Join(" / ", bw.AltTitles.Take(4)), 80)}\n" : "") +
                   $"作者：{FirstOf(string.Join(" / ", bw.Authors.Select(a => a.Name)), "未知")}\n" +
                   (bw.Publisher.Length > 0 ? $"出版社：{bw.Publisher}\n" : "") +
                   $"来源：BookWalker（{bw.VolumeCount} 卷）";
        return Task.FromResult((meta, (string?)SubjectMapper.StripHtml(bw.Description)));
    }

    private async Task<(string Meta, string? Summary)> MoxDetailAsync(SearchCandidate c)
    {
        if (_mox == null || string.IsNullOrEmpty(c.MoxId))
            return ("来源：Mox", (string?)"（Mox 客户端未初始化）");
        var mox = await _mox.GetSeriesAsync(c.MoxId!);
        var meta = $"标题：{FirstOf(mox.Title, "-")}\n原名：{mox.OriTitle}\n" +
                   $"作者：{FirstOf(string.Join(" / ", mox.Authors), "未知")}\n" +
                   (mox.Publisher.Length > 0 ? $"出版社：{mox.Publisher}\n" : "") +
                   $"来源：Mox bookof.moe#{mox.Id}{(mox.IsAdult ? "（R18）" : "")}";
        return (meta, (string?)mox.Summary);
    }

    private async Task<(string Meta, string? Summary)> MDDetailAsync(SearchCandidate c)
    {
        if (_mdex == null || string.IsNullOrEmpty(c.MdId))
            return ("来源：MangaDex", (string?)"（MD 客户端未初始化）");
        var md = await _mdex.GetAsync(c.MdId!);
        if (md == null) return ($"来源：MangaDex {c.Title}", (string?)"（条目详情取回失败）");
        var meta = $"标题：{md.Title}\n原名：{md.OriTitle}\n" +
                   (md.AltTitles.Count > 0 ? $"别名：{Truncate(string.Join(" / ", md.AltTitles.Take(3)), 80)}\n" : "") +
                   (md.Tags.Count > 0 ? $"标签：{Truncate(string.Join(" / ", md.Tags.Take(6)), 80)}\n" : "") +
                   (md.Status.Length > 0 ? $"状态：{md.Status}\n" : "") +
                   "来源：MangaDex";
        return (meta, (string?)md.Description);
    }

    private async Task<(string Meta, string? Summary)> BaikeDetailAsync(SearchCandidate c)
    {
        if (_baike == null) return ("来源：百度百科", (string?)"（百科客户端未初始化）");
        var card = await _baike.SearchCardAsync(c.Title);
        if (card == null) return ($"来源：百度百科 {c.Title}", (string?)"（词条详情取回失败）");
        var author = FirstOf(card.Field("作者") is { Length: > 0 } a ? a : card.Field("作画"), "未知");
        var publisher = card.Field("出版社") is { Length: > 0 } p ? p : card.Field("其他出版社");
        var meta = $"标题：{card.Title}\n" +
                   (card.Field("原版名称") is { Length: > 0 } o ? $"原名：{o}\n" : "") +
                   $"作者：{author}\n" +
                   (publisher.Length > 0 ? $"出版社：{publisher}\n" : "") +
                   "来源：百度百科";
        return (meta, (string?)FirstOf(card.Abstract, card.Desc));
    }

    private static string FirstOf(string? a, string? fallback) =>
        !string.IsNullOrWhiteSpace(a) ? a.Trim() : (fallback?.Trim() ?? "");

    /// <summary>按候选来源选封面下载器：Bgm 走 Bangumi（自带 CDN 代理），BW/Mox/百科走通用封面客户端。</summary>
    private async Task<byte[]?> DownloadCoverAsync(string? url, string source)
    {
        if (string.IsNullOrEmpty(url)) return null;
        try
        {
            if (source == BatchMatcher.SourceBaike && _baike != null)
                return await _baike.DownloadImageAsync(url);   // bkimg 防盗链：必须带 Referer 直连
            if (source == BatchMatcher.SourceMangaDex || source == BatchMatcher.SourceMox ||
                source == BatchMatcher.SourceBaike || source == BookWalkerIndex.SourceBw)
                return await _downloadCover(url, null);
            return await _bangumi.DownloadImageAsync(url);
        }
        catch
        {
            return null;
        }
    }

    private async Task ManualInputAsync()
    {
        using var dlg = new ManualMatchDialog();
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var m = System.Text.RegularExpressions.Regex.Match(dlg.Input, @"subject/(\d+)");
        int id;
        if (m.Success && int.TryParse(m.Groups[1].Value, out var parsed)) id = parsed;
        else if (int.TryParse(dlg.Input.Trim(), out parsed)) id = parsed;
        else { MessageBox.Show("无法识别 ID"); return; }

        SelectedCandidate = new SearchCandidate(id, $"手动输入 subject {id}", "", "", "", null);
        DialogResult = DialogResult.OK;
        await Task.CompletedTask;
    }

    /// <summary>并行加载候选小封面（Bangumi 候选走 Bangumi 客户端；BW/Mox 走通用封面客户端）。</summary>
    private async Task LoadCoversAsync()
    {
        var tasks = _candidates.Select(async c =>
        {
            if (string.IsNullOrEmpty(c.CoverUrl)) return;
            try
            {
                var bytes = await DownloadCoverAsync(c.CoverUrl, c.Source);
                if (bytes is not { Length: > 0 }) return;
                using var ms = new MemoryStream(bytes);
                var img = Image.FromStream(ms);
                // 找到对应卡片并设置图片
                foreach (Control card in _cards.Controls)
                {
                    if (card is Panel p && p.Tag is SearchCandidate tag && tag.Id == c.Id &&
                        tag.Source == c.Source && tag.MoxId == c.MoxId)
                    {
                        var pic = p.Controls.OfType<PictureBox>().First();
                        // 弹窗已关时 BeginInvoke 会抛 ObjectDisposedException（全局"发生错误"弹窗的来源）
                        if (IsDisposed || Disposing) return;
                        BeginInvoke(() => { if (!IsDisposed) pic.Image = img; });
                    }
                }
            }
            catch
            {
                // 封面加载失败保持占位背景
            }
        });
        await Task.WhenAll(tasks);
    }
}

/// <summary>Selected 边框样式包装（避免枚举歧义）。</summary>
internal static class BorderStyles
{
    public const BorderStyle Selected = System.Windows.Forms.BorderStyle.Fixed3D;
}