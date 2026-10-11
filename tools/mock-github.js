// 模拟 GitHub API（tags / releases / 资产 zip），用于离线自测「检查更新」全链路。
// 配合 tools/build.ps1 -Version 注入一个小于 MOCK_LATEST 的版本号，即可看到「发现新版本」。
//
//   node tools/mock-github.js
//
// env：
//   MOCK_LATEST_DOCKER   docker tags 里返回的最新 tag（默认 docker-v9.9.9）
//   MOCK_LATEST_DESKTOP  releases 里返回的最新 tag（默认 v9.9.9）
//   MOCK_PORT            监听端口（默认 25693）
//
// 端点：
//   GET /repos/:owner/:repo/tags                → Go 端 update.Checker 消费
//   GET /repos/:owner/:repo/releases?per_page=N → 桌面端 UpdateService 消费
//   GET /asset/:name                            → 资产下载（内含标记文件的 zip，非可运行程序）
//   GET /health
//
// 注意：mock 资产 zip 只含标记文件（不是可运行程序）。桌面端「下载并安装」的完整自更新
// 请用真实 GitHub Releases（真实资产是完整程序包）；mock 覆盖的是检查/提示链路。
const http = require('http');

const PORT = Number(process.env.MOCK_PORT) || 25693;
const LATEST_DOCKER = process.env.MOCK_LATEST_DOCKER || 'docker-v9.9.9';
const LATEST_DESKTOP = process.env.MOCK_LATEST_DESKTOP || 'v9.9.9';
const REPO = 'zzzwannasleep/MangaMatcher';
const SELF = `http://127.0.0.1:${PORT}`;

// ---------- 最小 STORE zip 生成（无压缩，CRC32 自算；.NET ZipFile 可直接打开） ----------

const CRC_TABLE = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
    t[n] = c;
  }
  return t;
})();

function crc32(buf) {
  let c = -1;
  for (let i = 0; i < buf.length; i++) c = (c >>> 8) ^ CRC_TABLE[(c ^ buf[i]) & 0xFF];
  return (c ^ -1) >>> 0;
}

/** files: [{ name, data: Buffer }] → 合法 zip（stored） */
function makeZip(files) {
  const chunks = [];
  const central = [];
  let offset = 0;
  for (const f of files) {
    const name = Buffer.from(f.name, 'utf8');
    const crc = crc32(f.data);
    const lfh = Buffer.alloc(30);
    lfh.writeUInt32LE(0x04034b50, 0);
    lfh.writeUInt16LE(20, 4);            // version needed
    lfh.writeUInt16LE(0, 6);             // flags
    lfh.writeUInt16LE(0, 8);             // method: store
    lfh.writeUInt16LE(0, 10);            // mod time
    lfh.writeUInt16LE(0x2100, 12);       // mod date (1980-01-01)
    lfh.writeUInt32LE(crc, 14);
    lfh.writeUInt32LE(f.data.length, 18);
    lfh.writeUInt32LE(f.data.length, 22);
    lfh.writeUInt16LE(name.length, 26);
    lfh.writeUInt16LE(0, 28);
    chunks.push(lfh, name, f.data);

    const cdh = Buffer.alloc(46);
    cdh.writeUInt32LE(0x02014b50, 0);
    cdh.writeUInt16LE(20, 4);            // version made by
    cdh.writeUInt16LE(20, 6);            // version needed
    cdh.writeUInt16LE(0, 8);             // flags
    cdh.writeUInt16LE(0, 10);            // method
    cdh.writeUInt16LE(0, 12);            // mod time
    cdh.writeUInt16LE(0x2100, 14);       // mod date
    cdh.writeUInt32LE(crc, 16);
    cdh.writeUInt32LE(f.data.length, 20);
    cdh.writeUInt32LE(f.data.length, 24);
    cdh.writeUInt16LE(name.length, 28);
    cdh.writeUInt16LE(0, 30);            // extra len
    cdh.writeUInt16LE(0, 32);            // comment len
    cdh.writeUInt16LE(0, 34);            // disk
    cdh.writeUInt16LE(0, 36);            // internal attrs
    cdh.writeUInt32LE(0, 38);            // external attrs
    cdh.writeUInt32LE(offset, 42);       // local header offset
    central.push(Buffer.concat([cdh, name]));
    offset += 30 + name.length + f.data.length;
  }
  const cd = Buffer.concat(central);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0);
  eocd.writeUInt16LE(0, 4);              // this disk
  eocd.writeUInt16LE(0, 6);              // cd start disk
  eocd.writeUInt16LE(files.length, 8);
  eocd.writeUInt16LE(files.length, 10);
  eocd.writeUInt32LE(cd.length, 12);
  eocd.writeUInt32LE(offset, 16);        // cd offset
  eocd.writeUInt16LE(0, 20);             // comment len
  return Buffer.concat([...chunks, cd, eocd]);
}

