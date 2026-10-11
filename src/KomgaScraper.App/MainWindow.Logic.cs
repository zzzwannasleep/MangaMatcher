using Avalonia.Controls;
using Avalonia.Threading;
using KomgaScraper.Core;
using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Mylar;
using KomgaScraper.Core.Scraping;
using KomgaScraper.Core.Sources;
using KomgaScraper.Core.Text;

namespace KomgaScraper.App;

/// <summary>业务编排：从 WinForms 版 MainForm 1:1 移植（Core 层零改动）。</summary>
public sealed partial class MainWindow
{
    private void RebuildClients()
    {
        _komga?.Dispose();
        _bangumi?.Dispose();
        _ai?.Dispose();
        CancelCoverSweep();
        _state ??= new ScrapeStateStore(Path.Combine(AppSettings.DataDir, "scrape-state.json"));
        // Komga 走不走代理由设置开关决定（本机/内网部署默认直连）
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

        // 改动留痕：三条写入管线都汇到同一个日志框
        _scrape.ChangeObserved += OnScrapeChange;
        _batch.ChangeObserved += OnScrapeChange;
        _tagger.ChangeObserved += OnScrapeChange;

        // AI（可选）：匹配选择 + 简介翻译，共用同一接口配置
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
                _batch.TranslateSummary = true;
                Log("INFO", $"AI 简介翻译已启用（{_settings.AiModel}）：非中文简介将自动翻译成中文");
            }
        }
        else if (_settings.AiEnabled || _settings.AiTranslateSummary)
        {
            Log("WARN", "AI 功能已勾选但未填 API Key，本次不会生效（设置 → AI 匹配）");
        }

        _scrape.BwWriteTags = _settings.BwWriteTags;
        _batch.FetchVolumeData = _settings.FetchVolumeData;
        _batch.BwWriteTags = _settings.BwWriteTags;
        _batch.SeriesConcurrency = _settings.ScrapeConcurrency;

        // 版本标记黑白名单（三个写入管线共用）
        _scrape.MarkerWhitelist = _settings.TagWhitelist.Count > 0 ? _settings.TagWhitelist : null;
        _scrape.MarkerBlacklist = _settings.TagBlacklist.Count > 0 ? _settings.TagBlacklist : null;
        _batch.MarkerWhitelist = _scrape.MarkerWhitelist;
        _batch.MarkerBlacklist = _scrape.MarkerBlacklist;
        _tagger.MarkerWhitelist = _scrape.MarkerWhitelist;
        _tagger.MarkerBlacklist = _scrape.MarkerBlacklist;

        Log("INFO", _settings.Proxy.IsSet
            ? $"代理已启用: {_settings.Proxy.Display}（Bangumi API + 封面下载 + BookWalker 共用）"
            : "未使用代理：Bangumi API 与封面下载将直连（网络受限时请在 设置 → 代理 配置）");

        // BookWalker 本地索引（可选）
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
                    Log("WARN", "BookWalker 索引是旧格式（没有每卷/类型题材数据），请到设置里重新导入或重建");
            }
            catch (Exception e)
            {
                _bwIndex = null;
                _batch.BwIndex = null;
                _scrape.BwIndex = null;
                Log("WARN", $"BookWalker 索引加载失败: {e.Message}（可在设置里重建）");
            }
        }

        // Mox（bookof.moe）中文第三源
        _mox?.Dispose();
        _mox = new MoxClient(_settings.ProxyUrl, proxyUser: _settings.ProxyUser, proxyPassword: _settings.ProxyPassword);
        _scrape.Mox = _mox;
        _batch.Mox = _mox;

        // 百度百科（最终兜底源）
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

        // MangaDex（英文圈图源）
        _mdex?.Dispose();
        _mdex = null;
        OriginFilter.JapaneseOnly = _settings.JapaneseOnly;
        if (_settings.MangaDexEnabled)
        {
            _mdex = new MangaDexClient(proxy: _settings.Proxy, log: null);
            _batch.MangaDex = _mdex;
            Log("INFO", "MangaDex 源已启用（英文区回退，百科兜底之前）");
        }
        Log("INFO", _settings.JapaneseOnly
            ? "日漫限定：韩漫/美漫/国漫候选一律剔除"
            : "日漫限定已关闭：不再过滤非日漫候选");

        // OpenCC 繁简引擎后台预热
        _ = Task.Run(() =>
        {
            try
            {
                var s = CJK.T2S("繁簡轉換預熱");
                Log("INFO", string.Equals(s, "繁简转换预热", StringComparison.Ordinal)
                    ? "OpenCC 简繁引擎已就绪（词汇级）"
                    : "OpenCC 初始化降级：使用内置单字映射");
            }
            catch { /* 降级映射兜底 */ }
        });

        InitLibraryCache();
    }

    // ================= 本地库缓存（打开即用 + 自动对齐） =================

    /// <summary>初始化/复用库缓存；Komga 连接信息变更时自动清空重建。</summary>
    private void InitLibraryCache()
    {
        if (!_settings.CacheEnabled)
        {
            _libCacheTimer?.Stop();
            _libCache = null;
            return;
        }
        _libCache ??= new LibraryCache(AppSettings.LibraryCacheFile);
        if (_libCache.EnsureConnection(_settings))
            Log("INFO", "Komga 连接信息已变更，本地库缓存已清空（将在下次对齐时重建）");
        RestartCacheTimer();
    }

    private void SetupCacheTimer()
    {
        _libCacheTimer = new DispatcherTimer();
        _libCacheTimer.Tick += async (_, _) => await AutoCacheRefreshAsync();
    }

    private void RestartCacheTimer()
    {
        if (_libCacheTimer == null) return;
        if (!_settings.CacheEnabled || _settings.CacheRefreshMinutes <= 0)
        {
            _libCacheTimer.Stop();
            return;
        }
        _libCacheTimer.Stop();
        _libCacheTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(_settings.CacheRefreshMinutes, 1, 10080));
        _libCacheTimer.Start();
    }

    /// <summary>启动恢复：缓存有数据 → 库列表 + 上次使用的库直接秒出（零 Komga 请求），随后后台对齐。</summary>
    private void RestoreCacheView()
    {
        if (_libCache == null || !_libCache.Ready) return;
        var libs = _libCache.Libraries;
        if (libs.Count == 0) return;
        FillLibraryCombo(libs);

        var libId = _settings.LastLibraryId;
        if (libId.Length > 0 && _libCache.HasLibrary(libId))
        {
            var cached = _libCache.SeriesInLibrary(libId) ?? [];
            _seriesCache.Clear();
            _rows.Clear();
            foreach (var s in cached)
            {
                _seriesCache[s.Id] = s;
                _rows.Add(new SeriesRow(s, () => CoverCell(s.Id)));
            }
            var libName = libs.FirstOrDefault(l => l.Id == libId)?.Name ?? libId;
            StatusText.Text = $"库 \"{libName}\"：{cached.Count} 个系列（本地缓存，{CacheAgeText()}）";
            Log("INFO", $"已从本地缓存恢复 {cached.Count} 个系列（快照 {CacheAgeText()}）——后台自动对齐中…");
            StartCoverSweep(cached);
        }
        else
        {
            Log("INFO", $"已从本地缓存恢复 {libs.Count} 个库（快照 {CacheAgeText()}）");
        }
        _ = AutoCacheRefreshAsync();   // 打开软件即静默对齐一次
    }

    private string CacheAgeText()
    {
        var t = _libCache?.LastSync ?? default;
        if (t == default) return "未知时间";
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} 小时前";
        return $"{(int)span.TotalDays} 天前";
    }

    /// <summary>定时/启动触发的静默全量对齐；完成后当前视图从缓存重建（勾选保留）。</summary>
    private async Task AutoCacheRefreshAsync()
    {
        if (_libCache == null || _komga == null || _busy) return;
        if (!_settings.CacheEnabled || _settings.CacheRefreshMinutes <= 0) { _libCacheTimer?.Stop(); return; }
        try
        {
            var (total, changed, removed) = await _libCache.RefreshAllAsync(_komga, null, CancellationToken.None);
            Log("INFO", $"库缓存自动对齐完成：{total} 系列（新增/变更 {changed}，移除 {removed}）");
            RefreshViewFromCache();
        }
        catch (OperationCanceledException) { /* 忽略 */ }
        catch (Exception e)
        {
            Log("WARN", $"库缓存自动对齐失败: {e.Message}（继续使用本地缓存数据）");
        }
    }

    /// <summary>对齐完成后：当前显示的库从缓存重建行（勾选状态保留）。</summary>
    private void RefreshViewFromCache()
    {
        var libId = _settings.LastLibraryId;
        if (libId.Length == 0 || _libCache?.HasLibrary(libId) != true) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_busy) return;   // 任务运行中的行刷新由任务收尾负责
            var cached = _libCache!.SeriesInLibrary(libId);
            if (cached == null) return;
            ReplaceRowsWith(cached);
            var libName = _libCache.Libraries.FirstOrDefault(l => l.Id == libId)?.Name ?? libId;
            StatusText.Text = $"库 \"{libName}\"：{cached.Count} 个系列（已对齐）";
        }, DispatcherPriority.Background);
    }

    /// <summary>用给定系列集合重建行：勾选状态按 id 保留（复用 ApplyFilter 的重建模式）。</summary>
    private void ReplaceRowsWith(List<SeriesDto> series)
    {
        var checkedIds = new HashSet<string>(_rows.Where(r => r.Checked).Select(r => r.Series.Id));
        _seriesCache.Clear();
        _rows.Clear();
        foreach (var s in series)
        {
            _seriesCache[s.Id] = s;
            _rows.Add(new SeriesRow(s, () => CoverCell(s.Id)) { Checked = checkedIds.Contains(s.Id) });
        }
        UpdateEmptyHint();
    }

    /// <summary>「加载系列」命中缓存 → 秒出，后台静默对齐单库。</summary>
    private async Task SilentRefreshLibraryAsync(LibraryDto library)
    {
        try
        {
            var (total, changed) = await _libCache!.RefreshLibraryAsync(_komga!, library.Id, null, CancellationToken.None);
            Log("INFO", $"后台对齐完成：库 \"{library.Name}\" {total} 系列（新增/变更 {changed}）");
            Dispatcher.UIThread.Post(() =>
            {
                if (_busy) return;
                var cached = _libCache!.SeriesInLibrary(library.Id);
                if (cached == null || _settings.LastLibraryId != library.Id) return;
                ReplaceRowsWith(cached);
                StatusText.Text = $"库 \"{library.Name}\"：{cached.Count} 个系列（已对齐）";
            }, DispatcherPriority.Background);
        }
        catch (Exception e)
        {
            Log("WARN", $"后台对齐失败: {e.Message}（显示的是本地缓存数据）");
        }
    }

    private async Task TestConnectionAsync()
    {
        if (_komga == null) return;
        _settings.KomgaUrl = TxtUrl.Text?.Trim() ?? "";
        _settings.KomgaApiKey = TxtApiKey.Text?.Trim() ?? "";
        SaveSettings();
        RebuildClients();
        try
        {
            SetBusy(true);
            _cts = new CancellationTokenSource();
            var libs = await Task.Run(() => _komga!.GetLibrariesAsync(_cts.Token));
            FillLibraryCombo(libs);
            _libCache?.SetLibraries(libs, LibraryCache.ConnKeyOf(_settings));
            StatusText.Text = $"连接成功，共 {libs.Count} 个库";
            Log("INFO", $"连接成功：{_settings.KomgaUrl}，共 {libs.Count} 个库");
        }
        catch (Exception e)
        {
            StatusText.Text = "连接失败";
            Log("ERR ", $"连接失败: {e.Message}");
            await Ui.ErrorAsync(this, $"连接失败：{e.Message}");
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
        var dlg = new SettingsWindow(_settings);
        if (await dlg.ShowDialog<bool>(this))
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
        CmbLibrary.ItemsSource = libs;
        var idx = libs.FindIndex(l => l.Id == _settings.LastLibraryId);
        CmbLibrary.SelectedIndex = idx >= 0 ? idx : (libs.Count > 0 ? 0 : -1);
    }

    private async Task LoadSeriesAsync()
    {
        if (!GuardBusy()) return;
        if (CmbLibrary.SelectedIndex < 0)
        {
            try
            {
                var libs = await RunBusyAsync(ct => _komga!.GetLibrariesAsync(ct));
                FillLibraryCombo(libs);
            }
            catch (Exception e)
            {
                Log("ERR ", $"获取库列表失败: {e.Message}");
                await Ui.ErrorAsync(this, $"获取库列表失败：{e.Message}\n请先点击\"测试连接\"检查地址与密钥");
                return;
            }
            if (CmbLibrary.SelectedIndex < 0) { await Ui.MessageAsync(this, "请先选择库"); return; }
        }

        var library = _libraries[CmbLibrary.SelectedIndex];

        // 本地缓存命中 → 系列列表秒出（零 Komga 请求），后台静默对齐单库
        if (_settings.CacheEnabled && _libCache?.HasLibrary(library.Id) == true)
        {
            var cachedNow = _libCache.SeriesInLibrary(library.Id) ?? [];
            ReplaceRowsWith(cachedNow);
            _settings.LastLibraryId = library.Id;
            SaveSettings();
            StatusText.Text = $"库 \"{library.Name}\"：{cachedNow.Count} 个系列（本地缓存，{CacheAgeText()}）";
            Log("INFO", $"「加载系列」命中本地缓存：{cachedNow.Count} 个系列秒出（快照 {CacheAgeText()}），后台静默对齐 Komga…");
            StartCoverSweep(cachedNow);
            _ = SilentRefreshLibraryAsync(library);
            return;
        }

        try
        {
            var series = await RunBusyAsync(ct => _komga!.ListSeriesInLibraryAsync(library.Id, ct));
            _seriesCache.Clear();
            _rows.Clear();
            foreach (var s in series)
            {
                _seriesCache[s.Id] = s;
                _rows.Add(new SeriesRow(s, () => CoverCell(s.Id)));
            }
            _settings.LastLibraryId = library.Id;
            SaveSettings();
            _libCache?.ReplaceLibrary(library.Id, series, LibraryCache.ConnKeyOf(_settings));
            StatusText.Text = $"库 \"{library.Name}\"：{series.Count} 个系列";
            Log("INFO", $"已加载库 \"{library.Name}\" 的 {series.Count} 个系列");
            if (_komga!.LastSeriesTotalElements > 0 && _komga.LastSeriesTotalElements != series.Count)
                Log("WARN", $"注意：服务器报告总数 {_komga.LastSeriesTotalElements}，实际加载 {series.Count}，可能存在分页异常，请检查日志");
            if (_seriesCache.Count == 0)
                Log("WARN", "该库没有系列（或全部被过滤）");
            StartCoverSweep(series);
        }
        catch (OperationCanceledException)
        {
            Log("WARN", "加载已取消");
        }
        catch (Exception e)
        {
            Log("ERR ", $"加载系列失败: {e.Message}");
            await Ui.ErrorAsync(this, $"加载系列失败：{e.Message}");
        }
    }

    private async Task SearchAllLibrariesAsync()
    {
        var kw = TxtSearch.Text?.Trim() ?? "";
        if (kw.Length == 0) { Log("WARN", "先在搜索框输入漫画名"); return; }
        if (_komga == null) { Log("WARN", "请先测试连接（Komga 未就绪）"); return; }

        try
        {
            SetBusy(true);
            SetProgress(0, 0, $"搜索「{kw}」…");
            var hits = await _komga.SearchSeriesAsync(kw, 60, CancellationToken.None);
            _seriesCache.Clear();
            _rows.Clear();
            foreach (var s in hits)
            {
                _seriesCache[s.Id] = s;
                _rows.Add(new SeriesRow(s, () => CoverCell(s.Id)));
            }
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
        var s = _contextSeries ?? (SeriesGrid.SelectedItem as SeriesRow)?.Series
                ?? GetCheckedSeries().FirstOrDefault();
        if (s == null)
        {
            await Ui.MessageAsync(this, "先在列表里选中（或右键点在）要编辑的系列；\n也可以用上方搜索栏检索后再双击。", "编辑单部", okOnly: true);
            return;
        }
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
            await RefreshRowsFromServerAsync([seriesId]);
        }

        var dlg = new SeriesEditorWindow(
            _komga!, seriesId, Log,
            () => ChkDryRun.IsChecked == true,
            RescrapeVolumesAsync);
        dlg.ChangeObserved += OnScrapeChange;   // 编辑器写入也汇入日志框
        await dlg.ShowDialog(this);
        dlg.ChangeObserved -= OnScrapeChange;
        _contextSeries = null;
        await RefreshRowsFromServerAsync([seriesId]);
    }

    /// <summary>从服务器重拉系列数据并刷新行显示。</summary>
    private async Task RefreshRowsFromServerAsync(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                var s = await _komga!.GetSeriesAsync(id, _cts?.Token ?? CancellationToken.None);
                _seriesCache[id] = s;
                _libCache?.UpdateSeries(s);   // 写入后缓存即时回源（下次打开也是新数据）
                if (_state?.GetCover(id) == null)
                {
                    var has = await SeriesHasUserCoverAsync(id, _cts?.Token ?? CancellationToken.None);
                    if (has != null) _state!.SetCover(id, has == true);
                }
                var coverText = CoverCell(id);
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var r in _rows)
                    {
                        if (r.Series.Id == id) { r.Replace(s, coverText); break; }
                    }
                }, DispatcherPriority.Background);
            }
            catch (Exception e)
            {
                Log("WARN", $"刷新系列 {id} 显示失败: {e.Message}");
            }
        }
    }

    // ---- 刮削（勾选系列，弹候选面板） ----

    private async Task ScrapeCheckedAsync(SyncType syncType)
    {
        if (!GuardBusy()) return;
        var targets = GetCheckedSeries();
        if (targets.Count == 0) { await Ui.MessageAsync(this, "请先勾选要刮削的系列", "提示", okOnly: true); return; }

        // 全自动模式：走批量精确匹配管线（含 BookWalker/AI 回退），不弹候选框
        if (ChkAuto.IsChecked == true)
        {
            await RunBatchMatchAsync(targets, "勾选系列");
            RefreshCheckedRows();
            return;
        }

        var label = syncType == SyncType.All ? "元数据+封面" : "元数据";
        var dry = ChkDryRun.IsChecked == true;
        var okGo = await Ui.MessageAsync(this,
            $"将对 {targets.Count} 个勾选系列刮削{label}。\n模式：{(dry ? "Dry-run 预览" : "真实更新")}\n继续？",
            "确认");
        if (!okGo) return;
        ClearPreview();

        _cts = new CancellationTokenSource();
        int done = 0, ok = 0;
        foreach (var series in targets)
        {
            try
            {
                var name = SeriesRow.TitleOf(series);
                SetProgress(++done, targets.Count, $"搜索《{name}》");
                var term = TitleParser.ExtractSeriesTitles(name, 1).FirstOrDefault() ?? name;
                var candidates = new List<SearchCandidate>();
                try
                {
                    candidates.AddRange(await _bangumi!.SearchSubjectsAsync(term, _settings.MatchType, 8, _cts.Token));
                }
                catch (Exception e)
                {
                    Log("ERR ", $"《{name}》Bangumi 搜索失败: {e.Message}");
                }

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
                {
                    Log("WARN", $"《{name}》搜索\"{term}\"无结果，跳过");
                    continue;
                }

                var chosen = await PickCandidateAsync(name, term, candidates);
                if (chosen == null) { Log("INFO", $"《{name}》已跳过（未选择候选）"); continue; }

                var result = await ScrapeWithCandidateAsync(series, chosen, syncType, dry, done, targets.Count);
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
        if (!dry && targets.Count <= 50)
            await RefreshRowsFromServerAsync(targets.Select(s => s.Id).ToList());
    }

    /// <summary>弹候选面板（UI 线程模态），返回用户选择的候选（取消= null）。</summary>
    private async Task<SearchCandidate?> PickCandidateAsync(string name, string term, List<SearchCandidate> candidates)
    {
        var dlg = new MatchWindow(name, term, candidates, _bangumi!, _mox, _bwIndex, _baike, _mdex, DownloadExternalCoverAsync);
        var ok = await dlg.ShowDialog<bool>(this);
        return ok ? dlg.SelectedCandidate : null;
    }

    /// <summary>按候选来源分发到对应的刮削管线（Mox/MangaDex/百科/BW/Bangumi）。</summary>
    private async Task<ScrapeResult> ScrapeWithCandidateAsync(
        SeriesDto series, SearchCandidate chosen, SyncType syncType, bool dry, int done, int total, bool replace = false)
    {
        var name = SeriesRow.TitleOf(series);
        ScrapeOptions Opts() => new()
        {
            SyncType = syncType,
            FetchVolumeData = _settings.FetchVolumeData,
            DryRun = dry,
            ReplaceMatch = replace,
        };
        var ct = _cts?.Token ?? CancellationToken.None;

        if (chosen.Source == BatchMatcher.SourceMox && chosen.MoxId is { Length: > 0 } moxId && _mox != null)
        {
            var mox = await _mox.GetSeriesAsync(moxId, ct);
            SetProgress(done, total, $"刮削《{name}》← Mox 《{mox.Title}》");
            return await _scrape!.ScrapeFromMoxAsync(series.Id, mox, Opts(), series, null, ct);
        }
        if (chosen.Source == BatchMatcher.SourceMangaDex && chosen.MdId is { Length: > 0 } mdId && _mdex != null)
        {
            var md = await _mdex.GetAsync(mdId, ct);
            if (md == null) return new ScrapeResult { Success = false, Error = "MangaDex 条目取回失败" };
            SetProgress(done, total, $"刮削《{name}》← MangaDex 《{md.Title}》");
            return await _scrape!.ScrapeFromMangaDexAsync(series.Id, md, Opts(), series, null, ct);
        }
        if (chosen.Source == BatchMatcher.SourceBaike && _baike != null)
        {
            var bkCard = await _baike.SearchCardAsync(chosen.Title, ct);
            if (bkCard == null) return new ScrapeResult { Success = false, Error = "百度百科词条取回失败" };
            SetProgress(done, total, $"刮削《{name}》← 百度百科 《{bkCard.Title}》");
            return await _scrape!.ScrapeFromBaikeAsync(series.Id, bkCard, Opts(), series, null, ct);
        }
        if (chosen.Source == BookWalkerIndex.SourceBw)
        {
            var bw = _bwIndex?.FindById(chosen.BwId ?? "");
            if (bw == null) return new ScrapeResult { Success = false, Error = "BookWalker 条目丢失" };
            SetProgress(done, total, $"刮削《{name}》← BookWalker 《{bw.Title}》");
            return await _scrape!.ScrapeFromBookWalkerAsync(series.Id, bw, Opts(), series, null, ct);
        }
        SetProgress(done, total, $"刮削《{name}》← subject {chosen.Id}");
        return await _scrape!.ScrapeAsync(series.Id, chosen.Id, Opts(), series, null, ct);
    }

    // ---- 覆盖匹配 ----

    private async Task RematchAsync()
    {
        if (!GuardBusy()) return;
        var targets = GetCheckedSeries();
        if (targets.Count == 0)
        {
            var clicked = _contextSeries;
            if (clicked == null)
            {
                await Ui.MessageAsync(this, "请先勾选要覆盖的系列，或右键点在要覆盖的系列上", "提示", okOnly: true);
                return;
            }
            targets = [clicked];
        }

        var dry = ChkDryRun.IsChecked == true;
        var okGo = await Ui.MessageAsync(this,
            $"对 {targets.Count} 个系列重走自动匹配：完全不看旧链接和旧元数据（按文件夹名搜索），\n" +
            "匹配到的新条目覆盖旧的错误匹配（旧链接剔除、被锁字段解锁重写、全部卷重抓详情）；\n" +
            $"没匹配到的系列保持原样。\n模式：{(dry ? "Dry-run 预览" : "真实更新")}\n继续？",
            "覆盖匹配");
        if (!okGo) return;
        ClearPreview();

        _batch!.MatchTypePlatform = _settings.MatchType;
        _batch!.DryRun = dry;
        try
        {
            var stats = await RunBusyAsync(ct => _batch!.ReMatchAsync(targets, SyncType.All, ct));
            Log("INFO", $"覆盖匹配完成{(dry ? "（Dry-run 预览，未修改 Komga）" : "")}：{stats}");
            await Ui.InfoAsync(this,
                $"覆盖匹配完成{(dry ? "（Dry-run 预览）" : "")}：\n覆盖成功 {stats.SuccessCount}，未命中 {stats.FailureCount}，共 {stats.Total}",
                "覆盖匹配");
            if (!dry && stats.SuccessCount > 0)
                await RefreshRowsFromServerAsync(targets.Select(t => t.Id).ToList());
        }
        catch (OperationCanceledException)
        {
            Log("WARN", "覆盖匹配已取消");
        }
    }

    // ---- 纠正匹配 ----

    private async Task CorrectMatchAsync()
    {
        if (!GuardBusy()) return;
        var targets = GetCheckedSeries();
        if (targets.Count == 0)
        {
            var clicked = _contextSeries;
            if (clicked == null)
            {
                await Ui.MessageAsync(this, "请先勾选系列，或右键点在要覆盖的系列上", "提示", okOnly: true);
                return;
            }
            targets = [clicked];
        }

        var dry = ChkDryRun.IsChecked == true;
        var okGo = await Ui.MessageAsync(this,
            $"为 {targets.Count} 个系列重新选择正确条目并刮削（元数据+封面），用新数据覆盖旧的错误匹配。\n" +
            $"候选面板里也可以「手动输入 Bangumi 链接/ID」。\n" +
            $"模式：{(dry ? "Dry-run 预览" : "真实更新")}\n继续？",
            "纠正匹配");
        if (!okGo) return;
        ClearPreview();

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
                    // 纠正匹配：搜索词优先取文件夹名（元数据标题可能已被旧匹配污染）
                    var name = series.Name;
                    var metaTitle = SeriesRow.TitleOf(series);
                    SetProgress(++done, targets.Count, $"纠正《{name}》");
                    var term = TitleParser.ExtractSeriesTitles(name, 1).FirstOrDefault()
                            ?? TitleParser.ExtractSeriesTitles(metaTitle, 1).FirstOrDefault()
                            ?? metaTitle;
                    if (!string.Equals(term, metaTitle, StringComparison.Ordinal))
                        Log("INFO", $"纠正匹配：按文件夹名搜索\"{term}\"（元数据标题《{metaTitle}》已被旧匹配污染，不作为搜索词）");

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

                    var chosen = await PickCandidateAsync(name, term, candidates);
                    if (chosen == null) { Log("INFO", $"《{name}》已跳过（未选择候选）"); continue; }

                    var result = await ScrapeWithCandidateAsync(series, chosen, SyncType.All, dry, done, targets.Count, replace: true);
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

    // ---- 刮削封面（独立封面覆盖） ----

    private async Task CoverScrapeAsync()
    {
        if (!GuardBusy()) return;
        var targets = GetCheckedSeries();
        var scope = "勾选系列";
        if (targets.Count == 0)
        {
            targets = _rows.Select(r => r.Series).ToList();
            scope = "整库全部系列";
        }
        if (targets.Count == 0) { await Ui.MessageAsync(this, "没有可刮削的系列", "提示", okOnly: true); return; }

        var dry = ChkDryRun.IsChecked == true;
        var okGo = await Ui.MessageAsync(this,
            $"对{scope}共 {targets.Count} 个系列重新匹配并只刮封面（系列封面 + 卷封面）。\n" +
            "元数据一个字段都不会动（已刮好的简介/卷信息保持原样）。\n" +
            $"模式：{(dry ? "Dry-run 预览" : "真实更新")}\n继续？",
            "刮削封面");
        if (!okGo) return;
        ClearPreview();

        _batch!.MatchTypePlatform = _settings.MatchType;
        _batch!.DryRun = dry;
        try
        {
            var stats = await RunBusyAsync(ct => _batch!.ReMatchCoverOnlyAsync(targets, ct));
            Log("INFO", $"封面独立刮削完成{(dry ? "（Dry-run 预览，未修改 Komga）" : "")}：{stats}");
            await Ui.InfoAsync(this,
                $"封面刮削完成{(dry ? "（Dry-run 预览）" : "")}：\n成功 {stats.SuccessCount}，未命中 {stats.FailureCount}，共 {stats.Total}",
                "刮削封面");
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
        var dry = ChkDryRun.IsChecked == true;
        var okGo = await Ui.MessageAsync(this,
            $"对{scope}共 {targets.Count} 个系列自动匹配刮削（Bangumi + 各源自动回退" +
            (_baike != null ? "，含百度百科" : "") + "）。\n" +
            $"模式：{(dry ? "Dry-run 预览" : "真实更新")}\n" +
            "已有 Btv 链接的跳过，失败的加入\"手动匹配\"收藏夹。\n继续？",
            "自动匹配刮削");
        if (!okGo) return;
        ClearPreview();

        _batch!.MatchTypePlatform = _settings.MatchType;
        _batch!.DryRun = dry;
        var stats = await RunBusyAsync(ct => _batch!.BatchMatchAsync(targets,
            SyncType.All, _settings.ForceRefreshBtv, ct));
        Log("INFO", $"自动匹配刮削完成{(dry ? "（Dry-run 预览，未修改 Komga）" : "")}！{stats}");
        await Ui.InfoAsync(this, $"自动匹配完成{(dry ? "（Dry-run 预览）" : "")}：{stats}", "完成");
    }

    // ---- 文件夹名打标签 ----

    private async Task FolderTagAsync()
    {
        if (!GuardBusy()) return;
        var all = _rows.Select(r => r.Series).ToList();
        if (all.Count == 0) { await Ui.MessageAsync(this, "请先加载系列", "提示", okOnly: true); return; }

        var dry = ChkDryRun.IsChecked == true;
        var okGo = await Ui.MessageAsync(this,
            $"对当前库 {all.Count} 个系列打标：文件夹名标记 → 标签+标题后缀，并补 MACHINE 标签。\n" +
            $"模式：{(dry ? "Dry-run 预览（不修改）" : "真实更新")}\n继续？",
            "打标");
        if (!okGo) return;
        ClearPreview();

        var count = await RunBusyAsync(ct => _tagger!.TagAsync(all, dry, ct));
        await Ui.InfoAsync(this, $"打标完成：{count} 个系列更新", "完成");
    }

    // ---- Mylar 导出 ----

    private async Task MylarExportAsync()
    {
        if (!GuardBusy()) return;
        if (CmbLibrary.SelectedIndex < 0)
        {
            await Ui.MessageAsync(this, "请先测试连接并选择库", "提示", okOnly: true);
            return;
        }
        var library = _libraries[CmbLibrary.SelectedIndex];

        var outputDir = await Ui.PickFolderAsync(this, _settings.MylarOutputDir);
        if (outputDir == null) return;

        var coverChoice = await Ui.ChoiceAsync(this, "是否同时下载系列封面（cover.jpg）？", "封面",
            ("下载封面", "yes"), ("仅导出 JSON", "no"));
        if (coverChoice == null) return;
        var downloadCovers = coverChoice == "yes";

        _settings.MylarOutputDir = outputDir;
        SaveSettings();

        var libraryRoot = library.Root;
        var libId = library.Id;
        var covers = downloadCovers;
        await RunBusyAsync(ct => _mylar!.ExportAsync(libId, libraryRoot, covers, outputDir, ct));
        await Ui.InfoAsync(this, "Mylar 导出完成，详见日志", "完成");
    }
}
