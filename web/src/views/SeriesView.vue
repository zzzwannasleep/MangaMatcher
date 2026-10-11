<script setup>
import { ref, reactive, computed, onMounted, onBeforeUnmount, watch } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { api, apiBlob, apiDownload } from '../api/client'
import { notify } from '../stores/toast'
import EmptyState from '../components/ui/EmptyState.vue'
import CorrectMatchModal from '../components/CorrectMatchModal.vue'
import SeriesDetailModal from '../components/SeriesDetailModal.vue'

const router = useRouter()
const route = useRoute()

const libraryId = computed(() => String(route.params.id))
const libraryName = ref('')

const series = ref([])
const total = ref(0)
const page = ref(0)
const PAGE_SIZE = 60
const loading = ref(true)
const loadingMore = ref(false)
const loadError = ref('')

/* ---------- 状态筛选 + 服务端搜索 ---------- */
const FILTERS = [
  { value: 'all', label: '全部' },
  { value: 'unscraped', label: '未刮削' },
  { value: 'scraped', label: '已刮削' },
  { value: 'no_cover', label: '未刮封面' },
  { value: 'no_summary', label: '未刮简介' },
  { value: 'incomplete', label: '待补全' },
  { value: 'complete', label: '已完整' },
]

const filter = ref('all')
const searchQ = ref('')
let searchTimer = null

/* ---------- 多选 ---------- */
const selected = ref(new Set())
const selectedCount = computed(() => selected.value.size)

const allChecked = computed(() => {
  if (!series.value.length) return false
  return series.value.every((s) => selected.value.has(s.id))
})

const someChecked = computed(() => selectedCount.value > 0 && !allChecked.value)

const scraping = ref(false)

const correcting = ref(null) // { id, name }
const detailId = ref('') // 详情/编辑大窗模态
const detailEdit = ref(false) // 打开即进入编辑模式

const detailName = computed(() => {
  const s = series.value.find((x) => x.id === detailId.value)
  return s ? seriesTitle(s) : ''
})

/* ---------- 数据加载（分批 + 无限滚动） ---------- */
const hasMore = computed(() => series.value.length < total.value)

async function fetchPage(pageNum, size) {
  const params = new URLSearchParams({
    page: String(pageNum),
    size: String(size),
  })
  if (filter.value && filter.value !== 'all') params.set('filter', filter.value)
  const q = searchQ.value.trim()
  if (q) params.set('q', q)
  return api(`/api/libraries/${libraryId.value}/series?${params.toString()}`)
}

/** 首屏/重置：拉第一批 */
async function load() {
  loading.value = true
  loadError.value = ''
  try {
    const data = await fetchPage(0, PAGE_SIZE)
    series.value = data.content || []
    total.value = data.totalElements ?? series.value.length
    page.value = 1
  } catch (err) {
    loadError.value = err.message
    notify.error(err.message)
  } finally {
    loading.value = false
    reobserve()
  }
}

/** 滚动到底：追加下一批 */
async function loadMore() {
  if (loading.value || loadingMore.value || !hasMore.value) return
  if (window.__kbSidebarAnimating) return // 动画期不追加 DOM（IO 回调结束后自然重试）
  loadingMore.value = true
  try {
    const data = await fetchPage(page.value, PAGE_SIZE)
    const more = data.content || []
    const known = new Set(series.value.map((s) => s.id))
    series.value.push(...more.filter((s) => !known.has(s.id)))
    total.value = data.totalElements ?? total.value
    page.value += 1
  } catch (err) {
    notify.error(err.message)
  } finally {
    loadingMore.value = false
    reobserve()
  }
}

/** 编辑保存后刷新：一次拉回当前已加载数量 */
async function refresh() {
  const size = Math.max(PAGE_SIZE, Math.ceil(series.value.length / PAGE_SIZE) * PAGE_SIZE)
  try {
    const data = await fetchPage(0, size)
    series.value = data.content || []
    total.value = data.totalElements ?? series.value.length
    page.value = Math.max(1, Math.ceil(series.value.length / PAGE_SIZE))
    releaseThumbs()
    reobserve()
  } catch (err) {
    notify.error(err.message)
  }
}

function onSearchInput() {
  if (searchTimer) clearTimeout(searchTimer)
  searchTimer = setTimeout(() => {
    resetWall()
    load()
  }, 420)
}

function searchNow() {
  if (searchTimer) {
    clearTimeout(searchTimer)
    searchTimer = null
  }
  resetWall()
  load()
}

watch(filter, () => {
  resetWall()
  selected.value = new Set()
  load()
})

