// Package sources：v2 五源客户端 + 代理（移植自 KomgaScraper.Core/Sources）。
// proxy.go：代理配置 → http.Transport（http/https 走 HTTP CONNECT，socks4/4a/5 纯 Go 握手）。
package sources

import (
	"context"
	"errors"
	"fmt"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// ProxyConfig 代理配置（SPEC：protocol none|http|https|socks4|socks4a|socks5 + host/port/账号密码）。
type ProxyConfig struct {
	Protocol string `json:"protocol"`
	Host     string `json:"host"`
	Port     int    `json:"port"`
	Username string `json:"username"`
	Password string `json:"password"`
}

// IsSet 代理是否已配置。
func (p ProxyConfig) IsSet() bool {
	return p.Protocol != "" && p.Protocol != "none" && strings.TrimSpace(p.Host) != "" && p.Port > 0
}

// Validate 校验代理配置；返回错误文案，"" = 合法（none/空永远合法）。
func (p ProxyConfig) Validate() string {
	scheme := NormalizeScheme(p.Protocol)
	if scheme == "" {
		return ""
	}
	h := strings.TrimSpace(p.Host)
	if h == "" {
		return "代理地址不能为空；不需要代理请把协议选成「不使用代理」。"
	}
	if p.Port < 1 || p.Port > 65535 {
		return "代理端口必须在 1–65535 之间。"
	}
	if strings.ContainsAny(h, " /\\") {
		return fmt.Sprintf("代理地址格式不对：%q。只填主机名或 IP，不要带 http://、端口号或路径。", h)
	}
	return ""
}

// NormalizeScheme 规范协议名；非法协议返回空串。
func NormalizeScheme(s string) string {
	switch strings.ToLower(strings.TrimSpace(s)) {
	case "http", "https", "socks4", "socks4a", "socks5":
		return strings.ToLower(strings.TrimSpace(s))
	default:
		return ""
	}
}

// Display 日志用展示（不显示密码）。
func (p ProxyConfig) Display() string {
	if !p.IsSet() {
		return "不使用"
	}
	s := NormalizeScheme(p.Protocol) + "://" + strings.TrimSpace(p.Host) + ":" + itoa(p.Port)
	if p.Username != "" {
		s += "（账号 " + p.Username + "）"
	}
	return s
}

func itoa(n int) string {
	if n == 0 {
		return "0"
	}
	neg := n < 0
	if neg {
		n = -n
	}
	var b []byte
	for n > 0 {
		b = append([]byte{byte('0' + n%10)}, b...)
		n /= 10
	}
	if neg {
		return "-" + string(b)
	}
	return string(b)
}

// NewHTTPClient 构建 HTTP 客户端：可选代理 + 超时 + UA（+可选 Referer）。
// Komga 永远直连——本工厂只服务 bangumi/封面下载/BW 下载/五源请求。
func NewHTTPClient(p ProxyConfig, timeout time.Duration, userAgent, referrer string) (*http.Client, error) {
	if p.IsSet() {
		if msg := p.Validate(); msg != "" {
			return nil, errors.New("代理配置无效：" + msg)
		}
	}
	tp := &http.Transport{
		MaxIdleConns:        20,
		IdleConnTimeout:     5 * time.Minute,
		TLSHandshakeTimeout: 20 * time.Second,
	}
	if p.IsSet() {
		scheme := NormalizeScheme(p.Protocol)
		host := strings.TrimSpace(p.Host)
		if scheme == "http" || scheme == "https" {
			u := &url.URL{Scheme: scheme, Host: net.JoinHostPort(host, itoa(p.Port))}
			if p.Username != "" {
				u.User = url.UserPassword(p.Username, p.Password)
			}
			tp.Proxy = http.ProxyURL(u)
		} else {
			tp.Proxy = nil
			tp.DialContext = socksDialerFunc(p, host, p.Port)
		}
	}
	client := &http.Client{Timeout: timeout, Transport: tp}
	return client, nil
}

func socksDialerFunc(p ProxyConfig, host string, port int) func(ctx context.Context, network, addr string) (net.Conn, error) {
	return func(ctx context.Context, network, addr string) (net.Conn, error) {
		h, sport, err := net.SplitHostPort(addr)
		if err != nil {
			return nil, fmt.Errorf("SOCKS 目标地址无效 %q: %w", addr, err)
		}
		return socksDial(ctx, network, host, port, h, sport, p)
	}
}

func connectTCP(ctx context.Context, host string, port int) (net.Conn, error) {
	d := net.Dialer{Timeout: 20 * time.Second}
	return d.DialContext(ctx, "tcp", net.JoinHostPort(host, itoa(port)))
}

// socksDial 纯 Go SOCKS4/4a/5 握手（RFC 1928 + RFC 1929；socks4a 按 AOL 约定）。
func socksDial(ctx context.Context, network, proxyHost string, proxyPort int, dstHost, dstPort string, p ProxyConfig) (net.Conn, error) {
	scheme := NormalizeScheme(p.Protocol)
	conn, err := connectTCP(ctx, proxyHost, proxyPort)
	if err != nil {
		return nil, fmt.Errorf("SOCKS 连接代理失败: %w", err)
	}
	deadline := time.Now().Add(30 * time.Second)
	_ = conn.SetDeadline(deadline)

	switch scheme {
	case "socks5":
		if err := socks5Handshake(conn, dstHost, dstPort, p); err != nil {
			conn.Close()
			return nil, err
		}
	case "socks4":
		if err := socks4Handshake(conn, dstHost, dstPort, p.Username, false); err != nil {
			conn.Close()
			return nil, err
		}
	case "socks4a":
		if err := socks4Handshake(conn, dstHost, dstPort, p.Username, true); err != nil {
			conn.Close()
			return nil, err
		}
	default:
		conn.Close()
		return nil, fmt.Errorf("不支持的代理协议: %s", p.Protocol)
	}
	// 代理隧道建立成功：恢复常规超时（取消交由 ctx 侧管理）
	_ = conn.SetDeadline(time.Time{})
	return conn, nil
}

func socks5Handshake(conn net.Conn, host, portStr string, p ProxyConfig) error {
	port := 0
	fmt.Sscanf(portStr, "%d", &port)
	if port <= 0 || port > 65535 {
		return fmt.Errorf("SOCKS5 目标端口无效: %s", portStr)
	}
	// 问候：支持 NO_AUTH 与 USER_PASS 两种方法
	methods := []byte{5, 2, 0, 2}
	if p.Username == "" {
		methods = []byte{5, 1, 0}
	}
	if _, err := conn.Write(methods); err != nil {
		return err
	}
	resp := make([]byte, 2)
	if _, err := readFull(conn, resp); err != nil {
		return err
	}
	if resp[0] != 5 {
		return fmt.Errorf("SOCKS5 代理协议版本异常: %d", resp[0])
	}
	switch resp[1] {
	case 0:
		// 无需鉴权
	case 2:
		if p.Username == "" {
			return errors.New("SOCKS5 代理要求账号密码，但设置里没有配置")
		}
		auth := append([]byte{1, byte(len(p.Username))}, []byte(p.Username)...)
		auth = append(auth, byte(len(p.Password)))
		auth = append(auth, []byte(p.Password)...)
		if _, err := conn.Write(auth); err != nil {
			return err
		}
		ar := make([]byte, 2)
		if _, err := readFull(conn, ar); err != nil {
			return err
		}
		if ar[0] != 1 || ar[1] != 0 {
			return errors.New("SOCKS5 账号密码鉴权失败（RFC 1929）")
		}
	default:
		return fmt.Errorf("SOCKS5 代理拒绝的鉴权方法: %d", resp[1])
	}
	// 连接请求：域名一律交给代理解析（socks5h 语义）
	req := []byte{5, 1, 0}
	if ip := net.ParseIP(host); ip != nil {
		if v4 := ip.To4(); v4 != nil {
			req = append(req, 1)
			req = append(req, v4...)
		} else {
			req = append(req, 4)
			req = append(req, ip.To16()...)
		}
	} else {
		req = append(req, 3, byte(len(host)))
		req = append(req, []byte(host)...)
	}
	req = append(req, byte(port>>8), byte(port&0xff))
	if _, err := conn.Write(req); err != nil {
		return err
	}
	head := make([]byte, 4)
	if _, err := readFull(conn, head); err != nil {
		return err
	}
	if head[1] != 0 {
		return fmt.Errorf("SOCKS5 代理连接失败（回复码 %d）", head[1])
	}
	rest := 0
	switch head[3] {
	case 1:
		rest = 4 + 2
	case 4:
		rest = 16 + 2
	case 3:
		// 域名长度字节还没读：先读 1 字节长度再读 len+2
		l := make([]byte, 1)
		if _, err := readFull(conn, l); err != nil {
			return err
		}
		rest = int(l[0]) + 2
	default:
		return fmt.Errorf("SOCKS5 回复地址类型异常: %d", head[3])
	}
	if rest > 0 {
		discard := make([]byte, rest)
		if _, err := readFull(conn, discard); err != nil {
			return err
		}
	}
	return nil
}

func socks4Handshake(conn net.Conn, host, portStr, user string, sendHost bool) error {
	port := 0
	fmt.Sscanf(portStr, "%d", &port)
	if port <= 0 || port > 65535 {
		return fmt.Errorf("SOCKS4 目标端口无效: %s", portStr)
	}
	req := []byte{4, 1, byte(port >> 8), byte(port & 0xff)}
	if sendHost {
		// socks4a：IP 占位 0.0.0.1，USERID 00 之后跟目标域名
		req = append(req, 0, 0, 0, 1)
	} else {
		ip := net.ParseIP(host)
		v4 := ip.To4()
		if v4 == nil {
			addrs, err := net.LookupIP(host)
			if err != nil || len(addrs) == 0 {
				return fmt.Errorf("SOCKS4 无法解析目标主机 %s: %v", host, err)
			}
			for _, a := range addrs {
				if v4 = a.To4(); v4 != nil {
					break
				}
			}
			if v4 == nil {
				return fmt.Errorf("SOCKS4 目标主机 %s 没有 IPv4 地址", host)
			}
		}
		req = append(req, v4...)
	}
	req = append(req, []byte(user)...)
	req = append(req, 0)
	if sendHost {
		req = append(req, []byte(host)...)
		req = append(req, 0)
	}
	if _, err := conn.Write(req); err != nil {
		return err
	}
	resp := make([]byte, 8)
	if _, err := readFull(conn, resp); err != nil {
		return err
	}
	if resp[1] != 0x5a {
		return fmt.Errorf("SOCKS4 代理连接失败（回复码 %d）", resp[1])
	}
	return nil
}

func readFull(conn net.Conn, buf []byte) (int, error) {
	total := 0
	for total < len(buf) {
		n, err := conn.Read(buf[total:])
		total += n
		if err != nil {
			return total, err
		}
	}
	return total, nil
}
