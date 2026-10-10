using KomgaScraper.Core;
using KomgaScraper.Core.Komga;
using KomgaScraper.Core.Sources;
using KomgaScraper.Core.Scraping;

// 封面压缩验证：真实封面 → WebP 分级压缩 → 真实服务器上传（不带 selected，不碰用户选择）→ 清理
const string baseUrl = "https://comic.wenjian.de";
const string apiKey = "f15598270ba94d6480a6cd415163bbcf";
var proxy = new ProxyConfig("http://127.0.0.1:7890");
using var komga = new KomgaClient(baseUrl, apiKey, null, null, proxy, new ConsLog());
var seriesId = "0RVS34DK9ZTY1";

byte[] jpeg = await komga.GetSeriesThumbnailAsync(seriesId);
Console.WriteLine($"原图: {jpeg.Length / 1024.0:F0}KB（真实封面）");

// 1) 分级压缩逻辑验证：假上传器模拟"413 两次后成功"，确认降档路径与体积
var tryCount = 0;
var accepted = await CoverCompressor.CompressAndUploadAsync(jpeg, async data =>
{
    tryCount++;
    Console.WriteLine($"  第 {tryCount} 档: {data.Length / 1024.0:F0}KB (WebP 头: {Convert.ToHexString(data[..4])})");
    return tryCount >= 3;   // 前 2 档模拟 413，第 3 档成功
}, new ConsLog(), "《测试》", default);
Console.WriteLine($"分级压缩 + 模拟降档: {(accepted ? "成功" : "失败")}（共试 {tryCount} 档）");

// 2) 真实服务器验证：压缩档上传（不带 selected=true → 不改用户选中的封面）
byte[] compressed = CoverCompressor.EncodeWebp(jpeg, 95, 1.0);
Console.WriteLine($"q95 压缩档: {compressed.Length / 1024.0:F0}KB ← 原图 {jpeg.Length / 1024.0:F0}KB");

var before = (await komga.GetSeriesThumbnailsAsync(seriesId)).Select(t => t.Id).ToHashSet();
using var http = HttpFactory.Create(proxy, TimeSpan.FromSeconds(30), "KomgaScraper-test");
http.BaseAddress = new Uri(baseUrl + "/api/");
http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
using var form = new MultipartFormDataContent();
var fc = new ByteArrayContent(compressed);
fc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/webp");
form.Add(fc, "file", "compress-test.webp");
using var req = new HttpRequestMessage(HttpMethod.Post, $"v1/series/{seriesId}/thumbnails") { Content = form };
using var resp = await http.SendAsync(req);
Console.WriteLine($"[压缩 WebP 真实上传] → HTTP {(int)resp.StatusCode}");

var after = await komga.GetSeriesThumbnailsAsync(seriesId);
var added = after.Where(t => !before.Contains(t.Id)).ToList();
foreach (var t in added)
{
    await komga.DeleteSeriesThumbnailAsync(seriesId, t.Id);
    Console.WriteLine($"已清理测试缩略图 {t.Id}");
}
Console.WriteLine($"=== 验证完成（现场还原: {after.Count - added.Count}/{before.Count}）===");

sealed class ConsLog : IAppLogger
{
    public void Info(string message) => Console.WriteLine($"[INFO] {message}");
    public void Warn(string message) => Console.WriteLine($"[WARN] {message}");
    public void Error(string message) => Console.WriteLine($"[ERR ] {message}");
    public void Progress(int current, int total, string message) { }
}