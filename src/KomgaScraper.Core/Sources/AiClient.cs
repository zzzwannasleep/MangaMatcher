using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KomgaScraper.Core.Text;

namespace KomgaScraper.Core.Sources;

/// <summary>
/// AI 匹配客户端（OpenAI 兼容 /chat/completions）。
/// 用途：Bangumi 搜索返回多个候选且无精确标题匹配时，让 AI 依据标题/原名/别名/作者挑最合适的一个。
/// 可选代理：实测部分中转站直连路由 500、走代理 200（2026-10-10，api.yjs.im）——代理可用时一律走代理。
/// </summary>
public sealed partial class AiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;

    public AiClient(string baseUrl, string apiKey, string model,
        ProxyConfig? proxy = null, IAppLogger? log = null)
    {
        var root = baseUrl.TrimEnd('/');
        if (!root.EndsWith("/v1", StringComparison.Ordinal) && !root.EndsWith("/v1/", StringComparison.Ordinal))
            root += "/v1";
        _model = string.IsNullOrWhiteSpace(model) ? "deepseek-chat" : model;
        // 统一走 HttpFactory：支持代理（中转站直连常被掐）、显式 60s 超时、网络告警入日志
        _http = HttpFactory.Create(proxy ?? ProxyConfig.None, TimeSpan.FromSeconds(60),
            "KomgaScraper/1.0", warn: log is null ? null : log.Warn);
        _http.BaseAddress = new Uri(root + "/");
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    [GeneratedRegex("\"?index\"?\\s*:\\s*(-?\\d+)")]
    private static partial Regex IndexRegex();

    /// <summary>
    /// AI 接口统一入口：/chat/completions，网络异常或 60s 超时（非用户取消）自动重试一次。
    /// 历史事故：批量匹配时 AI 识别偶发 60s 超时 → 整个系列按失败处理；重试一次能把抖动兜回来。
    /// </summary>
    /// <summary>单次发送 /chat/completions（不含重试）。</summary>
    private async Task<HttpResponseMessage> SendChatOnceAsync(object payload, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(payload),
        };
        return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// AI 接口统一入口：/chat/completions，网络异常或 60s 超时（非用户取消）自动重试一次。
    /// 历史事故 1：批量匹配时 AI 识别偶发 60s 超时 → 整个系列按失败处理；重试一次能把抖动兜回来。
    /// 历史事故 2：重试循环里误写成递归调用自己 → System.StackOverflowException 整进程闪退
    /// （2026-10-10，WER 事件日志 0xc00000fd 实锤）。重试必须调用 SendChatOnceAsync，绝不能自调用。
    /// </summary>
    private async Task<HttpResponseMessage> PostChatAsync(object payload, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendChatOnceAsync(payload, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (attempt < 2 && !ct.IsCancellationRequested)
            {
                // TaskCanceledException 是 OCE 子类：HttpClient.Timeout 到期抛的就是它，
                // 用 ct.IsCancellationRequested 区分"用户取消"（不重试）与"超时"（重试一次）
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>从候选中挑最佳匹配；返回 null 表示 AI 认为都不匹配。</summary>
    public async Task<SearchCandidate?> PickBestAsync(
        string seriesTitle, IReadOnlyList<SearchCandidate> candidates, CancellationToken ct = default)
    {
        if (candidates.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"漫画/轻小说系列《{seriesTitle}》在 Bangumi 的搜索候选如下（编号从 1 开始）：");
        for (int i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            sb.AppendLine($"{i + 1}. 《{c.Title}》 原:{c.OriTitle} 作者:{c.Author} 别名:{c.Aliases}");
        }
        sb.AppendLine("请判断哪个候选与目标系列是同一部作品（注意区分续作/番外/画集/不同译名）。" +
                      "本地漫画库只收录日本漫画：候选若为韩国漫画（谚文标题、Naver/Lezhin/Kakao 等韩国平台连载）、" +
                      "美漫或国产漫画，一律视为不匹配（输出 -1）。" +
                      "只输出 JSON：{\"index\": 编号}；若都不匹配则输出 {\"index\": -1}。不要输出其他内容。");

        var payload = new
        {
            model = _model,
            temperature = 0.0,
            messages = new object[]
            {
                new { role = "system", content = "你是漫画元数据匹配助手，严格输出 JSON。" },
                new { role = "user", content = sb.ToString() },
            },
        };

        using var resp = await PostChatAsync(payload, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var data = await resp.Content.ReadFromJsonAsync<ChatCompletionDto>(cancellationToken: ct).ConfigureAwait(false);
        var text = data?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
        var m = IndexRegex().Match(text);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[1].Value, out var idx)) return null;
        if (idx < 1 || idx > candidates.Count) return null; // -1 或越界 = 无匹配
        return candidates[idx - 1];
    }

    // ================= 标题翻译（中文 → 日文，供 BookWalker 目录匹配） =================

    /// <summary>标题译名缓存（同一书名批量里不会翻第二遍）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> TitleJpCache = new(StringComparer.Ordinal);

    /// <summary>标题里是否含汉字（中文标题才需要译日文；纯日文假名/纯英文直接跳过）。</summary>
    private static bool ContainsHan(string s)
    {
        foreach (var ch in s)
            if (ch is >= '\u4e00' and <= '\u9fff') return true;
        return false;
    }

    /// <summary>
    /// 把中文标题翻译成日文原名（用于 BookWalker 本地索引匹配——BW 目录只有日文/英文标题，
    /// 本地是中文名时按原标题永远查不到）。不含汉字（已是日文/纯英文）或失败时返回 null，绝不阻塞主流程。
    /// </summary>
    public async Task<string?> TranslateTitleToJapaneseAsync(string title, CancellationToken ct = default)
    {
        var key = title.Trim();
        if (key.Length == 0 || !ContainsHan(key)) return null;
        if (TitleJpCache.TryGetValue(key, out var cached)) return cached.Length > 0 ? cached : null;

        try
        {
            var payload = new
            {
                model = _model,
                temperature = 0.0,
                messages = new object[]
                {
                    new { role = "system", content = "你是漫画/轻小说资料整理助手。" },
                    new { role = "user", content = "把下面这部漫画/轻小说的中文标题还原成日文原名。" +
                                                   "只输出日文标题本身，不要解释、不要书名号、不要引号：\n" + key },
                },
            };
            using var resp = await PostChatAsync(payload, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadFromJsonAsync<ChatCompletionDto>(cancellationToken: ct).ConfigureAwait(false);
            var jp = CleanTranslation(data?.Choices?.FirstOrDefault()?.Message?.Content ?? "")
                .Trim('《', '》', '「', '」', '『', '』').Trim();
            if (jp.Length == 0 || jp == key) { TitleJpCache[key] = ""; return null; }
            TitleJpCache[key] = jp;
            return jp;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return null;   // 翻译失败只是少一条匹配路径
        }
    }

    // ================= AI 识别作品（匹配回退的主路径） =================

    /// <summary>AI 识别结果：日文原名 / 通行中文名（简体）/ 英文或罗马字名。</summary>
    public sealed record IdentifyResult(string Jp, string Cn, string En, bool Confident);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, IdentifyResult?> IdentifyCache =
        new(StringComparer.Ordinal);

    private sealed class IdentifyDto
    {
        [JsonPropertyName("jp")] public string? Jp { get; set; }
        [JsonPropertyName("cn")] public string? Cn { get; set; }
        [JsonPropertyName("en")] public string? En { get; set; }
        [JsonPropertyName("confident")] public bool? Confident { get; set; }
    }

    /// <summary>
    /// 问 AI「这是哪部作品」：给出日文原名/中文名/英文名。
    /// 简中/繁中/日文/英文的漫画名差异很大（海贼王≠ワンピース≠ONE PIECE），机械翻译不可靠；
    /// AI 直接认识这些通行译名，一次调用同时拿到三语名字，供 Bangumi/BW/Mox 三源重搜。
    /// 失败/无法识别返回 null（缓存住，同一系列批量里不重复问）。
    /// </summary>
    public async Task<IdentifyResult?> IdentifySeriesAsync(
        string rawTitle, IReadOnlyList<string> candidateTerms, CancellationToken ct = default)
    {
        var key = rawTitle.Trim();
        if (key.Length == 0) return null;
        if (IdentifyCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var termList = string.Join(" / ", candidateTerms.Where(t => !string.IsNullOrWhiteSpace(t)).Take(5));
            var prompt =
                $"本地漫画文件夹的原始名称：{key}\n" +
                (termList.Length > 0 ? $"从中提取的可能标题：{termList}\n\n" : "\n") +
                "请判断这是哪部漫画/轻小说，输出它的通行名字：\n" +
                "- jp：日文原名（官方标题，保留原有括号/符号写法）\n" +
                "- cn：最通行的简体中文译名（没有通行译名就留空串）\n" +
                "- en：官方英文或罗马字名（没有就留空串）\n" +
                "注意区分重名作品、续作、外传、总集篇、画集；如果无法确定是哪部作品，confident 填 false，" +
                "但仍有把握的字段照填。只输出 JSON：" +
                "{\"jp\":\"...\",\"cn\":\"...\",\"en\":\"...\",\"confident\":true}";

            var payload = new
            {
                model = _model,
                temperature = 0.0,
                messages = new object[]
                {
                    new { role = "system", content = "你是资深漫画/轻小说资料库助手，" +
                                                     "熟悉作品在 Bangumi、BookWalker 与中文圈的通行命名。" },
                    new { role = "user", content = prompt },
                },
            };
            using var resp = await PostChatAsync(payload, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadFromJsonAsync<ChatCompletionDto>(cancellationToken: ct).ConfigureAwait(false);
            var text = CleanTranslation(data?.Choices?.FirstOrDefault()?.Message?.Content ?? "");
            var jsonStart = text.IndexOf('{');
            var jsonEnd = text.LastIndexOf('}');
            if (jsonStart < 0 || jsonEnd <= jsonStart) { IdentifyCache[key] = null; return null; }
            var dto = System.Text.Json.JsonSerializer.Deserialize<IdentifyDto>(
                text[jsonStart..(jsonEnd + 1)]);
            var jp = (dto?.Jp ?? "").Trim('《', '》', '「', '」', '『', '』').Trim();
            var cn = (dto?.Cn ?? "").Trim('《', '》').Trim();
            var en = (dto?.En ?? "").Trim();
            if (jp.Length == 0 && cn.Length == 0 && en.Length == 0) { IdentifyCache[key] = null; return null; }
            var result = new IdentifyResult(jp, cn, en, dto?.Confident ?? false);
            IdentifyCache[key] = result;
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            IdentifyCache[key] = null;
            return null;
        }
    }

    // ================= AI 补写简介（各源都没有时） =================

    /// <summary>简介补写缓存（"" = 无法补写，避免重复浪费调用）。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> SynopsisCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 让 AI 直接为这部作品写一段简介（80~160 字简体中文）。
    /// AI 不认识这部作品时返回 UNKNOWN → 这里转成 null。失败/取消不抛异常（null），绝不阻塞刮削。
    /// </summary>
    public async Task<string?> GenerateSynopsisAsync(
        string seriesTitle, string? originalTitle, string? author, CancellationToken ct = default)
    {
        var key = seriesTitle.Trim();
        if (key.Length == 0) return null;
        if (SynopsisCache.TryGetValue(key, out var cached)) return cached.Length > 0 ? cached : null;

        try
        {
            var prompt = "请为下面这部漫画/轻小说写一段简体中文的作品简介：\n" +
                         $"作品名：{key}\n" +
                         (!string.IsNullOrWhiteSpace(originalTitle) ? $"原名：{originalTitle}\n" : "") +
                         (!string.IsNullOrWhiteSpace(author) ? $"作者：{author}\n" : "") +
                         "要求：80~160 字，概括作品核心设定与看点，不要剧透结局，" +
                         "写成可直接放在漫画站上的简介文案；全文必须是纯简体中文，" +
                         "不得夹杂日文假名或繁体字。\n" +
                         "如果你不确定这是哪部作品（怕写错内容），只输出：UNKNOWN";
            var payload = new
            {
                model = _model,
                temperature = 0.2,
                messages = new object[]
                {
                    new { role = "system", content = "你是漫画资料站的编辑，简介基于真实作品内容，绝不能编造。" },
                    new { role = "user", content = prompt },
                },
            };
            using var resp = await PostChatAsync(payload, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadFromJsonAsync<ChatCompletionDto>(cancellationToken: ct).ConfigureAwait(false);
            var text = CleanTranslation(data?.Choices?.FirstOrDefault()?.Message?.Content ?? "").Trim();
            if (text.Length == 0 || text.Contains("UNKNOWN", StringComparison.OrdinalIgnoreCase) || text.Length < 20)
            {
                SynopsisCache[key] = "";
                return null;
            }
            SynopsisCache[key] = text;
            return text;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return null;
        }
    }

    // ================= 简介翻译 =================

    /// <summary>单次翻译的最大字符数（超出截断，防止超长简介烧 token）。</summary>
    private const int MaxTranslateChars = 3000;

    /// <summary>是否纯中文文本（有汉字且无假名/谚文）——OpenCC 繁→简只对这类文本生效，
    /// 日文（新字体汉字）绝不过繁简转换（進撃の巨人 → 进撃の巨人 这种破坏性转换）。</summary>
    public static bool IsHanOnly(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        int han = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is >= '\u3040' and <= '\u30ff') return false;      // 假名 → 日文
            if (ch is >= '\uac00' and <= '\ud7a3') return false;      // 谚文 → 韩文
            if (ch is >= '\u4e00' and <= '\u9fff') han++;
        }
        return han > 0;
    }

    /// <summary>
    /// 判断文本是否需要翻译成中文：空/纯符号 → 否；含假名或谚文 → 是；
    /// 以拉丁字母为主 → 是；汉字不足一半 → 是（即已是中文简介则不必翻译）。
    /// </summary>
    public static bool NeedsChineseTranslation(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        int han = 0, kana = 0, hangul = 0, latin = 0, other = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            if (ch is >= '\u3040' and <= '\u30ff') kana++;          // 平假名/片假名
            else if (ch is >= '\uac00' and <= '\ud7a3') hangul++;    // 谚文
            else if (ch is >= '\u4e00' and <= '\u9fff') han++;       // 汉字
            else if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) latin++;
            else if (char.IsDigit(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) { /* 数字/标点不计 */ }
            else other++;
        }
        if (kana > 0 || hangul > 0) return true;                     // 日文/韩文
        int letters = han + latin + other;
        if (letters == 0) return false;                              // 只有数字/符号
        if (latin > letters * 0.5) return true;                      // 英文为主
        return han < letters * 0.5;                                  // 汉字不到一半 → 需要翻译
    }

    /// <summary>
    /// 把非中文简介翻译成简体中文；已是中文或空文本时原样返回（不调用接口）。
    /// 网络/接口异常由调用方兜底，此处正常抛出。
    /// </summary>
    public async Task<string> TranslateToChineseAsync(string text, CancellationToken ct = default)
    {
        if (!NeedsChineseTranslation(text)) return text;
        var input = text.Trim();
        if (input.Length > MaxTranslateChars) input = input[..MaxTranslateChars];

        var payload = new
        {
            model = _model,
            temperature = 0.0,
            messages = new object[]
            {
                new { role = "system", content = "你是漫画/轻小说资料翻译，负责把外文简介翻译成自然准确的简体中文。" },
                new { role = "user", content = "请把下面这段作品简介完整翻译成简体中文，严格要求：\n" +
                                               "1. 全文译成简体中文，不允许保留任何日文假名、谚文或未翻译的日文词句；\n" +
                                               "2. 人名/作品名等专有名词用通行简体译名，没有通行译名的写成汉字；\n" +
                                               "3. 不使用繁体字，统一输出简体；\n" +
                                               "4. 保留原有分段和书名号；只输出译文本身，不要解释、不要前缀、不要加引号：\n\n" + input },
            },
        };

        using var resp = await PostChatAsync(payload, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var data = await resp.Content.ReadFromJsonAsync<ChatCompletionDto>(cancellationToken: ct).ConfigureAwait(false);
        var translated = CleanTranslation(data?.Choices?.FirstOrDefault()?.Message?.Content ?? "");
        return translated.Length == 0 ? text : translated;   // 模型没给出内容就保留原文
    }

    /// <summary>清理模型输出的 ```代码块```、整体引号、"译文："前缀等。</summary>
    public static string CleanTranslation(string s)
    {
        var t = (s ?? "").Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            int nl = t.IndexOf('\n');
            t = nl >= 0 ? t[(nl + 1)..] : "";
            if (t.EndsWith("```", StringComparison.Ordinal)) t = t[..^3];
            t = t.Trim();
        }
        if (t.Length >= 2 &&
            ((t[0] == '"' && t[^1] == '"') || (t[0] == '“' && t[^1] == '”') || (t[0] == '\'' && t[^1] == '\'')))
            t = t[1..^1].Trim();
        foreach (var prefix in new[] { "译文：", "译文:", "翻译：", "翻译:", "中文简介：", "中文：" })
        {
            if (t.StartsWith(prefix, StringComparison.Ordinal)) { t = t[prefix.Length..].Trim(); break; }
        }
        return t;
    }

    private sealed class ChatCompletionDto
    {
        [JsonPropertyName("choices")] public List<ChoiceDto>? Choices { get; set; }
    }
    private sealed class ChoiceDto
    {
        [JsonPropertyName("message")] public MessageDto? Message { get; set; }
    }

    private sealed class MessageDto
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
    }

    public void Dispose() => _http.Dispose();
}