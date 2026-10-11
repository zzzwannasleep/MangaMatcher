<script setup>
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { auth } from '../stores/auth'
import { setToken } from '../api/client'
import ToastHost from './ui/ToastHost.vue'

const route = useRoute()
const router = useRouter()

const searchQ = ref('')
const sidebarOpen = ref(false)
const collapsed = ref(localStorage.getItem('komgabangumi.sidebar') === '1')
let animTimer = null

function toggleCollapse() {
  collapsed.value = !collapsed.value
  localStorage.setItem('komgabangumi.sidebar', collapsed.value ? '1' : '0')
  // 动画期标记：重度列表页（缩略图懒加载/无限滚动）挂起重活，避免长帧打断过渡
  window.__kbSidebarAnimating = true
  clearTimeout(animTimer)
  animTimer = setTimeout(() => {
    window.__kbSidebarAnimating = false
    window.dispatchEvent(new Event('kb-sidebar-anim-end'))
  }, 420)
}

function goSearch() {
  const term = searchQ.value.trim()
  if (!term) return
  sidebarOpen.value = false
  router.push({ path: '/search', query: { q: term } })
}

function logout() {
  setToken('')
  auth.user = null
  auth.loaded = false
  router.push('/login')
}

/* Lucide 图标（静态内联 SVG，v-html 渲染内层） */
const ICONS = {
  library: '<path d="m16 6 4 14"/><path d="M12 6v14"/><path d="M8 8v12"/><path d="M4 4v16"/>',
  jobs: '<path d="M22 12h-4l-3 9L9 3l-3 9H2"/>',
  users:
    '<path d="M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2"/><circle cx="9" cy="7" r="4"/><path d="M22 21v-2a4 4 0 0 0-3-3.87"/><path d="M16 3.13a4 4 0 0 1 0 7.75"/>',
  settings:
    '<line x1="4" x2="4" y1="21" y2="14"/><line x1="4" x2="4" y1="10" y2="3"/><line x1="12" x2="12" y1="21" y2="12"/><line x1="12" x2="12" y1="8" y2="3"/><line x1="20" x2="20" y1="21" y2="16"/><line x1="20" x2="20" y1="12" y2="3"/><line x1="2" x2="6" y1="14" y2="14"/><line x1="10" x2="14" y1="8" y2="8"/><line x1="18" x2="22" y1="16" y2="16"/>',
  logs:
    '<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8Z"/><path d="M14 2v6h6"/><line x1="8" x2="16" y1="13" y2="13"/><line x1="8" x2="16" y1="17" y2="17"/><line x1="10" x2="10" y1="9" y2="9"/>',
}

const NAV = [
  { to: '/', label: '图书馆', icon: 'library', exact: true },
  { to: '/jobs', label: '任务中心', icon: 'jobs' },
]

const ADMIN_NAV = [
  { to: '/admin/users', label: '用户管理', icon: 'users' },
  { to: '/admin/settings', label: '系统设置', icon: 'settings' },
  { to: '/admin/logs', label: '系统日志', icon: 'logs' },
]

function isActive(item) {
  if (item.exact) return route.path === item.to
  return route.path === item.to || route.path.startsWith(item.to + '/')
}
</script>

