using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using KomgaScraper.Core.Scraping;
using KomgaScraper.Core.Sources;

namespace KomgaScraper.App;

/// <summary>
/// 刮削候选选择面板（Avalonia 版）：
/// 左侧候选卡片（封面+标题+作者，来源分组），右侧选中候选的详情
/// （大封面 + 中文名/原名/作者/别名/来源 + 完整简介）。
/// 卡片 hover 放大浮起动效、选中强调色描边（见 App.axaml .card 样式）。
/// </summary>
public sealed partial class MatchWindow : Window
{
    public SearchCandidate? SelectedCandidate { get; private set; }

    private readonly List<SearchCandidate> _candidates;
    private readonly BangumiClient _bangumi;
    private readonly MoxClient? _mox;
    private readonly BookWalkerIndex? _bwIndex;
    private readonly BaikeClient? _baike;
    private readonly MangaDexClient? _mdex;
    private readonly Func<string, string?, Task<byte[]?>> _downloadCover;

    private sealed record Card(SearchCandidate Cand, Border Box, Image Pic, TextBlock Title, TextBlock Author);
    private readonly List<Card> _cards = [];
    private SearchCandidate? _previewed;
    private int _detailGeneration;

    public MatchWindow(string seriesName, string searchTerm, List<SearchCandidate> candidates,
        BangumiClient bangumi, MoxClient? mox, BookWalkerIndex? bwIndex, BaikeClient? baike, MangaDexClient? mdex,
        Func<string, string?, Task<byte[]?>> downloadCover)
    {
        InitializeComponent();
        AvaloniaXamlLoader.Load(this);

        _candidates = [.. candidates];
        _bangumi = bangumi;
        _mox = mox;
        _bwIndex = bwIndex;
        _baike = baike;
        _mdex = mdex;
        _downloadCover = downloadCover;
        Title = $"为《{seriesName}》选择匹配条目（搜索词：{searchTerm}）";

        // 卡片按源分组展示，配小节标题；没有候选的源整组不显示
        AddGroup("Bangumi", "Bgm");
        AddGroup("BookWalker", BookWalkerIndex.SourceBw);
        AddGroup("Mox bookof.moe", BatchMatcher.SourceMox);
        AddGroup("MangaDex", BatchMatcher.SourceMangaDex);
        AddGroup("百度百科", BatchMatcher.SourceBaike);
        var knownSources = new HashSet<string>
            { "Bgm", BookWalkerIndex.SourceBw, BatchMatcher.SourceMox, BatchMatcher.SourceMangaDex, BatchMatcher.SourceBaike };
        var rest = _candidates.Where(c => !knownSources.Contains(c.Source)).ToList();
        if (rest.Count > 0) AddRawGroup("其它", rest);

        _ = LoadCoversAsync();
    }

    // ================= 卡片构建 =================

    private void AddGroup(string label, string source)
    {
        var group = _candidates.Where(c => c.Source == source).ToList();
        if (group.Count > 0) AddRawGroup(label, group);
    }

