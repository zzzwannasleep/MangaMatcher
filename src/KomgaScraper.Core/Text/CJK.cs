using System.Text;

namespace KomgaScraper.Core.Text;

/// <summary>
/// 简繁转换引擎：OpenCC（词汇级，一简对多繁按词分词，如「内存/記憶體」）。
/// 初始化失败（缺字典文件等）自动降级为内置单字映射（CjkData），绝不抛异常。
/// OpenCCNET 的 Dictionary 资源目录随 NuGet 包复制到输出目录，初始化只在首次调用时尝试一次。
/// </summary>
public static class CJK
{
    private static readonly object Gate = new();
    private static bool _tried;
    private static bool _openCcReady;

    private static readonly Dictionary<char, char> Jt2FtMap;
    private static readonly Dictionary<char, char> Ft2JtMap;

    static CJK()
    {
        Jt2FtMap = new Dictionary<char, char>(CjkData.Jt2Ft.Length);
        for (int i = 0; i < CjkData.Jt2Ft.Length && i < CjkData.Ft2Jt.Length; i++)
            Jt2FtMap[CjkData.Jt2Ft[i]] = CjkData.Ft2Jt[i];
        Ft2JtMap = new Dictionary<char, char>(CjkData.Ft2Jt.Length);
        for (int i = 0; i < CjkData.Jt2Ft.Length && i < CjkData.Ft2Jt.Length; i++)
            Ft2JtMap[CjkData.Ft2Jt[i]] = CjkData.Jt2Ft[i];
    }

    private static bool EnsureReady()
    {
        if (_tried) return _openCcReady;
        lock (Gate)
        {
            if (_tried) return _openCcReady;
            try
            {
                var dictDir = Path.Combine(AppContext.BaseDirectory, "Dictionary");
                if (Directory.Exists(dictDir))
                {
                    // MaxMatch = OpenCC 原版最大匹配分词（不依赖 Jieba 资源，出错面更小）
                    OpenCCNET.ZhConverter.Initialize(dictionaryDirectory: dictDir,
                        jiebaResourceDirectory: "", segmentMode: OpenCCNET.SegmentMode.MaxMatch);
                    _openCcReady = true;
                }
            }
            catch
            {
                _openCcReady = false;   // 降级单字映射，功能不中断
            }
            _tried = true;
            return _openCcReady;
        }
    }

    /// <summary>简体 → 繁体（OpenCC 词汇级；引擎不可用时降级单字映射）。</summary>
    public static string S2T(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return EnsureReady() ? OpenCCNET.ZhConverter.HansToHant(input) : S2TFallback(input);
    }

    /// <summary>繁体 → 简体（OpenCC 词汇级；引擎不可用时降级单字映射）。</summary>
    public static string T2S(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return EnsureReady() ? OpenCCNET.ZhConverter.HantToHans(input) : T2SFallback(input);
    }

    private static string S2TFallback(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
            sb.Append(Jt2FtMap.TryGetValue(c, out var t) ? t : c);
        return sb.ToString();
    }

    private static string T2SFallback(string input)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var c in input)
            sb.Append(Ft2JtMap.TryGetValue(c, out var s) ? s : c);
        return sb.ToString();
    }
}
