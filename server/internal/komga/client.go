package komga

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"mime/multipart"
	"net/http"
	"net/url"
	"reflect"
	"strconv"
	"strings"
	"time"
)

func itoa(n int) string { return strconv.Itoa(n) }

// APIError Komga API 错误（含状态码）。
type APIError struct {
	Status  int
	Message string
}

func (e *APIError) Error() string { return e.Message }

// Client Komga REST API 客户端。
// 逻辑对齐 KomgaScraper.Core/Komga/KomgaClient.cs：X-API-Key 优先，Basic 认证兜底。
type Client struct {
	baseURL string
	apiKey  string
	basic   string
	http    *http.Client
}

// New 创建客户端。baseURL 如 http://komga:25600；apiKey 或 user/password 二选一。
func New(baseURL, apiKey, username, password string) *Client {
	c := &Client{
		baseURL: strings.TrimRight(baseURL, "/"),
		http:    &http.Client{Timeout: 30 * time.Second},
	}
	if apiKey != "" {
		c.apiKey = apiKey
	} else if username != "" {
		c.basic = base64.StdEncoding.EncodeToString([]byte(username + ":" + password))
	}
	return c
}

// BaseURL 返回 Komga 基址。
func (c *Client) BaseURL() string { return c.baseURL }

func (c *Client) newRequest(method, path string, body []byte, contentType string) (*http.Request, error) {
	req, err := http.NewRequest(method, c.baseURL+"/api"+path, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	if c.apiKey != "" {
		req.Header.Set("X-API-Key", c.apiKey)
	} else if c.basic != "" {
		req.Header.Set("Authorization", "Basic "+c.basic)
	}
	req.Header.Set("User-Agent", "KomgaBangumi/1.0")
	if contentType != "" {
		req.Header.Set("Content-Type", contentType)
	}
	return req, nil
}

// send 发送请求：网络异常最多重试 3 次（1.5s/4s 退避）；非 2xx 抛 APIError。
func (c *Client) send(req *http.Request) (*http.Response, error) {
	var resp *http.Response
	var lastErr error
	for attempt := 1; ; attempt++ {
		r, err := c.http.Do(req)
		if err == nil {
			resp = r
			break
		}
		lastErr = err
		if attempt >= 3 {
			return nil, fmt.Errorf("Komga 请求网络异常: %w", err)
		}
		delay := 1500 * time.Millisecond
		if attempt == 2 {
			delay = 4 * time.Second
		}
		time.Sleep(delay)
		_ = lastErr
	}
	if resp.StatusCode == http.StatusUnauthorized {
		resp.Body.Close()
		return nil, &APIError{Status: 401, Message: "认证失败(401)：请检查 API Key 或用户名/密码"}
	}
	if resp.StatusCode == 413 {
		resp.Body.Close()
		return nil, &APIError{Status: 413, Message: "服务器拒绝(413 Payload Too Large)：封面过大"}
	}
	if resp.StatusCode >= 300 {
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 4096))
		resp.Body.Close()
		return nil, &APIError{Status: resp.StatusCode,
			Message: fmt.Sprintf("Komga API %d: %s", resp.StatusCode, truncate(string(b), 200))}
	}
	return resp, nil
}

// truncate 截断字符串（日志用）。
func truncate(s string, n int) string {
	r := []rune(s)
	if len(r) <= n {
		return s
	}
	return string(r[:n])
}

