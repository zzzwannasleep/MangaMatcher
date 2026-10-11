<script setup>
import { ref, computed, onMounted, onBeforeUnmount } from 'vue'
import { api } from '../api/client'
import { notify } from '../stores/toast'
import { formatDateTime, ITEM_STATUS_TEXT } from '../utils/labels'
import EmptyState from '../components/ui/EmptyState.vue'
import ProgressBar from '../components/ui/ProgressBar.vue'
import ChangeList from '../components/ui/ChangeList.vue'

const props = defineProps({
  id: { type: String, required: true },
})

const job = ref(null)
const loading = ref(true)
const loadError = ref('')
let timer = null

const running = computed(() => job.value?.status === 'running')
const isDryRun = computed(() => !!job.value?.dryRun)

const manualCount = computed(() => (job.value?.items || []).filter((i) => i.status === 'manual').length)

/* 数据源标签（SeriesItem.source：Bgm|Bw|Mox|MangaDex|Baike|bangumi|bookwalker…） */
function sourceLabel(source) {
  const map = {
    Bgm: 'Bangumi', bangumi: 'Bangumi',
    Bw: 'BookWalker', bookwalker: 'BookWalker',
    Mox: 'Mox', mangadex: 'MangaDex', MangaDex: 'MangaDex',
    Baike: '百度百科', baike: '百度百科',
  }
  return map[source] || source
}

/* 字段更新计数（系列级 + 卷级合并的 changed map） */
function changeCount(item) {
  return Object.keys(item?.changed || {}).length
}

async function load(silent = false) {
  try {
    job.value = await api(`/api/jobs/${props.id}`)
    loadError.value = ''
  } catch (err) {
    loadError.value = err.message
    if (!silent) notify.error(err.message)
  } finally {
    loading.value = false
  }
  if (running.value) {
    stopTimer()
    timer = setTimeout(() => load(true), 1500)
  }
}

function stopTimer() {
  if (timer) {
    clearTimeout(timer)
    timer = null
  }
}

async function cancel() {
  try {
    await api(`/api/jobs/${props.id}/cancel`, { method: 'POST' })
    notify.info('已请求取消任务')
    await load()
  } catch (err) {
    notify.error(err.message)
  }
}

onMounted(() => load())
onBeforeUnmount(stopTimer)
</script>

<template>
  <div>
    <header class="page-head">
      <div>
        <RouterLink to="/jobs" class="crumb">← 任务中心</RouterLink>
        <h1 class="page-title num">任务 {{ id.slice(0, 8) }}</h1>
        <p class="page-sub num">开始于 {{ formatDateTime(job?.startedAt) }}<template v-if="job?.finishedAt"> · 结束于 {{ formatDateTime(job.finishedAt) }}</template></p>
      </div>
      <div class="page-actions">
        <span v-if="job" class="badge" :data-kind="job.status">
          {{ job.status === 'running' ? '运行中' : job.status === 'done' ? '完成' : '已取消' }}
        </span>
        <span v-if="job" class="badge badge--kind" :data-kindtype="job.kind === 'tag' ? 'tag' : 'scrape'">
          {{ job.kind === 'tag' ? '打标任务' : '刮削任务' }}
        </span>
        <span v-if="job" class="badge" :class="{ 'badge--dry': isDryRun }">
          {{ isDryRun ? 'Dry-run 预览' : job.kind === 'tag' ? '执行打标' : '执行刮削' }}
        </span>
        <button
          v-if="running"
          class="btn btn-danger"
          type="button"
          @click="cancel"
        >
          取消任务
        </button>
      </div>
    </header>

    <div v-if="loading" class="loading-block">
      <span class="spinner" aria-hidden="true"></span>
      正在加载任务…
    </div>

    <EmptyState v-else-if="loadError" icon="⚠" title="加载失败" :hint="loadError">
      <button class="btn" type="button" @click="load()">重试</button>
    </EmptyState>

    <template v-else-if="job">
      <div class="card card-pad job-summary">
        <div class="job-progress">
          <ProgressBar :done="job.done" :total="job.total" :status="job.status" />
        </div>
        <div class="job-stats num">
          <span class="stat stat-ok"><i>✓</i> 成功 {{ job.ok ?? 0 }}</span>
          <span class="stat stat-err"><i>✕</i> 失败 {{ job.failed ?? 0 }}</span>
          <span class="stat stat-manual"><i>⊙</i> 待手动 {{ manualCount }}</span>
          <span class="stat stat-total">共 {{ job.total }} 项</span>
        </div>
      </div>

      <div v-if="!job.items?.length" class="loading-block">
        <span class="spinner" aria-hidden="true"></span>
        等待任务产出结果…
      </div>

      <div v-else class="item-cards">
        <article v-for="item in job.items" :key="item.seriesId" class="item-card" :data-status="item.status">
          <div class="ic-head">
            <span class="ic-name">{{ item.seriesName || item.seriesId }}</span>
            <span class="badge" :data-kind="item.status">
              {{ ITEM_STATUS_TEXT[item.status] || item.status }}
            </span>
          </div>

          <!-- 匹配产物：用了哪个数据源 / 匹配到哪 / Bangumi 直链 -->
          <div class="ic-meta">
            <template v-if="item.source || item.matchTitle || item.subjectId">
              <span v-if="item.source" class="badge badge--source">
                <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><circle cx="12" cy="12" r="10"/><path d="M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20"/><path d="M2 12h20"/></svg>
                {{ sourceLabel(item.source) }}
              </span>
              <span v-if="item.matchTitle" class="ic-match">匹配「{{ item.matchTitle }}」</span>
              <a
                v-if="item.subjectId"
                class="ic-bglink num"
                :href="`https://bgm.tv/subject/${item.subjectId}`"
                target="_blank"
                rel="noopener"
                title="在 Bangumi 打开该条目"
              >BG#{{ item.subjectId }} ↗</a>
            </template>
            <span v-else class="text-dim">无匹配信息</span>
          </div>

          <p v-if="item.message" class="ic-msg" :data-kind="item.status">{{ item.message }}</p>

          <!-- 刮削差异：改了什么（系列级 + 卷级合并） -->
          <details v-if="changeCount(item)" class="ic-changes">
            <summary>
              <span class="ic-changes-count num">{{ changeCount(item) }}</span> 项字段更新
              <svg class="ic-caret" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><polyline points="6 9 12 15 18 9"/></svg>
            </summary>
            <div class="ic-changes-body">
              <ChangeList :changed="item.changed" />
            </div>
          </details>
        </article>
      </div>
    </template>
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

