// mock-ehentai.js — E-Hentai 表站（e-hentai.org）本地假站（Node，零依赖，默认 :25693）。
//
// 只模拟表站的最小面，够把 MangaMatcher 的 EH 链路整条跑通（不翻墙、不配代理）：
//   1) GET  /?f_search=…&advsearch=1&f_sfu=on&f_sft=on&f_sfl=on  → 搜索结果页（<tr>/<div class="glink">）
//   2) POST /api.php   {"method":"gdata","gidlist":[[gid,token]],"namespace":1} → {"gmetadata":[…]}
//   3) GET  /g/<gid>/  → 画廊页（页面里带规范链接，供「只给 gid 没 token」时补 token）
//   4) GET  /w/…       → 图床（image/jpeg，供系列封面上传）
//
// 用法：
//   node tools/mock-ehentai.js                # 监听 25693（默认，启动时会打印）
//   node tools/mock-ehentai.js 3000           # 换端口
//   node tools/mock-ehentai.js --empty        # 搜索永远 0 条（演示「EH 无命中 → 回退百科」）
//   node tools/mock-ehentai.js --error        # gdata 返回 {"error":"Gallery not found"}（演示详情 502）
//   node tools/mock-ehentai.js --http500      # 所有请求 500（演示「EH 失败不阻断整链」）
//
// 接到后端（Go server）—— EH_API_BASE 是 main.go 里已有的非持久化源基址注入（空 = 官方表站）：
//   PowerShell:
//     $env:EH_API_BASE="http://127.0.0.1:25693"
//     $env:MOX_API_BASE="http://127.0.0.1:1"        # 可选：把其它源顶到死端口，确保命中 EH
//     $env:BAIKE_API_BASE="http://127.0.0.1:1"      # 可选：同上
//     .\server.exe                                  # 或 docker compose 里加 environment
//   然后 Web 里确认「设置 → 百度百科·MangaDex 开关」旁没有关掉 E-Hentai（ehEnabled 默认 true）。
//
// 注意（实测）：
//   - EH 节流固定 1100ms/请求（main.go 给 SetSourceBaseOverrides 的 ehPaceMs 传 0 = 默认值），
//     目前没有 EH_PACE_MS 环境变量，所以本地演示每次 EH 搜索约 1.1s，属正常。
//   - 假站只有一个固定 gid/token，搜索标题按 f_search 回显，gdata 标题用该 gid 最近一次的搜索词，
//     方便「随便点一个系列都能演示」。批量刮多个系列时标题会互相覆盖，这是假站的已知取舍。
const http = require("http");

// ---------- 参数 ----------
const argv = process.argv.slice(2);
const flags = new Set(argv.filter((a) => a.startsWith("--")));
const portArg = argv.find((a) => /^\d+$/.test(a));
const PORT = Number(portArg || process.env.MOCK_EHENTAI_PORT || 25693);

const EMPTY = flags.has("--empty"); // 搜索永远 0 条
const GDATA_ERROR = flags.has("--error"); // gdata 回 {"error":...}
const HTTP_500 = flags.has("--http500"); // 所有请求 500

// ---------- 固定画廊（一个假站 = 一个画廊，够演示） ----------
const GID = 4264304;
const TOKEN = "aabbccdd01";
const DEFAULT_TITLE = "[Asanagi] Mesu Kyoushi 3 (Azur Lane) [Chinese]";
const TITLE_JPN = "雌教師3 (アズールレーン) [中国翻訳]";
const TAGS = [
  "parody:azur lane",
  "character:ganyu",
  "artist:asanagi",
  "female:ahegao",
  "male:teacher",
  "mixed:sole female",
  "other:full color",
  "language:chinese",
  "category:doujinshi",
];
const POSTED = "1600000000"; // unix 秒 → 2020-09-13

// 每个 gid 最近一次搜索词（让 gdata 标题和候选标题对得上）
const lastTermByGid = new Map();

function esc(s) {
  return String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}

function j(v) {
  return JSON.stringify(v);
}

// 搜索结果页：结构照真实 DOM 形状（<tr> + <a href="…/g/gid/token/"><div class="glink">标题</div>）。
function searchPage(base, term) {
  if (EMPTY || !term) {
    return `<!DOCTYPE html><html><body><p class="noresult">No hits found</p></body></html>`;
  }
  lastTermByGid.set(GID, term);
  const title = `[TestCircle (Asanagi)] ${term}`;
  return `<!DOCTYPE html><html><head><title>E-Hentai Galleries</title></head><body>
<table class="itg">
<tr>
<td class="gl1c glcat"><div class="cn ct2">Doujinshi</div></td>
<td class="gl2c"><div class="glthumb" id="it${GID}"><div><img style="height:360px" alt="thumb" src="${base}/w/00/${GID}-mock.webp"></div></div></td>
<td class="gl3c glname"><a href="${base}/g/${GID}/${TOKEN}/"><div class="glink">${esc(title)}</div><div><div class="gt" title="parody:azur lane">azur lane</div></div></a></td>
<td class="gl4c glhide"><div><a href="${base}/uploader/asanagi">asanagi</a></div><div>24 pages</div></td>
</tr>
</table></body></html>`;
}

