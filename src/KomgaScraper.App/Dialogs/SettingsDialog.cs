using KomgaScraper.Core;
using KomgaScraper.Core.Sources;

namespace KomgaScraper.App;

/// <summary>
/// 设置对话框：对称两列网格（右对齐标签列 + 自适应输入列），分组标题横跨两列；
/// 窗口过矮时内容区自动滚动，控件不会被裁掉。
/// AutoScaleMode.Dpi：高 DPI 屏幕上布局与字体同步缩放，避免"字大框小"。
/// </summary>
internal sealed class SettingsDialog : Form
{
    private readonly AppSettings _current;
    public AppSettings EditedSettings { get; private set; } = new();

    private readonly TextBox _txtUrl = new();
    private readonly TextBox _txtApiKey = new();
    private readonly TextBox _txtUsername = new();
    private readonly TextBox _txtPassword = new() { UseSystemPasswordChar = true };
    private readonly TextBox _txtBgmToken = new();
    private readonly ComboBox _cmbProxyScheme = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _txtProxyHost = new();
    private readonly NumericUpDown _numProxyPort = new() { Minimum = 1, Maximum = 65535, Value = 7890 };
    private readonly TextBox _txtProxyUser = new();
    private readonly TextBox _txtProxyPass = new() { UseSystemPasswordChar = true };
    private readonly TextBox _txtBkwkUrl = new();
    private readonly Button _btnBwDownload = new() { Text = "直接下载建索引", AutoSize = true };
    private readonly Button _btnBwOpen = new() { Text = "打开下载页", AutoSize = true };
    private readonly Button _btnBwImport = new() { Text = "导入本地文件…", AutoSize = true };
    private readonly Label _lblBwStatus = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly CheckBox _chkAi = new() { Text = "启用（OpenAI 兼容接口）", AutoSize = true };
    private readonly CheckBox _chkAiTranslate = new() { Text = "翻译简介（日文/英文简介自动翻译成中文）", AutoSize = true };
    private readonly TextBox _txtAiBase = new();
    private readonly TextBox _txtAiKey = new();
    private readonly TextBox _txtAiModel = new();
    private readonly ComboBox _cmbMatchType = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _numConcurrency = new() { Minimum = 1, Maximum = 8, Value = 3 };
    private readonly CheckBox _chkVolumeData = new() { Text = "抓取每卷详情（简介/日期/ISBN）", AutoSize = true };
    private readonly CheckBox _chkForceRefresh = new() { Text = "强制刷新已有 Btv 链接的系列", AutoSize = true };
    private readonly CheckBox _chkBwTags = new()
    {
        Text = "写入类型/题材标签、阅读方向（来自 BookWalker）",
        AutoSize = true,
    };
    private readonly CheckBox _chkBaike = new() { Text = "启用（其它源都查不到时按中文名查百科词条）", AutoSize = true };
    private readonly CheckBox _chkMangaDex = new() { Text = "启用（英文圈图源，百科兜底之前；走代理）", AutoSize = true };
    private readonly CheckBox _chkJapaneseOnly = new() { Text = "只匹配日漫（韩漫/美漫/国漫候选一律剔除）", AutoSize = true };
    private readonly CheckBox _chkKomgaProxy = new() { Text = "Komga 连接走代理（本机/内网部署保持关闭）", AutoSize = true };
    private readonly TextBox _txtTagWhitelist = new() { Multiline = true, Height = 54, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _txtTagBlacklist = new() { Multiline = true, Height = 54, ScrollBars = ScrollBars.Vertical };

    public SettingsDialog(AppSettings current)
    {
        _current = current;
        Text = "设置";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 9F);
        MinimumSize = new Size(620, 500);
        ClientSize = new Size(640, 740);

        _txtUrl.Text = current.KomgaUrl;
        _txtApiKey.Text = current.KomgaApiKey;
        _txtUsername.Text = current.KomgaUsername;
        _txtPassword.Text = current.KomgaPassword;
        _txtBgmToken.Text = current.BangumiAccessToken;
        foreach (var (scheme, label) in ProxyProtocols.All) _cmbProxyScheme.Items.Add(new ProxyItem(scheme, label));
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
        _chkAi.Checked = current.AiEnabled;
        _chkAiTranslate.Checked = current.AiTranslateSummary;
        _chkVolumeData.Checked = current.FetchVolumeData;
        _chkForceRefresh.Checked = current.ForceRefreshBtv;
        _chkBwTags.Checked = current.BwWriteTags;
        _chkBaike.Checked = current.BaikeEnabled;
        _chkMangaDex.Checked = current.MangaDexEnabled;
        _chkJapaneseOnly.Checked = current.JapaneseOnly;
        _chkKomgaProxy.Checked = current.KomgaProxyEnabled;
        _numConcurrency.Value = Math.Clamp(current.ScrapeConcurrency, 1, 8);
        _cmbMatchType.Items.AddRange(["漫画", "小说"]);
        _cmbMatchType.SelectedItem = current.MatchType;
        _txtTagWhitelist.Text = string.Join("\r\n", current.TagWhitelist);
        _txtTagBlacklist.Text = string.Join("\r\n", current.TagBlacklist);
        _lblBwStatus.Text = File.Exists(AppSettings.BkwkIndexPath)
            ? "索引已存在 ✓"
            : "尚未下载";

        // ---- 底部按钮条（固定高度，永不遮挡内容） ----
        var btnOk = new Button
        {
            Text = "保存", DialogResult = DialogResult.OK,
            Size = new Size(96, 34),
        };
        // 代理填错就别让保存，否则会在运行时到处超时（历史上还出现过启动即崩）
        btnOk.Click += (_, e) =>
        {
            var err = ProxyProtocols.Validate(SelectedScheme(), _txtProxyHost.Text, (int)_numProxyPort.Value);
            if (err == null) return;
            MessageBox.Show(err, "代理配置有误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            DialogResult = DialogResult.None;
            _txtProxyHost.Focus();
        };
        var btnCancel = new Button
        {
            Text = "取消", DialogResult = DialogResult.Cancel,
            Size = new Size(96, 34),
        };
        var btnPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 48, ColumnCount = 3,
            Margin = new Padding(0), Padding = new Padding(0, 6, 10, 6),
        };
        btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        btnPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        btnPanel.Controls.Add(btnOk, 1, 0);
        btnPanel.Controls.Add(btnCancel, 2, 0);

        // ---- 对称网格：标签列（固定 150）+ 输入列（拉伸） ----
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0),
            Padding = new Padding(4, 6, 16, 14),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void Section(string title)
        {
            var l = new Label
            {
                Text = title,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
                ForeColor = Color.FromArgb(50, 82, 148),
                Margin = new Padding(2, 14, 2, 2),
            };
            grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            int r = grid.RowCount - 1;
            grid.Controls.Add(l, 0, r);
            grid.SetColumnSpan(l, 2);
        }

