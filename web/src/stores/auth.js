import { reactive } from 'vue'
import { api, getToken } from '../api/client'

export const auth = reactive({
  user: null, // { id, username, role, libraries }
  loaded: false,
})

export async function fetchMe() {
  if (!getToken()) {
    auth.user = null
    auth.loaded = true
    return null
  }
  try {
    auth.user = await api('/api/me')
  } finally {
    auth.loaded = true
  }
}

export function isAdmin() {
  return auth.user?.role === 'admin'
}