func (c *Client) doJSON(method, path string, payload any, out any) error {
	var body []byte
	contentType := ""
	if payload != nil {
		b, err := json.Marshal(payload)
		if err != nil {
			return err
		}
		body = b
		contentType = "application/json"
	}
	req, err := c.newRequest(method, path, body, contentType)
	if err != nil {
		return err
	}
	resp, err := c.send(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	if out != nil {
		return json.NewDecoder(resp.Body).Decode(out)
	}
	io.Copy(io.Discard, resp.Body)
	return nil
}

// ---------- 系列 ----------

// GetLibraries 库列表。
func (c *Client) GetLibraries() ([]LibraryDto, error) {
	var libs []LibraryDto
	if err := c.doJSON(http.MethodGet, "/v1/libraries", nil, &libs); err != nil {
		return nil, err
	}
	return libs, nil
}

// ListSeriesInLibrary 分页拉取库内全部系列（POST /series/list，size=2000，对齐桌面版）。
func (c *Client) ListSeriesInLibrary(libraryID string) ([]SeriesDto, error) {
	var all []SeriesDto
	const size = 2000
	page := 0
	for {
		var data Page[SeriesDto]
		path := fmt.Sprintf("/v1/series/list?size=%d&page=%d", size, page)
		if err := c.doJSON(http.MethodPost, path, SeriesInLibraryQuery(libraryID), &data); err != nil {
			return nil, err
		}
		all = append(all, data.Content...)
		if len(data.Content) < size {
			break
		}
		page++
	}
	return all, nil
}

// GetSeries 系列详情。
func (c *Client) GetSeries(seriesID string) (*SeriesDto, error) {
	var s SeriesDto
	if err := c.doJSON(http.MethodGet, "/v1/series/"+url.PathEscape(seriesID), nil, &s); err != nil {
		return nil, err
	}
	return &s, nil
}

// GetSeriesMetadata 系列元数据；无元数据/请求失败时返回 nil 继续流程（对齐桌面版容错）。
func (c *Client) GetSeriesMetadata(seriesID string) *SeriesMetadataDto {
	var m SeriesMetadataDto
	if err := c.doJSON(http.MethodGet, "/v1/series/"+url.PathEscape(seriesID)+"/metadata", nil, &m); err != nil {
		return nil
	}
	return &m
}

// PatchSeriesMetadata PATCH 系列元数据：只发非空标量，空数组允许发送（cleanMeta 规则）。
func (c *Client) PatchSeriesMetadata(seriesID string, meta *SeriesMetadataDto) error {
	body := CleanMeta(meta)
	if len(body) == 0 {
		return nil
	}
	return c.doJSON(http.MethodPatch, "/v1/series/"+url.PathEscape(seriesID)+"/metadata", body, nil)
}

// PatchSeriesMetadataRaw 编辑器/清空专用：按给定键值直接 PATCH（空串会真实写入）。
func (c *Client) PatchSeriesMetadataRaw(seriesID string, body map[string]any) error {
	if len(body) == 0 {
		return nil
	}
	return c.doJSON(http.MethodPatch, "/v1/series/"+url.PathEscape(seriesID)+"/metadata", body, nil)
}

// ListSeriesPaged 分页拉取库内系列（POST /series/list?page&size，API 代理用）。
func (c *Client) ListSeriesPaged(libraryID string, page, size int) (*Page[SeriesDto], error) {
	var data Page[SeriesDto]
	path := "/v1/series/list?size=" + itoa(size) + "&page=" + itoa(page)
	if err := c.doJSON(http.MethodPost, path, SeriesInLibraryQuery(libraryID), &data); err != nil {
		return nil, err
	}
	return &data, nil
}

// ListBooksInSeries 列出库内全部书（POST /books/list，unpaged=true，numberSort 升序）。
func (c *Client) ListBooksInSeries(seriesID string) ([]BookDto, error) {
	var data Page[BookDto]
	if err := c.doJSON(http.MethodPost, "/v1/books/list?unpaged=true&sort=metadata.numberSort,asc",
		BooksInSeriesQuery(seriesID), &data); err != nil {
		return nil, err
	}
	return data.Content, nil
}

// PatchBookMetadata PATCH 单册元数据（cleanMeta 规则）。
func (c *Client) PatchBookMetadata(bookID string, meta *BookMetadataDto) error {
	body := CleanMeta(meta)
	if len(body) == 0 {
		return nil
	}
	return c.doJSON(http.MethodPatch, "/v1/books/"+url.PathEscape(bookID)+"/metadata", body, nil)
}

// PatchBookMetadataRaw 按给定键值直接 PATCH 单卷元数据（空串=清空）。
func (c *Client) PatchBookMetadataRaw(bookID string, body map[string]any) error {
	if len(body) == 0 {
		return nil
	}
	return c.doJSON(http.MethodPatch, "/v1/books/"+url.PathEscape(bookID)+"/metadata", body, nil)
}

// GetBook 单册详情（含 seriesId，用于书卷级权限校验）。
func (c *Client) GetBook(bookID string) (*BookDto, error) {
	var b BookDto
	if err := c.doJSON(http.MethodGet, "/v1/books/"+url.PathEscape(bookID), nil, &b); err != nil {
		return nil, err
	}
	return &b, nil
}

// GetSeriesThumbnails 系列缩略图列表。
func (c *Client) GetSeriesThumbnails(seriesID string) ([]ThumbnailDto, error) {
	var list []ThumbnailDto
	if err := c.doJSON(http.MethodGet, "/v1/series/"+url.PathEscape(seriesID)+"/thumbnails", nil, &list); err != nil {
		return nil, err
	}
	return list, nil
}

// DeleteSeriesThumbnail 删除系列缩略图。
func (c *Client) DeleteSeriesThumbnail(seriesID, thumbID string) error {
	return c.doJSON(http.MethodDelete, "/v1/series/"+url.PathEscape(seriesID)+"/thumbnails/"+url.PathEscape(thumbID), nil, nil)
}

// UploadSeriesCover 上传系列封面（multipart, selected=true）。
func (c *Client) UploadSeriesCover(seriesID string, image []byte, fileName string) error {
	return c.uploadCover("/v1/series/"+url.PathEscape(seriesID)+"/thumbnails?selected=true", image, fileName)
}

// GetBookThumbnails 单册缩略图列表（旧版 Komga 无此接口时优雅降级为空）。
func (c *Client) GetBookThumbnails(bookID string) ([]ThumbnailDto, error) {
	var list []ThumbnailDto
	err := c.doJSON(http.MethodGet, "/v1/books/"+url.PathEscape(bookID)+"/thumbnails", nil, &list)
	if err != nil {
		var apiErr *APIError
		if errors.As(err, &apiErr) && apiErr.Status == 404 {
			return []ThumbnailDto{}, nil
		}
		return nil, err
	}
	return list, nil
}

// DeleteBookThumbnail 删除单册缩略图。
func (c *Client) DeleteBookThumbnail(bookID, thumbID string) error {
	return c.doJSON(http.MethodDelete, "/v1/books/"+url.PathEscape(bookID)+"/thumbnails/"+url.PathEscape(thumbID), nil, nil)
}

// UploadBookCover 上传单册封面（multipart, selected=true）。
func (c *Client) UploadBookCover(bookID string, image []byte, fileName string) error {
	return c.uploadCover("/v1/books/"+url.PathEscape(bookID)+"/thumbnails?selected=true", image, fileName)
}

func (c *Client) uploadCover(path string, image []byte, fileName string) error {
	var buf bytes.Buffer
	w := multipart.NewWriter(&buf)
	fw, err := w.CreateFormFile("file", fileName)
	if err != nil {
		return err
	}
	if _, err := fw.Write(image); err != nil {
		return err
	}
	if err := w.Close(); err != nil {
		return err
	}
	// multipart：原始字节体 + multipart Content-Type，绝不 json.Marshal
	req, err := c.newRequest(http.MethodPost, path, buf.Bytes(), w.FormDataContentType())
	if err != nil {
		return err
	}
	resp, err := c.send(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()
	io.Copy(io.Discard, resp.Body)
	return nil
}

// GetSeriesThumbnail 获取系列当前选中缩略图原图（请求头带 Accept: image/jpeg；
// Komga 对图片端点只认 image/jpeg，默认 application/json 会恒 406）。
func (c *Client) GetSeriesThumbnail(seriesID string) ([]byte, error) {
	req, err := c.newRequest(http.MethodGet, "/v1/series/"+url.PathEscape(seriesID)+"/thumbnail", nil, "")
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "image/jpeg")
	resp, err := c.send(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	return io.ReadAll(resp.Body)
}

// GetBookThumbnail 获取单册当前选中缩略图原图（请求头带 Accept: image/jpeg，
// 与系列封面同一规则：Komga 图片端点只认 image/jpeg）。
func (c *Client) GetBookThumbnail(bookID string) ([]byte, error) {
	req, err := c.newRequest(http.MethodGet, "/v1/books/"+url.PathEscape(bookID)+"/thumbnail", nil, "")
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", "image/jpeg")
	resp, err := c.send(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()
	return io.ReadAll(resp.Body)
}

// ---------- 收藏夹 ----------

// GetCollections 收藏夹列表（unpaged）。
func (c *Client) GetCollections() ([]CollectionDto, error) {
	var data Page[CollectionDto]
	if err := c.doJSON(http.MethodGet, "/v1/collections?unpaged=true", nil, &data); err != nil {
		return nil, err
	}
	return data.Content, nil
}

// CreateCollection 创建收藏夹。
func (c *Client) CreateCollection(name string, seriesIDs []string, ordered bool) (*CollectionDto, error) {
	payload := map[string]any{"name": name, "seriesIds": seriesIDs, "ordered": ordered}
	var cDto CollectionDto
	if err := c.doJSON(http.MethodPost, "/v1/collections", payload, &cDto); err != nil {
		return nil, err
	}
	return &cDto, nil
}

// UpdateCollectionSeries 更新收藏夹成员。
func (c *Client) UpdateCollectionSeries(collectionID string, seriesIDs []string) error {
	return c.doJSON(http.MethodPatch, "/v1/collections/"+url.PathEscape(collectionID),
		map[string]any{"seriesIds": seriesIDs}, nil)
}

// TestKomga 连通性测试：返回版本号。
func (c *Client) TestKomga() (string, error) {
	var info struct {
		Version string `json:"version"`
	}
	if err := c.doJSON(http.MethodGet, "/v1/system/info", nil, &info); err != nil {
		return "", err
	}
	return info.Version, nil
}

// cleanMetaValue 字段值是否应写入 PATCH 体。
// 规则（对齐 C# CleanMeta）：nil 不发；空串不发；整数 0 不发但 ageRating 例外；
// 空数组允许发送（用于清空 tags）；false 布尔锁字段照发。
func cleanMetaValue(name string, v reflect.Value) (any, bool) {
	switch v.Kind() {
	case reflect.Ptr:
		if v.IsNil() {
			return nil, false
		}
		return cleanMetaValue(name, v.Elem())
	case reflect.String:
		if v.String() == "" {
			return nil, false
		}
		return v.String(), true
	case reflect.Int, reflect.Int8, reflect.Int16, reflect.Int32, reflect.Int64:
		if v.Int() == 0 && name != "ageRating" {
			return nil, false
		}
		return v.Int(), true
	case reflect.Float32, reflect.Float64:
		if v.Float() == 0 {
			return nil, false
		}
		return v.Float(), true
	case reflect.Slice, reflect.Array:
		if v.Kind() == reflect.Slice && v.IsNil() {
			return nil, false // null 不发；空数组允许发送（清空字段）
		}
		if v.Len() == 0 {
			return []any{}, true
		}
		return v.Interface(), true
	case reflect.Bool:
		return v.Bool(), true
	default:
		if v.IsZero() {
			return nil, false
		}
		return v.Interface(), true
	}
}

// CleanMeta 把 DTO 转成可 PATCH 的 map：只含非空标量与数组字段，键名取 json tag。
func CleanMeta(meta any) map[string]any {
	out := map[string]any{}
	if meta == nil {
		return out
	}
	rv := reflect.ValueOf(meta)
	for rv.Kind() == reflect.Ptr {
		if rv.IsNil() {
			return out
		}
		rv = rv.Elem()
	}
	if rv.Kind() != reflect.Struct {
		return out
	}
	t := rv.Type()
	for i := 0; i < t.NumField(); i++ {
		f := t.Field(i)
		tag := f.Tag.Get("json")
		if tag == "" || tag == "-" {
			continue
		}
		name := strings.Split(tag, ",")[0]
		val, ok := cleanMetaValue(name, rv.Field(i))
		if ok {
			out[name] = val
		}
	}
	return out
}

// HasChanges 该 DTO 经 CleanMeta 后是否还有可写字段。
func HasChanges(meta any) bool { return len(CleanMeta(meta)) > 0 }
