import { reactive } from 'vue'

let uid = 0
export const toasts = reactive([])

export function toast(message, type = 'info', duration = 3800) {
  const id = ++uid
  toasts.push({ id, message, type })
  setTimeout(() => dismiss(id), duration)
  return id
}

export function dismiss(id) {
  const index = toasts.findIndex((t) => t.id === id)
  if (index !== -1) toasts.splice(index, 1)
}

export const notify = {
  success(message) {
    toast(message, 'success')
  },
  error(message) {
    toast(message, 'error', 5200)
  },
  info(message) {
    toast(message, 'info')
  },
}
