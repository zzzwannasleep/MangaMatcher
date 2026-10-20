<script setup>
import { ref, reactive, computed, onMounted } from 'vue'
import { api } from '../api/client'
import { notify } from '../stores/toast'
import Modal from './ui/Modal.vue'
import EmptyState from './ui/EmptyState.vue'

const props = defineProps({
  seriesId: { type: String, required: true },
  seriesName: { type: String, default: '' },
})

const emit = defineEmits(['close', 'applied'])

/* 模式：resolve = 贴链接→解析→预览→确认（主流程）；search = 候选搜索 */
const mode = ref('resolve')

// 源分组固定顺序（后端空源不返回该 key）
const SOURCE_META = {
  bangumi: { label: 'Bangumi', bg: 'bgm' },
  bookwalker: { label: 'BookWalker', bg: 'bw' },
  mox: { label: 'Mox', bg: 'mox' },
  mangadex: { label: 'MangaDex', bg: 'md' },
  ehentai: { label: 'E-Hentai', bg: 'eh' },
  baike: { label: '百度百科', bg: 'bk' },
}

function sourceLabel(source) {
  const map = { bangumi: 'Bangumi', bookwalker: 'BookWalker', mox: 'Mox', mangadex: 'MangaDex', ehentai: 'E-Hentai', baike: '百度百科', Bgm: 'Bangumi', Bw: 'BookWalker', Mox: 'Mox', MangaDex: 'MangaDex', EH: 'E-Hentai', Baike: '百度百科' }
  return map[source] || source
}

/* ================= 链接解析（主流程） ================= */
const linkInput = ref('')
const resolving = ref(false)
const resolveError = ref('')
const resolved = ref(null) // { source, id, title, oriTitle, author, aliases, summary, coverUrl, tags, tagsRaw, url }

const aliasTextOf = (d) => {
  const a = d?.aliases
  if (!a) return ''
  if (Array.isArray(a)) return a.filter(Boolean).join('、')
  return String(a).split(' / ').filter(Boolean).join('、')
}

/* E-Hentai 中文标签（后端已按写库同源排序并截断 12 条，这里只做防御性兜底；其它源不返回） */
const tagChips = (d) => (Array.isArray(d?.tags) ? d.tags : []).slice(0, 12)

/* E-Hentai 原始英文标签（ns:tag），仅用于「查看原文标签」折叠 */
const rawTags = (d) => (Array.isArray(d?.tagsRaw) ? d.tagsRaw : [])

const resolvedAlias = computed(() => aliasTextOf(resolved.value))

async function resolve() {
  const raw = linkInput.value.trim()
  if (!raw || resolving.value) return
  resolving.value = true
  resolveError.value = ''
  resolved.value = null
  try {
    resolved.value = await api('/api/match/resolve', { method: 'POST', body: { url: raw } })
  } catch (err) {
    resolveError.value = err.message
  } finally {
    resolving.value = false
  }
}

function resetResolve() {
  resolved.value = null
  resolveError.value = ''
  linkInput.value = ''
}

/* ================= 候选搜索（第二入口） ================= */
const loading = ref(true)
const loadError = ref('')
const groups = ref({}) // { bangumi: [cand], ... }
const q = ref(props.seriesName || '')
const activeSource = ref('')
const selectedId = ref('') // 当前选中候选（源内 id）

const sources = computed(() =>
  Object.keys(SOURCE_META).filter((s) => (groups.value[s] || []).length > 0)
)

const activeCandidates = computed(() => groups.value[activeSource.value] || [])

let searchTimer = null

async function search(showLoading = true) {
  if (searchTimer) {
    clearTimeout(searchTimer)
    searchTimer = null
  }
  if (showLoading) loading.value = true
  loadError.value = ''
  try {
    const term = q.value.trim()
    const url = `/api/series/${props.seriesId}/match/candidates${term ? `?q=${encodeURIComponent(term)}` : ''}`
    const data = await api(url)
    groups.value = data?.groups || {}
    const first = sources.value[0] || ''
    if (!sources.value.includes(activeSource.value)) {
      activeSource.value = first
      selectedId.value = ''
    }
  } catch (err) {
    loadError.value = err.message
  } finally {
    loading.value = false
  }
}

