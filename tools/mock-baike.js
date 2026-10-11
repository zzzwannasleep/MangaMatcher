// mock-baike.js — 百度百科开放卡片接口 mock（:25694）。
// GET /api/openapi/BaikeLemmaCardApi?scope=103&format=json&appid=379020&bk_key=... + 概述图 /image。
// 用法：node tools/mock-baike.js [port]
const http = require("http");

const PORT = Number(process.argv[2] || process.env.MOCK_BAIKE_PORT || 25694);

const LEMMAS = {
  碧蓝之海: {
    id: 9527,
    title: "碧蓝之海",
    desc: "井上坚二作画、吉冈公威执笔的日本漫画",
    abstract:
      "《碧蓝之海》是井上坚二、吉冈公威创作的日本漫画，讲述大学生加入潜水社团后围绕潜水与饮酒的青春喜剧故事。",
    image: "/image/cover-baike.jpg",
    url: "http://baike.baidu.com/item/碧蓝之海",
    card: [
      { name: "中文名", value: ["碧蓝之海"] },
      { name: "原版名称", value: ["ぐらんぶる"] },
      { name: "作画", value: ["吉冈公威"] },
      { name: "作者", value: ["井上坚二"] },
      { name: "出版社", value: ["讲谈社"] },
      { name: "类型", value: ["喜剧", "校园"] },
    ],
  },
};

const server = http.createServer((req, res) => {
  const u = new URL(req.url, `http://localhost:${PORT}`);
  const p = u.pathname;

  if (p === "/api/openapi/BaikeLemmaCardApi") {
    const key = u.searchParams.get("bk_key") || "";
    const lemma = LEMMAS[key];
    if (!lemma) {
      // 未命中：接口只回 errno/errMsg，没有 title
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ errno: -1, errMsg: "词条不存在" }));
      return;
    }
    res.writeHead(200, { "Content-Type": "application/json" });
    res.end(
      JSON.stringify({
        id: lemma.id, newLemmaId: lemma.id, title: lemma.title, desc: lemma.desc,
        abstract: lemma.abstract, image: lemma.image, url: lemma.url,
        card: lemma.card,
      }),
    );
    return;
  }

  // 概述图（bkimg 模拟：带 x-bce-process 参数也 200；无参数 200）
  if (p.startsWith("/image/")) {
    res.writeHead(200, { "Content-Type": "image/jpeg" });
    res.end(Buffer.from([0xff, 0xd8, 0xff, 0xe0, 3, 3, 3, 3]));
    return;
  }

  res.writeHead(404, { "Content-Type": "application/json" });
  res.end(JSON.stringify({ error: "no route: " + req.method + " " + req.url }));
});

server.listen(PORT, () => console.log(`[mock-baike] listening on :${PORT} (lemma card api)`));
