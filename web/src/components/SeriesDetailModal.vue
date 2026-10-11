<script setup>
import { ref, reactive, computed, onMounted, onBeforeUnmount, nextTick } from 'vue'
import { useRouter } from 'vue-router'
import { api, apiBlob, apiForm } from '../api/client'
import { notify } from '../stores/toast'

const props = defineProps({
  seriesId: { type: String, required: true },
  startEdit: { type: Boolean, default: false },
})

const emit = defineEmits(['close', 'correct', 'changed'])

const router = useRouter()

const loading = ref(true)
const loadError = ref('')
const saving = ref(false)
const series = ref(null)
const books = ref([])
const state = ref(null)
const coverUrl = ref('')

/* 编辑模式（就地展开，不跳页） */
const editing = ref(props.startEdit)

async function load(silent = false) {
  if (!silent) {
    loading.value = true
    loadError.value = ''
  }
  try {
    const data = await api(`/api/series/${props.seriesId}`)
    releaseBookThumbs()
    series.value = data
    books.value = (data.books || []).map((b) => {
      const bm = b.metadata || {}
      const orig = {
        title: bm.title || '',
        number: bm.number ?? '',
        releaseDate: (bm.releaseDate || '').slice(0, 10),
        isbn: bm.isbn || '',
        summary: bm.summary || '',
      }
      return {
        id: b.id,
        name: b.name || orig.title || b.id,
        orig,
        locks: lockFields(bm),
        form: { ...orig },
        coverBusy: false,
        open: false,
      }
    })
    state.value = data.state || null
    hydrateSeriesForm(data)
  } catch (err) {
    loadError.value = err.message
  } finally {
    loading.value = false
  }
}

/* ---------- 系列表单（编辑模式） ---------- */
const seriesForm = reactive({
  title: '',
  titleSort: '',
  status: '',
  summary: '',
  tagsText: '',
  altTitlesText: '',
})
const seriesOrig = reactive({
  title: '',
  titleSort: '',
  status: '',
  summary: '',
  tags: [],
  altTitles: [],
})
const seriesMeta = ref(null)

const STATUS_OPTIONS = ['ENDED', 'ONGOING', 'ABANDONED', 'HIATUS']

function hydrateSeriesForm(data) {
  const meta = data?.metadata || {}
  seriesMeta.value = meta
  const tags = Array.isArray(meta.tags) ? meta.tags : []
  const alts = Array.isArray(meta.alternateTitles)
    ? meta.alternateTitles.map((a) => (typeof a === 'string' ? a : a?.label || '')).filter(Boolean)
    : Array.isArray(meta.altTitles)
      ? meta.altTitles
      : []
  seriesForm.title = meta.title || ''
  seriesForm.titleSort = meta.titleSort || ''
  seriesForm.status = meta.status || ''
  seriesForm.summary = meta.summary || ''
  seriesForm.tagsText = tags.join('\n')
  seriesForm.altTitlesText = alts.join('\n')
  seriesOrig.title = meta.title || ''
  seriesOrig.titleSort = meta.titleSort || ''
  seriesOrig.status = meta.status || ''
  seriesOrig.summary = meta.summary || ''
  seriesOrig.tags = [...tags]
  seriesOrig.altTitles = [...alts]
}

function splitLines(text) {
  return String(text || '')
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean)
}

function changedEntries(form, orig, keys, transformers = {}) {
  const patch = {}
  for (const key of keys) {
    const current = transformers[key] ? transformers[key](form[key]) : form[key]
    const base = orig[key]
    const same = Array.isArray(current) || Array.isArray(base)
      ? JSON.stringify(current ?? []) === JSON.stringify(base ?? [])
      : String(current ?? '') === String(base ?? '')
    if (!same) patch[key] = current
  }
  return patch
}

function lockFields(meta) {
  const locks = {}
  if (meta) {
    for (const key of Object.keys(meta)) {
      if (key.endsWith('Lock') && typeof meta[key] === 'boolean') locks[key] = meta[key]
    }
  }
  return locks
}

