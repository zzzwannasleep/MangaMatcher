// Package auth：JWT 签发/校验 + bcrypt 密码哈希 + 请求中间件。
package auth

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"time"

	"github.com/golang-jwt/jwt/v5"
	"golang.org/x/crypto/bcrypt"

	"komgabangumi/internal/store"
)

// Claims JWT 载荷。
type Claims struct {
	UserID   string `json:"uid"`
	Username string `json:"username"`
	Role     string `json:"role"`
	jwt.RegisteredClaims
}

// Manager 认证管理器。
type Manager struct {
	secret []byte
}

// NewManager 创建（secret 来自 DATA_DIR/jwt.secret）。
func NewManager(secret []byte) *Manager { return &Manager{secret: secret} }

// HashPassword bcrypt 哈希。
func HashPassword(pw string) (string, error) {
	b, err := bcrypt.GenerateFromPassword([]byte(pw), bcrypt.DefaultCost)
	return string(b), err
}

// CheckPassword 校验密码。
func CheckPassword(hash, pw string) bool {
	return bcrypt.CompareHashAndPassword([]byte(hash), []byte(pw)) == nil
}

// Issue 签发 JWT（HS256，7 天有效）。
func (m *Manager) Issue(u *store.User) (string, error) {
	claims := Claims{
		UserID:   u.ID,
		Username: u.Username,
		Role:     u.Role,
		RegisteredClaims: jwt.RegisteredClaims{
			IssuedAt:  jwt.NewNumericDate(time.Now()),
			ExpiresAt: jwt.NewNumericDate(time.Now().Add(7 * 24 * time.Hour)),
			Issuer:    "komgabangumi",
		},
	}
	return jwt.NewWithClaims(jwt.SigningMethodHS256, claims).SignedString(m.secret)
}

// Parse 校验并解析 JWT。
func (m *Manager) Parse(tokenStr string) (*Claims, error) {
	claims := &Claims{}
	token, err := jwt.ParseWithClaims(tokenStr, claims, func(t *jwt.Token) (any, error) {
		if _, ok := t.Method.(*jwt.SigningMethodHMAC); !ok {
			return nil, fmt.Errorf("非预期的签名算法: %v", t.Header["alg"])
		}
		return m.secret, nil
	})
	if err != nil || !token.Valid {
		return nil, errors.New("token 无效")
	}
	return claims, nil
}

type ctxKey string

const claimsKey ctxKey = "claims"

// withClaims 把 claims 塞进请求 context。
func withClaims(r *http.Request, c *Claims) *http.Request {
	return r.WithContext(context.WithValue(r.Context(), claimsKey, c))
}

// FromContext 取请求中的 claims（未认证为 nil）。
func FromContext(ctx context.Context) *Claims {
	c, _ := ctx.Value(claimsKey).(*Claims)
	return c
}

// Middleware 认证中间件：校验 Bearer token；失败返回 401。
func (m *Manager) Middleware(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		h := r.Header.Get("Authorization")
		if !strings.HasPrefix(h, "Bearer ") {
			writeError(w, 401, "未登录")
			return
		}
		claims, err := m.Parse(strings.TrimPrefix(h, "Bearer "))
		if err != nil {
			writeError(w, 401, "登录已过期，请重新登录")
			return
		}
		next.ServeHTTP(w, withClaims(r, claims))
	})
}

// RequireAdmin admin 门禁。
func RequireAdmin(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		c := FromContext(r.Context())
		if c == nil || c.Role != "admin" {
			writeError(w, 403, "需要管理员权限")
			return
		}
		next.ServeHTTP(w, r)
	})
}

func writeError(w http.ResponseWriter, status int, msg string) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	fmt.Fprintf(w, `{"error":%q}`, msg)
}
