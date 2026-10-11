// mock-mox.js — bookof.moe mock（:25696）。
// 搜索（datainfo-B 行内封面 URL）/详情（b/{id}.htm）/封面帧页（datainfo-V）。
// 强制 520 一次再成功（测试退避路径）。
// 用法：node tools/mock-mox.js [port]
const http = require("http");

const PORT = Number(process.argv[2] || process.env.MOCK_MOX_PORT || 25696);

let bof500Counter = 0; // 520 触发计数（首个详情请求先 520 再成功）
let searchCount = 0;
let detailCount = 0;
let frameCount = 0;

const SERIES = {
  "123": {
    id: "123",
    titleMain: "ぐらんぶる", // 繁体站标题（name_main，保持繁体原样）
    eng: "Grand Blue",
    info: "狀態：連載 分類：喜劇 校園 版本：東立",
    desc: "大學生伊織加入潛水社團「うみほの会」，<br>圍繞潛水與飲酒的青春故事。【電子版】",
    authors: ["井上堅二", "吉岡公威"],
    frame: "/data_vol.php?id=123",
  },
};

function dataInfoB(term) {
  // 搜索行：datainfo-B=分类,ID,标题,作者,出版日期,封面URL...
  return [
    `datainfo-B=漫画,123,${term},井上堅二,2014-05-09,https://img.mxomo.com/cover/123.jpg?sign=a,b`,
    `datainfo-B=漫画,456,${term}外傳,其他人,2016-01-01,https://img.mxomo.com/cover/456.jpg`,
  ].join("\n");
}

const server = http.createServer((req, res) => {
  const u = new URL(req.url, `http://localhost:${PORT}`);
  const p = u.pathname;

  // 搜索
  if (p === "/data_list.php") {
    searchCount++;
    const term = u.searchParams.get("s") || "";
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    res.end(`<html><script>var a=1;</script>\n${dataInfoB(term)}\n</html>`);
    return;
  }

  // 详情页（b/{id}.htm）
  const m = p.match(/^\/b\/(.+)\.htm$/);
  if (m) {
    detailCount++;
    // 强制 520 一次再成功（测试退避）
    if (bof500Counter === 0) {
      bof500Counter++;
      res.writeHead(520, { "Content-Type": "text/plain" });
      res.end("cloudflare origin error (mock)");
      return;
    }
    const s = SERIES[m[1]];
    if (!s) {
      res.writeHead(404, { "Content-Type": "text/plain" });
      res.end("not found");
      return;
    }
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    res.end(
      `<html><head><title>${s.titleMain}</title></head><body>` +
        `<img id="logo_r18" src="/r18.png">` +
        `<div class="name_main">${s.titleMain}</div>` +
        `<div class="name_subt">(${s.eng}) 狀態：連載</div>` +
        `<div class="name_subt">分類：喜劇 校園　版本：東立　最後出版：2020</div>` +
        `<div id="div_desctext">${s.desc}</div>` +
        `<a href="https://bookof.moe/s/AUT/1">${s.authors[0]}</a>` +
        `<a href="https://bookof.moe/s/AUT/2">${s.authors[1]}</a>` +
        `<script>window.iframe_action.location.href="${s.frame}"</script>` +
        `</body></html>`,
    );
    return;
  }

  // 封面帧页（data_vol.php）
  if (p === "/data_vol.php") {
    frameCount++;
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    res.end(
      [
        "datainfo-V=1, Vol.01, 漫画, x, /v/123_1.jpg, y",
        "datainfo-V=2, Vol.02, 漫画, x, https://img.mxomo.com/v/123_2.jpg, y",
        "datainfo-V=3, Vol.03, 漫画, x, /v/123_3.jpg, y",
      ].join("\n"),
    );
    return;
  }

  // 站内图片
  if (p.startsWith("/v/") || p.startsWith("/cover/")) {
    res.writeHead(200, { "Content-Type": "image/jpeg" });
    res.end(Buffer.from([0xff, 0xd8, 0xff, 0xe0, 9, 9, 9, 9]));
    return;
  }

  // 观测端点（测试用）
  if (p === "/__stats") {
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(JSON.stringify({ searchCount, detailCount, frameCount, bof500Counter }));
    return;
  }

  res.writeHead(404, { "Content-Type": "application/json" });
  res.end(JSON.stringify({ error: "no route: " + req.method + " " + req.url }));
});

server.listen(PORT, () => console.log(`[mock-mox] listening on :${PORT} (520-then-200 retry baked in)`));
