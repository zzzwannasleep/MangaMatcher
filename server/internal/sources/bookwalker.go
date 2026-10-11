package sources

import (
	"compress/gzip"
	"database/sql"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"komgabangumi/internal/text"
)

// BwVolume BookWalker 单卷（顶层 product）：卷名/发售日/ISBN/独立封面/独立简介。
type BwVolume struct {
	Order       int
	Title       string
	ReleaseDate string
	Isbn        string
	CoverName   string
	Summary     string
}

// IsEmpty 除了卷号之外没有任何有用数据。
func (v BwVolume) IsEmpty() bool {
	return v.ReleaseDate == "" && v.Isbn == "" && v.CoverName == "" && v.Summary == ""
}

// BwAuthor BookWalker 作者（Role 1=作者 2=作画）。
type BwAuthor struct {
	Name string
	Role int
}

// BwSeries BookWalker 系列条目（来自官方目录数据库导出的索引）。
type BwSeries struct {
	ID          string // CNT_xxx（BookWalker 内部 id）
	Title       string // 主标题（多为英文/日文）
	AltTitles   []string
	Description string
	Publisher   string
	Imprint     string // 出版品牌（labels.name，如 VIZ Media / KADOKAWA MANGA）
	Authors     []BwAuthor
	CoverURLs   []string // 多 CDN 候选
	SeriesURL   string   // https://bookwalker.jp/series/{n}/
	VolumeCount int      // 顶层卷数（不含分话）
	IsAdult     bool
	Genres      []string // tags.namespace=1（Shounen / Action / Romance…）
	Themes      []string // tags.namespace=2（Prince / Military / War…）
	Complete    bool     // tags.namespace=3 含 complete
	Type        int      // series.type：1=漫画 2=小说 3/4=其他
	Volumes     []BwVolume
}

// IsComic series.type = 1：漫画（阅读方向按从右往左）。
func (b *BwSeries) IsComic() bool { return b.Type == 1 }

// IsNovel series.type = 2：小说 / 轻小说。
func (b *BwSeries) IsNovel() bool { return b.Type == 2 }

// CurrentFormatVersion 当前索引格式版本。
const CurrentFormatVersion = 2

// BwSource 候选来源标签。
const BwSource = "Bw"

// BwIndexFormatError 索引格式无法识别。
type BwIndexFormatError struct{ Msg string }

func (e *BwIndexFormatError) Error() string { return e.Msg }

type bwAuthorDTO struct {
	N string `json:"n"`
	R int    `json:"r"`
}

type bwVolumeDTO struct {
	O int     `json:"o"`
	T *string `json:"t"`
	D *string `json:"d"`
	I *string `json:"i"`
	C *string `json:"c"`
	S *string `json:"s"`
}

type bwEntryDTO struct {
	ID  string        `json:"id"`
	T   string        `json:"t"`
	A   []string      `json:"a"`
	D   string        `json:"d"`
	P   string        `json:"p"`
	C   string        `json:"c"`
	V   int           `json:"v"`
	S   string        `json:"s"`
	AU  []bwAuthorDTO `json:"au"`
	X   int           `json:"x"`
	Ty  int           `json:"ty"`
	G   []string      `json:"g"`
	Th  []string      `json:"th"`
	Cp  int           `json:"cp"`
	Lb  *string       `json:"lb"`
	Vol []bwVolumeDTO `json:"vol"`
}

type bwIndexFile struct {
	V int          `json:"v"`
	N int          `json:"n"`
	E []bwEntryDTO `json:"e"`
}

// BookWalkerIndex BookWalker 本地离线索引（bkwk-index.json.gz）。
// 标题 + 别名做规范化精确匹配；数据为全球站目录（英/日文）。
// 索引格式：v2 = {"v":2,"e":[...]}（含每卷数据与标签）；v1 = 裸数组（旧版，可加载但会提示重建）。
type BookWalkerIndex struct {
	byNormTitle map[string]*BwSeries
	byID        map[string]*BwSeries
	count       int
	volumeTotal int
	formatVer   int
}

// Count 索引条目数。
func (ix *BookWalkerIndex) Count() int { return ix.count }