<template>
  <div class="shell">
    <!-- 移动端顶栏 -->
    <header class="mobilebar">
      <button
        class="icon-btn"
        type="button"
        aria-label="打开菜单"
        @click="sidebarOpen = true"
      >
        <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="4" y1="6" x2="20" y2="6"/><line x1="4" y1="12" x2="20" y2="12"/><line x1="4" y1="18" x2="20" y2="18"/></svg>
      </button>
      <span class="brand-name">Komga<em>Bangumi</em></span>
      <button v-if="auth.user" class="icon-btn" type="button" aria-label="退出登录" @click="logout">
        <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><polyline points="16 17 21 12 16 7"/><line x1="21" y1="12" x2="9" y2="12"/></svg>
      </button>
    </header>

    <div v-if="sidebarOpen" class="sidebar-backdrop" aria-hidden="true" @click="sidebarOpen = false"></div>

    <!-- 侧边栏 -->
    <aside class="sidebar" :class="{ 'is-open': sidebarOpen, 'is-collapsed': collapsed }">
      <div class="side-top">
        <RouterLink to="/" class="brand" @click="sidebarOpen = false">
          <span class="brand-mark" aria-hidden="true">
            <svg viewBox="0 0 32 32" width="22" height="22">
              <rect x="3" y="4" width="18" height="24" rx="3" fill="var(--accent)" opacity="0.9" />
              <rect x="11" y="4" width="18" height="24" rx="3" fill="var(--bg-1)" stroke="var(--accent)" stroke-width="2" />
            </svg>
          </span>
          <span class="brand-name">Komga<em>Bangumi</em></span>
        </RouterLink>

        <form class="side-search" role="search" @submit.prevent="goSearch">
          <svg class="side-search-icon" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>
          <input
            v-model="searchQ"
            class="input search-input"
            type="search"
            id="global-search"
            name="global-search"
            placeholder="搜索全库…"
            aria-label="全局搜索"
          />
        </form>
      </div>

      <nav class="side-nav" aria-label="主导航">
        <RouterLink
          v-for="item in NAV"
          :key="item.to"
          :to="item.to"
          class="side-link"
          :class="{ 'is-active': isActive(item) }"
          :title="collapsed ? item.label : undefined"
          @click="sidebarOpen = false"
        >
          <svg class="side-link-icon" viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" v-html="ICONS[item.icon]"></svg>
          <span class="side-link-text">{{ item.label }}</span>
        </RouterLink>

        <template v-if="auth.user?.role === 'admin'">
          <span class="side-group">管理</span>
          <RouterLink
            v-for="item in ADMIN_NAV"
            :key="item.to"
            :to="item.to"
            class="side-link"
            :class="{ 'is-active': isActive(item) }"
            :title="collapsed ? item.label : undefined"
            @click="sidebarOpen = false"
          >
            <svg class="side-link-icon" viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" v-html="ICONS[item.icon]"></svg>
            <span class="side-link-text">{{ item.label }}</span>
          </RouterLink>
        </template>
      </nav>

      <div v-if="auth.user" class="side-foot">
        <span class="side-user">
          <span class="user-avatar num" :title="auth.user.username" aria-hidden="true">{{ auth.user.username.slice(0, 1).toUpperCase() }}</span>
          <span class="side-user-name">
            <span class="user-name">{{ auth.user.username }}</span>
            <span class="role-badge" :data-role="auth.user.role">{{ auth.user.role }}</span>
          </span>
        </span>
        <button class="icon-btn" type="button" aria-label="退出登录" title="退出登录" @click="logout">
          <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><polyline points="16 17 21 12 16 7"/><line x1="21" y1="12" x2="9" y2="12"/></svg>
        </button>
      </div>

      <!-- 折叠开关（吸顶按钮） -->
      <button
        class="collapse-btn"
        type="button"
        :aria-label="collapsed ? '展开侧栏' : '折叠侧栏'"
        :title="collapsed ? '展开侧栏' : '折叠侧栏'"
        @click="toggleCollapse"
      >
        <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" :style="{ transform: collapsed ? 'none' : 'scaleX(-1)' }"><path d="m9 18 6-6-6-6"/></svg>
      </button>
    </aside>

    <main class="shell-main">
      <slot />
    </main>

    <ToastHost />
  </div>
</template>

<style scoped>
.shell {
  min-height: 100vh;
  display: flex;
  align-items: stretch;
}

/* ---- 移动端顶栏（仅小屏出现） ---- */
.mobilebar {
  display: none;
}

.sidebar-backdrop {
  display: none;
}

/* ---- 侧栏 ---- */
.sidebar {
  flex-shrink: 0;
  width: 232px;
  position: sticky;
  top: 0;
  height: 100vh;
  display: flex;
  flex-direction: column;
  gap: var(--s-4);
  padding: 20px 14px 16px;
  background: var(--bg-1);
  border-right: 1px solid var(--line-1);
  z-index: 50;
  transition: width 0.22s var(--ease-out), padding 0.22s var(--ease-out);
}

.sidebar.is-collapsed {
  width: 62px;
  padding: 20px 9px 16px;
}

/* ---- 折叠/展开动画：宽度与内容同步过渡（禁止 display 瞬切） ---- */
.brand-name,
.side-link-text,
.side-user-name,
.side-foot .icon-btn {
  transition: opacity 0.18s var(--ease-out), max-width 0.18s var(--ease-out), margin 0.18s var(--ease-out);
}