function onQInput() {
  if (searchTimer) clearTimeout(searchTimer)
  searchTimer = setTimeout(() => search(), 420)
}

function pickSource(source) {
  activeSource.value = source
  selectedId.value = ''
}

function pickCandidate(cand) {
  selectedId.value = cand.id
  loadDetail(cand.source || activeSource.value, cand.id)
}

// data: { source, id, title, oriTitle, author, aliases, summary, coverUrl, tags, tagsRaw, url }
const detail = reactive({ loading: false, data: null, error: '' })

async function loadDetail(source, id) {
  detail.loading = true
  detail.error = ''
  detail.data = null
  try {
    detail.data = await api(`/api/match/detail?source=${encodeURIComponent(source)}&id=${encodeURIComponent(id)}`)
  } catch (err) {
    detail.error = err.message
  } finally {
    detail.loading = false
  }
}

/* ================= 应用（两模式共用） ================= */
const applying = ref(false)

const canApply = computed(() => {
  if (applying.value) return false
  if (mode.value === 'resolve') return !!resolved.value
  return !!selectedId.value
})

async function apply() {
  if (!canApply.value) return
  applying.value = true
  try {
    const body =
      mode.value === 'resolve'
        ? { source: resolved.value.source, id: resolved.value.id, dryRun: false }
        : { source: activeSource.value, id: selectedId.value, dryRun: false }
    const res = await api(`/api/series/${props.seriesId}/match/apply`, { method: 'POST', body })
    emit('applied', res?.jobId || '')
  } catch (err) {
    notify.error(err.message)
  } finally {
    applying.value = false
  }
}

function switchMode(next) {
  mode.value = next
  if (next === 'search' && Object.keys(groups.value).length === 0) {
    search()
  }
}

function imgError(event) {
  event.target.style.display = 'none'
}

onMounted(() => search())
</script>

