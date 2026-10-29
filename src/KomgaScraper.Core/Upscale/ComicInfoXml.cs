using System.Text;
using System.Xml.Linq;

namespace KomgaScraper.Core.Upscale;

/// <summary>导出 CBZ 时写入 ComicInfo.xml 的元信息。</summary>
public sealed record UpscaleMetadata(
    string Title,
    string Series = "",
    string Writer = "",
    string Summary = "",
    string Language = "zh",
    string Volume = "");

/// <summary>
/// 生成 Komga / 主流阅读器都认的 ComicInfo.xml（v2.0 常用字段）。
/// 页面使用 WebP，Manga 固定 YesAndRightToLeft（日漫右起）。
/// </summary>
public static class ComicInfoXml
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    public static string Build(UpscaleMetadata meta, IReadOnlyList<long> pageSizes, string notes)
    {
        var title = string.IsNullOrWhiteSpace(meta.Title) ? "未命名" : meta.Title.Trim();
        var series = string.IsNullOrWhiteSpace(meta.Series) ? title : meta.Series.Trim();

        var root = new XElement("ComicInfo",
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
            new XAttribute(XNamespace.Xmlns + "xsd", Xsd),
            new XAttribute(Xsi + "noNamespaceSchemaLocation", "ComicInfo.xsd"));

        Add(root, "Title", title);
        Add(root, "Series", series);
        Add(root, "Number", meta.Volume);
        Add(root, "Writer", meta.Writer);
        Add(root, "Summary", meta.Summary);
        Add(root, "LanguageISO", string.IsNullOrWhiteSpace(meta.Language) ? "zh" : meta.Language.Trim());
        Add(root, "PageCount", pageSizes.Count.ToString());
        Add(root, "Manga", "YesAndRightToLeft");
        Add(root, "Notes", notes);

        var pageEls = new XElement("Pages");
        for (var i = 0; i < pageSizes.Count; i++)
        {
            pageEls.Add(new XElement("Page",
                new XAttribute("Image", i),
                new XAttribute("ImageSize", pageSizes[i]),
                new XAttribute("Type", i == 0 ? "FrontCover" : "Story")));
        }
        root.Add(pageEls);

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        // 用 ToString 取正文，由调用方补 UTF-8 声明（StringBuilder+XmlWriter 会强制 utf-16 声明）
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + doc.ToString();
    }

    private static void Add(XElement root, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // XML 1.0 不接受大部分控制字符，先清掉再写
        var clean = new string(value.Where(c => c >= 0x20 || c is '\t' or '\n' or '\r').ToArray()).Trim();
        if (clean.Length == 0) return;
        root.Add(new XElement(name, clean));
    }

    public static byte[] ToUtf8Bytes(string xml) => new UTF8Encoding(false).GetBytes(xml);
}