// VolumeTotal 索引里的总卷数。
func (ix *BookWalkerIndex) VolumeTotal() int { return ix.volumeTotal }

// FormatVersion 文件里记录的格式版本。
func (ix *BookWalkerIndex) FormatVersion() int { return ix.formatVer }

// IsLegacyFormat 旧格式（v1）索引：没有每卷数据与类型/题材标签。
func (ix *BookWalkerIndex) IsLegacyFormat() bool { return ix.formatVer < CurrentFormatVersion }

// BwNormalizeKey 标题规范化键。
func BwNormalizeKey(title string) string { return text.ExtractAndNormalizeTitle(title) }

// BuildCoverURLs 封面候选 URL（c.bookwalker.jp 优先，CloudFront 兜底）。
func BuildCoverURLs(coverName string) []string {
	if coverName == "" {
		return nil
	}
	return []string{"https://c.bookwalker.jp/" + coverName, "https://d2snwnmzyr8jue.cloudfront.net/" + coverName}
}

// LoadBookWalkerIndex 从 gzip JSON 加载索引。
func LoadBookWalkerIndex(gzPath string) (*BookWalkerIndex, error) {
	f, err := os.Open(gzPath)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	gz, err := gzip.NewReader(f)
	if err != nil {
		return nil, err
	}
	defer gz.Close()
	raw, err := ioReadAll(gz)
	if err != nil {
		return nil, err
	}
	ix := &BookWalkerIndex{
		byNormTitle: map[string]*BwSeries{}, byID: map[string]*BwSeries{}, formatVer: CurrentFormatVersion,
	}

	var entries []bwEntryDTO
	var arr []bwEntryDTO
	if json.Unmarshal(raw, &arr) == nil && arr != nil {
		entries = arr
		ix.formatVer = 1
	} else {
		var obj bwIndexFile
		if err := json.Unmarshal(raw, &obj); err != nil || obj.E == nil {
			return nil, &BwIndexFormatError{Msg: "BookWalker 索引格式无法识别，请在设置里重新构建索引。"}
		}
		entries = obj.E
		if obj.V > 0 {
			ix.formatVer = obj.V
		}
	}

	volTotal := 0
	for i := range entries {
		e := &entries[i]
		vols := make([]BwVolume, 0, len(e.Vol))
		for _, v := range e.Vol {
			deref := func(p *string) string {
				if p == nil {
					return ""
				}
				return *p
			}
			vols = append(vols, BwVolume{Order: v.O, Title: deref(v.T), ReleaseDate: deref(v.D),
				Isbn: deref(v.I), CoverName: deref(v.C), Summary: deref(v.S)})
		}
		volTotal += len(vols)
		bw := &BwSeries{
			ID: e.ID, Title: e.T, AltTitles: e.A, Description: e.D, Publisher: e.P,
			Authors: make([]BwAuthor, 0, len(e.AU)), CoverURLs: BuildCoverURLs(e.C),
			VolumeCount: e.V, IsAdult: e.X == 1, Genres: e.G, Themes: e.Th,
			Complete: e.Cp == 1, Type: e.Ty, Volumes: vols,
		}
		if e.Lb != nil {
			bw.Imprint = *e.Lb
		}
		for _, a := range e.AU {
			bw.Authors = append(bw.Authors, BwAuthor{Name: a.N, Role: a.R})
		}
		if e.S != "" {
			bw.SeriesURL = "https://bookwalker.jp/series/" + e.S + "/"
		}
		ix.byID[bw.ID] = bw
		ix.addKey(BwNormalizeKey(bw.Title), bw)
		for _, alt := range bw.AltTitles {
			ix.addKey(BwNormalizeKey(alt), bw)
		}
	}
	ix.count = len(entries)
	ix.volumeTotal = volTotal
	return ix, nil
}

func ioReadAll(r interface{ Read([]byte) (int, error) }) ([]byte, error) {
	return io.ReadAll(r)
}

func (ix *BookWalkerIndex) addKey(key string, bw *BwSeries) {
	if key == "" {
		return
	}
	if _, ok := ix.byNormTitle[key]; !ok {
		ix.byNormTitle[key] = bw
	}
}

