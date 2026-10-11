package sources

import (
	"golang.org/x/text/encoding/simplifiedchinese"
	"golang.org/x/text/encoding/traditionalchinese"
)

// decodeCharset 老站 Big5/GBK 兜底解码（mox.go decodeHTML 使用）。
func decodeCharset(b []byte, name string) (string, error) {
	switch name {
	case "GBK":
		out, err := simplifiedchinese.GBK.NewDecoder().Bytes(b)
		return string(out), err
	case "big5":
		out, err := traditionalchinese.Big5.NewDecoder().Bytes(b)
		return string(out), err
	}
	return "", nil
}
