<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { api } from '../api/client'
import { notify } from '../stores/toast'
import { formatDateTime } from '../utils/labels'
import EmptyState from '../components/ui/EmptyState.vue'
import ProgressBar from '../components/ui/ProgressBar.vue'

const jobs = ref([])
const libraries = ref([])
const loading = ref(true)
const loadError = ref('')
let timer = null

const libNameMap = computed(() => {
  const map = {}
  libraries.value.forEach((l) => {
    map[l.id] = l.name
  })
  return map
})

function libName(id) {
  return libNameMap.value[id] || id
}

const anyRunning = computed(() => jobs.value.some((j) => j.status === 'running'))

const sortedJobs = computed(() =>
  [...jobs.value].sort((a, b) => {
    const ta = a.startedAt ? new Date(a.startedAt).getTime() : 0
    const tb = b.startedAt ? new Date(b.startedAt).getTime() : 0
    return tb - ta
  })
)

async function load() {
  try {
    const [jobList, libList] = await Promise.all([api('/api/jobs'), api('/api/libraries')])
    jobs.value = Array.isArray(jobList) ? jobList : []
    libraries.value = Array.isArray(libList) ? libList : []
    loadError.value = ''
  } catch (err) {
    loadError.value = err.message
    notify.error(err.message)
  } finally {
    loading.value = false
  }
}

function schedule() {
  stopTimer()
  timer = setInterval(async () => {
    if (anyRunning.value) {
      try {
        jobs.value = await api('/api/jobs')
      } catch {
        /* 轮询失败不打断页面 */
      }
    }
  }, 2500)
}

function stopTimer() {
  if (timer) {
    clearInterval(timer)
    timer = null
  }
}

async function cancel(job) {
  try {
    await api(`/api/jobs/${job.id}/cancel`, { method: 'POST' })
    notify.info('已请求取消任务')
    await load()
  } catch (err) {
    notify.error(err.message)
  }
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
        <h1 class="page-title">任务中心</h1>
        <p class="page-sub">刮削任务进度与结果 · 运行中任务自动轮询刷新</p>
      </div>
    </header>

    <div v-if="loading" class="loading-block">
      <span class="spinner" aria-hidden="true"></span>
      正在加载任务…
    </div>

    <EmptyState v-else-if="loadError" icon="⚠" title="加载失败" :hint="loadError">
      <button class="btn" type="button" @click="load">重试</button>
    </EmptyState>

    <EmptyState
      v-else-if="!jobs.length"
      icon="▷"
      title="还没有任务"
      hint="到「图书馆」选择系列，发起刮削或 Dry-run 预览"
    >
      <RouterLink to="/" class="btn btn-primary">去图书馆</RouterLink>
    </EmptyState>

    <div v-else class="job-cards">
      <article v-for="job in sortedJobs" :key="job.id" class="job-card" :data-status="job.status">
        <div class="jc-head">
          <div class="jc-badges">
            <span class="badge" :data-kind="job.status">
              {{ job.status === 'running' ? '运行中' : job.status === 'done' ? '完成' : '已取消' }}
            </span>
            <span class="badge badge--kind" :data-kindtype="job.kind === 'tag' ? 'tag' : 'scrape'">
              {{ job.kind === 'tag' ? '打标' : '刮削' }}
            </span>
            <span v-if="job.dryRun" class="badge badge--dry">Dry-run</span>
          </div>
          <span class="jc-time num">{{ formatDateTime(job.startedAt) }}</span>
        </div>

        <RouterLink :to="`/jobs/${job.id}`" class="jc-title num">{{ job.id }}</RouterLink>
        <p class="jc-lib">
          <svg viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d="m16 6 4 14"/><path d="M12 6v14"/><path d="M8 8v12"/><path d="M4 4v16"/></svg>
          {{ libName(job.libraryId) }}
        </p>

        <div class="jc-progress">
          <ProgressBar :done="job.done" :total="job.total" :status="job.status" />
        </div>

        <div class="jc-foot">
          <span class="jc-stats num">
            <span class="result-ok">✓ {{ job.ok ?? 0 }}</span>
            <span class="result-failed">✕ {{ job.failed ?? 0 }}</span>
          </span>
          <span class="jc-actions">
            <RouterLink :to="`/jobs/${job.id}`" class="btn btn-ghost btn-sm">详情</RouterLink>
            <button v-if="job.status === 'running'" class="btn btn-danger btn-sm" type="button" @click="cancel(job)">
              取消
            </button>
          </span>
        </div>
      </article>
    </div>
  </div>
</template>

<style scoped>
/* ---- 任务卡片墙 ---- */
.job-cards {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(340px, 1fr));
  gap: 14px;
  align-items: start;
}

.job-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 16px 18px;
  background: var(--bg-2);
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
  transition: border-color var(--t-fast), box-shadow var(--t-fast);
}

.job-card:hover {
  border-color: var(--line-2);
  box-shadow: var(--shadow-1);
}

.job-card[data-status='running'] {
  border-color: color-mix(in srgb, var(--warn) 35%, transparent);
}

.jc-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.jc-badges {
  display: flex;
  gap: 6px;
  flex-wrap: wrap;
}

.jc-time {
  font-size: 12px;
  color: var(--text-2);
  white-space: nowrap;
}

.jc-title {
  font-size: 14px;
  font-weight: 650;
  color: var(--text-0);
  text-decoration: none;
  word-break: break-all;
  font-family: var(--font-mono);
}

.jc-title:hover {
  color: var(--accent-strong);
}

.jc-lib {
  margin: 0;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 12.5px;
  color: var(--text-2);
}

.jc-progress {
  display: flex;
  align-items: center;
  gap: 10px;
}

.jc-progress :deep(.progress) {
  flex: 1;
  min-width: 0;
}

.jc-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
}

.jc-stats {
  display: flex;
  gap: 12px;
  font-size: 13px;
}

.jc-actions {
  display: flex;
  gap: 8px;
}

.result-ok {
  color: var(--ok);
}

.result-failed {
  color: var(--err);
}

.badge[data-kind='done'] {
  color: var(--ok);
  background: var(--ok-soft);
  border-color: transparent;
}

.badge[data-kind='running'] {
  color: var(--warn);
  background: var(--warn-soft);
  border-color: transparent;
}

.badge[data-kind='canceled'] {
  color: var(--text-2);
  background: var(--bg-3);
  border-color: transparent;
}

.badge--kind[data-kindtype='tag'] {
  color: var(--accent-strong);
  background: var(--accent-soft);
  border-color: transparent;
}

.badge--kind[data-kindtype='scrape'] {
  color: var(--info);
  background: var(--info-soft);
  border-color: transparent;
}

@media (max-width: 640px) {
  .job-cards {
    grid-template-columns: 1fr;
  }
}
</style>