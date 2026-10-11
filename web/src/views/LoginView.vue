<script setup>
import { ref, onMounted } from 'vue'
import { useRouter, useRoute } from 'vue-router'
import { api, setToken } from '../api/client'
import { auth } from '../stores/auth'
import { notify } from '../stores/toast'

const router = useRouter()
const route = useRoute()

const username = ref('')
const password = ref('')
const loading = ref(false)
const error = ref('')
const version = ref('')

async function submit() {
  if (!username.value.trim() || !password.value) {
    error.value = '请输入用户名和密码'
    return
  }
  loading.value = true
  error.value = ''
  try {
    const data = await api('/api/auth/login', {
      method: 'POST',
      body: { username: username.value.trim(), password: password.value },
    })
    setToken(data.token)
    auth.user = data.user
    auth.loaded = true
    notify.success(`欢迎回来，${data.user.username}`)
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/'
    router.push(redirect)
  } catch (err) {
    error.value = err.message
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  try {
    const health = await api('/api/health')
    version.value = health?.version || ''
  } catch {
    /* 服务未就绪不阻塞登录 */
  }
})
</script>

<template>
  <div class="login-page">
    <div class="login-card">
      <div class="login-brand">
        <svg class="login-logo" viewBox="0 0 32 32" width="44" height="44" aria-hidden="true">
          <rect x="3" y="4" width="18" height="24" rx="3" fill="var(--accent)" opacity="0.9" />
          <rect x="11" y="4" width="18" height="24" rx="3" fill="var(--bg-2)" stroke="var(--accent)" stroke-width="2" />
        </svg>
        <h1 class="login-title">Komga<em>Bangumi</em></h1>
        <p class="login-sub">漫画元数据刮削 · Docker 控制台</p>
      </div>

      <form class="login-form" @submit.prevent="submit">
        <div class="field">
          <label class="field-label" for="login-username">用户名</label>
          <input
            id="login-username"
            v-model="username"
            class="input"
            type="text"
            autocomplete="username"
            placeholder="admin"
            :aria-invalid="!!error"
            autofocus
          />
        </div>
        <div class="field">
          <label class="field-label" for="login-password">密码</label>
          <input
            id="login-password"
            v-model="password"
            class="input"
            type="password"
            autocomplete="current-password"
            placeholder="••••••••"
            :aria-invalid="!!error"
          />
        </div>

        <p v-if="error" class="login-error" role="alert">{{ error }}</p>

        <button class="btn btn-primary login-submit" type="submit" :data-loading="loading" :disabled="loading">
          <span v-if="loading" class="spinner" aria-hidden="true"></span>
          {{ loading ? '登录中…' : '登录' }}
        </button>
      </form>

      <footer v-if="version" class="login-foot num">server v{{ version }}</footer>
    </div>
  </div>
</template>

<style scoped>
.login-page {
  min-height: 100vh;
  display: grid;
  place-items: center;
  padding: 24px;
}

.login-card {
  width: 100%;
  max-width: 380px;
  background: var(--bg-2);
  border: 1px solid var(--line-1);
  border-radius: var(--radius-l);
  box-shadow: var(--shadow-2);
  padding: 40px 32px 28px;
  animation: card-rise 0.55s var(--t-med) both;
}

@keyframes card-rise {
  from {
    opacity: 0;
    transform: translateY(18px) scale(0.985);
  }
  to {
    opacity: 1;
    transform: none;
  }
}

.login-brand {
  text-align: center;
  margin-bottom: var(--s-8);
}

.login-logo {
  margin: 0 auto var(--s-3);
  filter: drop-shadow(0 4px 14px rgba(232, 96, 76, 0.25));
}

.login-title {
  font-size: 21px;
  font-weight: 700;
  letter-spacing: 0.01em;
}

.login-title em {
  font-style: normal;
  color: var(--accent);
}

.login-sub {
  margin-top: 4px;
  font-size: 12.5px;
  color: var(--text-2);
}

.login-form {
  display: flex;
  flex-direction: column;
  gap: var(--s-4);
}

.login-error {
  font-size: 12.5px;
  color: var(--err);
  background: var(--err-soft);
  border-radius: var(--radius-s);
  padding: 8px 10px;
}

.login-submit {
  width: 100%;
  height: 40px;
  margin-top: var(--s-1);
  font-size: 14px;
}

.login-foot {
  text-align: center;
  margin-top: var(--s-6);
  font-size: 11.5px;
  color: var(--text-2);
}
</style>