// FindByID 按 BookWalker 内部 id 查找。
func (ix *BookWalkerIndex) FindByID(id string) *BwSeries {
	if ix == nil {
		return nil
	}
	return ix.byID[id]
}

// All 全部条目（诊断/测试用，按 ID 排序稳定输出）。
func (ix *BookWalkerIndex) All() []*BwSeries {
	out := make([]*BwSeries, 0, len(ix.byID))
	for _, v := range ix.byID {
		out = append(out, v)
	}
	sort.Slice(out, func(i, k int) bool { return out[i].ID < out[k].ID })
	return out
}

// SearchFuzzy 模糊查找（规范化标题互含），供候选面板展示；最多返回 max 条。
func (ix *BookWalkerIndex) SearchFuzzy(term string, max int) []*BwSeries {
	if ix == nil {
		return nil
	}
	key := BwNormalizeKey(term)
	if len([]rune(key)) < 2 {
		return nil
	}
	var result []*BwSeries
	for _, kv := range ix.All() {
		hit := strings.Contains(BwNormalizeKey(kv.Title), key)
		if !hit {
			for _, alt := range kv.AltTitles {
				if strings.Contains(BwNormalizeKey(alt), key) {
					hit = true
					break
				}
			}
		}
		if hit {
			result = append(result, kv)
			if len(result) >= max {
				break
			}
		}
	}
	return result
}

// FindExact 按标题（含别名）规范化精确查找；主词未命中时依次尝试备选词。
func (ix *BookWalkerIndex) FindExact(titles ...string) *BwSeries {
	if ix == nil {
		return nil
	}
	for _, t := range titles {
		if strings.TrimSpace(t) == "" {
			continue
		}
		if bw, ok := ix.byNormTitle[BwNormalizeKey(t)]; ok {
			return bw
		}
	}
	return nil
}

// ToCandidate 转成候选面板用的候选条目。
func (ix *BookWalkerIndex) ToCandidate(bw *BwSeries) Candidate {
	author := ""
	for _, a := range bw.Authors {
		if a.Role == 1 {
			author = a.Name
			break
		}
	}
	if author == "" {
		for _, a := range bw.Authors {
			if a.Role == 2 {
				author = a.Name
				break
			}
		}
	}
	aliases := bw.AltTitles
	if len(aliases) > 5 {
		aliases = aliases[:5]
	}
	return Candidate{BgmID: 0, Title: bw.Title, OriTitle: bw.Title, Author: author,
		Aliases: strings.Join(aliases, " / "), CoverURL: firstOf(bw.CoverURLs), Source: BwSource, BwID: bw.ID}
}

func firstOf(s []string) string {
	if len(s) == 0 {
		return ""
	}
	return s[0]
}

// ---------- 索引构建（官方 sqlite → gzip JSON） ----------

// MaxVolumesPerSeries 单个系列最多收录多少卷（防止个别超长连载把索引撑爆）。
const MaxVolumesPerSeries = 400

// MaxVolumeSummary 每卷简介的保留上限（超出截断）。
const MaxVolumeSummary = 400

// StripHtml 去掉 HTML 标签 + 压缩空白（对齐 SubjectMapper.StripHtml）。
func StripHtml(s string) string {
	var b strings.Builder
	inTag, inEntity := false, false
	var ent []rune
	flush := func() {
		if len(ent) > 0 {
			switch string(ent) {
			case "lt":
				b.WriteString("<")
			case "gt":
				b.WriteString(">")
			case "amp":
				b.WriteString("&")
			case "quot":
				b.WriteString("\"")
			case "apos":
				b.WriteString("'")
			case "nbsp":
				b.WriteString(" ")
			}
			ent = ent[:0]
		}
		inEntity = false
	}
	for _, r := range s {
		switch {
		case inTag:
			if r == '>' {
				inTag = false
			}
		case inEntity:
			if r == ';' {
				flush()
			} else if len(ent) < 10 {
				ent = append(ent, r)
			} else {
				flush()
				b.WriteRune(r)
			}
		case r == '<':
			inTag = true
		case r == '&':
			inEntity = true
		default:
			b.WriteRune(r)
		}
	}
	if inEntity {
		flush()
	}
	var out strings.Builder
	prevSpace := false
	for _, r := range b.String() {
		if r == ' ' || r == '\t' || r == '\n' || r == '\r' {
			if !prevSpace {
				out.WriteRune(' ')
			}
			prevSpace = true
		} else {
			out.WriteRune(r)
			prevSpace = false
		}
	}
	return strings.TrimSpace(out.String())
}