const seriesPatch = computed(() => {
  const patch = changedEntries(seriesForm, seriesOrig, ['title', 'titleSort', 'status', 'summary'])
  const tags = splitLines(seriesForm.tagsText)
  if (JSON.stringify(tags) !== JSON.stringify(seriesOrig.tags)) patch.tags = tags
  const alts = splitLines(seriesForm.altTitlesText)
  if (JSON.stringify(alts) !== JSON.stringify(seriesOrig.altTitles)) patch.altTitles = alts
  return patch
})

const bookPatches = computed(() =>
  books.value
    .map((book) => ({
      book,
      patch: changedEntries(book.form, book.orig, ['title', 'number', 'releaseDate', 'isbn', 'summary'], {
        number: (v) => (v === '' || v === null ? '' : Number(v)),
      }),
    }))
    .filter((entry) => Object.keys(entry.patch).length > 0)
)

const dirty = computed(() => Object.keys(seriesPatch.value).length > 0 || bookPatches.value.length > 0)

async function save() {
  if (!dirty.value) {
    notify.info('没有改动需要保存')
    return
  }
  saving.value = true
  try {
    const sPatch = seriesPatch.value
    if (Object.keys(sPatch).length) {
      await api(`/api/series/${props.seriesId}/metadata-raw`, {
        method: 'PATCH',
        body: { ...sPatch, ...lockFields(seriesMeta.value) },
      })
    }
    for (const { book, patch } of bookPatches.value) {
      await api(`/api/books/${book.id}/metadata-raw`, {
        method: 'PATCH',
        body: { ...patch, ...book.locks },
      })
    }
    notify.success('元数据已保存')
    editing.value = false
    await load(true)
    await loadCover()
    emit('changed')
  } catch (err) {
    notify.error(err.message)
  } finally {
    saving.value = false
  }
}

function cancelEdit() {
  editing.value = false
  hydrateSeriesForm(series.value)
  books.value.forEach((b) => {
    b.form = { ...b.orig }
  })
}

/* ---------- 封面 ---------- */
async function loadCover() {
  try {
    const blob = await apiBlob(`/api/series/${props.seriesId}/thumbnail`)
    if (coverUrl.value) URL.revokeObjectURL(coverUrl.value)
    coverUrl.value = URL.createObjectURL(blob)
  } catch {
    coverUrl.value = ''
  }
}

const seriesCoverBusy = ref(false)

async function onSeriesCoverChange(event) {
  const file = event.target.files?.[0]
  event.target.value = ''
  if (!file) return
  seriesCoverBusy.value = true
  try {
    const fd = new FormData()
    fd.append('image', file)
    await apiForm(`/api/series/${props.seriesId}/cover`, fd)
    notify.success('系列封面已更新')
    await loadCover()
    emit('changed')
  } catch (err) {
    notify.error(err.message)
  } finally {
    seriesCoverBusy.value = false
  }
}

async function onBookCoverChange(book, event) {
  const file = event.target.files?.[0]
  event.target.value = ''
  if (!file) return
  book.coverBusy = true
  try {
    const fd = new FormData()
    fd.append('image', file)
    await apiForm(`/api/books/${book.id}/cover`, fd)
    notify.success(`已更新封面（${book.orig.title || book.name}）`)
  } catch (err) {
    notify.error(err.message)
  } finally {
    book.coverBusy = false
  }
}

/* ---------- 重刮每卷（Docker 版无 Dry-run） ---------- */
const rescrapeBusy = ref(false)

async function rescrapeVolumes() {
  rescrapeBusy.value = true
  try {
    const res = await api(`/api/series/${props.seriesId}/rescrapevolumes`, {
      method: 'POST',
      body: { dryRun: false },
    })
    const changed = Array.isArray(res?.changed) ? res.changed : []
    notify.success(changed.length ? `重刮完成：${changed.length} 项更新` : '重刮完成：无更新')
    if (changed.length) {
      await load(true)
      emit('changed')
    }
  } catch (err) {
    notify.error(err.message)
  } finally {
    rescrapeBusy.value = false
  }
}

/* ---------- 只读视图数据 ---------- */
const meta = computed(() => series.value?.metadata || {})

const STATUS_LABELS = { ENDED: '已完结', ONGOING: '连载中', ABANDONED: '中止', HIATUS: '休刊' }
const statusLabel = computed(() => {
  const s = meta.value.status
  return s ? STATUS_LABELS[s] || s : ''
})