        void Field(string label, Control input)
        {
            var l = new Label
            {
                Text = label,
                AutoSize = true,
                Anchor = AnchorStyles.Left | AnchorStyles.Right,   // 横向撑满格子
                TextAlign = ContentAlignment.MiddleRight,          // 文字右对齐 → 对称
                Margin = new Padding(2, 3, 10, 3),
            };
            input.Margin = new Padding(0, 3, 8, 3);
            input.Anchor = AnchorStyles.Left | AnchorStyles.Right; // 输入框撑满整列
            grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            int r = grid.RowCount - 1;
            grid.Controls.Add(l, 0, r);
            grid.Controls.Add(input, 1, r);
        }

        Section("Komga");
        Field("Komga 地址", _txtUrl);
        Field("API Key", _txtApiKey);
        Field("用户名（可选）", _txtUsername);
        Field("密码（可选）", _txtPassword);

        Section("Bangumi");
        Field("Token（可选）", _txtBgmToken);

        Section("代理（Bangumi / 封面 / BookWalker 共用）");
        Field("协议", _cmbProxyScheme);
        var proxyHostRow = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, Margin = new Padding(0, 3, 8, 3),
        };
        _txtProxyHost.Width = 190;
        _txtProxyHost.PlaceholderText = "127.0.0.1 或 proxy.example.com";
        _numProxyPort.Width = 78;
        _numProxyPort.Margin = new Padding(8, 0, 0, 0);
        proxyHostRow.Controls.Add(_txtProxyHost);
        proxyHostRow.Controls.Add(new Label { Text = "端口", AutoSize = true, Margin = new Padding(10, 6, 2, 0) });
        proxyHostRow.Controls.Add(_numProxyPort);
        Field("地址", proxyHostRow);
        Field("账号（可选）", _txtProxyUser);
        Field("密码（可选）", _txtProxyPass);
        Field("", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = Color.DimGray,
            Text = "支持 HTTP / HTTPS / SOCKS4(a) / SOCKS5（含账密认证）。留空 = 不使用代理。",
        });
        _cmbProxyScheme.SelectedIndexChanged += (_, _) => ApplyProxyScheme();
        ApplyProxyScheme();

        Section("刮削行为");
        Field("匹配类型", _cmbMatchType);
        Field("卷详情", _chkVolumeData);
        Field("强制刷新", _chkForceRefresh);
        Field("百科兜底", _chkBaike);
        Field("MangaDex", _chkMangaDex);
        Field("日漫限定", _chkJapaneseOnly);
        Field("Komga 代理", _chkKomgaProxy);
        var concurrencyRow = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, Margin = new Padding(0, 3, 8, 3),
        };
        _numConcurrency.Width = 64;
        concurrencyRow.Controls.Add(_numConcurrency);
        concurrencyRow.Controls.Add(new Label
        {
            Text = "个系列同时刮削（1 = 逐个）",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(8, 6, 0, 0),
        });
        Field("批量并发", concurrencyRow);

        Section("版本标记（黑白名单）");
        Field("白名单", _txtTagWhitelist);
        Field("黑名单", _txtTagBlacklist);
        Field("", new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            ForeColor = Color.DimGray,
            Text = "每行一个词。白名单 = 后缀命中即落一个标签；黑名单 = 命中即丢弃（标题旧后缀一并清掉）。\r\n" +
                   "例：黑名单 join → 乱马1/2（join）变 乱马1/2；白名单 C.C → 落 C.C 标签。",
        });

        Section("BookWalker 数据源");
        Field("目录库地址", _txtBkwkUrl);
        Field("推断字段", _chkBwTags);
        // 两行按钮：① 程序直连下载（Cloudflare 可能 403）② 浏览器下载 + 本地导入（推荐兜底）
        var bwRow1 = new FlowLayoutPanel
        {
            AutoSize = false, Height = 32, WrapContents = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 3, 8, 3),
        };
        _btnBwDownload.Margin = new Padding(0, 3, 8, 3);
        bwRow1.Controls.Add(_btnBwDownload);
        bwRow1.Controls.Add(_lblBwStatus);
        Field("本地索引", bwRow1);

        var bwRow2 = new FlowLayoutPanel
        {
            AutoSize = false, Height = 32, WrapContents = false,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = new Padding(0, 3, 8, 3),
        };
        _btnBwOpen.Margin = new Padding(0, 3, 8, 3);
        _btnBwImport.Margin = new Padding(0, 3, 0, 3);
        bwRow2.Controls.Add(_btnBwOpen);
        bwRow2.Controls.Add(_btnBwImport);
        Field("浏览器下载", bwRow2);

        var bwHint = new Label
        {
            Text = "直连下载报 403 时：点『打开下载页』用浏览器下载，再点『导入本地文件…』。",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(4, 2, 2, 2),
        };
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        int bwr = grid.RowCount - 1;
        grid.Controls.Add(bwHint, 0, bwr);
        grid.SetColumnSpan(bwHint, 2);

        _btnBwDownload.Click += async (_, _) => await DownloadBwIndexAsync();
        _btnBwOpen.Click += (_, _) => OpenBwDownloadPage();
        _btnBwImport.Click += async (_, _) => await ImportBwIndexAsync();

        Section("AI 匹配（可选）");
        Field("AI 匹配", _chkAi);
        Field("简介翻译", _chkAiTranslate);
        Field("接口地址", _txtAiBase);
        Field("API Key", _txtAiKey);
        Field("模型", _txtAiModel);

        var hint = new Label
        {
            Text = "AI 兼容 OpenAI 接口（如 DeepSeek）；匹配与简介翻译共用 API Key",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(4, 10, 2, 2),
        };
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        int hr = grid.RowCount - 1;
        grid.Controls.Add(hint, 0, hr);
        grid.SetColumnSpan(hint, 2);

        var txtDataDir = new TextBox
        {
            ReadOnly = true,
            TabStop = false,
            Text = AppSettings.DataDir,
            BackColor = SystemColors.Control,
        };
        Field("数据目录", txtDataDir);
        var dataHint = new Label
        {
            Text = "绿色便携：设置与索引都存这里",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Margin = new Padding(4, 2, 2, 2),
        };
        grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        int dr = grid.RowCount - 1;
        grid.Controls.Add(dataHint, 0, dr);
        grid.SetColumnSpan(dataHint, 2);

        // ---- 内容区可滚动：窗口再矮也只是滚动，不会裁掉控件 ----
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            Padding = new Padding(8, 4, 2, 4),
        };
        scroll.Controls.Add(grid);

        Controls.Add(scroll);
        Controls.Add(btnPanel);
        AcceptButton = btnOk;
        CancelButton = btnCancel;

        // 高 DPI（如 150%）：布局等比放大，与 GDI 字体缩放保持一致
        float dpi = DeviceDpi / 96f;
        if (dpi > 1.01f)
        {
            Scale(new SizeF(dpi, dpi));
            MinimumSize = new Size((int)(620 * dpi), (int)(500 * dpi));
        }
    }

    private void SetBwStatus(string m)
    {
        if (_lblBwStatus.InvokeRequired) _lblBwStatus.BeginInvoke(() => _lblBwStatus.Text = m);
        else _lblBwStatus.Text = m;
    }

    private void SetBwBusy(bool busy)
    {
        _btnBwDownload.Enabled = !busy;
        _btnBwOpen.Enabled = !busy;
        _btnBwImport.Enabled = !busy;
    }

    /// <summary>用系统浏览器打开目录库下载地址（浏览器能过 Cloudflare 人机校验）。</summary>
    private void OpenBwDownloadPage()
    {
        var url = _txtBkwkUrl.Text.Trim();
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
            MessageBox.Show($"无法打开浏览器：{e.Message}\n\n可手动在浏览器访问：\n{url}", "错误",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task DownloadBwIndexAsync()
    {
        SetBwBusy(true);
        try
        {
            SetBwStatus("准备下载…");
            var svc = new BookWalkerService(new SettingsDialogLogger(SetBwStatus));
            // 直连下载复用同一个代理设置（部分网络需要代理才能访问 BookWalker）
            var proxy = CurrentProxy();
            await Task.Run(() => svc.BuildIndexAsync(
                _txtBkwkUrl.Text.Trim(), AppSettings.BkwkDbPath, AppSettings.BkwkIndexPath, proxy,
                (_, _, m) => SetBwStatus(m)));
            SetBwStatus("完成 ✓");
            MessageBox.Show($"索引已构建：{AppSettings.BkwkIndexPath}\n点『保存』生效。",
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            SetBwStatus("失败: " + e.Message);
            MessageBox.Show($"下载/构建失败：{e.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBwBusy(false);
        }
    }

    /// <summary>导入本地已下载的 bkwk-db.sqlite.zst（或解压后的 .sqlite）并建索引。</summary>
    private async Task ImportBwIndexAsync()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "选择已下载的 BookWalker 目录库（bkwk-db.sqlite.zst 或解压后的 .sqlite）",
            Filter = "BookWalker 目录库 (*.zst;*.sqlite;*.db)|*.zst;*.sqlite;*.db|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(downloads)) dlg.InitialDirectory = downloads;
        }
        catch { /* 取不到下载目录就不过滤 */ }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        SetBwBusy(true);
        try
        {
            SetBwStatus("导入中…");
            var svc = new BookWalkerService(new SettingsDialogLogger(SetBwStatus));
            var file = dlg.FileName;
            await Task.Run(() => svc.ImportLocalAsync(
                file, AppSettings.BkwkDbPath, AppSettings.BkwkIndexPath,
                (_, _, m) => SetBwStatus(m)));
            SetBwStatus("导入完成 ✓");
            MessageBox.Show($"索引导入完成：{AppSettings.BkwkIndexPath}\n点『保存』生效。",
                "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception e)
        {
            SetBwStatus("导入失败: " + e.Message);
            MessageBox.Show($"导入失败：{e.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBwBusy(false);
        }
    }

    private sealed class SettingsDialogLogger(Action<string> status) : IAppLogger
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
        _txtProxyHost.Enabled = _numProxyPort.Enabled = _txtProxyUser.Enabled = _txtProxyPass.Enabled = on;
        if (!on) return;
        if ((int)_numProxyPort.Value == ProxyProtocols.DefaultPort(_lastProxyScheme))
            _numProxyPort.Value = Math.Clamp(ProxyProtocols.DefaultPort(scheme), 1, 65535);
        _lastProxyScheme = scheme;
    }

    /// <summary>当前界面上的代理（点『直接下载建索引』时还没保存，必须用界面值）。</summary>
    private ProxyConfig CurrentProxy()
    {
        var scheme = SelectedScheme();
        var host = _txtProxyHost.Text.Trim();
        if (scheme.Length == 0 || host.Length == 0) return ProxyConfig.None;
        return new ProxyConfig($"{scheme}://{host}:{(int)_numProxyPort.Value}",
            _txtProxyUser.Text.Trim(), _txtProxyPass.Text);
    }

    private string _lastProxyScheme = "http";

    /// <summary>"a\r\nb, c" → ["a","b","c"]（换行/逗号都是分隔符；空段丢弃）。</summary>
    private static List<string> SplitLines(string text) =>
        [.. text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (DialogResult == DialogResult.OK)
        {
            EditedSettings = new AppSettings
            {
                KomgaUrl = _txtUrl.Text.Trim(),
                KomgaApiKey = _txtApiKey.Text.Trim(),
                KomgaUsername = _txtUsername.Text.Trim(),
                KomgaPassword = _txtPassword.Text,
                BangumiAccessToken = _txtBgmToken.Text.Trim(),
                ProxyScheme = SelectedScheme(),
                ProxyHost = _txtProxyHost.Text.Trim(),
                ProxyPort = (int)_numProxyPort.Value,
                ProxyUser = _txtProxyUser.Text.Trim(),
                ProxyPassword = _txtProxyPass.Text,
                // 已迁移到 proxy* 字段，清掉旧值，避免删掉代理后老配置"复活"
                LegacyBangumiProxy = "",
                BangumiApiBase = _current.BangumiApiBase,
                BkwkUrl = _txtBkwkUrl.Text.Trim(),
                AiEnabled = _chkAi.Checked,
                AiTranslateSummary = _chkAiTranslate.Checked,
                AiBaseUrl = _txtAiBase.Text.Trim(),
                AiApiKey = _txtAiKey.Text.Trim(),
                AiModel = _txtAiModel.Text.Trim(),
                MatchType = _cmbMatchType.SelectedItem?.ToString() ?? "漫画",
                FetchVolumeData = _chkVolumeData.Checked,
                ScrapeConcurrency = (int)_numConcurrency.Value,
                BwWriteTags = _chkBwTags.Checked,
                ForceRefreshBtv = _chkForceRefresh.Checked,
                DryRun = _current.DryRun,
                LastLibraryId = _current.LastLibraryId,
                MylarOutputDir = _current.MylarOutputDir,
                TagWhitelist = SplitLines(_txtTagWhitelist.Text),
                TagBlacklist = SplitLines(_txtTagBlacklist.Text),
                BaikeEnabled = _chkBaike.Checked,
                MangaDexEnabled = _chkMangaDex.Checked,
                JapaneseOnly = _chkJapaneseOnly.Checked,
                KomgaProxyEnabled = _chkKomgaProxy.Checked,
            };
        }
    }
}