// BuildBookWalkerIndexFromSqlite 从官方目录库构建 v2 索引（gzip JSON 写 indexPath，只读打开）。
func BuildBookWalkerIndexFromSqlite(dbPath, indexPath string) error {
	dsn := "file:" + filepath.ToSlash(dbPath) + "?mode=ro"
	db, err := sql.Open("sqlite", dsn)
	if err != nil {
		return fmt.Errorf("打开 BookWalker 目录库失败: %w", err)
	}
	defer db.Close()
	db.SetMaxOpenConns(1)
	if _, err := db.Exec("PRAGMA temp_store = MEMORY"); err != nil {
		return err
	}
	if _, err := db.Exec("PRAGMA cache_size = -65536"); err != nil {
		return err
	}

	// ---- 出版品牌 label → 品牌名 + 出版社 ----
	pubByLabel := map[string]string{}
	labelName := map[string]string{}
	func() {
		rows, err := db.Query("SELECT l.id, l.name, p.display_name FROM labels l JOIN publishers p ON l.publisher_id = p.id")
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var id, name, pub string
			if err := rows.Scan(&id, &name, &pub); err == nil {
				labelName[id] = name
				pubByLabel[id] = pub
			}
		}
	}()

	// ---- series → 外部数字 id（bookwalker.jp 系列 id, type=1）----
	extBySeries := map[string]string{}
	func() {
		rows, err := db.Query("SELECT series_id, external_id FROM series_external_ids WHERE type = 1")
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var sid, ext string
			if err := rows.Scan(&sid, &ext); err == nil {
				extBySeries[sid] = ext
			}
		}
	}()

	// ---- 系列主表 ----
	type seriesRow struct {
		id, title, altJSON, desc, imgID string
		typ                             int
	}
	var seriesRows []seriesRow
	func() {
		rows, err := db.Query("SELECT id, title, alt_titles, description, image_id, type FROM series")
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var r seriesRow
			var title, alt, desc, img sql.NullString
			var typ sql.NullInt64
			if err := rows.Scan(&r.id, &title, &alt, &desc, &img, &typ); err == nil {
				r.title, r.altJSON, r.desc, r.imgID = title.String, alt.String, StripHtml(desc.String), img.String
				r.typ = int(typ.Int64)
				seriesRows = append(seriesRows, r)
			}
		}
	}()

	// ---- 顶层卷数（parent_content_id IS NULL，排除「分话」）----
	topVolBySeries := map[string]int{}
	func() {
		rows, err := db.Query("SELECT series_id, COUNT(*) FROM products WHERE parent_content_id IS NULL GROUP BY series_id")
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var sid string
			var n int
			if err := rows.Scan(&sid, &n); err == nil {
				topVolBySeries[sid] = n
			}
		}
	}()

	// ---- 每系列的 label / 封面（优先取顶层 product；再兜底所有 product）----
	labelBySeries := map[string]string{}
	imgBySeries := map[string]string{}
	loadLabelImg := func(query string, onlyMissing bool) {
		rows, err := db.Query(query)
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var sid, label, img string
			if err := rows.Scan(&sid, &label, &img); err == nil {
				if onlyMissing {
					if _, ok := labelBySeries[sid]; ok {
						continue
					}
				}
				labelBySeries[sid] = label
				imgBySeries[sid] = img
			}
		}
	}
	loadLabelImg("SELECT series_id, label_id, image_id FROM products WHERE parent_content_id IS NULL GROUP BY series_id", false)
	loadLabelImg("SELECT series_id, label_id, image_id FROM products GROUP BY series_id", true)

	// ---- contributor 名称 ----
	nameByCon := map[string]string{}
	func() {
		rows, err := db.Query("SELECT id, name, name_alt FROM contributors")
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var id, name, alt sql.NullString
			if err := rows.Scan(&id, &name, &alt); err == nil {
				if alt.String != "" {
					nameByCon[id.String] = alt.String
				} else {
					nameByCon[id.String] = name.String
				}
			}
		}
	}()

	// ---- 系列作者（role 1=作者 2=作画）----
	type authorPair struct {
		name string
		role int
	}
	authorsBySeries := map[string][]authorPair{}
	func() {
		rows, err := db.Query("SELECT DISTINCT p.series_id, pc.contributor_id, pc.role FROM product_contributors pc " +
			"JOIN products p ON p.id = pc.product_id WHERE pc.role IN (1, 2)")
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var sid, cid string
			var role int
			if err := rows.Scan(&sid, &cid, &role); err == nil {
				if len(authorsBySeries[sid]) < 8 {
					authorsBySeries[sid] = append(authorsBySeries[sid], authorPair{name: nameByCon[cid], role: role})
				}
			}
		}
	}()

	// ---- 系列标签：namespace 1=类型 2=题材 3=状态(complete/adult) ----
	genresBySeries := map[string][]string{}
	themesBySeries := map[string][]string{}
	completeSeries := map[string]bool{}
	adultSeries := map[string]bool{}
	func() {
		rows, err := db.Query("SELECT st.series_id, t.namespace, t.name FROM series_tags st " +
			"JOIN tags t ON t.id = st.tag_id WHERE t.namespace IN (1, 2, 3)")
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var sid string
			var ns int
			var name sql.NullString
			if err := rows.Scan(&sid, &ns, &name); err != nil {
				continue
			}
			nm := strings.TrimSpace(name.String)
			if nm == "" {
				continue
			}
			switch ns {
			case 1:
				if len(genresBySeries[sid]) < 12 && !containsStr(genresBySeries[sid], nm) {
					genresBySeries[sid] = append(genresBySeries[sid], nm)
				}
			case 2:
				if len(themesBySeries[sid]) < 15 && !containsStr(themesBySeries[sid], nm) {
					themesBySeries[sid] = append(themesBySeries[sid], nm)
				}
			case 3:
				if nm == "complete" {
					completeSeries[sid] = true
				} else if nm == "adult" {
					adultSeries[sid] = true
				}
			}
		}
	}()

	// ---- image_id → 文件名 ----
	imgName := map[string]string{}
	func() {
		rows, err := db.Query("SELECT id, name FROM images")
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var id, name string
			if err := rows.Scan(&id, &name); err == nil {
				imgName[id] = name
			}
		}
	}()

	// ---- ISBN（product_external_ids.type = 3）----
	isbnByProduct := map[string]string{}
	func() {
		rows, err := db.Query("SELECT product_id, external_id FROM product_external_ids WHERE type = 3")
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var pid, eid sql.NullString
			if err := rows.Scan(&pid, &eid); err == nil {
				e := strings.TrimSpace(eid.String)
				if len(e) == 13 || len(e) == 10 {
					isbnByProduct[pid.String] = e
				}
			}
		}
	}()

	// ---- 每卷原始行（顶层 product，按 display_order 排序）----
	type volRow struct {
		pid         string
		order       int
		title, date string
		imgID, desc string
	}
	rawBySeries := map[string][]volRow{}
	func() {
		rows, err := db.Query("SELECT series_id, id, display_order, title, on_sale_at, image_id, description " +
			"FROM products WHERE parent_content_id IS NULL ORDER BY series_id, display_order")
		if err != nil {
			return
		}
		defer rows.Close()
		for rows.Next() {
			var sid string
			var r volRow
			var pid, title, sale, img, desc sql.NullString
			var ord sql.NullInt64
			if err := rows.Scan(&sid, &pid, &ord, &title, &sale, &img, &desc); err != nil {
				continue
			}
			list := rawBySeries[sid]
			if len(list) >= MaxVolumesPerSeries {
				continue
			}
			r.pid, r.order = pid.String, int(ord.Int64)
			r.title = strings.TrimSpace(title.String)
			if len(sale.String) >= 10 {
				r.date = sale.String[:10]
			} else {
				r.date = sale.String
			}
			r.imgID, r.desc = img.String, StripHtml(desc.String)
			rawBySeries[sid] = append(list, r)
		}
	}()

	// ---- 组装 ----
	entries := make([]bwEntryDTO, 0, len(seriesRows))
	for _, s := range seriesRows {
		var alts []string
		if s.altJSON != "" {
			var arr []string
			if json.Unmarshal([]byte(s.altJSON), &arr) == nil {
				for _, a := range arr {
					if a != "" {
						alts = append(alts, a)
					}
					if len(alts) >= 12 {
						break
					}
				}
			}
		}
		coverName := imgName[s.imgID]
		if coverName == "" {
			coverName = imgName[imgBySeries[s.id]]
		}

		// 每卷：只保留「有增量信息」的字段
		var vols []bwVolumeDTO
		for _, v := range rawBySeries[s.id] {
			isbn := isbnByProduct[v.pid]
			volCover := ""
			if v.imgID != "" && v.imgID != s.imgID {
				volCover = imgName[v.imgID]
			}
			summary := ""
			if v.desc != "" && v.desc != s.desc {
				summary = runeTruncate(v.desc, MaxVolumeSummary)
			}
			vtitle := ""
			if v.title != "" && v.title != s.title {
				vtitle = runeTruncate(v.title, 60)
			}
			item := bwVolumeDTO{O: v.order}
			if vtitle != "" {
				item.T = &vtitle
			}
			if v.date != "" {
				item.D = &v.date
			}
			if isbn != "" {
				item.I = &isbn
			}
			if volCover != "" {
				item.C = &volCover
			}
			if summary != "" {
				item.S = &summary
			}
			vols = append(vols, item)
		}

		labelID := labelBySeries[s.id]
		e := bwEntryDTO{
			ID: s.id, T: s.title, A: alts, D: runeTruncate(s.desc, 1600),
			P: pubByLabel[labelID], C: coverName, V: topVolBySeries[s.id], S: extBySeries[s.id],
			X: boolInt(adultSeries[s.id]), Ty: s.typ,
			G: genresBySeries[s.id], Th: themesBySeries[s.id], Cp: boolInt(completeSeries[s.id]),
			Lb: strPtr(labelName[labelID]),
		}
		for _, a := range authorsBySeries[s.id] {
			e.AU = append(e.AU, bwAuthorDTO{N: a.name, R: a.role})
		}
		if len(vols) > 0 {
			e.Vol = vols
		}
		entries = append(entries, e)
	}

	if err := os.MkdirAll(filepath.Dir(indexPath), 0o755); err != nil {
		return err
	}
	out, err := os.Create(indexPath)
	if err != nil {
		return err
	}
	defer out.Close()
	gz := gzip.NewWriter(out)
	defer gz.Close()
	enc := json.NewEncoder(gz)
	return enc.Encode(map[string]any{"v": CurrentFormatVersion, "n": len(entries), "e": entries})
}