const ver = LATEST_DESKTOP.replace(/^v/, '');
const marker = Buffer.from(
  `这是 tools/mock-github.js 生成的测试更新包（v${ver}）。\r\n` +
  '仅用于离线自测「检查更新」链路——不是可运行程序，请勿用它覆盖真实安装目录。\r\n',
  'utf8');

// 平台资产名与桌面端 UpdateService.PickAsset 的匹配规则一致
const assets = [
  'KomgaScraper-{v}-win-x64.zip',
  'KomgaScraper-{v}-macos-arm64.zip',
  'KomgaScraper-{v}-macos-x64.zip',
].map((tpl) => {
  const name = tpl.replace('{v}', ver);
  return { name, browser_download_url: `${SELF}/asset/${name}` };
});

const json = (w, code, obj) => {
  const body = JSON.stringify(obj);
  w.writeHead(code, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) });
  w.end(body);
};

const server = http.createServer((req, res) => {
  const url = new URL(req.url, SELF);
  const path = url.pathname;
  log(`${req.method} ${path}`);

  if (req.method === 'GET' && path === '/health') {
    return json(res, 200, { ok: true, latestDocker: LATEST_DOCKER, latestDesktop: LATEST_DESKTOP });
  }

  // Go 端 docker 更新检查
  if (req.method === 'GET' && /^\/repos\/[^/]+\/[^/]+\/tags$/.test(path)) {
    return json(res, 200, [
      { name: LATEST_DOCKER },
      { name: 'docker-v0.0.1' },
      { name: 'not-a-version' },
    ]);
  }

  // 桌面端 release 检查
  if (req.method === 'GET' && /^\/repos\/[^/]+\/[^/]+\/releases$/.test(path)) {
    return json(res, 200, [
      {
        tag_name: LATEST_DESKTOP,
        name: `v${ver}（mock）`,
        draft: false,
        prerelease: false,
        html_url: `${SELF}/releases/tag/${LATEST_DESKTOP}`,
        published_at: new Date().toISOString(),
        assets,
      },
      { tag_name: 'v0.0.1', draft: false, prerelease: false, html_url: '', published_at: '', assets: [] },
    ]);
  }

  // 资产下载（按请求的文件名即时生成）
  if (req.method === 'GET' && path.startsWith('/asset/')) {
    const assetName = decodeURIComponent(path.slice('/asset/'.length));
    const zip = makeZip([{ name: 'MOCK-UPDATE.txt', data: marker }]);
    res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': zip.length });
    return res.end(zip);
  }

  res.writeHead(404, { 'Content-Type': 'application/json' });
  res.end('{"message":"Not Found"}');
});

function log(msg) {
  console.log(`[MOCK-GH] ${new Date().toISOString().slice(11, 19)} ${msg}`);
}

server.listen(PORT, () => {
  console.log(`[MOCK] github mock ready → ${SELF}`);
  console.log(`[MOCK] docker tags: ${LATEST_DOCKER} · desktop release: ${LATEST_DESKTOP}`);
  console.log(`[MOCK] 配合构建注入更小版本号即可看到「发现新版本」（见 docs/DEVELOPMENT.md §6）`);
});
