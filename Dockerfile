# syntax=docker/dockerfile:1
# KomgaBangumi Docker 版 —— 多阶段构建：前端(Vite) → Go 单二进制(go:embed) → distroless 运行层

ARG GO_VERSION=1.27
ARG NODE_VERSION=22

# ---------- 1. 前端构建 ----------
FROM node:${NODE_VERSION}-alpine AS webbuild
WORKDIR /src/web
COPY web/package.json web/package-lock.json* ./
RUN npm ci --no-audit --no-fund || npm install --no-audit --no-fund
COPY web/ .
RUN npm run build

# ---------- 2. Go 后端构建（嵌入前端产物） ----------
FROM golang:${GO_VERSION}-alpine AS gobuild
ARG VERSION=dev
WORKDIR /src/server
COPY server/ .
# 用真实前端产物覆盖占位 dist
RUN rm -rf internal/webdist/dist
COPY --from=webbuild /src/web/dist internal/webdist/dist
ENV GOTOOLCHAIN=local CGO_ENABLED=0 GOOS=linux GOFLAGS=-trimpath
RUN go build -mod=mod -ldflags="-s -w -X main.version=${VERSION}" -o /out/komgabangumi ./cmd/server

# ---------- 3. 运行层 ----------
FROM gcr.io/distroless/static-debian12:nonroot
LABEL org.opencontainers.image.title="KomgaBangumi" \
      org.opencontainers.image.description="Komga 漫画元数据刮削（Bangumi）· Docker 版 · Go 后端" \
      org.opencontainers.image.source="https://github.com/zzzwannasleep/MangaMatcher"
COPY --from=gobuild /out/komgabangumi /komgabangumi
ENV DATA_DIR=/data \
    PORT=8080 \
    TZ=Asia/Shanghai
EXPOSE 8080
VOLUME ["/data"]
USER nonroot:nonroot
ENTRYPOINT ["/komgabangumi"]