const LABELS = {
  title: '标题',
  titleSort: '排序标题',
  status: '连载状态',
  summary: '简介',
  language: '语言',
  readingDirection: '阅读方向',
  ageRating: '年龄分级',
  genres: '类型',
  tags: '标签',
  alternateTitles: '别名',
  altTitles: '别名',
  totalBookCount: '总卷数',
  publisher: '出版社',
  releaseDate: '发售日期',
  isbn: 'ISBN',
  number: '卷号',
  authors: '作者',
  links: '链接',
  metadataAgeRating: '年龄分级',
}

const SKIP_KEYS = ['summary', 'title', 'titleSort', 'status']

const LANGUAGE_LABELS = { ja: '日语', zh: '中文', en: '英语' }

function valueLabel(key, value) {
  if (key === 'ageRating') {
    const n = Number(value)
    return Number.isFinite(n) && n > 0 ? `${n}+` : '全年龄'
  }
  if (key === 'readingDirection') {
    return { LEFT_TO_RIGHT: '从左到右', RIGHT_TO_LEFT: '从右到左', VERTICAL: '垂直', WEBTOON: '条漫' }[value] || value
  }
  if (key === 'language') return LANGUAGE_LABELS[value] || value
  if (key === 'status') return STATUS_LABELS[value] || value
  return null
}

function labelOf(key) {
  if (LABELS[key]) return LABELS[key]
  if (key.endsWith('Lock')) return `${LABELS[key.slice(0, -4)] || key.slice(0, -4)}·锁`
  return key
}

function formatValue(value) {
  if (value === null || value === undefined || value === '') return null
  if (Array.isArray(value)) {
    const parts = value.map((v) => formatValue(v)).filter(Boolean)
    return parts.length ? parts.join('、') : null
  }
  if (typeof value === 'object') {
    if (value.label && value.url) return null
    return Object.values(value).filter((v) => v !== null && v !== '').join(' · ') || null
  }
  if (typeof value === 'string') return value.trim() || null
  return String(value)
}

const metaRows = computed(() => {
  const rows = []
  for (const [key, value] of Object.entries(meta.value)) {
    if (key === 'links') continue
    if (key.endsWith('Lock')) continue
    if (SKIP_KEYS.includes(key)) continue
    const special = valueLabel(key, value)
    const text = special !== null ? special : formatValue(value)
    if (text === null) continue
    rows.push({ key, label: labelOf(key), text })
  }
  return rows
})

const metaLocks = computed(() => {
  const locks = []
  for (const [key, value] of Object.entries(meta.value)) {
    if (!key.endsWith('Lock') || value !== true) continue
    locks.push({ key, label: labelOf(key) })
  }
  return locks
})

const summary = computed(() => formatValue(meta.value.summary) || '')

const links = computed(() => {
  const raw = meta.value.links
  return Array.isArray(raw) ? raw.filter((l) => l?.url) : []
})

const rootRows = computed(() => {
  const rows = []
  const root = series.value || {}
  for (const [key, label] of [
    ['created', '入库时间'],
    ['lastModified', '最后修改'],
    ['fileSize', '文件大小'],
    ['booksCount', '书卷数'],
    ['name', '文件夹名'],
  ]) {
    const text = formatValue(root[key])
    if (text === null) continue
    rows.push({ key, label, text })
  }
  return rows
})

function stateIcon(key) {
  if (!state.value) return '…'
  return state.value[key] ? '✓' : '✗'
}

function fmtDate(value) {
  if (!value) return ''
  return String(value).slice(0, 10)
}

function goJob(jobId) {
  emit('close')
  router.push(`/jobs/${jobId}`)
}

/* ---------- 书卷封面：进入视口才取 blob ---------- */
const bookThumbs = reactive({})
const bookThumbPending = new Set()
let bookThumbObserver = null

function watchBookThumb(el, bookId) {
  if (!el || !bookThumbObserver || !bookId) return
  if (el.dataset.btObs === bookId) return
  el.dataset.btObs = bookId
  bookThumbObserver.observe(el)
}