// IsSqliteFile 按文件头识别 sqlite（不看扩展名）。
func IsSqliteFile(path string) bool {
	f, err := os.Open(path)
	if err != nil {
		return false
	}
	defer f.Close()
	b := make([]byte, 16)
	if _, err := f.Read(b); err != nil {
		return false
	}
	return string(b[:15]) == "SQLite format 3" && b[15] == 0
}

// IsZstdFile 按魔数 28 B5 2F FD 识别 zstd 压缩包（不看扩展名）。
func IsZstdFile(path string) bool {
	f, err := os.Open(path)
	if err != nil {
		return false
	}
	defer f.Close()
	b := make([]byte, 4)
	if _, err := f.Read(b); err != nil {
		return false
	}
	return b[0] == 0x28 && b[1] == 0xB5 && b[2] == 0x2F && b[3] == 0xFD
}

func containsStr(list []string, v string) bool {
	for _, s := range list {
		if s == v {
			return true
		}
	}
	return false
}

func boolInt(b bool) int {
	if b {
		return 1
	}
	return 0
}

func strPtr(s string) *string {
	if s == "" {
		return nil
	}
	return &s
}

func runeTruncate(s string, max int) string {
	r := []rune(s)
	if len(r) <= max {
		return s
	}
	return string(r[:max])
}
