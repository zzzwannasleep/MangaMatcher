import sqlite3, sys

con = sqlite3.connect(r'D:\WorkProgram\komgabangumidocker\reference\bkwk-db.sqlite')
con.text_factory = lambda b: b.decode('utf-8', 'replace')
cur = con.cursor()

print('=== tables ===')
for (name,) in cur.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name"):
    n = cur.execute(f'SELECT COUNT(*) FROM "{name}"').fetchone()[0]
    print(f'{name}: {n} rows')

print()
print('=== schema ===')
for (sql,) in cur.execute("SELECT sql FROM sqlite_master WHERE type='table' AND sql IS NOT NULL ORDER BY name"):
    print(sql[:800])
    print('---')