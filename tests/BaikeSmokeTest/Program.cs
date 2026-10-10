using KomgaScraper.Core;
using KomgaScraper.Core.Sources;

// 验证 1：AI 能否从 [作者][作品][汉化组][卷数] 文件夹名里识别出真正的作品名
var proxy = new ProxyConfig("http://127.0.0.1:7890");
using var ai = new AiClient("https://api.yjs.im/v1", "sk-YnYobEBC2sSQBYveKFgwiwXkSuCi0CIzREhY0pF3MnjW0PWe",
    "deepseek-v4.1-flash", proxy, new ConsLog());

(string Folder, string Author)[] folders =
[
    ("[齐藤千穗][VS亚森罗苹][4未]", "齐藤千穗"),
    ("[黑乃奈奈绘][新撰组异闻录 PEACE MAKER][1-2完]", "黑乃奈奈绘"),
    ("[高桥留美子][犬夜叉 奥义皆传][青文][pmdpikachu][1+1完]", "高桥留美子"),
];
foreach (var (folder, author) in folders)
{
    var terms = KomgaScraper.Core.Text.TitleParser.ExtractSeriesTitles(folder, 4).ToArray();
    var r = await ai.IdentifySeriesAsync(folder, terms);
    Console.WriteLine($"文件夹: {folder}\n  分词: [{string.Join(" | ", terms)}]\n  AI → jp:{r?.Jp} cn:{r?.Cn} en:{r?.En} confident:{r?.Confident}");
}

// 验证 2：百科按 AI 识别名搜索，命中作品词条（此前撞的是作者人物词条）
using var bk = new BaikeClient(log: new ConsLog());
foreach (var kw in new[] { "VS灵异猎人", "新撰组异闻录", "犬夜叉" })
{
    var card = await bk.SearchCardAsync(kw);
    Console.WriteLine($"百科按《{kw}》→ 《{card?.Title}》 漫画作品词条={card?.LooksLikeMangaWork}");
}
Console.WriteLine("=== AI 识别名 + 百科门禁 联合验证完成 ===");

sealed class ConsLog : IAppLogger
{
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Progress(int current, int total, string message) { }
}