async function loadBookThumb(bookId) {
  if (bookThumbPending.has(bookId) || bookThumbs[bookId]) return
  bookThumbPending.add(bookId)
  try {
    const blob = await apiBlob(`/api/books/${bookId}/thumbnail`)
    bookThumbs[bookId] = URL.createObjectURL(blob)
  } catch {
    bookThumbs[bookId] = ''
  } finally {
    bookThumbPending.delete(bookId)
  }
}

function releaseBookThumbs() {
  Object.keys(bookThumbs).forEach((id) => {
    if (bookThumbs[id]) URL.revokeObjectURL(bookThumbs[id])
    delete bookThumbs[id]
  })
  bookThumbPending.clear()
}

function bookSummaryText(book) {
  const s = book.orig.summary
  return typeof s === 'string' ? s.trim() : ''
}

function onKeydown(e) {
  if (e.key === 'Escape') {
    if (editing.value && dirty.value) {
      if (!confirm('有未保存的改动，确定关闭？')) return
    }
    emit('close')
  }
}

onMounted(async () => {
  window.addEventListener('keydown', onKeydown)
  bookThumbObserver = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue
        const id = entry.target.dataset.bt
        if (id) loadBookThumb(id)
        bookThumbObserver.unobserve(entry.target)
      }
    },
    { rootMargin: '200px 0px' }
  )
  await load()
  await loadCover()
})

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
  if (coverUrl.value) URL.revokeObjectURL(coverUrl.value)
  releaseBookThumbs()
  bookThumbObserver?.disconnect()
})
</script>

