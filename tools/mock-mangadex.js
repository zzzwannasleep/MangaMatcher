// mock-mangadex.js — MangaDex API mock（:25695）。
// GET /manga?title=（zh-Hans 标题）+ /manga/{id}?includes[]=cover_art + /covers/{id}/{file}。
// 用法：node tools/mock-mangadex.js [port]
const http = require("http");

const PORT = Number(process.argv[2] || process.env.MOCK_MANGADEX_PORT || 25695);

const MANGA = {
  "a1b2c3d4-0000-0000-0000-000000000001": {
    title: { "zh-Hans": "碧蓝之海", ja: "ぐらんぶる", en: "Grand Blue" },
    altTitles: [{ en: "Grand Blue" }, { ja: "ぐらんぶる" }, { "zh-Hant": "碧藍之海" }],
    description: { en: "University students dive and drink. Comedy.", ja: "大学生と潜水とお酒の話。" },
    originalLanguage: "ja",
    status: "ongoing",
    tags: [{ attributes: { name: { en: "Comedy" } } }, { attributes: { name: { en: "School" } } }],
    coverFile: "cover-a1b2.jpg",
  },
};

const server = http.createServer((req, res) => {
  const u = new URL(req.url, `http://localhost:${PORT}`);
  const p = u.pathname;
  const json = (code, obj) => {
    res.writeHead(code, { "Content-Type": "application/json" });
    res.end(JSON.stringify(obj));
  };

  if (p === "/manga") {
    const title = u.searchParams.get("title") || "";
    const data = [];
    for (const [id, m] of Object.entries(MANGA)) {
      // title 匹配主标题 + 别名（含中文别名命中：实测「海贼王」搜到 One Piece）
      const hay = JSON.stringify([m.title, m.altTitles]);
      if (hay.includes(title)) {
        data.push({
          id,
          type: "manga",
          attributes: {
            title: m.title, altTitles: m.altTitles, description: m.description,
            originalLanguage: m.originalLanguage, status: m.status, tags: m.tags,
          },
          relationships: [
            { id: "cover-" + id.slice(0, 4), type: "cover_art", attributes: { fileName: m.coverFile } },
          ],
        });
      }
    }
    return json(200, { result: "ok", data });
  }

  const m = p.match(/^\/manga\/([0-9a-f-]+)$/);
  if (m) {
    const id = m[1];
    const md = MANGA[id];
    if (!md) return json(404, { result: "error" });
    return json(200, {
      result: "ok",
      data: [{
        id, type: "manga",
        attributes: {
          title: md.title, altTitles: md.altTitles, description: md.description,
          originalLanguage: md.originalLanguage, status: md.status, tags: md.tags,
        },
        relationships: [{ id: "cover-x", type: "cover_art", attributes: { fileName: md.coverFile } }],
      }],
    });
  }

  // 封面 CDN
  if (p.startsWith("/covers/")) {
    res.writeHead(200, { "Content-Type": "image/jpeg" });
    res.end(Buffer.from([0xff, 0xd8, 0xff, 0xe0, 5, 5, 5, 5]));
    return;
  }

  json(404, { error: "no route: " + req.method + " " + req.url });
});

server.listen(PORT, () => console.log(`[mock-mangadex] listening on :${PORT} (zh-Hans titles)`));
