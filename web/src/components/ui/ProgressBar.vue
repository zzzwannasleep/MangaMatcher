<script setup>
import { computed } from 'vue'

const props = defineProps({
  done: { type: Number, default: 0 },
  total: { type: Number, default: 0 },
  status: { type: String, default: 'running' }, // running | done | canceled
})

const percent = computed(() => {
  if (!props.total) return 0
  return Math.min(100, Math.round((props.done / props.total) * 100))
})
</script>

<template>
  <div
    class="progress"
    :data-status="status"
    role="progressbar"
    :aria-valuenow="percent"
    aria-valuemin="0"
    aria-valuemax="100"
  >
    <div class="progress-track">
      <div
        class="progress-fill"
        :style="{ width: percent + '%' }"
      ></div>
    </div>
    <span class="progress-label num">{{ done }}/{{ total }} · {{ percent }}%</span>
  </div>
</template>

<style scoped>
.progress {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
}

.progress-track {
  flex: 1;
  height: 6px;
  border-radius: var(--radius-full);
  background: var(--bg-inset);
  border: 1px solid var(--line-1);
  overflow: hidden;
  min-width: 60px;
}

.progress-fill {
  height: 100%;
  border-radius: var(--radius-full);
  background: var(--accent);
  transition: width 0.5s var(--ease-out);
  position: relative;
  overflow: hidden;
}

/* 流动感：仅 running 状态 */
.progress[data-status='running'] .progress-fill::after {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(
    100deg,
    transparent 20%,
    rgba(255, 255, 255, 0.35) 50%,
    transparent 80%
  );
  transform: translateX(-100%);
  animation: flow 1.6s ease-in-out infinite;
}

@keyframes flow {
  to {
    transform: translateX(100%);
  }
}

.progress[data-status='done'] .progress-fill {
  background: var(--ok);
}

.progress[data-status='canceled'] .progress-fill {
  background: var(--text-2);
}

.progress-label {
  font-size: 12px;
  color: var(--text-1);
  white-space: nowrap;
}
</style>
