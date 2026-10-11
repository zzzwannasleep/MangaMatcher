package scrape

import (
	"archive/zip"
	"bytes"
	"encoding/json"
	"os"
	"path"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"komgabangumi/internal/komga"
)

// MylarExporter series.json 导出（移植 MylarExporter.cs / KomgaMylar export_series_as_mylar_json）：
// 导出库内系列元数据为 Mylar 格式，保持相对目录结构，可选下载封面。
type MylarExporter struct {
	Komga *komga.Client
	Log   Logger
}

// MylarEntry 单个导出产物（ZIP 条目或直写文件）。
type MylarEntry struct {
	RelPath string // 库内相对路径（目录/series.json 或 xxx.oneshot.json）
	Data    []byte
}

// NormalizeAgeRating Komga 数字分级 → Mylar 分级字符串（对齐 normalize_age_rating）。
func NormalizeAgeRating(value *int) string {
	if value == nil {
		return ""
	}
	switch v := *value; {
	case v <= 0:
		return "All"
	case v < 12:
		return "9+"
	case v < 15:
		return "12+"
	case v < 17:
		return "15+"
	case v < 18:
		return "17+"
	default:
		return "Adult"
	}
}

// validSeries 过滤书籍数异常的系列（totalBookCount 与 booksCount 都无 → 跳过）。
func mylarValid(series []komga.SeriesDto) []komga.SeriesDto {
	var out []komga.SeriesDto
	for _, s := range series {
		total := 0
		if s.Metadata != nil && s.Metadata.TotalBookCount != nil {
			total = *s.Metadata.TotalBookCount
		}
		if total <= 0 {
			total = s.BooksCount
		}
		if total <= 0 {
			continue
		}
		out = append(out, s)
	}
	return out
}

// mylarSeriesJSON 生成单个系列的 Mylar series.json 内容。
func mylarSeriesJSON(series komga.SeriesDto) []byte {
	metadata := series.Metadata
	title := series.Name
	if metadata != nil && metadata.Title != "" {
		title = metadata.Title
	}
	komgaStatus := ""
	if metadata != nil {
		komgaStatus = strings.ToUpper(metadata.Status)
	}
	mylarStatus := "Continuing"
	switch komgaStatus {
	case "ENDED":
		mylarStatus = "Ended"
	}
	description := ""
	authors := []komga.AuthorDto{}
	tags := []string{}
	var releaseDate string
	if series.BooksMetadata != nil {
		description = series.BooksMetadata.Summary
		authors = series.BooksMetadata.Authors
		releaseDate = series.BooksMetadata.ReleaseDate
		tags = series.BooksMetadata.Tags
	}
	if metadata != nil && metadata.Summary != "" {
		description = metadata.Summary
	}
	if metadata != nil && len(metadata.Tags) > 0 {
		tags = metadata.Tags
	}
	var links []komga.LinkDto
	var alts []komga.AlternateTitleDto
	var genres []string
	if metadata != nil {
		links = metadata.Links
		alts = metadata.AlternateTitles
		genres = metadata.Genres
	}
	var language, rd string
	if metadata != nil {
		language, rd = metadata.Language, metadata.ReadingDirection
	}
	totalBooks := 0
	if metadata != nil && metadata.TotalBookCount != nil {
		totalBooks = *metadata.TotalBookCount
	}
	year := 2001
	if len(releaseDate) >= 4 && allDigits(releaseDate[:4]) {
		year = atoiSafe(releaseDate[:4])
	}
	publisher := ""
	if metadata != nil {
		publisher = metadata.Publisher
	}
	data := map[string]any{
		"version": "1.0.2",
		"metadata": map[string]any{
			"type":                  "comicSeries",
			"publisher":             publisher,
			"imprint":               nil,
			"name":                  title,
			"comicid":               9527,
			"year":                  year,
			"description_text":      description,
			"description_formatted": nil,
			"volume":                nil,
			"booktype":              "Print",
			"age_rating":            normalizeAgeRatingOrNil(metadata),
			"collects":              nil,
			"comic_image":           "",
			"total_issues":          totalBooks,
			"publication_run":       "",
			"status":                mylarStatus,
			"language":              language,
			"readingDirection":      rd,
			"releaseDate":           releaseDate,
			"authors":               authors,
			"links":                 links,
			"alternateTitles":       alts,
			"genres":                genres,
			"tags":                  tags,
		},
	}
	b, _ := json.MarshalIndent(data, "", "  ")
	return b
}

func normalizeAgeRatingOrNil(m *komga.SeriesMetadataDto) any {
	if m == nil || m.AgeRating == nil {
		return nil
	}
	return NormalizeAgeRating(m.AgeRating)
}

