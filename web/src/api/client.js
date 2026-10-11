const TOKEN_KEY = 'komgabangumi.token'

export function getToken() {
  try {
    return localStorage.getItem(TOKEN_KEY) || ''
  } catch {
    return ''
  }
}

export function setToken(token) {
  try {
    if (token) localStorage.setItem(TOKEN_KEY, token)
    else localStorage.removeItem(TOKEN_KEY)
  } catch {
    /* storage unavailable */
  }
}

export class ApiError extends Error {
  constructor(message, status) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

let onUnauthorized = null

/** 由 router 注入：收到 401 时清 token 并跳登录 */
export function setUnauthorizedHandler(fn) {
  onUnauthorized = fn
}

function handleUnauthorized() {
  setToken('')
  if (onUnauthorized) onUnauthorized()
}

async function request(path, { method = 'GET', body, signal, raw = false } = {}) {
  const headers = {}
  if (body !== undefined && !raw) headers['Content-Type'] = 'application/json'
  const token = getToken()
  if (token) headers.Authorization = `Bearer ${token}`

  let res
  try {
    res = await fetch(path, {
      method,
      headers,
      signal,
      body: body === undefined ? undefined : raw ? body : JSON.stringify(body),
    })
  } catch (err) {
    if (err && err.name === 'AbortError') throw err
    throw new ApiError('网络错误：无法连接到服务器', 0)
  }

  if (res.status === 401) {
    handleUnauthorized()
    throw new ApiError('登录已失效，请重新登录', 401)
  }

  if (!res.ok) {
    let message = `请求失败（HTTP ${res.status}）`
    try {
      const data = await res.json()
      if (data && typeof data.error === 'string') message = data.error
    } catch {
      /* 非 JSON 错误体 */
    }
    throw new ApiError(message, res.status)
  }

  if (res.status === 204) return null
  const contentType = res.headers.get('content-type') || ''
  if (contentType.includes('application/json')) return res.json()
  if (raw) return res
  return res.text()
}

/** JSON 请求封装 */
export function api(path, options = {}) {
  return request(path, options)
}

/** 二进制请求（如缩略图），返回 Blob */
export async function apiBlob(path, signal) {
  const res = await request(path, { signal, raw: true })
  return res.blob()
}

/** multipart 请求（如封面上传 / BW 导入），body 为 FormData */
export function apiForm(path, formData, options = {}) {
  return request(path, { ...options, body: formData, raw: true })
}

/** 文件下载：返回 { blob, filename }（filename 取自 Content-Disposition，可能为空） */
export async function apiDownload(path) {
  const res = await request(path, { raw: true })
  const blob = await res.blob()
  const cd = res.headers.get('content-disposition') || ''
  const match = cd.match(/filename\*?=(?:UTF-8''|")?([^";]+)"?/i)
  let filename = ''
  if (match) {
    try {
      filename = decodeURIComponent(match[1])
    } catch {
      filename = match[1]
    }
  }
  return { blob, filename }
}