<template>
  <Teleport to="body">
    <div class="sdm-root" role="dialog" aria-modal="true" :aria-label="meta.title || '系列详情'">
      <div class="sdm-backdrop" @click="emit('close')"></div>

      <div class="sdm-modal">
        <div v-if="loading" class="sdm-loading">
          <span class="spinner" aria-hidden="true"></span>
          正在加载详情…
        </div>

        <div v-else-if="loadError" class="sdm-error">
          <p>{{ loadError }}</p>
          <button class="btn" type="button" @click="load()">重试</button>
        </div>

        <template v-else>
          <!-- 头部：封面 + 标题 + 徽章 + 动作 -->
          <header class="sdm-head">
            <span class="sdm-cover" :class="{ 'is-editable': editing }">
              <img v-if="coverUrl" :src="coverUrl" alt="" @error="coverUrl = ''" />
              <span v-else class="sdm-cover-fallback" aria-hidden="true">▤</span>
              <label v-if="editing" class="sdm-cover-upload" :class="{ 'is-busy': seriesCoverBusy }">
                <span v-if="seriesCoverBusy" class="spinner" aria-hidden="true"></span>
                <template v-else>更换封面</template>
                <input type="file" accept="image/*" class="visually-hidden" @change="onSeriesCoverChange" />
              </label>
            </span>

            <div class="sdm-head-main">
              <template v-if="!editing">
                <h2 class="sdm-title">{{ meta.title || series.name }}</h2>
                <div class="sdm-head-meta">
                  <span v-if="statusLabel" class="badge">{{ statusLabel }}</span>
                  <span v-if="meta.totalBookCount" class="badge num">{{ meta.totalBookCount }} 卷</span>
                  <span class="state-chip" :data-state="stateIcon('hasCover')" title="封面状态">封 {{ stateIcon('hasCover') }}</span>
                  <span class="state-chip" :data-state="stateIcon('hasSummary')" title="简介状态">简 {{ stateIcon('hasSummary') }}</span>
                </div>
                <div v-if="metaLocks.length" class="sdm-locks">
                  <span v-for="lock in metaLocks" :key="lock.key" class="lock-chip" title="已锁定：Komga 不会自动覆盖此字段">🔒 {{ lock.label }}</span>
                </div>
              </template>
              <template v-else>
                <h2 class="sdm-title">编辑元数据<span class="sdm-title-note">只提交改动字段</span></h2>
              </template>
            </div>

            <div class="sdm-head-actions">
              <template v-if="!editing">
                <button class="btn btn-primary btn-sm" type="button" @click="editing = true">
                  <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M17 3a2.85 2.83 0 1 1 4 4L7.5 20.5 2 22l1.5-5.5Z"/></svg>
                  编辑元数据
                </button>
                <button class="btn btn-sm" type="button" @click="emit('correct')">纠正匹配</button>
              </template>
              <template v-else>
                <button class="btn btn-sm" type="button" :disabled="saving" @click="cancelEdit">取消</button>
                <button class="btn btn-primary btn-sm" type="button" :data-loading="saving" :disabled="saving || !dirty" @click="save">
                  {{ saving ? '保存中…' : dirty ? '保存改动' : '无改动' }}
                </button>
              </template>
            </div>

            <button class="icon-btn sdm-close" type="button" aria-label="关闭" @click="emit('close')">
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>
            </button>
          </header>

          <div class="sdm-body">
            <!-- ======== 只读视图 ======== -->
            <template v-if="!editing">
              <section v-if="summary" class="sdm-section">
                <h3 class="sdm-section-title">简介</h3>
                <p class="sdm-summary">{{ summary }}</p>
              </section>

              <section v-if="metaRows.length" class="sdm-section">
                <h3 class="sdm-section-title">Komga 元数据</h3>
                <dl class="meta-list">
                  <div v-for="row in metaRows" :key="row.key" class="meta-row">
                    <dt class="meta-label">{{ row.label }}</dt>
                    <dd class="meta-value">{{ row.text }}</dd>
                  </div>
                </dl>
              </section>

              <section v-if="links.length" class="sdm-section">
                <h3 class="sdm-section-title">相关链接</h3>
                <ul class="sdm-links">
                  <li v-for="(link, i) in links" :key="i">
                    <a :href="link.url" target="_blank" rel="noopener">{{ link.label || link.url }}</a>
                  </li>
                </ul>
              </section>

              <section v-if="rootRows.length" class="sdm-section">
                <h3 class="sdm-section-title">库内信息</h3>
                <dl class="meta-list">
                  <div v-for="row in rootRows" :key="row.key" class="meta-row">
                    <dt class="meta-label">{{ row.label }}</dt>
                    <dd class="meta-value num">{{ row.text }}</dd>
                  </div>
                </dl>
              </section>

              <section v-if="books.length" class="sdm-section">
                <h3 class="sdm-section-title">书卷<span class="sdm-count num">{{ books.length }}</span></h3>
                <ul class="book-list">
                  <li v-for="(book, i) in books" :key="book.id" class="book-item">
                    <span class="book-thumb" :ref="(el) => watchBookThumb(el, book.id)" :data-bt="book.id">
                      <img v-if="bookThumbs[book.id]" :src="bookThumbs[book.id]" alt="" loading="lazy" @error="bookThumbs[book.id] = ''" />
                      <span v-else class="book-thumb-fb" aria-hidden="true">▤</span>
                    </span>
                    <div class="book-main">
                      <div class="book-line">
                        <span class="book-index num">{{ i + 1 }}</span>
                        <span class="book-name">{{ book.orig.title || book.name }}</span>
                        <span v-if="book.orig.number !== ''" class="badge num">卷 {{ book.orig.number }}</span>
                        <span v-if="book.orig.releaseDate" class="book-date num">{{ fmtDate(book.orig.releaseDate) }}</span>
                      </div>
                      <p v-if="bookSummaryText(book)" class="book-summary" :title="bookSummaryText(book)">{{ bookSummaryText(book) }}</p>
                    </div>
                  </li>
                </ul>
              </section>
            </template>

            <!-- ======== 编辑视图（就地展开） ======== -->
            <template v-else>
              <section class="sdm-section">
                <h3 class="sdm-section-title">系列信息</h3>
                <div class="edit-grid">
                  <div class="field">
                    <label class="field-label" for="sdm-title">标题</label>
                    <input id="sdm-title" v-model="seriesForm.title" class="input" type="text" />
                  </div>
                  <div class="field">
                    <label class="field-label" for="sdm-titlesort">排序标题</label>
                    <input id="sdm-titlesort" v-model="seriesForm.titleSort" class="input" type="text" placeholder="用于排序" />
                  </div>
                  <div class="field">
                    <label class="field-label" for="sdm-status">连载状态</label>
                    <select id="sdm-status" v-model="seriesForm.status" class="select">
                      <option value="">（未设置）</option>
                      <option v-for="opt in STATUS_OPTIONS" :key="opt" :value="opt">{{ STATUS_LABELS[opt] }}（{{ opt }}）</option>
                    </select>
                  </div>
                  <div class="field field-span">
                    <label class="field-label" for="sdm-summary">简介</label>
                    <textarea id="sdm-summary" v-model="seriesForm.summary" class="input" rows="5"></textarea>
                  </div>
                  <div class="field">
                    <label class="field-label" for="sdm-tags">标签（每行一个）</label>
                    <textarea id="sdm-tags" v-model="seriesForm.tagsText" class="input" rows="4"></textarea>
                  </div>
                  <div class="field">
                    <label class="field-label" for="sdm-alts">别名（每行一个）</label>
                    <textarea id="sdm-alts" v-model="seriesForm.altTitlesText" class="input" rows="4"></textarea>
                  </div>
                </div>
              </section>

              <section class="sdm-section">
                <div class="sdm-section-row">
                  <h3 class="sdm-section-title">每卷<span class="sdm-count num">{{ books.length }}</span></h3>
                  <button class="btn btn-sm" type="button" :data-loading="rescrapeBusy" :disabled="rescrapeBusy" title="从 Bangumi 重新对齐每卷标题/卷号/日期" @click="rescrapeVolumes">
                    {{ rescrapeBusy ? '重刮中…' : '重刮每卷' }}
                  </button>
                </div>

                <p v-if="!books.length" class="empty-hint">这个系列还没有卷</p>

                <div v-else class="volume-list">
                  <details v-for="(book, i) in books" :key="book.id" class="volume" :open="book.open">
                    <summary class="volume-head" @click.prevent="book.open = !book.open">
                      <span class="book-index num">{{ i + 1 }}</span>
                      <span class="volume-name">{{ book.form.title || book.name }}</span>
                      <span class="badge num" :class="{ 'badge--skipped': book.orig.number === '' }">
                        {{ book.orig.number === '' ? '无卷号' : `卷 ${book.orig.number}` }}
                      </span>
                      <svg class="volume-caret" :class="{ 'is-open': book.open }" viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><polyline points="6 9 12 15 18 9"/></svg>
                    </summary>
                    <div v-if="book.open" class="volume-body">
                      <div class="edit-grid">
                        <div class="field">
                          <label class="field-label" :for="`v-title-${book.id}`">标题</label>
                          <input :id="`v-title-${book.id}`" v-model="book.form.title" class="input" type="text" />
                        </div>
                        <div class="field">
                          <label class="field-label" :for="`v-number-${book.id}`">卷号</label>
                          <input :id="`v-number-${book.id}`" v-model="book.form.number" class="input" type="number" step="any" />
                        </div>
                        <div class="field">
                          <label class="field-label" :for="`v-date-${book.id}`">发售日期</label>
                          <input :id="`v-date-${book.id}`" v-model="book.form.releaseDate" class="input" type="date" />
                        </div>
                        <div class="field">
                          <label class="field-label" :for="`v-isbn-${book.id}`">ISBN</label>
                          <input :id="`v-isbn-${book.id}`" v-model="book.form.isbn" class="input" type="text" placeholder="978-4-06-…" />
                        </div>
                        <div class="field field-span">
                          <label class="field-label" :for="`v-summary-${book.id}`">简介</label>
                          <textarea :id="`v-summary-${book.id}`" v-model="book.form.summary" class="input" rows="3"></textarea>
                        </div>
                      </div>
                      <label class="btn btn-sm cover-btn" :class="{ 'is-busy': book.coverBusy }">
                        <span v-if="book.coverBusy" class="spinner" aria-hidden="true"></span>
                        {{ book.coverBusy ? '上传中…' : '更换本卷封面' }}
                        <input type="file" accept="image/*" class="visually-hidden" @change="onBookCoverChange(book, $event)" />
                      </label>
                    </div>
                  </details>
                </div>
              </section>
            </template>
          </div>
        </template>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.sdm-root {
  position: fixed;
  inset: 0;
  z-index: 100;
}