async function loadLibraryName() {
  try {
    const libs = await api('/api/libraries')
    const found = (Array.isArray(libs) ? libs : []).find((l) => String(l.id) === libraryId.value)
    libraryName.value = found?.name || ''
  } catch {
    /* 名称拿不到不阻塞 */
  }
}

/* ---------- 缩略图：懒加载（进入视口才取 blob） ---------- */
const thumbs = reactive({})
const thumbPending = new Set()
const thumbAbort = new AbortController()

async function loadThumb(seriesId) {
  if (thumbPending.has(seriesId) || thumbs[seriesId]) return
  if (window.__kbSidebarAnimating) {
    scheduleDeferredThumb(seriesId)
    return
  }
  thumbPending.add(seriesId)
  try {
    const blob = await apiBlob(`/api/series/${seriesId}/thumbnail`, thumbAbort.signal)
    thumbs[seriesId] = URL.createObjectURL(blob)
  } catch (err) {
    if (err?.name !== 'AbortError') thumbs[seriesId] = ''
  } finally {
    thumbPending.delete(seriesId)
  }
}

function releaseThumbs() {
  Object.keys(thumbs).forEach((id) => {
    if (thumbs[id]) URL.revokeObjectURL(thumbs[id])
    delete thumbs[id]
  })
  thumbPending.clear()
}

/* 卡片注册进懒加载观察器 */
let thumbObserver = null

/* 侧栏动画期挂起：新进视口的缩略图先排队，动画结束后统一取（避免解码风暴打断过渡帧） */
const deferredThumbs = new Set()

function onSidebarAnimEnd() {
  const ids = [...deferredThumbs]
  deferredThumbs.clear()
  // 分块解码：每帧最多 3 张，避免集中解码再造长帧
  let i = 0
  function chunk() {
    const slice = ids.slice(i, i + 3)
    i += 3
    slice.forEach((id) => loadThumb(id))
    if (i < ids.length) requestAnimationFrame(chunk)
  }
  if (ids.length) requestAnimationFrame(chunk)
  // 动画结束补一次哨兵检查：IO 对「持续相交」不会重新回调
  if (sentinelEl.value && hasMore.value && !loading.value && !loadingMore.value) {
    const rect = sentinelEl.value.getBoundingClientRect()
    if (rect.top < window.innerHeight + 500 && rect.bottom > -500) loadMore()
  }
}

function watchCard(el) {
  if (!el || !thumbObserver || el.dataset.thumbObserved) return
  el.dataset.thumbObserved = '1'
  thumbObserver.observe(el)
}

/* 无限滚动哨兵 */
let sentinelObserver = null
const sentinelEl = ref(null)

function watchSentinel(el) {
  sentinelEl.value = el
  if (el && sentinelObserver) sentinelObserver.observe(el)
  else if (!el && sentinelObserver) sentinelObserver.disconnect()
}

function resetWall() {
  releaseThumbs()
  page.value = 0
}

/** 分批重置后重新注册全部卡片 */
function reobserve() {
  if (!thumbObserver) return
  document.querySelectorAll('.card-grid .card-cover').forEach((el) => {
    delete el.dataset.thumbObserved
    thumbObserver.observe(el)
  })
}

/* ---------- 多选 ---------- */
function toggle(seriesId, checked) {
  const next = new Set(selected.value)
  if (checked) next.add(seriesId)
  else next.delete(seriesId)
  selected.value = next
}

function toggleAll(checked) {
  const next = new Set()
  if (checked) series.value.forEach((s) => next.add(s.id))
  selected.value = next
}

function clearSelection() {
  selected.value = new Set()
}

/* ---------- 动作 ---------- */
async function createJob(extra, label) {
  scraping.value = true
  try {
    const res = await api('/api/jobs', {
      method: 'POST',
      body: {
        libraryId: libraryId.value,
        seriesIds: [...selected.value],
        dryRun: false,
        ...extra,
      },
    })
    notify.success(label)
    router.push(`/jobs/${res.id}`)
  } catch (err) {
    notify.error(err.message)
  } finally {
    scraping.value = false
  }
}

async function scrapeSelected() {
  const ids = [...selected.value]
  if (!ids.length) return
  await createJob({}, `刮削任务已创建（${ids.length} 个系列）`)
}

/** 覆盖匹配：重走全链，忽略旧链接（设置里的批量强制刷新不影响本按钮） */
function overrideMatch() {
  const scope = selectedCount.value ? `${selectedCount.value} 个系列` : '全库'
  createJob({ overrideMatch: true }, `覆盖匹配任务已创建（${scope}）`)
}