<template>
  <Modal :title="`纠正匹配 · ${seriesName || seriesId.slice(0, 8)}`" :width="880" @close="emit('close')">
    <div class="cm-root">
      <!-- 模式切换 -->
      <div class="cm-modes" role="tablist" aria-label="匹配方式">
        <button type="button" role="tab" class="cm-mode" :data-active="mode === 'resolve'" :aria-selected="mode === 'resolve'" @click="switchMode('resolve')">
          链接解析
        </button>
        <button type="button" role="tab" class="cm-mode" :data-active="mode === 'search'" :aria-selected="mode === 'search'" @click="switchMode('search')">
          搜索候选
        </button>
      </div>

      <!-- ======== 模式一：贴链接 → 解析 → 预览 → 确认 ======== -->
      <template v-if="mode === 'resolve'">
        <div class="cm-resolve-row">
          <input
            v-model="linkInput"
            class="input"
            type="text"
            placeholder="粘贴 Bangumi / 百度百科 / E-Hentai 链接（也支持纯 Bangumi ID）"
            aria-label="来源链接"
            @keydown.enter.prevent="resolve"
          />
          <button class="btn" type="button" :data-loading="resolving" :disabled="resolving || !linkInput.trim()" @click="resolve">
            {{ resolving ? '解析中…' : '解析' }}
          </button>
        </div>
        <p class="cm-hint">支持 bgm.tv/subject/… · baike.baidu.com/item/… · e-hentai.org/g/123456/abcdef/ · 纯数字 Bangumi ID</p>

        <div v-if="resolveError" class="cm-error" role="alert">
          <span>{{ resolveError }}</span>
        </div>

        <div v-if="resolving" class="loading-block cm-loading">
          <span class="spinner" aria-hidden="true"></span>
          正在拉取来源信息…
        </div>

        <!-- 预览区：确认无误才入 -->
        <div v-else-if="resolved" class="cm-preview">
          <span class="cm-preview-cover">
            <img v-if="resolved.coverUrl" :src="resolved.coverUrl" alt="" loading="lazy" referrerpolicy="no-referrer" @error="imgError" />
            <span v-else class="cm-cand-fallback" aria-hidden="true">▤</span>
          </span>
          <div class="cm-preview-main">
            <div class="cm-preview-head">
              <span class="badge badge--source" :data-source="resolved.source">{{ sourceLabel(resolved.source) }}</span>
              <span v-if="resolved.source === 'bangumi'" class="badge num">BG#{{ resolved.id }}</span>
            </div>
            <h3 class="cm-preview-title">{{ resolved.title }}</h3>
            <p v-if="resolved.oriTitle && resolved.oriTitle !== resolved.title" class="cm-preview-sub">{{ resolved.oriTitle }}</p>
            <p v-if="resolved.author" class="cm-preview-sub">作者：{{ resolved.author }}</p>
            <p v-if="resolvedAlias" class="cm-preview-sub">别名：{{ resolvedAlias }}</p>
            <p class="cm-preview-summary">{{ resolved.summary || '（无简介）' }}</p>

            <!-- E-Hentai 中文标签（与写库同源，最多 12 条）+ 原文折叠 -->
            <div v-if="tagChips(resolved).length" class="cm-tags">
              <span v-for="(tag, i) in tagChips(resolved)" :key="`${i}-${tag}`" class="cm-tag">{{ tag }}</span>
            </div>
            <details v-if="rawTags(resolved).length" class="cm-tags-raw">
              <summary>查看原文标签（{{ rawTags(resolved).length }}）</summary>
              <p class="cm-tags-raw-line">{{ rawTags(resolved).join('、') }}</p>
            </details>
            <p class="cm-preview-note">确认信息无误后写入该系列（旧匹配链接会被替换，卷级数据全部重抓）</p>
          </div>
        </div>

        <EmptyState
          v-else
          icon="🔗"
          title="粘贴来源链接开始纠正"
          hint="后端会解析出标题/作者/简介/封面，预览确认后再写入"
        />
      </template>

      <!-- ======== 模式二：候选搜索 ======== -->
      <template v-else>
        <div class="cm-search">
          <input
            v-model="q"
            class="input"
            type="text"
            placeholder="搜索候选（名称/原名）"
            aria-label="候选搜索"
            @input="onQInput"
            @keydown.enter.prevent="search()"
          />
        </div>

        <div v-if="loading" class="loading-block cm-loading">
          <span class="spinner" aria-hidden="true"></span>
          正在搜索候选…
        </div>

        <EmptyState v-else-if="loadError" icon="⚠" title="候选搜索失败" :hint="loadError">
          <button class="btn btn-sm" type="button" @click="search()">重试</button>
        </EmptyState>

        <EmptyState
          v-else-if="!sources.length"
          icon="◌"
          title="没有候选结果"
          hint="试试换个搜索词，或切到「链接解析」直接贴链接"
        />

        <div v-else class="cm-body">
          <div class="cm-tabs" role="tablist" aria-label="候选来源">
            <button
              v-for="source in sources"
              :key="source"
              type="button"
              role="tab"
              class="cm-tab"
              :aria-selected="activeSource === source"
              :data-active="activeSource === source"
              :data-bg="SOURCE_META[source]?.bg"
              @click="pickSource(source)"
            >
              {{ SOURCE_META[source]?.label || source }}
              <span class="cm-tab-count num">{{ (groups[source] || []).length }}</span>
            </button>
          </div>

          <div class="cm-columns">
            <div class="cm-list" role="tabpanel">
              <button
                v-for="cand in activeCandidates"
                :key="cand.id"
                type="button"
                class="cm-cand"
                :data-selected="selectedId === cand.id"
                @click="pickCandidate(cand)"
              >
                <span class="cm-cand-cover">
                  <img v-if="cand.coverUrl" :src="cand.coverUrl" alt="" loading="lazy" referrerpolicy="no-referrer" @error="imgError" />
                  <span v-else class="cm-cand-fallback" aria-hidden="true">▤</span>
                </span>
                <span class="cm-cand-text">
                  <span class="cm-cand-title">{{ cand.title }}</span>
                  <span v-if="cand.oriTitle && cand.oriTitle !== cand.title" class="cm-cand-sub">{{ cand.oriTitle }}</span>
                  <span v-if="cand.author" class="cm-cand-sub">{{ cand.author }}</span>
                </span>
              </button>
              <p v-if="!activeCandidates.length" class="cm-empty">该源没有候选</p>
            </div>

            <aside class="cm-detail">
              <div v-if="detail.loading" class="loading-block cm-detail-loading">
                <span class="spinner" aria-hidden="true"></span>
              </div>
              <p v-else-if="detail.error" class="cm-detail-error" role="alert">{{ detail.error }}</p>

              <template v-else-if="detail.data">
                <span class="cm-detail-cover">
                  <img v-if="detail.data.coverUrl" :src="detail.data.coverUrl" alt="" loading="lazy" referrerpolicy="no-referrer" @error="imgError" />
                  <span v-else class="cm-cand-fallback" aria-hidden="true">▤</span>
                </span>
                <h3 class="cm-detail-title">{{ detail.data.title }}</h3>
                <p v-if="detail.data.oriTitle && detail.data.oriTitle !== detail.data.title" class="cm-detail-sub">{{ detail.data.oriTitle }}</p>
                <p v-if="detail.data.author" class="cm-detail-sub">作者：{{ detail.data.author }}</p>
                <p v-if="aliasTextOf(detail.data)" class="cm-detail-sub">别名：{{ aliasTextOf(detail.data) }}</p>
                <p class="cm-detail-summary">{{ detail.data.summary || '（无简介）' }}</p>

                <!-- E-Hentai 中文标签（与写库同源，最多 12 条）+ 原文折叠 -->
                <div v-if="tagChips(detail.data).length" class="cm-tags">
                  <span v-for="(tag, i) in tagChips(detail.data)" :key="`${i}-${tag}`" class="cm-tag">{{ tag }}</span>
                </div>
                <details v-if="rawTags(detail.data).length" class="cm-tags-raw">
                  <summary>查看原文标签（{{ rawTags(detail.data).length }}）</summary>
                  <p class="cm-tags-raw-line">{{ rawTags(detail.data).join('、') }}</p>
                </details>
              </template>

              <EmptyState v-else icon="☞" title="选择一个候选" hint="查看大封面与完整简介" />
            </aside>
          </div>
        </div>
      </template>

      <!-- 底部动作 -->
      <div class="cm-foot">
        <div class="cm-foot-actions">
          <button class="btn" type="button" @click="emit('close')">取消</button>
          <button
            class="btn btn-primary"
            type="button"
            :data-loading="applying"
            :disabled="!canApply"
            :title="mode === 'resolve' && resolved ? '确认无误后写入' : ''"
            @click="apply"
          >
            {{ applying ? '应用中…' : mode === 'resolve' ? '确认无误，应用匹配' : '应用匹配' }}
          </button>
        </div>
      </div>
    </div>
  </Modal>