.badge--kind {
  color: var(--info);
  background: var(--info-soft);
  border-color: transparent;
}

.badge--kind[data-kindtype='tag'] {
  color: var(--accent-strong);
  background: var(--accent-soft);
  border-color: transparent;
}

.job-summary {
  margin-bottom: var(--s-4);
}

.job-progress + .job-stats {
  margin-top: 16px;
}

.job-stats {
  display: flex;
  align-items: center;
  gap: 20px;
  flex-wrap: wrap;
  font-size: 13px;
  color: var(--text-1);
}

.stat {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.stat i {
  font-style: normal;
  font-size: 12px;
}

.stat-ok i {
  color: var(--ok);
}

.stat-err i {
  color: var(--err);
}

.stat-manual i {
  color: var(--manual);
}

.stat-total {
  color: var(--text-2);
}

/* ---- 任务条目卡片墙 ---- */
.item-cards {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(380px, 1fr));
  gap: 14px;
  align-items: start;
}

.item-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 15px 18px;
  background: var(--bg-2);
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
  transition: border-color var(--t-fast);
}

.item-card:hover {
  border-color: var(--line-2);
}

.item-card[data-status='ok'] {
  border-left: 3px solid var(--ok);
}

.item-card[data-status='failed'] {
  border-left: 3px solid var(--err);
}

.item-card[data-status='manual'] {
  border-left: 3px solid var(--manual);
}

.item-card[data-status='skipped'] {
  border-left: 3px solid var(--line-2);
}

.ic-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 10px;
}

.ic-name {
  font-size: 13.5px;
  font-weight: 600;
  line-height: 1.45;
  word-break: break-word;
}

.ic-meta {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  font-size: 12.5px;
}

.badge--source {
  color: var(--info);
  background: var(--info-soft);
  border-color: transparent;
  display: inline-flex;
  align-items: center;
  gap: 5px;
}

.ic-match {
  color: var(--text-1);
  word-break: break-word;
}

.ic-bglink {
  color: var(--accent-strong);
  text-decoration: none;
  font-family: var(--font-mono);
  font-size: 12px;
  white-space: nowrap;
}

.ic-bglink:hover {
  text-decoration: underline;
}

.ic-msg {
  margin: 0;
  font-size: 12.5px;
  color: var(--text-2);
  line-height: 1.6;
  word-break: break-word;
  padding: 8px 12px;
  background: var(--bg-inset);
  border-radius: var(--radius-s);
}

.ic-msg[data-kind='failed'] {
  color: var(--err);
  background: var(--err-soft);
}

/* 差异折叠区 */
.ic-changes {
  border-top: 1px dashed var(--line-1);
  padding-top: 8px;
}

.ic-changes summary {
  display: flex;
  align-items: center;
  gap: 6px;
  cursor: pointer;
  user-select: none;
  list-style: none;
  font-size: 12.5px;
  color: var(--text-1);
  font-weight: 550;
}

.ic-changes summary::-webkit-details-marker {
  display: none;
}

.ic-changes summary:hover {
  color: var(--text-0);
}

.ic-changes-count {
  display: inline-grid;
  place-items: center;
  min-width: 20px;
  height: 18px;
  padding: 0 5px;
  border-radius: 99px;
  background: var(--accent-soft);
  color: var(--accent-strong);
  font-size: 11px;
}

.ic-caret {
  color: var(--text-2);
  transition: transform var(--t-fast);
}

.ic-changes[open] .ic-caret {
  transform: rotate(180deg);
}

.ic-changes-body {
  padding: 8px 2px 0;
}

.text-dim {
  color: var(--text-2);
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

.badge[data-kind='ok'] {
  color: var(--ok);
  background: var(--ok-soft);
  border-color: transparent;
}

.badge[data-kind='failed'] {
  color: var(--err);
  background: var(--err-soft);
  border-color: transparent;
}

.badge[data-kind='manual'] {
  color: var(--manual);
  background: var(--manual-soft);
  border-color: transparent;
}

.badge[data-kind='skipped'] {
  color: var(--text-2);
  background: var(--bg-3);
  border-color: transparent;
}
</style>