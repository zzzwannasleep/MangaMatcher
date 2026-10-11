import { createRouter, createWebHistory } from 'vue-router'
import { setUnauthorizedHandler, getToken } from './api/client'
import { auth, fetchMe } from './stores/auth'

const routes = [
  {
    path: '/login',
    name: 'login',
    component: () => import('./views/LoginView.vue'),
    meta: { public: true, bare: true, title: '登录' },
  },
  {
    path: '/',
    name: 'libraries',
    component: () => import('./views/LibrariesView.vue'),
    meta: { title: '图书馆' },
  },
  {
    path: '/library/:id',
    name: 'series',
    component: () => import('./views/SeriesView.vue'),
    props: true,
    meta: { title: '系列' },
  },
  {
    path: '/search',
    name: 'search',
    component: () => import('./views/SearchView.vue'),
    meta: { title: '搜索' },
  },
  {
    path: '/jobs',
    name: 'jobs',
    component: () => import('./views/JobsView.vue'),
    meta: { title: '任务中心' },
  },
  {
    path: '/jobs/:id',
    name: 'job',
    component: () => import('./views/JobDetailView.vue'),
    props: true,
    meta: { title: '任务详情' },
  },
  {
    path: '/admin/users',
    name: 'admin-users',
    component: () => import('./views/UsersView.vue'),
    meta: { admin: true, title: '用户管理' },
  },
  {
    path: '/admin/settings',
    name: 'admin-settings',
    component: () => import('./views/SettingsView.vue'),
    meta: { admin: true, title: '设置' },
  },
  {
    path: '/admin/logs',
    name: 'admin-logs',
    component: () => import('./views/LogsView.vue'),
    meta: { admin: true, title: '系统日志' },
  },
  { path: '/:pathMatch(.*)*', redirect: '/' },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})

// 401 统一处理：清 token → 回登录页
setUnauthorizedHandler(() => {
  auth.user = null
  auth.loaded = false
  const current = router.currentRoute.value
  if (current.path !== '/login') {
    router.push({ path: '/login', query: current.path === '/' ? {} : { redirect: current.fullPath } })
  }
})

router.beforeEach(async (to) => {
  document.title = to.meta.title ? `${to.meta.title} · KomgaBangumi` : 'KomgaBangumi'

  if (to.meta.public) {
    if (getToken() && !auth.loaded) {
      try {
        await fetchMe()
      } catch {
        /* 401 已由统一处理器清理 */
      }
    }
    if (getToken() && auth.user) return { path: '/' }
    return true
  }

  if (!getToken()) {
    return { path: '/login', query: to.path === '/' ? {} : { redirect: to.fullPath } }
  }

  if (!auth.loaded) {
    try {
      await fetchMe()
    } catch {
      return { path: '/login' }
    }
  }
  if (!auth.user) return { path: '/login' }
  if (to.meta.admin && auth.user.role !== 'admin') return { path: '/' }
  return true
})
