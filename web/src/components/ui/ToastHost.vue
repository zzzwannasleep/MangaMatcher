<script setup>
import { toasts, dismiss } from '../../stores/toast'

const ICONS = {
  success: '✓',
  error: '✕',
  info: 'ℹ',
}
</script>

<template>
  <div class="toast-region" aria-live="polite">
    <TransitionGroup name="toast">
      <div
        v-for="t in toasts"
        :key="t.id"
        class="toast"
        :data-type="t.type"
        role="status"
        @click="dismiss(t.id)"
      >
        <span class="toast-icon" aria-hidden="true">{{ ICONS[t.type] || ICONS.info }}</span>
        <span class="toast-msg">{{ t.message }}</span>
      </div>
    </TransitionGroup>
  </div>
</template>

<style scoped>
.toast-region {
  position: fixed;
  top: 68px;
  right: 20px;
  z-index: 100;
  display: flex;
  flex-direction: column;
  gap: 10px;
  max-width: min(380px, calc(100vw - 40px));
  pointer-events: none;
}

.toast {
  pointer-events: auto;
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 12px 14px;
  border-radius: var(--radius-m);
  background: var(--bg-3);
  border: 1px solid var(--line-2);
  box-shadow: var(--shadow-2);
  font-size: 13.5px;
  cursor: pointer;
}

.toast-icon {
  flex-shrink: 0;
  width: 18px;
  height: 18px;
  display: grid;
  place-items: center;
  border-radius: 50%;
  font-size: 11px;
  font-weight: 700;
  margin-top: 1px;
}

.toast[data-type='success'] .toast-icon {
  background: var(--ok-soft);
  color: var(--ok);
}

.toast[data-type='error'] .toast-icon {
  background: var(--err-soft);
  color: var(--err);
}

.toast[data-type='info'] .toast-icon {
  background: var(--info-soft);
  color: var(--info);
}

.toast-msg {
  word-break: break-word;
}

.toast-enter-active {
  transition: opacity 0.22s var(--ease-out), transform 0.22s var(--ease-out);
}

.toast-leave-active {
  transition: opacity 0.16s ease, transform 0.16s ease;
}

.toast-enter-from {
  opacity: 0;
  transform: translateX(16px);
}

.toast-leave-to {
  opacity: 0;
  transform: translateY(-6px);
}
</style>
