// 模拟 Bangumi API（v0），用于端到端测试
const http = require('http');

const IMG_JPEG = Buffer.concat([Buffer.from([0xff, 0xd8, 0xff, 0xe0]), Buffer.alloc(200, 7)]);

const subject = {
  id: 600001,
  name: 'ぐらんぶる',
  name_cn: '碧藍之海',
  platform: '漫画',
  summary: '  大学生・伊志田空美与表哥北原伊织一起加入大学社团「うみほの会」。\r\n  围绕潜水与饮酒的青春故事。  ',
  volumes: 16,
  eps: null,
  total_episodes: null,
  image: 'http://127.0.0.1:25698/img/main.jpg',
  images: {
    large: 'http://127.0.0.1:25698/img/large.jpg',
    medium: 'http://127.0.0.1:25698/img/medium.jpg',
    common: 'http://127.0.0.1:25698/img/common.jpg',
    small: 'http://127.0.0.1:25698/img/small.jpg',
  },
  date: '2014-05-09',
  nsfw: false,
  age_rating: 0,
  tags: [
    { name: '搞笑', count: 1200 },
    { name: '校园', count: 900 },
    { name: '青春', count: 700 },
    { name: '水下探险', count: 300 },
    { name: '井上堅二', count: 200 },
    { name: '軽小说', count: 5 },
    { name: '连载中', count: 50 },
  ],
  rating: { score: 8.6 },
  infobox: [
    { key: '中文名', value: '碧蓝之海' },
    { key: '作者', value: [{ v: '井上堅二' }, { v: '吉岡公威' }] },
    { key: '作画', value: '吉岡公威' },
    { key: '出版社', value: '講談社' },
    { key: '连载杂志', value: 'good!アフタヌーン' },
    { key: '別名', value: [{ v: 'Grand Blue' }, { v: 'Grand Blue 碧藍之海' }] },
    { key: '状态', value: '完结' },
    { key: '发售日', value: '2014年5月9日' },
  ],
};

const volumes = [
  {
    id: 600101, name: 'ぐらんぶる (1)', name_cn: '碧蓝之海 1', relation: '单行本',
    image: 'http://127.0.0.1:25698/img/vol1.jpg',
    images: { large: 'http://127.0.0.1:25698/img/vol1.jpg', medium: null, common: null },
  },
  {
    id: 600102, name: 'ぐらんぶる (2)', name_cn: '碧蓝之海 2', relation: '单行本',
    image: 'http://127.0.0.1:25698/img/vol2.jpg',
    images: { large: 'http://127.0.0.1:25698/img/vol2.jpg', medium: null, common: null },
  },
  {
    id: 600103, name: 'another work', name_cn: '其他作品', relation: '主线故事',
    image: null, images: {},
  },
];

const volumeDetails = {
  600101: {
    id: 600101, name: 'ぐらんぶる (1)', name_cn: '碧蓝之海 1',
    summary: '  第1卷。  伊织大学生活开始。 ',
    infobox: [{ key: '发售日', value: '2014年5月9日' }, { key: 'ISBN', value: '9784063883237' }],
  },
  600102: {
    id: 600102, name: 'ぐらんぶる (2)', name_cn: '碧蓝之海 2',
    summary: '第2卷。',
    infobox: [{ key: '发售日', value: '2014年11月21日' }, { key: 'ISBN', value: '9784063885231' }],
  },
};

const server = http.createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const p = url.pathname;
  console.log(`[MOCK-BGM] ${req.method} ${req.url}`);

  if (req.method === 'GET' && p.startsWith('/img/')) {
    res.setHeader('Content-Type', 'image/jpeg');
    res.end(IMG_JPEG);
    return;
  }

  res.setHeader('Content-Type', 'application/json');

  if (req.method === 'POST' && p === '/v0/search/subjects') {
    let raw = '';
    req.on('data', (c) => (raw += c));
    req.on('end', () => {
      const body = JSON.parse(raw || '{}');
      console.log(`[MOCK-BGM]   search keyword="${body.keyword}" filter=${JSON.stringify(body.filter)}`);
      // 仅当关键词包含 碧蓝/ぐらんぶる 时返回结果，模拟精确匹配场景
      if (/碧蓝|ぐらんぶる|Grand Blue/i.test(body.keyword || '')) {
        res.end(JSON.stringify({ data: [subject] }));
      } else {
        res.end(JSON.stringify({ data: [] }));
      }
    });
    return;
  }

  const m = p.match(/^\/v0\/subjects\/(\d+)$/);
  if (req.method === 'GET' && m) {
    const id = Number(m[1]);
    if (id === 600001) res.end(JSON.stringify(subject));
    else if (volumeDetails[id]) res.end(JSON.stringify(volumeDetails[id]));
    else { res.statusCode = 404; res.end(JSON.stringify({ error: 'not found' })); }
    return;
  }

  if (req.method === 'GET' && p === '/v0/subjects/600001/subjects') {
    res.end(JSON.stringify(volumes));
    return;
  }

  res.statusCode = 404;
  res.end(JSON.stringify({ error: `no route ${req.method} ${p}` }));
});

server.listen(25698, '127.0.0.1', () => console.log('[MOCK-BGM] Bangumi mock listening on http://127.0.0.1:25698'));