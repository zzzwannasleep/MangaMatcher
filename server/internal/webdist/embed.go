// Package webdist：前端静态资源嵌入。Docker 构建时 dist/ 由真实前端产物覆盖；
// 本地开发/测试用占位 index.html。
package webdist

import (
	"embed"
	"io/fs"
)

//go:embed all:dist
var distFS embed.FS

// FS 返回嵌入的 dist 文件系统。
func FS() fs.FS {
	sub, err := fs.Sub(distFS, "dist")
	if err != nil {
		return distFS
	}
	return sub
}
