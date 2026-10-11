<script setup>
import { onMounted, onBeforeUnmount } from 'vue'

defineProps({
  title: { type: String, required: true },
  width: { type: Number, default: 480 },
})

const emit = defineEmits(['close'])

// ESC 关闭：绑在 window 上（backdrop div 无焦点，@keydown 是死代码）
function onKeydown(event) {
  if (event.key === 'Escape') emit('close')
}
onMounted(() => window.addEventListener('keydown', onKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))
</script>

<template>
  <Teleport to="body">
    <Transition name="modal">
      <div class="modal-backdrop" @click.self="emit('close')" @keydown="onKeydown">
        <div class="modal" :style="{ maxWidth: width + 'px' }" role="dialog" aria-modal="true">
          <header class="modal-head">
            <h2 class="modal-title">{{ title }}</h2>
            <button class="btn btn-ghost btn-sm" type="button" aria-label="关闭" @click="emit('close')">✕</button>
          </header>
          <div class="modal-body">
            <slot />
          </div>
        </div>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.modal-backdrop {
  position: fixed;
  inset: 0;
  z-index: 80;
  background: rgba(5, 6, 9, 0.65);
  backdrop-filter: blur(3px);
  display: flex;
  align-items: flex-start;
  justify-content: center;
  padding: 9vh 16px 16px;
  overflow-y: auto;
}

.modal {
  width: 100%;
  background: var(--bg-2);
  border: 1px solid var(--line-2);
  border-radius: var(--radius-l);
  box-shadow: var(--shadow-2);
  overflow: hidden;
}

.modal-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 20px;
  border-bottom: 1px solid var(--line-1);
}

.modal-title {
  font-size: 15.5px;
  font-weight: 650;
}

.modal-body {
  padding: 20px;
}

.modal-enter-active,
.modal-leave-active {
  transition: opacity 0.18s ease;
}

.modal-enter-active .modal,
.modal-leave-active .modal {
  transition: transform 0.2s var(--ease-out);
}

.modal-enter-from,
.modal-leave-to {
  opacity: 0;
}

.modal-enter-from .modal,
.modal-leave-to .modal {
  transform: translateY(10px) scale(0.985);
}
</style>
