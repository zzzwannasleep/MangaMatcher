using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using KomgaScraper.Core;
using KomgaScraper.Core.Sources;

namespace KomgaScraper.App;

/// <summary>
/// 设置窗口（Avalonia 版）：右对齐标签列 + 自适应输入列，分组标题横跨两列；
/// 内容过高自动滚动。保存时代理校验不过不放行。
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly AppSettings _current;
    public AppSettings EditedSettings { get; private set; } = new();

    private readonly TextBox _txtUrl = new();
    private readonly TextBox _txtApiKey = new();
    private readonly TextBox _txtUsername = new();
    private readonly TextBox _txtPassword = new() { PasswordChar = '●' };
    private readonly TextBox _txtBgmToken = new();
    private readonly ComboBox _cmbProxyScheme = new() { MinWidth = 160 };
    private readonly TextBox _txtProxyHost = new() { Watermark = "127.0.0.1 或 proxy.example.com", MinWidth = 170 };
    private readonly NumericUpDown _numProxyPort = new() { Minimum = 1, Maximum = 65535, Value = 7890, Width = 110 };
    private readonly TextBox _txtProxyUser = new();
    private readonly TextBox _txtProxyPass = new() { PasswordChar = '●' };
    private readonly TextBox _txtBkwkUrl = new();
    private readonly Button _btnBwDownload = new() { Content = "直接下载建索引", Classes = { "app" } };
    private readonly Button _btnBwOpen = new() { Content = "打开下载页", Classes = { "app" } };
    private readonly Button _btnBwImport = new() { Content = "导入本地文件…", Classes = { "app" } };
    private readonly TextBlock _lblBwStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _chkAi = new() { Content = "启用（OpenAI 兼容接口）" };
    private readonly CheckBox _chkAiTranslate = new() { Content = "翻译简介（日文/英文简介自动翻译成中文）" };
    private readonly TextBox _txtAiBase = new();
    private readonly TextBox _txtAiKey = new();
    private readonly TextBox _txtAiModel = new();
    private readonly ComboBox _cmbMatchType = new() { MinWidth = 160, ItemsSource = new[] { "漫画", "小说" } };
    private readonly NumericUpDown _numConcurrency = new() { Minimum = 1, Maximum = 8, Value = 3, Width = 100 };
    private readonly CheckBox _chkVolumeData = new() { Content = "抓取每卷详情（简介/日期/ISBN）" };
    private readonly CheckBox _chkForceRefresh = new() { Content = "强制刷新已有 Btv 链接的系列" };
    private readonly CheckBox _chkBwTags = new() { Content = "写入类型/题材标签、阅读方向（来自 BookWalker）" };
    private readonly CheckBox _chkBaike = new() { Content = "启用（其它源都查不到时按中文名查百科词条）" };
    private readonly CheckBox _chkMangaDex = new() { Content = "启用（英文圈图源，百科兜底之前；走代理）" };
    private readonly CheckBox _chkJapaneseOnly = new() { Content = "只匹配日漫（韩漫/美漫/国漫候选一律剔除）" };
    private readonly CheckBox _chkKomgaProxy = new() { Content = "Komga 连接走代理（本机/内网部署保持关闭）" };
    private readonly TextBox _txtTagWhitelist = new() { AcceptsReturn = true, Height = 56, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _txtTagBlacklist = new() { AcceptsReturn = true, Height = 56, TextWrapping = TextWrapping.Wrap };

    private string _lastProxyScheme = "http";
    private readonly Grid _grid = new();

    public SettingsWindow(AppSettings current)
    {
        _current = current;
        Title = "设置";
        Width = 680;
        Height = 780;
        MinWidth = 620;
        MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontFamily = new FontFamily("Microsoft YaHei UI, PingFang SC, Segoe UI");

        _txtUrl.Text = current.KomgaUrl;
        _txtApiKey.Text = current.KomgaApiKey;
        _txtUsername.Text = current.KomgaUsername;
        _txtPassword.Text = current.KomgaPassword;
        _txtBgmToken.Text = current.BangumiAccessToken;
        _cmbProxyScheme.ItemsSource = ProxyProtocols.All.Select(x => new ProxyItem(x.Scheme, x.Label)).ToList();
        _cmbProxyScheme.SelectedIndex = Math.Max(0,
            Array.FindIndex(ProxyProtocols.All, x => x.Scheme == ProxyProtocols.NormalizeScheme(current.ProxyScheme)));
        _txtProxyHost.Text = current.ProxyHost;
        _numProxyPort.Value = Math.Clamp(current.ProxyPort > 0 ? current.ProxyPort : 7890, 1, 65535);
        _txtProxyUser.Text = current.ProxyUser;
        _txtProxyPass.Text = current.ProxyPassword;
        _txtBkwkUrl.Text = current.BkwkUrl;
        _txtAiBase.Text = current.AiBaseUrl;
        _txtAiKey.Text = current.AiApiKey;
        _txtAiModel.Text = current.AiModel;
        _chkAi.IsChecked = current.AiEnabled;
        _chkAiTranslate.IsChecked = current.AiTranslateSummary;
        _chkVolumeData.IsChecked = current.FetchVolumeData;
        _chkForceRefresh.IsChecked = current.ForceRefreshBtv;
        _chkBwTags.IsChecked = current.BwWriteTags;
        _chkBaike.IsChecked = current.BaikeEnabled;
        _chkMangaDex.IsChecked = current.MangaDexEnabled;
        _chkJapaneseOnly.IsChecked = current.JapaneseOnly;
        _chkKomgaProxy.IsChecked = current.KomgaProxyEnabled;
        _numConcurrency.Value = Math.Clamp(current.ScrapeConcurrency, 1, 8);
        _cmbMatchType.SelectedItem = current.MatchType;
        _txtTagWhitelist.Text = string.Join("\r\n", current.TagWhitelist);
        _txtTagBlacklist.Text = string.Join("\r\n", current.TagBlacklist);
        _lblBwStatus.Text = File.Exists(AppSettings.BkwkIndexPath) ? "索引已存在 ✓" : "尚未下载";
        Ui.BindTheme(_lblBwStatus, TextBlock.ForegroundProperty, "TextSecondaryBrush");

        BuildGrid();

        // 按钮：宽度贴合文字 + 两轴文字居中（完全居中），不硬撑 MinWidth
        var btnOk = new Button
        {
            Content = "保存", Classes = { "primary" }, MinHeight = 32,
            Padding = new Thickness(20, 4), HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var btnCancel = new Button
        {
            Content = "取消", Classes = { "app" }, MinHeight = 32,
            Padding = new Thickness(20, 4), HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        btnOk.Click += async (_, _) =>
        {
            // 代理填错就别让保存，否则会在运行时到处超时
            var err = ProxyProtocols.Validate(SelectedScheme(), _txtProxyHost.Text ?? "", (int)_numProxyPort.Value);
            if (err != null)
            {
                await Ui.ErrorAsync(this, err, "代理配置有误");
                return;
            }
            EditedSettings = BuildEdited();
            Close(true);
        };
        btnCancel.Click += (_, _) => Close(false);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 12,
            Margin = new Thickness(0, 10, 16, 12),
        };
        btnPanel.Children.Add(btnOk);
        btnPanel.Children.Add(btnCancel);

        var scroll = new ScrollViewer
        {
            Content = new Border
            {
                Padding = new Thickness(10, 6, 16, 10),
                Child = _grid,
            },
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto") };
        Grid.SetRow(scroll, 0);
        Grid.SetRow(btnPanel, 1);
        root.Children.Add(scroll);
        root.Children.Add(btnPanel);
        Content = root;

        _cmbProxyScheme.SelectionChanged += (_, _) => ApplyProxyScheme();
        ApplyProxyScheme();

        _btnBwDownload.Click += async (_, _) => await DownloadBwIndexAsync();
        _btnBwOpen.Click += (_, _) => OpenBwDownloadPage();
        _btnBwImport.Click += async (_, _) => await ImportBwIndexAsync();
    }

    // ================= 网格构建 =================

    private int _row;

    private void AddToGrid(Control c, int col, int colSpan = 1)
    {
        Grid.SetColumn(c, col);
        Grid.SetRow(c, _row);
        Grid.SetColumnSpan(c, colSpan);
        _grid.Children.Add(c);
    }

    private void NextRow()
    {
        _row++;
        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
    }

    private void Section(string title)
    {
        NextRow();
        var t = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.Bold,
            FontSize = 13,
            Margin = new Thickness(2, 14, 2, 4),
        };
        Ui.BindTheme(t, TextBlock.ForegroundProperty, "AccentBrush");
        AddToGrid(t, 0, 2);
    }

    private void Field(string label, Control input)
    {
        NextRow();
        AddToGrid(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(2, 3, 10, 3),
            FontSize = 12.5,
        }, 0);
        input.HorizontalAlignment = HorizontalAlignment.Stretch;
        input.Margin = new Thickness(0, 3, 8, 3);
        AddToGrid(input, 1);
    }

    private void Hint(string text)
    {
        NextRow();
        var h = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 8, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Ui.BindTheme(h, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        // 与输入框同列（col 1）对齐 + 无 MaxWidth：跟真实列宽换行，绝不人为截断
        AddToGrid(h, 1);
    }

    private void BuildGrid()
    {
        _grid.ColumnDefinitions = new ColumnDefinitions("150,*");
        _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));   // row 0 占位（_row 从 0 开始，Section 先 ++）

        Section("Komga");
        Field("Komga 地址", _txtUrl);
        Field("API Key", _txtApiKey);
        Field("用户名（可选）", _txtUsername);
        Field("密码（可选）", _txtPassword);

        Section("Bangumi");
        Field("Token（可选）", _txtBgmToken);

        Section("代理（Bangumi / 封面 / BookWalker 共用）");
        Field("协议", _cmbProxyScheme);
        var hostRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        hostRow.Children.Add(_txtProxyHost);
        hostRow.Children.Add(new TextBlock { Text = "端口", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 });
        hostRow.Children.Add(_numProxyPort);
        Field("地址", hostRow);
        Field("账号（可选）", _txtProxyUser);
        Field("密码（可选）", _txtProxyPass);
        Hint("支持 HTTP / HTTPS / SOCKS4(a) / SOCKS5（含账密认证）。留空 = 不使用代理。");

        Section("刮削行为");
        Field("匹配类型", _cmbMatchType);
        Field("卷详情", _chkVolumeData);
        Field("强制刷新", _chkForceRefresh);
        Field("百科兜底", _chkBaike);
        Field("MangaDex", _chkMangaDex);
        Field("日漫限定", _chkJapaneseOnly);
        Field("Komga 代理", _chkKomgaProxy);
        var concRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        concRow.Children.Add(_numConcurrency);
        var concHint = new TextBlock
        {
            Text = "个系列同时刮削（1 = 逐个）",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        Ui.BindTheme(concHint, TextBlock.ForegroundProperty, "TextSecondaryBrush");
        concRow.Children.Add(concHint);
        Field("批量并发", concRow);

        Section("版本标记（黑白名单）");
        Field("白名单", _txtTagWhitelist);
        Field("黑名单", _txtTagBlacklist);
        Hint("每行一个词。白名单 = 后缀命中即落一个标签；黑名单 = 命中即丢弃（标题旧后缀一并清掉）。\r\n" +
             "例：黑名单 join → 乱马1/2（join）变 乱马1/2；白名单 C.C → 落 C.C 标签。");

        Section("BookWalker 数据源");
        Field("目录库地址", _txtBkwkUrl);
        Field("推断字段", _chkBwTags);
        var bwRow1 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        bwRow1.Children.Add(_btnBwDownload);
        bwRow1.Children.Add(_lblBwStatus);
        Field("本地索引", bwRow1);
        var bwRow2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        bwRow2.Children.Add(_btnBwOpen);
        bwRow2.Children.Add(_btnBwImport);
        Field("浏览器下载", bwRow2);
        Hint("直连下载报 403 时：点『打开下载页』用浏览器下载，再点『导入本地文件…』。");

        Section("AI 匹配（可选）");
        Field("AI 匹配", _chkAi);
        Field("简介翻译", _chkAiTranslate);
        Field("接口地址", _txtAiBase);
        Field("API Key", _txtAiKey);
        Field("模型", _txtAiModel);
        Hint("AI 兼容 OpenAI 接口（如 DeepSeek）；匹配与简介翻译共用 API Key");

        var txtDataDir = new TextBox
        {
            Text = AppSettings.DataDir,
            IsReadOnly = true,
            Background = Brushes.Transparent,
        };
        Field("数据目录", txtDataDir);
        Hint("绿色便携：设置与索引都存这里");
    }

    // ================= 行为 =================

    private void SetBwStatus(string m)
    {
        if (Dispatcher.UIThread.CheckAccess()) _lblBwStatus.Text = m;
        else Dispatcher.UIThread.Post(() => _lblBwStatus.Text = m);
    }

    private void SetBwBusy(bool busy)
    {
        _btnBwDownload.IsEnabled = !busy;
        _btnBwOpen.IsEnabled = !busy;
        _btnBwImport.IsEnabled = !busy;
    }

    /// <summary>用系统浏览器打开目录库下载地址（浏览器能过 Cloudflare 人机校验）。</summary>
    private void OpenBwDownloadPage()
    {
        var url = _txtBkwkUrl.Text?.Trim() ?? "";
        if (url.Length == 0) url = BookWalkerService.DefaultDownloadUrl;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
            SetBwStatus("已在浏览器打开，下载完成后点『导入本地文件…』");
        }
        catch (Exception e)
        {
            SetBwStatus("打开失败: " + e.Message);
        }
    }

    private async Task DownloadBwIndexAsync()
    {
        SetBwBusy(true);
        try
        {
            SetBwStatus("准备下载…");
            var svc = new BookWalkerService(new SettingsWindowLogger(SetBwStatus));
            var proxy = CurrentProxy();
            await Task.Run(() => svc.BuildIndexAsync(
                _txtBkwkUrl.Text?.Trim() ?? "", AppSettings.BkwkDbPath, AppSettings.BkwkIndexPath, proxy,
                (_, _, m) => SetBwStatus(m)));
            SetBwStatus("完成 ✓");
            await Ui.InfoAsync(this, $"索引已构建：{AppSettings.BkwkIndexPath}\n点『保存』生效。");
        }
        catch (Exception e)
        {
            SetBwStatus("失败: " + e.Message);
            await Ui.ErrorAsync(this, $"下载/构建失败：{e.Message}");
        }
        finally
        {
            SetBwBusy(false);
        }
    }

    private async Task ImportBwIndexAsync()
    {
        var file = await Ui.PickFileAsync(this,
            "选择已下载的 BookWalker 目录库（bkwk-db.sqlite.zst 或解压后的 .sqlite）",
            ["*.zst", "*.sqlite", "*.db"]);
        if (file == null) return;

        SetBwBusy(true);
        try
        {
            SetBwStatus("导入中…");
            var svc = new BookWalkerService(new SettingsWindowLogger(SetBwStatus));
            await Task.Run(() => svc.ImportLocalAsync(
                file, AppSettings.BkwkDbPath, AppSettings.BkwkIndexPath,
                (_, _, m) => SetBwStatus(m)));
            SetBwStatus("导入完成 ✓");
            await Ui.InfoAsync(this, $"索引导入完成：{AppSettings.BkwkIndexPath}\n点『保存』生效。");
        }
        catch (Exception e)
        {
            SetBwStatus("导入失败: " + e.Message);
            await Ui.ErrorAsync(this, $"导入失败：{e.Message}");
        }
        finally
        {
            SetBwBusy(false);
        }
    }

    private sealed class SettingsWindowLogger(Action<string> status) : IAppLogger
    {
        public void Info(string m) => status(m);
        public void Warn(string m) => status(m);
        public void Error(string m) => status(m);
        public void Progress(int c, int t, string m) => status(m);
    }

    /// <summary>下拉项：界面显示中文标签，值存协议名。</summary>
    private sealed record ProxyItem(string Scheme, string Label)
    {
        public override string ToString() => Label;
    }

    private string SelectedScheme() => (_cmbProxyScheme.SelectedItem as ProxyItem)?.Scheme ?? "";

    /// <summary>协议为空 = 不使用代理；切换协议时只在端口还是"上一协议的默认值"时才跟着换端口。</summary>
    private void ApplyProxyScheme()
    {
        var scheme = SelectedScheme();
        var on = scheme.Length > 0;
        _txtProxyHost.IsEnabled = _numProxyPort.IsEnabled = _txtProxyUser.IsEnabled = _txtProxyPass.IsEnabled = on;
        if (!on) return;
        if ((int)_numProxyPort.Value == ProxyProtocols.DefaultPort(_lastProxyScheme))
            _numProxyPort.Value = Math.Clamp(ProxyProtocols.DefaultPort(scheme), 1, 65535);
        _lastProxyScheme = scheme;
    }

    /// <summary>当前界面上的代理（点『直接下载建索引』时还没保存，必须用界面值）。</summary>
    private ProxyConfig CurrentProxy()
    {
        var scheme = SelectedScheme();
        var host = _txtProxyHost.Text?.Trim() ?? "";
        if (scheme.Length == 0 || host.Length == 0) return ProxyConfig.None;
        return new ProxyConfig($"{scheme}://{host}:{(int)_numProxyPort.Value}",
            _txtProxyUser.Text?.Trim() ?? "", _txtProxyPass.Text ?? "");
    }

    /// <summary>"a\r\nb, c" → ["a","b","c"]（换行/逗号都是分隔符；空段丢弃）。</summary>
    private static List<string> SplitLines(string text) =>
        [.. text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private AppSettings BuildEdited() => new()
    {
        KomgaUrl = _txtUrl.Text?.Trim() ?? "",
        KomgaApiKey = _txtApiKey.Text?.Trim() ?? "",
        KomgaUsername = _txtUsername.Text?.Trim() ?? "",
        KomgaPassword = _txtPassword.Text ?? "",
        BangumiAccessToken = _txtBgmToken.Text?.Trim() ?? "",
        ProxyScheme = SelectedScheme(),
        ProxyHost = _txtProxyHost.Text?.Trim() ?? "",
        ProxyPort = (int)_numProxyPort.Value,
        ProxyUser = _txtProxyUser.Text?.Trim() ?? "",
        ProxyPassword = _txtProxyPass.Text ?? "",
        // 已迁移到 proxy* 字段，清掉旧值，避免删掉代理后老配置"复活"
        LegacyBangumiProxy = "",
        BangumiApiBase = _current.BangumiApiBase,
        BkwkUrl = _txtBkwkUrl.Text?.Trim() ?? "",
        AiEnabled = _chkAi.IsChecked == true,
        AiTranslateSummary = _chkAiTranslate.IsChecked == true,
        AiBaseUrl = _txtAiBase.Text?.Trim() ?? "",
        AiApiKey = _txtAiKey.Text?.Trim() ?? "",
        AiModel = _txtAiModel.Text?.Trim() ?? "",
        MatchType = _cmbMatchType.SelectedItem?.ToString() ?? "漫画",
        FetchVolumeData = _chkVolumeData.IsChecked == true,
        ScrapeConcurrency = (int)_numConcurrency.Value,
        BwWriteTags = _chkBwTags.IsChecked == true,
        ForceRefreshBtv = _chkForceRefresh.IsChecked == true,
        DryRun = _current.DryRun,
        Theme = _current.Theme,
        LastLibraryId = _current.LastLibraryId,
        MylarOutputDir = _current.MylarOutputDir,
        TagWhitelist = SplitLines(_txtTagWhitelist.Text ?? ""),
        TagBlacklist = SplitLines(_txtTagBlacklist.Text ?? ""),
        BaikeEnabled = _chkBaike.IsChecked == true,
        MangaDexEnabled = _chkMangaDex.IsChecked == true,
        JapaneseOnly = _chkJapaneseOnly.IsChecked == true,
        KomgaProxyEnabled = _chkKomgaProxy.IsChecked == true,
    };
}