/** 一键打标：版本标记 → 标签/标题后缀/出版社（kind:tag） */
function tagJob() {
  const scope = selectedCount.value ? `${selectedCount.value} 个系列` : '全库'
  createJob({ kind: 'tag' }, `打标任务已创建（${scope}）`)
}

/* ---------- Mylar 导出 ---------- */
const exportingMylar = ref(false)
const mylarWithCovers = ref(false)

async function exportMylar() {
  exportingMylar.value = true
  try {
    const query = mylarWithCovers.value ? '?withCovers=1' : ''
    const { blob, filename } = await apiDownload(`/api/libraries/${libraryId.value}/mylar${query}`)
    const url = URL.createObjectURL(blob)
    const anchor = document.createElement('a')
    anchor.href = url
    anchor.download = filename || `mylar-${libraryName.value || libraryId.value}.zip`
    document.body.appendChild(anchor)
    anchor.click()
    anchor.remove()
    URL.revokeObjectURL(url)
    notify.success('Mylar 导出已下载')
  } catch (err) {
    notify.error(err.message)
  } finally {
    exportingMylar.value = false
  }
}

/* ---------- 状态图标 ---------- */
/** 封面/简介 ✓✗…（… = 后台确认中 / 未记录） */
function stateIcon(s, key) {
  if (!s?.state) return '…'
  return s.state[key] ? '✓' : '✗'
}

/* ---------- 行为 ---------- */
function openDetail(seriesId, edit = false) {
  detailId.value = seriesId
  detailEdit.value = edit
}

function closeDetail() {
  detailId.value = ''
}

/** 模态里保存/换封面/重刮后 → 刷新列表 */
function onDetailChanged() {
  refresh()
}

function correctFromDetail() {
  const id = detailId.value
  const name = detailName.value
  closeDetail()
  correcting.value = { id, name }
}

function onApplied(jobId) {
  correcting.value = null
  notify.success('已提交匹配应用任务')
  if (jobId) router.push(`/jobs/${jobId}`)
}

/* ---------- 生命周期 ---------- */
watch(libraryId, () => {
  resetWall()
  selected.value = new Set()
  libraryName.value = ''
  filter.value = 'all'
  searchQ.value = ''
  load()
  loadLibraryName()
})

function seriesTitle(s) {
  return s?.metadata?.title || s?.name || s?.title || s?.id
}

onMounted(() => {
  window.addEventListener('kb-sidebar-anim-end', onSidebarAnimEnd)
  thumbObserver = new IntersectionObserver(
    (entries) => {
      for (const entry of entries) {
        if (!entry.isIntersecting) continue
        const id = entry.target.dataset.sid
        if (id) loadThumb(id)
        thumbObserver.unobserve(entry.target)
      }
    },
    { rootMargin: '300px 0px' }
  )
  sentinelObserver = new IntersectionObserver(
    (entries) => {
      if (entries.some((e) => e.isIntersecting)) loadMore()
    },
    { rootMargin: '500px 0px' }
  )
  if (sentinelEl.value) sentinelObserver.observe(sentinelEl.value)
  load()
  loadLibraryName()
})

onBeforeUnmount(() => {
  window.removeEventListener('kb-sidebar-anim-end', onSidebarAnimEnd)
  thumbAbort.abort()
  releaseThumbs()
  if (searchTimer) clearTimeout(searchTimer)
  thumbObserver?.disconnect()
  sentinelObserver?.disconnect()
})
</script>