.sidebar.is-collapsed .brand-name {
  opacity: 0;
}

.brand {
  overflow: hidden;
  white-space: nowrap;
}

.side-search {
  position: relative;
  height: 34px;
  overflow: hidden;
  transition: height 0.18s var(--ease-out), opacity 0.18s var(--ease-out), margin-top 0.18s var(--ease-out);
}

.sidebar.is-collapsed .side-search {
  height: 0;
  opacity: 0;
  margin-top: calc(-1 * var(--s-4));
  pointer-events: none;
}

.side-link-text {
  white-space: nowrap;
  overflow: hidden;
  max-width: 170px;
}

.sidebar.is-collapsed .side-link-text {
  opacity: 0;
  max-width: 0;
  margin-left: -10px; /* 抵消图标-文字 gap，图标平滑归中 */
}

.side-group {
  max-height: 40px;
  overflow: hidden;
  transition: opacity 0.18s var(--ease-out), max-height 0.18s var(--ease-out), padding 0.18s var(--ease-out);
}

.sidebar.is-collapsed .side-group {
  opacity: 0;
  max-height: 0;
  padding: 0 10px;
}

.user-avatar {
  transition: width 0.18s var(--ease-out), height 0.18s var(--ease-out);
}

.sidebar.is-collapsed .side-user-name {
  opacity: 0;
  max-width: 0;
  overflow: hidden;
}

.side-foot .icon-btn {
  max-width: 32px;
  overflow: hidden;
}

.sidebar.is-collapsed .side-foot .icon-btn {
  opacity: 0;
  max-width: 0;
}

/* 折叠开关：吸在侧栏顶部 */
.collapse-btn {
  position: absolute;
  top: 16px;
  right: -11px;
  z-index: 2;
  width: 22px;
  height: 22px;
  display: grid;
  place-items: center;
  border: 1px solid var(--line-2);
  border-radius: var(--radius-full);
  background: var(--bg-3);
  color: var(--text-1);
  cursor: pointer;
  transition: color var(--t-fast), background-color var(--t-fast), border-color var(--t-fast);
}

.collapse-btn:hover {
  color: var(--accent-strong);
  border-color: var(--accent-line);
  background: var(--accent-soft);
}

/* 折叠态：图标归中（文字经 max-width 过渡收起，与宽度动画同步） */
.sidebar.is-collapsed .side-link {
  justify-content: center;
  padding: 10px 0;
}

.sidebar.is-collapsed .side-foot {
  justify-content: center;
}

.sidebar.is-collapsed .side-foot .user-avatar {
  width: 34px;
  height: 34px;
}

.side-avatar-collapsed {
  width: 34px;
  height: 34px;
}

.side-top {
  display: flex;
  flex-direction: column;
  gap: var(--s-4);
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 2px 8px;
  text-decoration: none;
  color: var(--text-0);
  font-weight: 650;
  letter-spacing: 0.01em;
}

.brand:hover {
  text-decoration: none;
}

.brand-name em {
  font-style: normal;
  color: var(--accent);
}

.side-search-icon {
  position: absolute;
  left: 11px;
  top: 50%;
  transform: translateY(-50%);
  color: var(--text-2);
  pointer-events: none;
}

.search-input {
  height: 34px;
  font-size: 13px;
  padding-left: 32px;
  background: var(--bg-inset);
}

.side-nav {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
  overflow-y: auto;
  scrollbar-width: none;
}

.side-nav::-webkit-scrollbar {
  display: none;
}

.side-group {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.08em;
  color: var(--text-2);
  padding: 14px 10px 6px;
}

.side-link {
  position: relative;
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 9px 10px;
  border-radius: var(--radius-s);
  color: var(--text-1);
  text-decoration: none;
  font-size: 13.5px;
  line-height: 1;
  transition: color var(--t-fast), background-color var(--t-fast), padding 0.18s var(--ease-out);
}

.side-link:hover {
  color: var(--text-0);
  background: var(--bg-3);
}

.side-link.is-active {
  color: var(--accent-strong);
  background: var(--accent-soft);
}

