using KomgaScraper.Core;
using KomgaScraper.Core.Sources;

// 修复验证：PostChatAsync 曾自递归烧栈（StackOverflowException，2026-10-10 WER 0xc00000fd）。
// 此 harness 直调 AI 链路；若再有栈溢出/网络异常立即暴露。
var proxy = new ProxyConfig("http://127.0.0.1:7890");
using var ai = new AiClient("https://api.yjs.im/v1", "sk-YnYobEBC2sSQBYveKFgwiwXkSuCi0CIzREhY0pF3MnjW0PWe",
    "deepseek-v4.1-flash", proxy, new ConsLog());
var sw = System.Diagnostics.Stopwatch.StartNew();
var cn = await ai.TranslateToChineseAsync("進撃の巨人は、巨人に支配された世界で人類の自由を求める物語である。エレンは壁の外の世界を見ると誓った。", ct: default);
Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] 翻译结果：{cn[..Math.Min(60, cn.Length)]}…");
var jp = await ai.TranslateTitleToJapaneseAsync("海贼王", default);
Console.WriteLine($"[{sw.ElapsedMilliseconds}ms] 中文→日文：海贼王 → {jp}");
Console.WriteLine("=== AI 链路验证通过：无栈溢出、真实调用 2 发 ===");

sealed class ConsLog : IAppLogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");
    public void Warn(string message) => Console.WriteLine($"[WARN] {message}");
    public void Error(string message) => Console.WriteLine($"[ERR ] {message}");
    public void Progress(int current, int total, string message) { }
}
