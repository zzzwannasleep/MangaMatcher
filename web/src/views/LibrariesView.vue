<script setup>
import { ref, computed, onMounted } from 'vue'
import { api } from '../api/client'
import { auth } from '../stores/auth'
import { notify } from '../stores/toast'
import EmptyState from '../components/ui/EmptyState.vue'

const libraries = ref([])
const loading = ref(true)
const loadError = ref('')

const isAdmin = computed(() => auth.user?.role === 'admin')

onMounted(load)

async function load() {
  loading.value = true
  loadError.value = ''
  try {
    const data = await api('/api/libraries')
    libraries.value = Array.isArray(data) ? data : []
  } catch (err) {
    loadError.value = err.message
    notify.error(err.message)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div>
    <header class="page-head">
      <div>
        <h1 class="page-title">图书馆</h1>
        <p class="page-sub">
          <template v-if="isAdmin">选择一个库，浏览系列并创建刮削任务</template>
          <template v-else>你被授权的库 · 点击进入系列浏览</template>
        </p>
      </div>
      <span v-if="!loading && !loadError" class="page-sub num">{{ libraries.length }} 个库</span>
    </header>

    <div v-if="loading" class="loading-block">
      <span class="spinner" aria-hidden="true"></span>
      正在加载图书馆…
    </div>

    <EmptyState
      v-else-if="loadError"
      icon="⚠"
      title="加载失败"
      :hint="loadError"
    >
      <button class="btn" type="button" @click="load">重试</button>
    </EmptyState>

    <EmptyState
      v-else-if="!libraries.length"
      icon="▤"
      title="没有可用的库"
      :hint="isAdmin ? 'Komga 中还没有图书馆，或 API Key 无效——去「设置」测试连接' : '管理员尚未给你分配任何库，请联系管理员'"
    >
      <RouterLink v-if="isAdmin" to="/admin/settings" class="btn">前往设置</RouterLink>
    </EmptyState>

    <div v-else class="lib-grid">
      <RouterLink
        v-for="(lib, index) in libraries"
        :key="lib.id"
        :to="`/library/${lib.id}`"
        class="lib-card lift"
      >
        <span class="lib-spine" aria-hidden="true"></span>
        <span class="lib-index num">{{ String(index + 1).padStart(2, '0') }}</span>
        <span class="lib-name">{{ lib.name }}</span>
        <span class="lib-cta" aria-hidden="true">浏览系列 →</span>
      </RouterLink>
    </div>
  </div>
</template>

<style scoped>
.lib-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
  gap: var(--s-4);
}

.lib-card {
  position: relative;
  display: flex;
  flex-direction: column;
  gap: var(--s-2);
  min-height: 128px;
  padding: 20px 20px 16px 24px;
  background: var(--bg-2);
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
  text-decoration: none;
  overflow: hidden;
}

.lib-card:hover {
  text-decoration: none;
}

.lib-spine {
  position: absolute;
  left: 0;
  top: 0;
  bottom: 0;
  width: 4px;
  background: linear-gradient(180deg, var(--accent), color-mix(in srgb, var(--accent) 30%, transparent));
  opacity: 0.55;
  transition: opacity var(--t-med), width var(--t-med);
}

.lib-card:hover .lib-spine {
  opacity: 1;
  width: 6px;
}

.lib-index {
  font-size: 11px;
  color: var(--text-2);
  font-family: var(--font-mono);
  letter-spacing: 0.08em;
}

.lib-name {
  font-size: 16px;
  font-weight: 650;
  color: var(--text-0);
  line-height: 1.4;
  word-break: break-word;
}

.lib-cta {
  margin-top: auto;
  font-size: 12.5px;
  color: var(--text-2);
  transition: color var(--t-fast), transform var(--t-fast);
}

.lib-card:hover .lib-cta {
  color: var(--accent-strong);
  transform: translateX(2px);
}
</style>
