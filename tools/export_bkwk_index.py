# 从 BookWalker 官方 sqlite 导出紧凑 JSON 索引（gzip），供 C# 端零依赖读取
import sqlite3, json, gzip, time

t0 = time.time()
con = sqlite3.connect(r'D:\WorkProgram\komgabangumidocker\reference\bkwk-db.sqlite')
cur = con.cursor()

# label -> publisher display name
pub_by_label = {}
for label_id, pub in cur.execute(
        "SELECT l.id, p.display_name FROM labels l JOIN publishers p ON l.publisher_id = p.id"):
    pub_by_label[label_id] = pub

# series -> external numeric id (bookwalker.jp series id, type 1)
ext_by_series = {}
for sid, eid in cur.execute("SELECT series_id, external_id FROM series_external_ids WHERE type = 1"):
    ext_by_series[sid] = eid

# series -> volume count
vol_by_series = {}
for sid, n in cur.execute("SELECT series_id, COUNT(*) FROM products GROUP BY series_id"):
    vol_by_series[sid] = n

# contributors by role: role 1 = author, 2 = artist(作画)
authors_by_series = {}   # series -> [{n, r}]
name_by_con = {}
for cid, name, name_alt in cur.execute("SELECT id, name, name_alt FROM contributors"):
    name_by_con[cid] = name_alt if name_alt else name

# products of series -> first product per series gives label_id (publisher)
label_by_series = {}
img_by_series = {}
for sid, label_id, img in cur.execute("SELECT series_id, label_id, image_id FROM products GROUP BY series_id"):
    label_by_series[sid] = label_id
    img_by_series[sid] = img

out = []
for sid, title, alts_json, desc, image_id in cur.execute(
        "SELECT id, title, alt_titles, description, image_id FROM series"):
    alts = json.loads(alts_json) if alts_json else []
    entry = {
        'id': sid,
        't': title,
        'a': [x for x in alts if x][:12],
        'd': (desc or '')[:1600],
        'p': pub_by_label.get(label_by_series.get(sid, ''), ''),
        'c': '', 'v': vol_by_series.get(sid, 0),
        's': ext_by_series.get(sid, ''),
        'au': [],
        'x': 0,
    }
    out.append(entry)

# contributors per series（字典索引，role 1=作者 2=作画）
idx_by_id = {e['id']: e for e in out}
for sid, cid, role in cur.execute(
        "SELECT DISTINCT p.series_id, pc.contributor_id, pc.role FROM product_contributors pc "
        "JOIN products p ON p.id = pc.product_id WHERE pc.role IN (1, 2)"):
    e = idx_by_id.get(sid)
    nm = name_by_con.get(cid, '')
    if e and nm and len(e['au']) < 8:
        e['au'].append({'n': nm, 'r': 1 if role == 1 else 2})

# adult 标记：series_tags -> tags name='adult'
adult_series = set()
for row in cur.execute(
        "SELECT st.series_id FROM series_tags st JOIN tags t ON st.tag_id = t.id WHERE t.name = 'adult'"):
    adult_series.add(row[0])
for sid in adult_series:
    e = idx_by_id.get(sid)
    if e:
        e['x'] = 1

# series 封面：series.image_id -> images.name
img_name_by_id = {}
for iid, name in cur.execute("SELECT id, name FROM images"):
    img_name_by_id[iid] = name
for sid, image_id in cur.execute("SELECT id, image_id FROM series"):
    e = idx_by_id.get(sid)
    if e:
        nm = img_name_by_id.get(image_id, '')
        if not nm and sid in img_by_series:
            nm = img_name_by_id.get(img_by_series[sid], '')
        e['c'] = nm

out = [e for e in out if e['c'] or e['t']]

path = r'D:\WorkProgram\komgabangumidocker\reference\bkwk-index.json.gz'
with gzip.open(path, 'wt', encoding='utf-8', compresslevel=9) as f:
    json.dump(out, f, ensure_ascii=False, separators=(',', ':'))

import os
print('entries:', len(out))
print('file MB:', round(os.path.getsize(path) / 1e6, 1))
print('elapsed:', round(time.time() - t0, 1), 's')
print('sample:', json.dumps(out[0], ensure_ascii=False)[:400])
print('onepiece:', json.dumps([e for e in out if 'One Piece' == e['t']][:1], ensure_ascii=False)[:400])