    private void AddRawGroup(string label, List<SearchCandidate> group)
    {
        var groupLabel = new TextBlock
        {
            Text = $"{label}（{group.Count}）",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(2, 8, 0, 4),
        };
        Ui.BindTheme(groupLabel, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        CardsPanel.Children.Add(groupLabel);
        var wrap = new WrapPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        foreach (var c in group) wrap.Children.Add(BuildCard(c));
        CardsPanel.Children.Add(wrap);
    }

    private Border BuildCard(SearchCandidate c)
    {
        var pic = new Image
        {
            Width = 116,
            Height = 138,
            Stretch = Stretch.Uniform,
        };
        var title = new TextBlock
        {
            Text = Truncate(SourceTag(c) + c.Title, 14),
            FontWeight = FontWeight.SemiBold,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var author = new TextBlock
        {
            Text = Truncate(c.Author, 12),
            FontSize = 11,
            TextAlignment = Avalonia.Media.TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Ui.BindTheme(author, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        var box = new Border
        {
            Classes = { "card" },
            Width = 126,
            Child = new StackPanel { Classes = { "cardInner" }, Children = { pic, title, author } },
            Tag = c,
        };
        box.PointerReleased += (_, _) => SelectCard(box);
        var card = new Card(c, box, pic, title, author);
        _cards.Add(card);
        return box;
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];

    private static string SourceTag(SearchCandidate c) =>
        c.Source == BookWalkerIndex.SourceBw ? "[BW]" :
        c.Source == BatchMatcher.SourceMox ? "[MOX]" :
        c.Source == BatchMatcher.SourceMangaDex ? "[MD]" :
        c.Source == BatchMatcher.SourceBaike ? "[百科]" : "";

    private void SelectCard(Border box)
    {
        foreach (var card in _cards) card.Box.Classes.Remove("selected");
        box.Classes.Add("selected");
        SelectedCandidate = (SearchCandidate)box.Tag!;
        BtnOk.IsEnabled = true;
        _ = ShowDetailAsync(SelectedCandidate!);
    }

    // ================= 详情 =================

    /// <summary>右侧详情：大封面 + 元数据行 + 完整简介（按候选来源现取）。</summary>
    private async Task ShowDetailAsync(SearchCandidate c)
    {
        if (_previewed == c) return;
        _previewed = c;
        var gen = ++_detailGeneration;

        // 1) 大封面
        try
        {
            var bytes = await DownloadCoverAsync(c.CoverUrl, c.Source);
            if (gen != _detailGeneration) return;
            if (bytes is { Length: > 0 })
                DetailImage.Source = ImageFromBytes(bytes);
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
            if (gen != _detailGeneration) return;
            DetailMeta.Text = detail.Meta;
            DetailSummary.Text = string.IsNullOrWhiteSpace(detail.Summary) ? "（该来源没有简介）" : detail.Summary;
        }
        catch (Exception e)
        {
            if (gen != _detailGeneration) return;
            DetailMeta.Text = SourceTag(c) + c.Title;
            DetailSummary.Text = $"（详情加载失败：{e.Message}）";
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
                return await _baike.DownloadImageAsync(url);
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

    /// <summary>并行加载候选小封面。</summary>
    private async Task LoadCoversAsync()
    {
        var tasks = _cards.Select(async card =>
        {
            if (string.IsNullOrEmpty(card.Cand.CoverUrl)) return;
            try
            {
                var bytes = await DownloadCoverAsync(card.Cand.CoverUrl, card.Cand.Source);
                if (bytes is not { Length: > 0 }) return;
                var img = ImageFromBytes(bytes);
                if (img == null) return;
                await Dispatcher.UIThread.InvokeAsync(() => card.Pic.Source = img);
            }
            catch
            {
                // 封面加载失败保持占位背景
            }
        });
        await Task.WhenAll(tasks);
    }

    private static Bitmap? ImageFromBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return null;
        try { return new Bitmap(new MemoryStream(bytes)); }
        catch { return null; }
    }

    // ================= 按钮 =================

    private void OnOk(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SelectedCandidate == null) return;
        Close(true);
    }

    private async void OnManual(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var dlg = new ManualInputWindow();
        if (await dlg.ShowDialog<string?>(this) is not { Length: > 0 } input) return;
        var m = System.Text.RegularExpressions.Regex.Match(input, @"subject/(\d+)");
        int id;
        if (m.Success && int.TryParse(m.Groups[1].Value, out var parsed)) id = parsed;
        else if (int.TryParse(input.Trim(), out parsed)) id = parsed;
        else { await Ui.MessageAsync(this, "无法识别 ID", "错误", okOnly: true, error: true); return; }

        SelectedCandidate = new SearchCandidate(id, $"手动输入 subject {id}", "", "", "", null);
        Close(true);
    }

    private void OnCancel(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close(false);
}