.sdm-backdrop {
  position: absolute;
  inset: 0;
  background: rgba(5, 6, 9, 0.66);
  backdrop-filter: blur(4px);
  animation: fade-in 0.2s ease;
}

.sdm-modal {
  position: absolute;
  left: 50%;
  top: 50%;
  transform: translate(-50%, -50%);
  width: min(1080px, 94vw);
  height: min(88vh, 900px);
  background: var(--bg-2);
  border: 1px solid var(--line-2);
  border-radius: var(--radius-l);
  box-shadow: var(--shadow-2);
  display: flex;
  flex-direction: column;
  overflow: hidden;
  animation: modal-in 0.26s cubic-bezier(0.2, 0.8, 0.3, 1);
}

@keyframes modal-in {
  from {
    opacity: 0;
    transform: translate(-50%, -47%) scale(0.985);
  }
  to {
    opacity: 1;
    transform: translate(-50%, -50%) scale(1);
  }
}

@keyframes fade-in {
  from {
    opacity: 0;
  }
}

.sdm-loading,
.sdm-error {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 14px;
  color: var(--text-1);
}

.sdm-error p {
  color: var(--err);
}

/* ---- 头部 ---- */
.sdm-head {
  display: flex;
  align-items: flex-start;
  gap: var(--s-4);
  padding: 22px 22px 16px;
  border-bottom: 1px solid var(--line-1);
  flex-shrink: 0;
}