// gdata 响应：注意 filesize/rating 这里**故意**保留字符串形态；真实站被 expunge 时会退化成裸数字 0，
// 解析器用 ehStr 宽容接收（见 server/internal/sources/ehentai.go 的 ehStr）。
function gdataJSON(base, gid, token) {
  if (GDATA_ERROR) {
    return `{"error":"Gallery not found"}`;
  }
  const title = lastTermByGid.get(gid) ? `[TestCircle (Asanagi)] ${lastTermByGid.get(gid)}` : DEFAULT_TITLE;
  return JSON.stringify({
    gmetadata: [
      {
        gid,
        token,
        title,
        title_jpn: TITLE_JPN,
        category: "Doujinshi",
        thumb: `${base}/w/00/${GID}-mock.webp`,
        uploader: "asanagi",
        posted: POSTED,
        filecount: "24",
        filesize: "123456789",
        rating: "4.32",
        tags: TAGS,
      },
    ],
  });
}

const server = http.createServer((req, res) => {
  const base = `http://127.0.0.1:${PORT}`;
  const u = new URL(req.url, base);
  const p = u.pathname;

  if (HTTP_500) {
    res.writeHead(500, { "Content-Type": "text/plain" });
    res.end("mock-ehentai: forced 500");
    console.log(`[mock-ehentai] 500 ${req.method} ${req.url}`);
    return;
  }

  // 1) 搜索页
  if (req.method === "GET" && p === "/") {
    const term = u.searchParams.get("f_search") || "";
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    res.end(searchPage(base, term));
    console.log(`[mock-ehentai] search f_search=${JSON.stringify(term)}`);
    return;
  }

  // 2) gdata
  if (req.method === "POST" && p === "/api.php") {
    let raw = "";
    req.on("data", (c) => (raw += c));
    req.on("end", () => {
      let gid = GID;
      let token = TOKEN;
      try {
        const body = JSON.parse(raw || "{}");
        const pair = body && Array.isArray(body.gidlist) && body.gidlist[0];
        if (Array.isArray(pair)) {
          gid = Number(pair[0]) || GID;
          token = pair[1] || TOKEN;
        }
      } catch (_) {
        /* 保持默认 */
      }
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(gdataJSON(base, gid, token));
      console.log(`[mock-ehentai] gdata gid=${gid} token=${token}`);
    });
    return;
  }

  // 3) 画廊页（只给 gid 时从这里补 token）
  if (req.method === "GET" && p.startsWith("/g/")) {
    const gid = p.replace(/^\/g\//, "").replace(/\/+$/, "") || String(GID);
    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
    res.end(
      `<!DOCTYPE html><html><head><title>Gallery</title></head><body>` +
        `<a href="${base}/g/${gid}/${TOKEN}/">gallery</a></body></html>`,
    );
    console.log(`[mock-ehentai] gallery page gid=${gid}`);
    return;
  }

  // 4) 图床
  if (req.method === "GET" && p.startsWith("/w/")) {
    res.writeHead(200, { "Content-Type": "image/jpeg" });
    res.end(Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46, 0x00, 0x01]));
    return;
  }

  res.writeHead(404, { "Content-Type": "application/json" });
  res.end(j({ error: "no route: " + req.method + " " + p }));
});

server.listen(PORT, () => {
  const base = `http://127.0.0.1:${PORT}`;
  console.log(`[mock-ehentai] 本地假表站已启动：${base}`);
  console.log(`[mock-ehentai] 模式：${EMPTY ? "搜索永远空" : GDATA_ERROR ? "gdata error" : HTTP_500 ? "全部 500" : "正常"}`);
  console.log(`[mock-ehentai] 接入后端（PowerShell）：`);
  console.log(`    $env:EH_API_BASE="${base}"`);
  console.log(`    $env:MOX_API_BASE="http://127.0.0.1:1"; $env:BAIKE_API_BASE="http://127.0.0.1:1"   # 可选：逼命中 EH`);
  console.log(`[mock-ehentai] 画廊：${base}/g/${GID}/${TOKEN}/   候选 id：${GID}/${TOKEN}`);
});
