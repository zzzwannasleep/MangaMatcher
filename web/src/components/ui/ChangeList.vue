<script setup>
import { diffEntries } from '../../utils/labels'

defineProps({
  changed: { type: Object, default: null },
})
</script>

<template>
  <div v-if="diffEntries(changed).length" class="changes">
    <div v-for="entry in diffEntries(changed)" :key="entry.key" class="change-row">
      <span class="change-field">{{ entry.label }}</span>
      <span class="change-diff">
        <s class="change-old">{{ entry.oldText }}</s>
        <span class="change-arrow" aria-hidden="true">→</span>
        <span class="change-new">{{ entry.newText }}</span>
      </span>
    </div>
  </div>
  <span v-else class="changes-none">无差异</span>
</template>

<style scoped>
.changes {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.change-row {
  display: flex;
  align-items: baseline;
  gap: 8px;
  font-size: 12.5px;
  min-width: 0;
}

.change-field {
  flex-shrink: 0;
  color: var(--text-2);
  font-size: 12px;
  min-width: 56px;
}

.change-diff {
  display: flex;
  align-items: baseline;
  gap: 6px;
  min-width: 0;
  flex-wrap: wrap;
}

.change-old {
  color: var(--text-2);
  font-family: var(--font-mono);
  font-size: 11.5px;
  word-break: break-all;
}

.change-arrow {
  color: var(--text-2);
}

.change-new {
  color: var(--accent-strong);
  font-family: var(--font-mono);
  font-size: 11.5px;
  word-break: break-all;
}

.changes-none {
  color: var(--text-2);
  font-size: 12.5px;
}
</style>
