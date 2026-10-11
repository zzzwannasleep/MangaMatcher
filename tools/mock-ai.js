// mock-ai.js — OpenAI 兼容 AI mock（:25697）。
// 按 prompt 关键词返回罐头 JSON（识别/选择/翻译/补写），供浏览器联调与 Go 集成测试对齐。
// 用法：node tools/mock-ai.js [port]
const http = require("http");

const PORT = Number(process.argv[2] || process.env.MOCK_AI_PORT || 25697);

function extractUserText(body) {
  const msgs = (body && body.messages) || [];
  const user = msgs.filter((m) => m.role === "user").map((m) => m.content).join("\n");
  return user || "";
}

// 罐头库：按关键词命中最合适的回复
const CANNED = [
  {
    when: (u) => /请判断这是哪部|通行名字|"jp"|输出它的通行/.test(u),
    reply: () =>
      JSON.stringify({ jp: "ぐらんぶる", cn: "碧蓝之海", en: "Grand Blue", confident: true }),
  },
  {
    when: (u) => /编号从 1 开始/.test(u) && /哪个候选/.test(u),
    reply: (u) => {
      // 选择：返回第 1 个候选（mock 语义：永远挑第一个）
      const lines = u.split("\n").filter((l) => /^\d+\./.test(l.trim()));
      const idx = lines.length > 0 ? 1 : -1;
      return JSON.stringify({ index: idx });
    },
  },
  {
    when: (u) => /还原成日文原名/.test(u),
    reply: () => "ぐらんぶる",
  },
  {
    when: (u) => /完整翻译成简体中文/.test(u),
    reply: (u) => {
      // 翻译：取原文第一句改写成固定译文（罐头）
      const m = u.match(/不要加引号：\n\n([\s\S]+)/);
      const src = m ? m[1].trim() : "原文";
      return "【mock译文】" + src.replace(/\s+/g, " ").slice(0, 200);
    },
  },
  {
    when: (u) => /写一段简体中文的作品简介/.test(u),
    reply: () =>
      "这是一部围绕潜水与饮酒展开的青春校园喜剧，大学生们在社团活动中闹出连串笑话，" +
      "同时在欢笑与冒险中收获友情与成长。作品以夸张的颜艺与清爽的海底作画见长，是夏日消暑的必看之作。",
  },
];

function replyFor(userText) {
  for (const c of CANNED) {
    try {
      if (c.when(userText)) return c.reply(userText);
    } catch (e) {
      /* 继续尝试下一个罐头 */
    }
  }
  return "UNKNOWN";
}

const server = http.createServer((req, res) => {
  if (req.method === "POST" && (req.url === "/v1/chat/completions" || req.url === "/chat/completions")) {
    let raw = "";
    req.on("data", (d) => (raw += d));
    req.on("end", () => {
      let body = {};
      try { body = JSON.parse(raw); } catch (e) { /* 空体容忍 */ }
      const content = replyFor(extractUserText(body));
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({
        id: "chatcmpl-mock", object: "chat.completion", model: body.model || "mock",
        choices: [{ index: 0, message: { role: "assistant", content }, finish_reason: "stop" }],
      }));
    });
    return;
  }
  res.writeHead(404, { "Content-Type": "application/json" });
  res.end(JSON.stringify({ error: "no route: " + req.method + " " + req.url }));
});

server.listen(PORT, () => console.log(`[mock-ai] listening on :${PORT} (OpenAI /v1/chat/completions)`));