<template>
  <div>
    <header class="page-head">
      <div>
        <RouterLink to="/" class="crumb">← 图书馆</RouterLink>
        <h1 class="page-title">{{ libraryName || '系列' }}</h1>
        <p class="page-sub num" v-if="total">已加载 {{ series.length }} / {{ total }} 个系列</p>
      </div>
      <div class="page-actions">
        <button
          class="btn btn-primary"
          type="button"
          :data-loading="scraping"
          :disabled="scraping || !selectedCount"
          @click="scrapeSelected"
        >
          刮削选中
        </button>
        <button
          class="btn"
          type="button"
          :data-loading="scraping"
          :disabled="scraping"
          title="重走匹配全链，忽略旧链接与元数据标题，搜索词只用文件夹名"
          @click="overrideMatch"
        >
          覆盖匹配<span v-if="!selectedCount" class="btn-note">（全库）</span>
        </button>
        <button
          class="btn"
          type="button"
          :data-loading="scraping"
          :disabled="scraping"
          title="版本标记 → 标签 + 标题后缀、出版社词标签、publisher 填充"
          @click="tagJob"
        >
          一键打标<span v-if="!selectedCount" class="btn-note">（全库）</span>
        </button>
        <button
          class="btn"
          type="button"
          :data-loading="exportingMylar"
          :disabled="exportingMylar"
          title="导出全库 series.json（ZIP）"
          @click="exportMylar"
        >
          Mylar 导出
        </button>
        <label class="mylar-covers" title="导出 ZIP 时包含封面文件">
          <input type="checkbox" v-model="mylarWithCovers" />
          含封面
        </label>
      </div>
    </header>

    <!-- 状态筛选 + 搜索 -->
    <div class="toolbar">
      <label class="toolbar-field">
        <select v-model="filter" class="select" aria-label="状态筛选">
          <option v-for="f in FILTERS" :key="f.value" :value="f.value">{{ f.label }}</option>
        </select>
      </label>
      <input
        v-model="searchQ"
        class="input toolbar-search"
        type="search"
        placeholder="搜索名称 / 原名…"
        aria-label="搜索系列"
        @input="onSearchInput"
        @keydown.enter.prevent="searchNow"
      />
    </div>

    <div v-if="loading" class="loading-block">
      <span class="spinner" aria-hidden="true"></span>
      正在加载系列…
    </div>

    <EmptyState v-else-if="loadError" icon="⚠" title="加载失败" :hint="loadError">
      <button class="btn" type="button" @click="load">重试</button>
    </EmptyState>

    <EmptyState
      v-else-if="!series.length"
      :icon="searchQ.trim() || filter !== 'all' ? '◌' : '▦'"
      :title="searchQ.trim() || filter !== 'all' ? '没有符合条件的系列' : '这个库还没有系列'"
      :hint="searchQ.trim() || filter !== 'all' ? '换个筛选条件或搜索词试试' : 'Komga 扫描到内容后会出现在这里'"
    />

    <template v-else>
      <div v-if="selectedCount" class="select-bar">
        已选 <strong class="num">{{ selectedCount }}</strong> 个系列
        <button class="btn btn-ghost btn-sm" type="button" @click="clearSelection">清空选择</button>
      </div>

      <!-- 封面卡片墙 -->
      <div class="card-grid">
        <div
          v-for="s in series"
          :key="s.id"
          class="series-card"
          :class="{ 'is-selected': selected.has(s.id) }"
        >
          <div class="card-cover lift" :ref="(el) => watchCard(el)" :data-sid="s.id" role="button" tabindex="0" :aria-label="`查看「${seriesTitle(s)}」详情`" @click="openDetail(s.id)" @keydown.enter.prevent="openDetail(s.id)">
            <img
              v-if="thumbs[s.id]"
              :src="thumbs[s.id]"
              alt=""
              loading="lazy"
              @error="thumbs[s.id] = ''"
            />
            <span v-else class="thumb-fallback" aria-hidden="true">▤</span>
            <label class="card-check" title="加入选择" @click.stop>
              <input
                type="checkbox"
                :checked="selected.has(s.id)"
                :aria-label="`选择 ${seriesTitle(s)}`"
                @change="toggle(s.id, $event.target.checked)"
              />
            </label>
          </div>
          <div class="card-info">
            <span class="card-title" :title="seriesTitle(s)" @click="openDetail(s.id)">{{ seriesTitle(s) }}</span>
            <div class="card-meta">
              <span class="badge num">{{ s.booksCount ?? 0 }} 册</span>
              <span class="state-icons" aria-label="刮削状态">
                <span class="state-chip" :data-state="stateIcon(s, 'hasCover')" title="封面状态">封 {{ stateIcon(s, 'hasCover') }}</span>
                <span class="state-chip" :data-state="stateIcon(s, 'hasSummary')" title="简介状态">简 {{ stateIcon(s, 'hasSummary') }}</span>
              </span>
            </div>
          </div>
        </div>
      </div>

      <!-- 无限滚动哨兵 + 加载状态 -->
      <div class="scroll-sentinel" :ref="(el) => watchSentinel(el)" aria-hidden="true"></div>
      <div class="wall-foot">
        <span v-if="loadingMore" class="wall-loading"><span class="spinner" aria-hidden="true"></span>加载更多…</span>
        <span v-else-if="!hasMore && series.length" class="wall-done num">已全部加载 · {{ total }} 个系列</span>
      </div>
    </template>

    <SeriesDetailModal
      v-if="detailId"
      :series-id="detailId"
      :start-edit="detailEdit"
      @close="closeDetail"
      @correct="correctFromDetail"
      @changed="onDetailChanged"
    />

    <CorrectMatchModal
      v-if="correcting"
      :series-id="correcting.id"
      :series-name="correcting.name"
      @close="correcting = null"
      @applied="onApplied"
    />
  </div>
</template>

