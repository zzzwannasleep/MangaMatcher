// 模拟 Komga API 服务器，用于 Core 层端到端测试
const http = require('http');

const state = {
  libraries: [{ id: 'lib1', name: 'Comics', root: '/data' }],
  series: {
    s1: {
      id: 's1', name: '[碧蓝之海][井上堅二×吉岡公威][Vol.01-Vol.05]', libraryId: 'lib1',
      url: '/data/Grand Blue', oneshot: false, booksCount: 2, deleted: false,
      metadata: {
        status: '', title: '', summary: '', publisher: '', ageRating: null,
        genres: [], tags: [], links: [], alternateTitles: [], authors: [],
        totalBookCount: null,
      },
      booksMetadata: { releaseDate: null, summary: null, authors: null, tags: null },
    },
  },
  books: [
    {
      id: 'b1', name: 'Grand Blue Vol.01', seriesId: 's1', number: 1, oneshot: false,
      metadata: { title: null, number: null, numberSort: 1, summary: null, releaseDate: null, isbn: null, authors: [], tags: [], links: [] },
    },
    {
      id: 'b2', name: 'Grand Blue Vol.02', seriesId: 's1', number: 2, oneshot: false,
      metadata: { title: null, number: null, numberSort: 2, summary: null, releaseDate: null, isbn: null, authors: [], tags: [], links: [] },
    },
  ],
  collections: [],
};

function logReq(method, url, body) {
  const short = body ? JSON.stringify(body).slice(0, 300) : '';
  console.log(`[MOCK] ${method} ${url} ${short}`);
}

const server = http.createServer((req, res) => {
  let raw = '';
  req.on('data', (c) => (raw += c));
  req.on('end', () => {
    let body = null;
    try { body = raw ? JSON.parse(raw) : null; } catch { body = raw; }
    logReq(req.method, req.url, body);
    const url = new URL(req.url, 'http://localhost');
    const p = url.pathname;
    const q = url.searchParams;

    res.setHeader('Content-Type', 'application/json');

    if (req.method === 'GET' && p === '/api/v1/libraries') {
      res.end(JSON.stringify(state.libraries));
    } else if (req.method === 'POST' && p === '/api/v1/series/list') {
      res.end(JSON.stringify({ content: Object.values(state.series), totalElements: 1, numberOfElements: 1 }));
    } else if (req.method === 'GET' && /^\/api\/v1\/series\/s1$/.test(p)) {
      res.end(JSON.stringify(state.series.s1));
    } else if (req.method === 'GET' && p === '/api/v1/series/s1/metadata') {
      res.end(JSON.stringify(state.series.s1.metadata));
    } else if (req.method === 'PATCH' && p === '/api/v1/series/s1/metadata') {
      Object.assign(state.series.s1.metadata, body);
      console.log(`[MOCK] >>> SERIES METADATA PATCHED: title=${body?.title} status=${body?.status} tags=${JSON.stringify(body?.tags?.slice(0, 5))} authors=${JSON.stringify(body?.authors)}`);
      res.statusCode = 204; res.end();
    } else if (req.method === 'POST' && p === '/api/v1/books/list') {
      const filtered = state.books.filter((b) => !b.deleted);
      res.end(JSON.stringify({ content: filtered, totalElements: filtered.length, numberOfElements: filtered.length }));
    } else if (req.method === 'PATCH' && /^\/api\/v1\/books\/(b\d+)\/metadata$/.test(p)) {
      const id = RegExp.$1;
      const book = state.books.find((b) => b.id === id);
      Object.assign(book.metadata, body);
      console.log(`[MOCK] >>> BOOK ${id} METADATA PATCHED: title=${body?.title} number=${body?.number} summary=${body?.summary ? 'yes' : 'no'}`);
      res.statusCode = 204; res.end();
    } else if (req.method === 'GET' && p === '/api/v1/series/s1/thumbnails') {
      res.end(JSON.stringify([]));
    } else if (req.method === 'POST' && p === '/api/v1/series/s1/thumbnails') {
      console.log(`[MOCK] >>> SERIES COVER UPLOADED (bytes ${raw.length})`);
      res.statusCode = 204; res.end();
    } else if (req.method === 'GET' && p === '/api/v1/series/s1/thumbnail') {
      res.setHeader('Content-Type', 'image/jpeg');
      res.end(Buffer.from([0xff, 0xd8, 0xff, 0xe0, 1, 2, 3, 4]));
    } else if (req.method === 'GET' && /^\/api\/v1\/books\/b\d+\/thumbnails$/.test(p)) {
      res.end(JSON.stringify([]));
    } else if (req.method === 'POST' && /^\/api\/v1\/books\/(b\d+)\/thumbnails$/.test(p)) {
      console.log(`[MOCK] >>> BOOK COVER UPLOADED (${raw.length} bytes)`);
      res.statusCode = 204; res.end();
    } else if (req.method === 'GET' && p === '/api/v1/collections') {
      res.end(JSON.stringify({ content: state.collections, totalElements: state.collections.length }));
    } else if (req.method === 'POST' && p === '/api/v1/collections') {
      const c = { id: 'c1', ...body };
      state.collections.push(c);
      console.log(`[MOCK] >>> COLLECTION CREATED: ${JSON.stringify(c)}`);
      res.end(JSON.stringify(c));
    } else if (req.method === 'PATCH' && /^\/api\/v1\/collections\//.test(p)) {
      const c = state.collections.find((x) => x.id === p.split('/').pop());
      if (c) c.seriesIds = body.seriesIds;
      console.log(`[MOCK] >>> COLLECTION UPDATED: ${JSON.stringify(c)}`);
      res.statusCode = 204; res.end();
    } else {
      res.statusCode = 404;
      res.end(JSON.stringify({ error: `no route: ${req.method} ${p}` }));
    }
  });
});

server.listen(25699, '127.0.0.1', () => console.log('[MOCK] Komga mock listening on http://127.0.0.1:25699'));