func allDigits(s string) bool {
	for _, c := range s {
		if c < '0' || c > '9' {
			return false
		}
	}
	return len(s) > 0
}

func atoiSafe(s string) int {
	n := 0
	for _, c := range s {
		n = n*10 + int(c-'0')
	}
	return n
}

// seriesRelPath 系列的库内相对目录 + 文件名：
// 普通系列 = series.URL 去掉库根前缀 + series.json；
// oneshot = 父目录 + {文件名}.oneshot.json（对齐桌面版目录结构推导）。
func mylarRelPath(series komga.SeriesDto, libRoot string) (dir, file string, ok bool) {
	urlPath := strings.ReplaceAll(series.URL, "\\", "/")
	if urlPath == "" {
		return "", "", false
	}
	var seriesDir string
	var stem string
	if series.Oneshot {
		seriesDir = path.Dir(urlPath)
		stem = strings.TrimSuffix(path.Base(urlPath), path.Ext(urlPath))
		if stem == "" || stem == "." || stem == "/" {
			return "", "", false
		}
	} else {
		seriesDir = urlPath
	}
	rel := seriesDir
	if libRoot != "" {
		root := strings.TrimRight(strings.ReplaceAll(libRoot, "\\", "/"), "/")
		norm := "/" + strings.TrimLeft(seriesDir, "/")
		if root != "" && strings.HasPrefix(norm, root+"/") {
			rel = strings.TrimPrefix(norm, root+"/")
		} else if strings.HasPrefix(seriesDir, root) {
			rel = strings.TrimPrefix(seriesDir, root)
		}
	}
	rel = strings.Trim(rel, "/")
	if rel == "" {
		rel = path.Base(seriesDir)
	}
	if series.Oneshot {
		return rel, stem + ".oneshot.json", true
	}
	return rel, "series.json", true
}

// BuildEntries 组装全部系列 + 可选封面的导出条目（内存态，供 ZIP/直写共用）。
func (e *MylarExporter) BuildEntries(seriesList []komga.SeriesDto, libRoot string, withCovers bool) ([]MylarEntry, int) {
	valid := mylarValid(seriesList)
	var entries []MylarEntry
	written := 0
	used := map[string]bool{}
	for _, series := range valid {
		dir, file, ok := mylarRelPath(series, libRoot)
		if !ok {
			if e.Log != nil {
				e.Log.Warn("[跳过] 系列 '" + series.Name + "' 缺少目录信息")
			}
			continue
		}
		rel := dir + "/" + file
		if used[rel] {
			continue
		}
		used[rel] = true
		entries = append(entries, MylarEntry{RelPath: rel, Data: mylarSeriesJSON(series)})
		written++
		if withCovers {
			name := "cover.jpg"
			if series.Oneshot {
				name = strings.TrimSuffix(file, ".oneshot.json") + ".cover.jpg"
			}
			blob, err := e.Komga.GetSeriesThumbnail(series.ID)
			if err != nil {
				if e.Log != nil {
					e.Log.Warn("[⚠️] 系列 '" + series.Name + "' 封面下载失败: " + err.Error())
				}
			} else {
				entries = append(entries, MylarEntry{RelPath: dir + "/" + name, Data: blob})
			}
		}
	}
	return entries, written
}

// BuildZip 打包为 ZIP 字节流（entries 需先经 BuildEntries）。
func BuildZip(entries []MylarEntry) ([]byte, error) {
	var buf bytes.Buffer
	zw := zip.NewWriter(&buf)
	for _, e := range entries {
		fh := &zip.FileHeader{Name: strings.ReplaceAll(e.RelPath, "\\", "/"), Method: zip.Deflate}
		fh.Modified = time.Now()
		w, err := zw.CreateHeader(fh)
		if err != nil {
			return nil, err
		}
		if _, err := w.Write(e.Data); err != nil {
			return nil, err
		}
	}
	if err := zw.Close(); err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

// WriteDirect 直写 comicsDir（库内相对目录结构；目录缺失时创建）。
func WriteDirect(entries []MylarEntry, comicsDir string) (int, error) {
	written := 0
	for _, e := range entries {
		target := filepath.Join(comicsDir, filepath.FromSlash(e.RelPath))
		if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
			return written, err
		}
		if err := os.WriteFile(target, e.Data, 0o644); err != nil {
			return written, err
		}
		written++
	}
	return written, nil
}

// sortedSeries 系列 DTO 稳定排序（测试/输出确定性）。
func sortedSeries(list []komga.SeriesDto) []komga.SeriesDto {
	out := append([]komga.SeriesDto{}, list...)
	sort.Slice(out, func(i, k int) bool { return out[i].ID < out[k].ID })
	return out
}
