/** changed 差异字段的中文标签映射（未命中时原样展示 key） */
const FIELD_LABELS = {
  title: '标题',
  summary: '简介',
  authors: '作者',
  tags: '标签',
  status: '连载状态',
  totalBookCount: '总卷数',
  number: '卷号',
  releaseDate: '发售日期',
  isbn: 'ISBN',
  name: '名称',
  titleSort: '排序标题',
}

export function fieldLabel(key) {
  return FIELD_LABELS[key] || key
}

/** 状态徽章文案 */
export const ITEM_STATUS_TEXT = {
  ok: '成功',
  failed: '失败',
  skipped: '跳过',
  manual: '待手动匹配',
}

export function formatDateTime(value) {
  if (!value) return '—'
  const d = new Date(value)
  if (Number.isNaN(d.getTime())) return String(value)
  const pad = (n) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
}

/** changed: { 字段: [旧, 新] } → [{ key, label, oldText, newText }] */
export function diffEntries(changed) {
  if (!changed || typeof changed !== 'object') return []
  return Object.entries(changed).map(([key, pair]) => {
    const [oldValue, newValue] = Array.isArray(pair) ? pair : ['', pair]
    return {
      key,
      label: fieldLabel(key),
      oldText: stringify(oldValue),
      newText: stringify(newValue),
    }
  })
}

function stringify(value) {
  if (value === null || value === undefined) return '（空）'
  if (Array.isArray(value)) return value.length ? value.map(stringifyOne).join('、') : '（空）'
  if (typeof value === 'object') return stringifyOne(value)
  return String(value)
}

/** 单个差异值 → 可读中文（对象取语义字段，避免 [object Object]） */
function stringifyOne(value) {
  if (value === null || value === undefined) return '（空）'
  if (typeof value !== 'object') return String(value)
  if (Array.isArray(value)) return value.map(stringifyOne).join('、')
  // Komga 元数据对象：作者 {name, role} / 别名 {label, title} / 链接 {label, url}
  if (typeof value.name === 'string') {
    return typeof value.role === 'string' && value.role ? `${value.name}（${value.role}）` : value.name
  }
  if (typeof value.title === 'string' && value.title) return value.title
  if (typeof value.url === 'string' && value.url) return value.url
  try {
    return JSON.stringify(value)
  } catch {
    return String(value)
  }
}