</template>

<style scoped>
.cm-root {
  display: flex;
  flex-direction: column;
  gap: var(--s-3);
}

/* ---- 模式切换 ---- */
.cm-modes {
  display: flex;
  gap: var(--s-1);
  border-bottom: 1px solid var(--line-1);
}

.cm-mode {
  padding: 8px 14px;
  background: none;
  border: none;
  border-bottom: 2px solid transparent;
  color: var(--text-1);
  font-family: inherit;
  font-size: 13px;
  font-weight: 550;
  cursor: pointer;
  transition: color var(--t-fast), border-color var(--t-fast);
}

.cm-mode:hover {
  color: var(--text-0);
}

.cm-mode[data-active='true'] {
  color: var(--accent-strong);
  border-bottom-color: var(--accent);
}

/* ---- 链接解析 ---- */
.cm-resolve-row {
  display: flex;
  gap: var(--s-2);
}

.cm-resolve-row .input {
  flex: 1;
  min-width: 0;
}

.cm-resolve-row .btn {
  flex-shrink: 0;
  min-width: 84px;
}

.cm-hint {
  margin: 0;
  font-size: 12px;
  color: var(--text-2);
}

.cm-error {
  padding: 10px 14px;
  border: 1px solid color-mix(in srgb, var(--err) 40%, transparent);
  background: var(--err-soft);
  border-radius: var(--radius-s);
  color: var(--err);
  font-size: 13px;
}