.side-link.is-active::before {
  content: '';
  position: absolute;
  left: -14px;
  top: 7px;
  bottom: 7px;
  width: 3px;
  border-radius: 2px;
  background: var(--accent);
}

.side-link-icon {
  flex-shrink: 0;
  opacity: 0.85;
}

/* ---- 侧栏底部用户 ---- */
.side-foot {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 8px;
  border-top: 1px solid var(--line-1);
}

.side-user {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 0;
  flex: 1;
}

.user-avatar {
  flex-shrink: 0;
  width: 30px;
  height: 30px;
  border-radius: var(--radius-full);
  display: grid;
  place-items: center;
  font-size: 13px;
  font-weight: 650;
  color: var(--accent-strong);
  background: var(--accent-soft);
  border: 1px solid var(--accent-line);
}

.side-user-name {
  display: flex;
  flex-direction: column;
  gap: 3px;
  min-width: 0;
}

.user-name {
  color: var(--text-0);
  font-size: 13px;
  font-weight: 550;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.role-badge {
  align-self: flex-start;
  font-size: 10.5px;
  line-height: 1;
  padding: 3px 7px;
  border-radius: 99px;
  border: 1px solid var(--line-2);
  color: var(--text-1);
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.role-badge[data-role='admin'] {
  color: var(--accent-strong);
  border-color: color-mix(in srgb, var(--accent) 45%, transparent);
  background: var(--accent-soft);
}

.icon-btn {
  display: grid;
  place-items: center;
  width: 32px;
  height: 32px;
  flex-shrink: 0;
  border: none;
  border-radius: var(--radius-s);
  background: transparent;
  color: var(--text-1);
  cursor: pointer;
  transition: color var(--t-fast), background-color var(--t-fast);
}

.icon-btn:hover {
  color: var(--text-0);
  background: var(--bg-3);
}

/* ---- 主区 ---- */
.shell-main {
  flex: 1;
  min-width: 0;
  padding: 28px 32px 64px;
}

/* ---- 移动端 ---- */
@media (max-width: 900px) {
  .mobilebar {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    height: 52px;
    padding: 0 14px;
    border-bottom: 1px solid var(--line-1);
    background: color-mix(in srgb, var(--bg-1) 88%, transparent);
    backdrop-filter: blur(10px);
    position: sticky;
    top: 0;
    z-index: 55;
  }

  .mobilebar .brand-name {
    font-weight: 650;
  }

  .sidebar-backdrop {
    display: block;
    position: fixed;
    inset: 0;
    z-index: 58;
    background: rgba(0, 0, 0, 0.5);
    animation: fade-in 0.2s ease;
  }

  .sidebar {
    position: fixed;
    left: 0;
    top: 0;
    bottom: 0;
    width: 264px;
    transform: translateX(-100%);
    transition: transform 0.26s var(--ease-out);
    box-shadow: var(--shadow-2);
  }

  /* 移动端抽屉模式：忽略折叠态（还原全部过渡后的隐藏态） */
  .sidebar.is-collapsed {
    width: 264px;
    padding: 20px 14px 16px;
  }

  .sidebar.is-collapsed .brand-name,
  .sidebar.is-collapsed .side-link-text,
  .sidebar.is-collapsed .side-user-name,
  .sidebar.is-collapsed .side-foot .icon-btn {
    opacity: 1;
    max-width: 170px;
    margin-left: 0;
  }

  .sidebar.is-collapsed .side-link-text,
  .sidebar.is-collapsed .side-user-name {
    max-width: 170px;
  }

  .sidebar.is-collapsed .side-search {
    height: 34px;
    opacity: 1;
    margin-top: 0;
    pointer-events: auto;
  }

  .sidebar.is-collapsed .side-group {
    opacity: 1;
    max-height: 40px;
    padding: 14px 10px 6px;
  }

  .sidebar.is-collapsed .side-link {
    justify-content: flex-start;
    padding: 9px 10px;
  }

  .sidebar.is-collapsed .side-foot {
    justify-content: flex-start;
  }

  .collapse-btn {
    display: none;
  }

  .sidebar.is-open {
    transform: none;
  }

  .shell-main {
    padding: 20px 16px 48px;
  }
}

@keyframes fade-in {
  from {
    opacity: 0;
  }
}
</style>