.sdm-cover {
  flex-shrink: 0;
  position: relative;
  width: 96px;
  height: 136px;
  border-radius: var(--radius-s);
  overflow: hidden;
  background: var(--bg-inset);
  border: 1px solid var(--line-1);
  display: grid;
  place-items: center;
}

.sdm-cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.sdm-cover-fallback {
  color: var(--text-2);
  font-size: 22px;
}

.sdm-cover-upload {
  position: absolute;
  inset: auto 0 0;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  padding: 7px 0;
  background: rgba(5, 6, 9, 0.78);
  color: var(--text-0);
  font-size: 11.5px;
  cursor: pointer;
  backdrop-filter: blur(4px);
}

.sdm-cover-upload.is-busy {
  pointer-events: none;
}

.sdm-head-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.sdm-title {
  font-size: 19px;
  font-weight: 700;
  letter-spacing: -0.01em;
  line-height: 1.35;
  word-break: break-word;
  display: flex;
  align-items: baseline;
  gap: 10px;
}

.sdm-title-note {
  font-size: 12px;
  font-weight: 400;
  color: var(--text-2);
}

.sdm-head-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.state-chip {
  font-size: 11px;
  line-height: 1;
  padding: 4px 7px;
  border-radius: var(--radius-s);
  background: var(--bg-inset);
  border: 1px solid var(--line-1);
  color: var(--text-1);
}

.state-chip[data-state='✓'] {
  color: var(--ok);
}

.state-chip[data-state='✗'] {
  color: var(--err);
}

.state-chip[data-state='…'] {
  color: var(--text-2);
}

.sdm-locks {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.lock-chip {
  font-size: 10.5px;
  line-height: 1;
  padding: 4px 7px;
  border-radius: 99px;
  color: var(--warn);
  border: 1px solid color-mix(in srgb, var(--warn) 40%, transparent);
  background: var(--warn-soft);
}

.sdm-head-actions {
  flex-shrink: 0;
  display: flex;
  gap: 8px;
}

.sdm-close {
  flex-shrink: 0;
  color: var(--text-1);
  background: var(--bg-3);
}

/* ---- 正文 ---- */
.sdm-body {
  flex: 1;
  overflow-y: auto;
  padding: 6px 22px 28px;
}

.sdm-section {
  padding: var(--s-3) 0;
}

.sdm-section + .sdm-section {
  border-top: 1px solid var(--line-1);
}

.sdm-section-title {
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.07em;
  color: var(--text-2);
  margin-bottom: var(--s-2);
}

.sdm-section-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: var(--s-2);
}

.sdm-section-row .sdm-section-title {
  margin-bottom: 0;
}

.sdm-count {
  color: var(--text-1);
  margin-left: 6px;
}

.sdm-summary {
  font-size: 13.5px;
  line-height: 1.75;
  color: var(--text-0);
  white-space: pre-line;
}

