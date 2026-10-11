package sources

import (
	"fmt"
	"io"
	"net"
	"strings"
)

// ioReadAllTest 测试辅助读全。
func ioReadAllTest(r io.Reader) ([]byte, error) { return io.ReadAll(r) }

// netListen 随机端口 TCP 监听（SOCKS5 测试服务器用）。
func netListen() (net.Listener, error) {
	return net.Listen("tcp", "127.0.0.1:0")
}

func portOf(ln net.Listener) int { return ln.Addr().(*net.TCPAddr).Port }

// atoiT 测试辅助字符串转数字。
func atoiT(s string) int {
	n := 0
	for _, c := range s {
		n = n*10 + int(c-'0')
	}
	return n
}

// splitHostPort 从 http://host:port[/...] 拆主机与端口。
func splitHostPort(rawURL string) (string, string) {
	u := rawURL[len("http://"):]
	i := strings.IndexByte(u, ':')
	if i < 0 {
		return u, "80"
	}
	host := u[:i]
	port := u[i+1:]
	if j := strings.IndexByte(port, '/'); j >= 0 {
		port = port[:j]
	}
	return host, port
}

// serveSocks5 最小 SOCKS5 无鉴权 CONNECT 服务器（测试隧道握手）。
func serveSocks5(conn net.Conn) {
	defer conn.Close()
	hello := make([]byte, 2)
	if _, err := io.ReadFull(conn, hello); err != nil {
		return
	}
	if hello[0] != 5 {
		return
	}
	n := int(hello[1])
	buf := make([]byte, n)
	if _, err := io.ReadFull(conn, buf); err != nil {
		return
	}
	// 选 NO_AUTH
	conn.Write([]byte{5, 0})

	head := make([]byte, 4)
	if _, err := io.ReadFull(conn, head); err != nil {
		return
	}
	if head[1] != 1 { // 仅 CONNECT
		return
	}
	var host string
	switch head[3] {
	case 1:
		ip := make([]byte, 4)
		if _, err := io.ReadFull(conn, ip); err != nil {
			return
		}
		host = net.IP(ip).String()
	case 3:
		l := make([]byte, 1)
		if _, err := io.ReadFull(conn, l); err != nil {
			return
		}
		name := make([]byte, int(l[0]))
		if _, err := io.ReadFull(conn, name); err != nil {
			return
		}
		host = string(name)
	default:
		return
	}
	portB := make([]byte, 2)
	if _, err := io.ReadFull(conn, portB); err != nil {
		return
	}
	port := int(portB[0])<<8 | int(portB[1])

	upstream, err := net.Dial("tcp", net.JoinHostPort(host, fmt.Sprintf("%d", port)))
	if err != nil {
		conn.Write([]byte{5, 1, 0, 1, 0, 0, 0, 0, 0, 0})
		return
	}
	defer upstream.Close()
	conn.Write([]byte{5, 0, 0, 1, 0, 0, 0, 0, 0, 0})

	// 双向转发
	done := make(chan struct{}, 2)
	go func() { io.Copy(upstream, conn); done <- struct{}{} }()
	go func() { io.Copy(conn, upstream); done <- struct{}{} }()
	<-done
	<-done
}