.cm-loading {
  padding: 40px 0;
}

/* 预览 */
.cm-preview {
  display: flex;
  gap: var(--s-4);
  padding: var(--s-4);
  background: var(--bg-inset);
  border: 1px solid var(--accent-line);
  border-radius: var(--radius-m);
  animation: cm-in 0.22s var(--t-med);
}

@keyframes cm-in {
  from {
    opacity: 0;
    transform: translateY(6px);
  }
}

.cm-preview-cover {
  flex-shrink: 0;
  width: 130px;
  height: 182px;
  border-radius: var(--radius-s);
  overflow: hidden;
  display: grid;
  place-items: center;
  background: var(--bg-3);
  border: 1px solid var(--line-1);
}

.cm-preview-cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.cm-cand-fallback {
  color: var(--text-2);
  font-size: 14px;
}

.cm-preview-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.cm-preview-head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.badge--source {
  color: var(--accent-strong);
  background: var(--accent-soft);
  border-color: transparent;
}

/* E-Hentai 徽章沿用其独立色相（info），与 Tab 一致 */
.badge--source[data-source='ehentai'],
.badge--source[data-source='EH'] {
  color: var(--info);
  background: var(--info-soft);
}

.cm-preview-title {
  margin: 2px 0 0;
  font-size: 16px;
  font-weight: 650;
  word-break: break-word;
}

.cm-preview-sub {
  margin: 0;
  font-size: 12.5px;
  color: var(--text-2);
  word-break: break-word;
}

.cm-preview-summary {
  margin: 4px 0 0;
  font-size: 13px;
  color: var(--text-1);
  line-height: 1.7;
  white-space: pre-wrap;
  word-break: break-word;
  display: -webkit-box;
  -webkit-line-clamp: 6;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.cm-preview-note {
  margin: auto 0 0;
  padding-top: var(--s-2);
  font-size: 12px;
  color: var(--text-2);
}

/* ---- 候选搜索 ---- */
.cm-search {
  display: flex;
  align-items: center;
  gap: var(--s-3);
}

.cm-tabs {
  display: flex;
  gap: var(--s-1);
  flex-wrap: wrap;
  border-bottom: 1px solid var(--line-1);
}

.cm-tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 8px 12px;
  background: none;
  border: none;
  border-bottom: 2px solid transparent;
  color: var(--text-1);
  font-family: inherit;
  font-size: 13px;
  font-weight: 550;
  cursor: pointer;
  transition: color var(--t-fast), border-color var(--t-fast);
}

.cm-tab:hover {
  color: var(--text-0);
}

.cm-tab[data-active='true'] {
  color: var(--accent-strong);
  border-bottom-color: var(--accent);
}

/* E-Hentai 用独立色相，与其余源（accent 朱红）区分 */
.cm-tab[data-bg='eh'] {
  color: var(--info);
}

.cm-tab[data-bg='eh'][data-active='true'] {
  color: var(--info);
  border-bottom-color: var(--info);
}

.cm-tab-count {
  font-size: 11px;
  color: var(--text-2);
  background: var(--bg-3);
  border-radius: var(--radius-full);
  padding: 2px 6px;
}