/* ---- 元数据两列表 ---- */
.meta-list {
  margin: 0;
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 0 var(--s-6);
}

.meta-row {
  display: flex;
  gap: 10px;
  padding: 7px 0;
  border-bottom: 1px dashed var(--line-1);
  min-width: 0;
}

.meta-label {
  flex-shrink: 0;
  width: 72px;
  font-size: 12.5px;
  color: var(--text-2);
  line-height: 1.5;
}

.meta-value {
  margin: 0;
  min-width: 0;
  font-size: 13px;
  color: var(--text-0);
  line-height: 1.5;
  word-break: break-word;
}

.sdm-links {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.sdm-links a {
  font-size: 13px;
  word-break: break-all;
}

/* ---- 书卷（只读：封面 + 简介 + 行信息） ---- */
.book-list {
  margin: 0;
  padding: 0;
  list-style: none;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.book-item {
  display: flex;
  gap: 12px;
  padding: 9px 10px;
  margin: 0 -10px;
  border-radius: var(--radius-s);
  transition: background-color var(--t-fast);
}

.book-item:hover {
  background: var(--bg-3);
}

.book-thumb {
  flex-shrink: 0;
  width: 44px;
  height: 62px;
  border-radius: var(--radius-s);
  overflow: hidden;
  display: grid;
  place-items: center;
  background: var(--bg-inset);
  border: 1px solid var(--line-1);
}

.book-thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.book-thumb-fb {
  color: var(--text-2);
  font-size: 14px;
}

.book-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 5px;
}

.book-line {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.book-index {
  flex-shrink: 0;
  width: 22px;
  height: 22px;
  display: grid;
  place-items: center;
  border-radius: var(--radius-full);
  background: var(--bg-inset);
  border: 1px solid var(--line-1);
  font-size: 11px;
  color: var(--text-2);
}

.book-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-weight: 550;
  font-size: 13px;
}

.book-date {
  flex-shrink: 0;
  font-size: 12px;
  color: var(--text-2);
}

.book-summary {
  margin: 0;
  font-size: 12px;
  line-height: 1.6;
  color: var(--text-2);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

/* ---- 编辑视图 ---- */
.edit-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--s-3);
}

.field-span {
  grid-column: 1 / -1;
}

.empty-hint {
  font-size: 13px;
  color: var(--text-2);
  padding: 12px 0;
}

/* ---- 每卷（编辑） ---- */
.volume-list {
  display: flex;
  flex-direction: column;
  gap: var(--s-2);
}

.volume {
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
  background: var(--bg-1);
  overflow: hidden;
}

.volume-head {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 11px 14px;
  cursor: pointer;
  user-select: none;
  list-style: none;
  transition: background-color var(--t-fast);
}

.volume-head::-webkit-details-marker {
  display: none;
}

.volume-head:hover {
  background: var(--bg-3);
}

.volume-name {
  flex: 1;
  min-width: 0;
  font-size: 13.5px;
  font-weight: 550;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.volume-caret {
  flex-shrink: 0;
  color: var(--text-2);
  transition: transform var(--t-fast);
}

.volume-caret.is-open {
  transform: rotate(180deg);
}

.volume-body {
  padding: 4px 14px 14px;
  border-top: 1px solid var(--line-1);
  display: flex;
  flex-direction: column;
  gap: var(--s-3);
}

.cover-btn {
  align-self: flex-start;
  cursor: pointer;
  position: relative;
}

.cover-btn.is-busy {
  pointer-events: none;
  opacity: 0.75;
}

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}

/* ---- 小屏 ---- */
@media (max-width: 720px) {
  .sdm-modal {
    width: 100vw;
    height: 100vh;
    border-radius: 0;
    left: 0;
    top: 0;
    transform: none;
  }

  @keyframes modal-in {
    from {
      opacity: 0;
      transform: scale(0.99);
    }
    to {
      opacity: 1;
      transform: none;
    }
  }

  .meta-list,
  .edit-grid {
    grid-template-columns: 1fr;
  }

  .field-span {
    grid-column: auto;
  }

  .sdm-head {
    flex-wrap: wrap;
  }
}
</style>
