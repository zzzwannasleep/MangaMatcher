<script setup>
defineProps({
  icon: { type: String, default: '◌' },
  title: { type: String, required: true },
  hint: { type: String, default: '' },
})
</script>

<template>
  <div class="empty">
    <div class="empty-icon" aria-hidden="true">{{ icon }}</div>
    <p class="empty-title">{{ title }}</p>
    <p v-if="hint" class="empty-hint">{{ hint }}</p>
    <div v-if="$slots.default" class="empty-actions">
      <slot />
    </div>
  </div>
</template>

<style scoped>
.empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  text-align: center;
  padding: 72px 24px;
  gap: var(--s-2);
}

.empty-icon {
  font-size: 30px;
  line-height: 1;
  color: var(--text-2);
  opacity: 0.7;
  margin-bottom: var(--s-2);
}

.empty-title {
  font-size: 15px;
  font-weight: 550;
  color: var(--text-0);
}

.empty-hint {
  font-size: 13px;
  color: var(--text-2);
  max-width: 360px;
}

.empty-actions {
  margin-top: var(--s-4);
}
</style>
