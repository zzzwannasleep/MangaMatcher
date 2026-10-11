<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { api } from '../api/client'
import { notify } from '../stores/toast'
import EmptyState from '../components/ui/EmptyState.vue'

const LINES_OPTIONS = [200, 500, 1000, 2000]

const lines = ref(500)
const logText = ref('')
const loading = ref(true)
const loadError = ref('')
const filterQ = ref('')
const autoRefresh = ref(false)
let timer = null

const filteredLines = computed(() => {
  const all = logText.value.split('\n')
  const q = filterQ.value.trim().toLowerCase()
  if (!q) return all
  return all.filter((l) => l.toLowerCase().includes(q))
})

const errorCount = computed(() =>
  logText.value.split('\n').filter((l) => / level=ERROR|\"level\":\"error\"|\bERROR\b/.test(l)).length
)

const warnCount = computed(() =>
  logText.value.split('\n').filter((l) => / level=WARN|\"level\":\"warn\"|\bWARN\b/.test(l)).length
)

async function load(silent = false) {
  if (!silent) loading.value = true
  try {
    logText.value = await api(`/api/logs?lines=${lines.value}`)
    loadError.value = ''
  } catch (err) {
    loadError.value = err.message
    if (!silent) notify.error(err.message)
  } finally {
    loading.value = false
  }
}

function schedule() {
  stopTimer()
  timer = setInterval(() => {
    if (autoRefresh.value && document.visibilityState === 'visible') load(true)
  }, 3000)
}

function stopTimer() {
  if (timer) {
    clearInterval(timer)
    timer = null
  }
}

function downloadLog() {
  const blob = new Blob([logText.value], { type: 'text/plain;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  const d = new Date()
  const pad = (n) => String(n).padStart(2, '0')
  anchor.download = `komgabangumi-log-${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}${pad(d.getSeconds())}.txt`
  anchor.href = url
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(url)
  notify.success('日志已导出')
}

async function copyLog() {
  try {
    await navigator.clipboard.writeText(logText.value)
    notify.success('日志已复制到剪贴板')
  } catch {
    notify.error('复制失败（浏览器拒绝剪贴板访问），请用导出')
  }
}

const logBox = ref(null)

function scrollToBottom() {
  const el = logBox.value
  if (el) el.scrollTop = el.scrollHeight
}

onMounted(async () => {
  await load()
  schedule()
})

onBeforeUnmount(stopTimer)
</script>

<template>
  <div>
    <header class="page-head">
      <div>
        <h1 class="page-title">系统日志</h1>
        <p class="page-sub">
          今日服务端日志（最近 {{ lines }} 行）
          <template v-if="errorCount || warnCount">
            · <span class="log-stat log-err">ERROR {{ errorCount }}</span>
            · <span class="log-stat log-warn">WARN {{ warnCount }}</span>
          </template>
        </p>
      </div>
      <div class="page-actions">
        <select v-model.number="lines" class="select log-lines" aria-label="日志行数" @change="load()">
          <option v-for="n in LINES_OPTIONS" :key="n" :value="n">{{ n }} 行</option>
        </select>
        <input
          v-model="filterQ"
          class="input log-filter"
          type="search"
          placeholder="过滤关键字…"
          aria-label="过滤日志"
        />
        <label class="log-auto" title="每 3 秒自动刷新">
          <input type="checkbox" v-model="autoRefresh" />
          自动刷新
        </label>
        <button class="btn btn-sm" type="button" :disabled="loading" @click="load()">刷新</button>
        <button class="btn btn-sm" type="button" @click="copyLog">复制</button>
        <button class="btn btn-primary btn-sm" type="button" :disabled="!logText" @click="downloadLog">导出</button>
      </div>
    </header>

    <div v-if="loading" class="loading-block">
      <span class="spinner" aria-hidden="true"></span>
      正在读取日志…
    </div>

    <EmptyState v-else-if="loadError" icon="⚠" title="日志读取失败" :hint="loadError">
      <button class="btn" type="button" @click="load()">重试</button>
    </EmptyState>

    <EmptyState v-else-if="!logText.trim()" icon="≡" title="今天还没有日志" hint="服务运行中产生日志后会出现在这里" />

    <template v-else>
      <p class="log-meta num" v-if="filterQ.trim()">
        匹配 {{ filteredLines.length }} / {{ logText.split('\n').length }} 行
      </p>
      <div ref="logBox" class="log-box">
        <pre class="log-pre">{{ filteredLines.join('\n') }}</pre>
      </div>
    </template>
  </div>
</template>

<style scoped>
.log-lines {
  width: auto;
  min-width: 92px;
}

.log-filter {
  width: 180px;
}

.log-auto {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
  color: var(--text-1);
  white-space: nowrap;
  cursor: pointer;
}

.log-stat {
  font-weight: 600;
}

.log-err {
  color: var(--err);
}

.log-warn {
  color: var(--warn);
}

.log-meta {
  margin: 0 0 8px;
  font-size: 12px;
  color: var(--text-2);
}

.log-box {
  background: var(--bg-inset);
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
  overflow: auto;
  max-height: calc(100vh - 220px);
  min-height: 320px;
}

.log-pre {
  margin: 0;
  padding: 14px 16px;
  font-family: var(--font-mono);
  font-size: 12px;
  line-height: 1.65;
  color: var(--text-1);
  white-space: pre-wrap;
  word-break: break-all;
}

@media (max-width: 760px) {
  .page-actions {
    width: 100%;
  }

  .log-filter {
    flex: 1;
    width: auto;
  }
}
</style>