<style scoped>
.crumb {
  display: inline-block;
  font-size: 12.5px;
  color: var(--text-2);
  margin-bottom: 6px;
}

.crumb:hover {
  color: var(--accent-strong);
}

.page-head .page-title {
  margin-top: 2px;
}

.page-actions .btn-note {
  color: var(--text-2);
  font-weight: 400;
  margin-left: 4px;
}

.page-actions .btn-primary .btn-note {
  color: color-mix(in srgb, var(--on-accent) 70%, transparent);
}

.mylar-covers {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
  color: var(--text-1);
  cursor: pointer;
  white-space: nowrap;
}

/* ---- 工具行 ---- */
.toolbar {
  display: flex;
  align-items: center;
  gap: var(--s-2);
  margin-bottom: var(--s-4);
}

.toolbar-field {
  flex-shrink: 0;
}

.toolbar .select {
  width: auto;
  min-width: 130px;
}

.toolbar-search {
  max-width: 320px;
}

.select-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 10px 16px;
  margin-bottom: var(--s-4);
  background: var(--accent-soft);
  border: 1px solid var(--accent-line);
  border-radius: var(--radius-s);
  font-size: 13px;
}

.select-bar .btn {
  margin-left: auto;
}

/* ---- 卡片墙 ---- */
.card-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(148px, 1fr));
  gap: 18px 16px;
}

.series-card {
  position: relative;
  min-width: 0;
  /* 屏外卡跳过布局/渲染（122 卡墙的关键）：侧栏折叠时每帧 relayout 成本大降 */
  content-visibility: auto;
  contain-intrinsic-size: auto 300px;
}

.card-cover {
  position: relative;
  aspect-ratio: 2 / 3;
  border-radius: var(--radius-m);
  overflow: hidden;
  background: var(--bg-inset);
  border: 1px solid var(--line-1);
  cursor: pointer;
  display: grid;
  place-items: center;
}

.card-cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
  transition: transform var(--t-med);
}

.series-card:hover .card-cover img {
  transform: scale(1.04);
}

.series-card.is-selected .card-cover {
  border-color: var(--accent-line);
  box-shadow: var(--shadow-accent);
}

.thumb-fallback {
  color: var(--text-2);
  font-size: 26px;
}

/* 选择框：hover / 已选时可见（大框大勾，多选操作友好） */
.card-check {
  position: absolute;
  top: 8px;
  left: 8px;
  z-index: 2;
  width: 32px;
  height: 32px;
  display: grid;
  place-items: center;
  border-radius: var(--radius-s);
  background: rgba(10, 11, 14, 0.72);
  backdrop-filter: blur(4px);
  border: 1px solid var(--line-2);
  opacity: 0;
  transform: translateY(-4px);
  transition: opacity var(--t-fast), transform var(--t-fast);
}

.card-cover:hover .card-check,
.card-check:focus-within,
.series-card.is-selected .card-check {
  opacity: 1;
  transform: none;
}

.card-check input {
  width: 20px;
  height: 20px;
}

/* 悬停操作浮层（编辑/纠正入口已收敛进详情大窗，卡片只保留选择框） */
.card-overlay {
  display: none;
}

/* 卡片下方信息 */
.card-info {
  display: flex;
  flex-direction: column;
  gap: 7px;
  padding: 9px 2px 0;
}

.card-title {
  font-size: 13px;
  font-weight: 600;
  line-height: 1.4;
  color: var(--text-0);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: pointer;
}

.card-title:hover {
  color: var(--accent-strong);
}

.card-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.state-icons {
  display: flex;
  gap: 4px;
}

.state-chip {
  font-size: 10.5px;
  line-height: 1;
  padding: 3px 6px;
  border-radius: var(--radius-s);
  background: var(--bg-inset);
  border: 1px solid var(--line-1);
  color: var(--text-1);
  white-space: nowrap;
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

.pager-label {
  font-size: 13px;
  color: var(--text-1);
}

/* ---- 无限滚动 ---- */
.scroll-sentinel {
  height: 1px;
}

.wall-foot {
  display: flex;
  justify-content: center;
  padding: var(--s-4) 0 var(--s-2);
  color: var(--text-2);
  font-size: 12.5px;
}

.wall-loading {
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.wall-loading .spinner {
  width: 14px;
  height: 14px;
  border-width: 2px;
}

@media (max-width: 640px) {
  .toolbar {
    flex-wrap: wrap;
  }

  .toolbar-search {
    max-width: none;
    flex: 1;
  }

  .card-grid {
    grid-template-columns: repeat(auto-fill, minmax(118px, 1fr));
    gap: 14px 12px;
  }
}
</style>