.cm-columns {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
  gap: var(--s-4);
  min-height: 320px;
  max-height: 52vh;
}

.cm-list {
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: var(--s-2);
  padding-right: 4px;
  min-width: 0;
}

.cm-cand {
  display: flex;
  gap: 12px;
  align-items: flex-start;
  padding: 10px;
  text-align: left;
  background: var(--bg-3);
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
  cursor: pointer;
  font-family: inherit;
  transition: border-color var(--t-fast), background-color var(--t-fast);
}

.cm-cand:hover {
  border-color: var(--line-2);
}

.cm-cand[data-selected='true'] {
  border-color: var(--accent-line);
  background: var(--accent-soft);
}

.cm-cand-cover {
  flex-shrink: 0;
  width: 42px;
  height: 58px;
  border-radius: var(--radius-s);
  background: var(--bg-inset);
  overflow: hidden;
  display: grid;
  place-items: center;
}

.cm-cand-cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.cm-cand-text {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.cm-cand-title {
  font-size: 13.5px;
  font-weight: 550;
  color: var(--text-0);
  word-break: break-word;
}

.cm-cand-sub {
  font-size: 12px;
  color: var(--text-2);
  word-break: break-word;
}

.cm-empty {
  color: var(--text-2);
  font-size: 13px;
  text-align: center;
  padding: 24px 0;
}

.cm-detail {
  overflow-y: auto;
  background: var(--bg-inset);
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
  padding: var(--s-4);
  display: flex;
  flex-direction: column;
  gap: var(--s-2);
  min-width: 0;
}

.cm-detail-loading {
  padding: 40px 0;
}

.cm-detail-error {
  color: var(--err);
  font-size: 13px;
}

.cm-detail-cover {
  width: 120px;
  height: 168px;
  border-radius: var(--radius-s);
  overflow: hidden;
  display: grid;
  place-items: center;
  background: var(--bg-3);
  margin-bottom: var(--s-2);
}

.cm-detail-cover img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.cm-detail-title {
  font-size: 15px;
  font-weight: 650;
  word-break: break-word;
}

.cm-detail-sub {
  font-size: 12.5px;
  color: var(--text-2);
  word-break: break-word;
}

.cm-detail-summary {
  font-size: 13px;
  color: var(--text-1);
  line-height: 1.7;
  white-space: pre-wrap;
  word-break: break-word;
}

/* ---- E-Hentai 标签：中文 chips + 原文折叠（候选详情 / 链接预览两处共用） ---- */
.cm-tags {
  display: flex;
  flex-wrap: wrap;
  gap: var(--s-1);
  margin-top: var(--s-2);
}

.cm-tag {
  display: inline-flex;
  align-items: center;
  padding: 2px 8px;
  border-radius: var(--radius-full);
  border: 1px solid transparent;
  background: var(--info-soft);
  color: var(--info);
  font-size: 11.5px;
  line-height: 1.7;
  word-break: break-word;
}

.cm-tags-raw {
  margin-top: var(--s-1);
  font-size: 11.5px;
  color: var(--text-2);
}

.cm-tags-raw > summary {
  cursor: pointer;
  color: var(--text-2);
  width: fit-content;
}

.cm-tags-raw-line {
  margin: var(--s-1) 0 0;
  font-family: var(--font-mono);
  color: var(--text-2);
  line-height: 1.6;
  word-break: break-word;
}

/* ---- 底部 ---- */
.cm-foot {
  display: flex;
  align-items: center;
  padding-top: var(--s-3);
  border-top: 1px solid var(--line-1);
}

.cm-foot-actions {
  display: flex;
  gap: var(--s-2);
  margin-left: auto;
}

@media (max-width: 760px) {
  .cm-columns {
    grid-template-columns: 1fr;
    max-height: 60vh;
  }

  .cm-detail {
    min-height: 200px;
  }

  .cm-preview {
    flex-direction: column;
  }

  .cm-preview-cover {
    width: 108px;
    height: 152px;
  }
}
</style>
