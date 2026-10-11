<script setup>
import { ref, computed, watch, onMounted } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { api } from '../api/client'
import { notify } from '../stores/toast'
import EmptyState from '../components/ui/EmptyState.vue'
import CorrectMatchModal from '../components/CorrectMatchModal.vue'
import SeriesDetailModal from '../components/SeriesDetailModal.vue'

const route = useRoute()
const router = useRouter()

const q = ref(String(route.query.q || ''))
const results = ref([])
const loading = ref(true)
const loadError = ref('')
const correcting = ref(null) // { id, name }
const detailId = ref('')
const detailEdit = ref(false)

function openEditor(seriesId) {
  detailId.value = seriesId
  detailEdit.value = true
}

/** 搜索结果没有标题缓存：保存后刷新列表 */
function onDetailChanged() {
  load()
}

let timer = null

async function load() {
  const term = q.value.trim()
  if (!term) {
    results.value = []
    loading.value = false
    loadError.value = ''
    return
  }
  loading.value = true
  loadError.value = ''
  try {
    const data = await api(`/api/search?q=${encodeURIComponent(term)}&size=50`)
    results.value = Array.isArray(data) ? data : []
  } catch (err) {
    loadError.value = err.message
    notify.error(err.message)
  } finally {
    loading.value = false
  }
}

function onInput() {
  if (timer) clearTimeout(timer)
  timer = setTimeout(() => {
    const term = q.value.trim()
    router.replace({ path: '/search', query: term ? { q: term } : {} })
  }, 420)
}

watch(
  () => route.query.q,
  (value) => {
    const term = String(value || '')
    if (term !== q.value) q.value = term
    load()
  }
)

watch(q, () => onInput())

function resultTitle(item) {
  return item?.metadata?.title || item?.name || item?.id
}

/** 封面/简介状态图标：…=未记录（后台确认中） */
function stateIcon(state, key) {
  if (!state) return '…'
  return state[key] ? '✓' : '✗'
}

function onApplied(jobId) {
  correcting.value = null
  notify.success('已提交匹配应用任务')
  if (jobId) router.push(`/jobs/${jobId}`)
}

onMounted(() => load())
</script>

<template>
  <div>
    <header class="page-head">
      <div>
        <h1 class="page-title">搜索</h1>
        <p class="page-sub">跨库搜索系列名称 / 原名</p>
      </div>
    </header>

    <div class="search-bar">
      <input
        v-model="q"
        class="input"
        type="text"
        placeholder="输入关键词…"
        aria-label="全局搜索"
        autofocus
      />
    </div>

    <div v-if="loading && q.trim()" class="loading-block">
      <span class="spinner" aria-hidden="true"></span>
      正在搜索…
    </div>

    <EmptyState
      v-else-if="!q.trim()"
      icon="⌕"
      title="输入关键词开始搜索"
      hint="在所有你有权限的库中查找系列"
    />

    <EmptyState v-else-if="loadError" icon="⚠" title="搜索失败" :hint="loadError">
      <button class="btn" type="button" @click="load">重试</button>
    </EmptyState>

    <EmptyState
      v-else-if="!results.length"
      icon="◌"
      title="没有匹配的系列"
      :hint="`没有找到包含「${q.trim()}」的系列`"
    />

    <div v-else class="result-list">
      <p class="result-count num">{{ results.length }} 个结果</p>
      <div v-for="item in results" :key="item.id" class="result-row lift">
        <span class="lib-badge" :title="`库：${item.libraryName || item.libraryId}`">
          {{ item.libraryName || item.libraryId }}
        </span>
        <span class="result-name">{{ resultTitle(item) }}</span>
        <span class="state-icons" aria-label="刮削状态">
          <span class="state-chip" :data-state="stateIcon(item.state, 'hasCover')" title="封面状态">封 {{ stateIcon(item.state, 'hasCover') }}</span>
          <span class="state-chip" :data-state="stateIcon(item.state, 'hasSummary')" title="简介状态">简 {{ stateIcon(item.state, 'hasSummary') }}</span>
        </span>
        <span class="badge num" v-if="item.booksCount != null">{{ item.booksCount }} 册</span>
        <span class="row-actions">
          <button class="btn btn-sm" type="button" @click="openEditor(item.id)">编辑</button>
          <button class="btn btn-sm" type="button" @click="correcting = { id: item.id, name: resultTitle(item) }">
            纠正匹配
          </button>
        </span>
      </div>
    </div>

    <SeriesDetailModal
      v-if="detailId"
      :series-id="detailId"
      :start-edit="detailEdit"
      @close="detailId = ''"
      @correct="correcting = { id: detailId, name: resultTitle(results.find((x) => x.id === detailId) || {}) }; detailId = ''"
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
.search-bar {
  margin-bottom: var(--s-4);
}

.result-count {
  font-size: 12.5px;
  color: var(--text-2);
  margin-bottom: var(--s-2);
}

.result-list {
  display: flex;
  flex-direction: column;
  gap: var(--s-2);
}

.result-row {
  display: flex;
  align-items: center;
  gap: var(--s-3);
  padding: 10px 16px;
  background: var(--bg-2);
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
}

.result-row:hover {
  border-color: var(--line-2);
}

.lib-badge {
  flex-shrink: 0;
  max-width: 140px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11.5px;
  padding: 3px 9px;
  border-radius: var(--radius-full);
  background: var(--bg-3);
  border: 1px solid var(--line-1);
  color: var(--text-1);
}

.result-name {
  flex: 1;
  min-width: 0;
  font-size: 14px;
  font-weight: 550;
  word-break: break-word;
}

.state-icons {
  display: flex;
  gap: 6px;
  flex-shrink: 0;
}

.state-chip {
  font-size: 11px;
  line-height: 1;
  padding: 4px 7px;
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

.row-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-shrink: 0;
}
</style>
