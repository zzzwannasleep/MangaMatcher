<script setup>
import { ref, reactive, onMounted, onBeforeUnmount } from 'vue'
import { api, apiForm } from '../api/client'
import { notify } from '../stores/toast'

const loading = ref(true)
const loadError = ref('')
const saving = ref(false)
const testing = ref(false)

const form = reactive({
  // v1
  komgaUrl: '',
  komgaApiKey: '',
  komgaUser: '',
  bangumiToken: '',
  concurrency: 2,
  dryRunDefault: false,
  manualCollection: true,
  // v2
  matchType: '漫画',
  proxy: { protocol: 'none', host: '', port: 0, username: '', password: '' },
  ai: {
    enabled: false,
    baseURL: '',
    apiKey: '',
    model: '',
    identify: true,
    smartPick: true,
    translateSummary: false,
    writeSummary: false,
  },
  baikeEnabled: true,
  mangaDexEnabled: true,
  ehEnabled: true,
  batchForceRefresh: false,
  bookwalker: { downloadURL: '', writeExtras: true },
  versionTagWhitelistText: '',
  versionTagBlacklistText: '',
  comicsDir: '',
  cache: { enabled: true, refreshMinutes: 60 },
  update: { autoCheck: true },
})

// 后端对密钥打码返回 "****"：输入框留空 = 不修改
const komgaKeyMasked = ref(false)
const bangumiMasked = ref(false)
const aiKeyMasked = ref(false)
const proxyPasswordMasked = ref(false)

const testResult = ref(null) // { ok, version?, message? }
const formError = ref('')

const PROTOCOLS = [
  { value: 'none', label: '不使用代理' },
  { value: 'http', label: 'HTTP' },
  { value: 'https', label: 'HTTPS' },
  { value: 'socks4', label: 'SOCKS4' },
  { value: 'socks4a', label: 'SOCKS4a' },
  { value: 'socks5', label: 'SOCKS5' },
]

async function load() {
  loading.value = true
  loadError.value = ''
  try {
    const data = await api('/api/settings')
    form.komgaUrl = data.komgaUrl || ''
    form.komgaUser = data.komgaUser || ''
    form.bangumiToken = ''
    form.concurrency = data.concurrency ?? 2
    form.dryRunDefault = !!data.dryRunDefault
    form.manualCollection = !!data.manualCollection

    // v2
    form.matchType = data.matchType === '小说' ? '小说' : '漫画'
    const proxy = data.proxy || {}
    form.proxy.protocol = proxy.protocol || 'none'
    form.proxy.host = proxy.host || ''
    form.proxy.port = proxy.port ?? 0
    form.proxy.username = proxy.username || ''
    form.proxy.password = ''
    const ai = data.ai || {}
    form.ai.enabled = !!ai.enabled
    form.ai.baseURL = ai.baseURL || ''
    form.ai.apiKey = ''
    form.ai.model = ai.model || ''
    form.ai.identify = ai.identify !== false
    form.ai.smartPick = ai.smartPick !== false
    form.ai.translateSummary = !!ai.translateSummary
    form.ai.writeSummary = !!ai.writeSummary
    form.baikeEnabled = data.baikeEnabled !== false
    form.mangaDexEnabled = data.mangaDexEnabled !== false
    form.ehEnabled = data.ehEnabled !== false
    form.batchForceRefresh = !!data.batchForceRefresh
    const bw = data.bookwalker || {}
    form.bookwalker.downloadURL = bw.downloadURL || ''
    form.bookwalker.writeExtras = bw.writeExtras !== false
    form.versionTagWhitelistText = (data.versionTagWhitelist || []).join('\n')
    form.versionTagBlacklistText = (data.versionTagBlacklist || []).join('\n')
    form.comicsDir = data.comicsDir || ''
    const cache = data.cache || {}
    form.cache.enabled = cache.enabled !== false
    form.cache.refreshMinutes = cache.refreshMinutes ?? 60
    form.update.autoCheck = data.update?.autoCheck !== false

    komgaKeyMasked.value = !!data.komgaApiKey
    bangumiMasked.value = !!data.bangumiToken
    aiKeyMasked.value = !!ai.apiKey
    proxyPasswordMasked.value = !!proxy.password
    refreshBwStatus()
    refreshCacheStatus()
    refreshUpdateStatus()
  } catch (err) {
    loadError.value = err.message
    notify.error(err.message)
  } finally {
    loading.value = false
  }
}

function linesToArray(text) {
  return String(text || '')
    .split('\n')
    .map((s) => s.trim())
    .filter(Boolean)
}

function cacheAgeText(lastSync) {
  const ms = Date.now() - new Date(lastSync).getTime()
  if (!Number.isFinite(ms) || ms < 0) return ''
  const min = Math.floor(ms / 60000)
  if (min < 1) return '刚刚'
  if (min < 60) return `${min} 分钟前`
  const h = Math.floor(min / 60)
  if (h < 24) return `${h} 小时前`
  return `${Math.floor(h / 24)} 天前`
}

function validate() {
  formError.value = ''
  if (form.proxy.protocol !== 'none') {
    if (!form.proxy.host.trim()) return '代理协议已启用，请填写代理地址'
    const port = Number(form.proxy.port)
    if (!Number.isInteger(port) || port <= 0 || port > 65535) return '代理端口需为 1–65535 的整数'
  }
  if (form.ai.enabled) {
    if (!form.ai.baseURL.trim()) return 'AI 已启用，请填写 baseURL'
    if (!form.ai.model.trim()) return 'AI 已启用，请填写模型名'
  }
  return ''
}

async function save() {
  const problem = validate()
  if (problem) {
    formError.value = problem
    notify.error(problem)
    return
  }
  saving.value = true
  try {
    const patch = {
      komgaUrl: form.komgaUrl.trim(),
      komgaUser: form.komgaUser.trim(),
      concurrency: Number(form.concurrency) || 1,
      dryRunDefault: form.dryRunDefault,
      manualCollection: form.manualCollection,
      // v1 密钥：留空 = 不修改（契约）
      komgaApiKey: form.komgaApiKey,
      bangumiToken: form.bangumiToken,
      // v2
      matchType: form.matchType,
      proxy: {
        protocol: form.proxy.protocol,
        host: form.proxy.host.trim(),
        port: form.proxy.protocol === 'none' ? 0 : Number(form.proxy.port) || 0,
        username: form.proxy.username.trim(),
        password: form.proxy.password,
      },
      ai: {
        enabled: form.ai.enabled,
        baseURL: form.ai.baseURL.trim(),
        apiKey: form.ai.apiKey,
        model: form.ai.model.trim(),
        identify: form.ai.identify,
        smartPick: form.ai.smartPick,
        translateSummary: form.ai.translateSummary,
        writeSummary: form.ai.writeSummary,
      },
      baikeEnabled: form.baikeEnabled,
      mangaDexEnabled: form.mangaDexEnabled,
      ehEnabled: form.ehEnabled,
      batchForceRefresh: form.batchForceRefresh,
      bookwalker: {
        downloadURL: form.bookwalker.downloadURL.trim(),
        writeExtras: form.bookwalker.writeExtras,
      },
      versionTagWhitelist: linesToArray(form.versionTagWhitelistText),
      versionTagBlacklist: linesToArray(form.versionTagBlacklistText),
      comicsDir: form.comicsDir.trim(),
      cache: {
        enabled: form.cache.enabled,
        refreshMinutes: Number(form.cache.refreshMinutes) || 0,
      },
      update: {
        autoCheck: form.update.autoCheck,
      },
    }
    await api('/api/settings', { method: 'PATCH', body: patch })
    notify.success('设置已保存')
    form.komgaApiKey = ''
    form.bangumiToken = ''
    form.ai.apiKey = ''
    form.proxy.password = ''
    await load()
  } catch (err) {
    notify.error(err.message)
  } finally {
    saving.value = false
  }
}

async function testKomga() {
  testing.value = true
  testResult.value = null
  try {
    const res = await api('/api/settings/test-komga', { method: 'POST', body: {} })
    testResult.value = { ok: true, version: res.version || '' }
    notify.success(`Komga 连接成功${res.version ? `（v${res.version}）` : ''}`)
  } catch (err) {
    testResult.value = { ok: false, message: err.message }
    notify.error(err.message)
  } finally {
    testing.value = false
  }
}

/* ---------- BookWalker 索引 ---------- */
const bwStatus = ref(null) // { indexed, building, seriesCount, volumeCount, formatVersion, lastError }
let bwTimer = null

async function refreshBwStatus() {
  try {
    bwStatus.value = await api('/api/settings/bw/status')
    if (bwStatus.value?.building) scheduleBwPoll()
    else stopBwPoll()
  } catch {
    /* 状态拿不到不阻塞设置页 */
  }
}

function scheduleBwPoll() {
  if (bwTimer) return
  bwTimer = setTimeout(async () => {
    bwTimer = null
    await refreshBwStatus()
  }, 2500)
}

function stopBwPoll() {
  if (bwTimer) {
    clearTimeout(bwTimer)
    bwTimer = null
  }
}

const bwDownloading = ref(false)

async function bwDownload() {
  bwDownloading.value = true
  try {
    await api('/api/settings/bw/download', { method: 'POST', body: {} })
    notify.info('BookWalker 索引已开始后台下载/构建')
    await refreshBwStatus()
  } catch (err) {
    notify.error(err.message)
  } finally {
    bwDownloading.value = false
  }
}

const bwImporting = ref(false)

async function bwImport(event) {
  const file = event.target.files?.[0]
  event.target.value = ''
  if (!file) return
  bwImporting.value = true
  try {
    const fd = new FormData()
    fd.append('file', file)
    const res = await apiForm('/api/settings/bw/import', fd)
    notify.success(`导入完成：${res?.seriesCount ?? 0} 系列 / ${res?.volumeCount ?? 0} 卷`)
    await refreshBwStatus()
  } catch (err) {
    notify.error(err.message)
  } finally {
    bwImporting.value = false
  }
}

/* ---------- 本地库缓存 ---------- */
const cacheStatus = ref(null) // { enabled, refreshMinutes, ready, refreshing, seriesCount, libraryCount, lastSync, ageMinutes }
const cacheRefreshing = ref(false)
let cacheTimer = null

async function refreshCacheStatus() {
  try {
    cacheStatus.value = await api('/api/cache/status')
  } catch {
    /* 状态拿不到不阻塞设置页 */
  }
}

async function cacheRefreshNow() {
  cacheRefreshing.value = true
  try {
    await api('/api/cache/refresh', { method: 'POST', body: {} })
    notify.info('库缓存对齐已开始（后台执行，可稍后查看状态）')
    // 轮询至 refreshing 结束
    stopCachePoll()
    cacheTimer = setInterval(async () => {
      await refreshCacheStatus()
      if (!cacheStatus.value?.refreshing) {
        stopCachePoll()
        notify.success(`库缓存已对齐（${cacheStatus.value?.seriesCount ?? 0} 系列）`)
      }
    }, 2000)
  } catch (err) {
    notify.error(err.message)
    cacheRefreshing.value = false
  }
}

function stopCachePoll() {
  if (cacheTimer) {
    clearInterval(cacheTimer)
    cacheTimer = null
  }
}

/* ---------- 软件更新（Docker 版：检查新版本 → 引导 docker compose pull） ---------- */
const updateStatus = ref(null) // { current, autoCheck, checking, latest?, hasUpdate?, releaseUrl?, checkedAt?, error? }
const updateChecking = ref(false)

async function refreshUpdateStatus() {
  try {
    updateStatus.value = await api('/api/update/status')
  } catch {
    /* 状态拿不到不阻塞设置页 */
  }
}

async function updateCheckNow() {
  updateChecking.value = true
  try {
    updateStatus.value = await api('/api/update/check', { method: 'POST', body: {} })
    const st = updateStatus.value
    if (st.error && !st.latest) notify.error(`检查更新失败：${st.error}`)
    else if (st.hasUpdate) notify.success(`发现新版本 v${st.latest}（当前 v${st.current}）`)
    else notify.success(`已是最新版本 v${st.current}`)
  } catch (err) {
    notify.error(err.message)
  } finally {
    updateChecking.value = false
  }
}

function checkedAgoText(checkedAt) {
  const ms = Date.now() - new Date(checkedAt).getTime()
  if (!Number.isFinite(ms) || ms < 0) return ''
  const min = Math.floor(ms / 60000)
  if (min < 1) return '刚刚'
  if (min < 60) return `${min} 分钟前`
  const h = Math.floor(min / 60)
  if (h < 24) return `${h} 小时前`
  return `${Math.floor(h / 24)} 天前`
}

onMounted(load)

onBeforeUnmount(() => {
  stopBwPoll()
  stopCachePoll()
})
</script>

<template>
  <div>
    <header class="page-head">
      <div>
        <h1 class="page-title">设置</h1>
        <p class="page-sub">Komga 连接、Bangumi 认证、刮削行为与 v2 能力</p>
      </div>
      <div class="page-actions">
        <p v-if="formError" class="form-error" role="alert">{{ formError }}</p>
        <button class="btn btn-primary" type="submit" form="settings-form" :data-loading="saving" :disabled="saving">
          <span v-if="saving" class="spinner" aria-hidden="true"></span>
          {{ saving ? '保存中…' : '保存设置' }}
        </button>
      </div>
    </header>

    <div v-if="loading" class="loading-block">
      <span class="spinner" aria-hidden="true"></span>
      正在加载设置…
    </div>

    <div v-else-if="loadError" class="card card-pad settings-error">
      <p>加载失败：{{ loadError }}</p>
      <button class="btn" type="button" @click="load">重试</button>
    </div>

    <form v-else id="settings-form" class="settings-grid" @submit.prevent="save">
      <!-- 连接：Komga + Bangumi -->
      <section class="card card-pad section">
        <h2 class="section-title">连接</h2>

        <div class="sub-head">Komga 服务</div>
        <div class="field-grid">
          <div class="field field-span">
            <label class="field-label" for="s-komga-url">Komga 地址</label>
            <input
              id="s-komga-url"
              v-model="form.komgaUrl"
              class="input"
              type="url"
              placeholder="http://komga:25600"
            />
            <span class="field-hint">Komga 服务器的完整 URL</span>
          </div>

          <div class="field">
            <label class="field-label" for="s-komga-key">API Key</label>
            <input
              id="s-komga-key"
              v-model="form.komgaApiKey"
              class="input"
              type="password"
              autocomplete="off"
              :placeholder="komgaKeyMasked ? '已设置 · 留空保持不变' : '粘贴 Komga API Key'"
            />
            <span class="field-hint">保存时留空表示不修改</span>
          </div>

          <div class="field">
            <label class="field-label" for="s-komga-user">Komga 用户（可选）</label>
            <input
              id="s-komga-user"
              v-model="form.komgaUser"
              class="input"
              type="text"
              autocomplete="off"
              placeholder="使用 Basic 认证时填写"
            />
          </div>
        </div>

        <div class="section-foot">
          <button class="btn" type="button" :data-loading="testing" :disabled="testing" @click="testKomga">
            <span v-if="testing" class="spinner" aria-hidden="true"></span>
            {{ testing ? '测试中…' : '测试连接' }}
          </button>
          <p v-if="testResult" class="test-result" :data-ok="testResult.ok">
            {{ testResult.ok ? `✓ 连接成功${testResult.version ? ` · Komga v${testResult.version}` : ''}` : `✕ ${testResult.message}` }}
          </p>
        </div>

        <div class="sub-head sub-head--gap">Bangumi</div>
        <div class="field">
          <label class="field-label" for="s-bangumi-token">Access Token</label>
          <input
            id="s-bangumi-token"
            v-model="form.bangumiToken"
            class="input"
            type="password"
            autocomplete="off"
            :placeholder="bangumiMasked ? '已设置 · 留空保持不变' : '粘贴 Bangumi Access Token'"
          />
          <span class="field-hint">用于 Bangumi API 搜索与详情拉取；留空表示不修改</span>
        </div>
      </section>

      <section class="card card-pad section">
        <h2 class="section-title">刮削行为</h2>

        <div class="field field-inline">
          <label class="field-label" for="s-concurrency">并发数</label>
          <input
            id="s-concurrency"
            v-model="form.concurrency"
            class="input input-narrow num"
            type="number"
            min="1"
            max="16"
          />
          <span class="field-hint">同时处理的系列数（1–16）</span>
        </div>

        <div class="field field-inline">
          <label class="field-label" for="s-match-type">匹配类型</label>
          <select id="s-match-type" v-model="form.matchType" class="select select-mid">
            <option value="漫画">漫画</option>
            <option value="小说">小说</option>
          </select>
          <span class="field-hint">Bangumi platform 过滤</span>
        </div>

        <label class="toggle-row">
          <input type="checkbox" v-model="form.dryRunDefault" />
          <span>
            <strong>默认 Dry-run</strong>
            <small>新任务默认只产出差异预览，不写入元数据</small>
          </span>
        </label>

        <label class="toggle-row">
          <input type="checkbox" v-model="form.batchForceRefresh" />
          <span>
            <strong>批量强制刷新</strong>
            <small>已有 Bangumi 链接的系列在批量任务中也重刮（默认跳过零请求）</small>
          </span>
        </label>

        <label class="toggle-row">
          <input type="checkbox" v-model="form.manualCollection" />
          <span>
            <strong>失败进「待手动匹配」收藏夹</strong>
            <small>刮削失败的系列自动加入 Komga 收藏夹，便于人工处理</small>
          </span>
        </label>
      </section>

      <section class="card card-pad section">
        <h2 class="section-title">本地缓存</h2>

        <label class="toggle-row">
          <input type="checkbox" v-model="form.cache.enabled" />
          <span>
            <strong>启用库数据缓存</strong>
            <small>库/系列数据存本地（内存 + SQLite），打开页面即出数据，不再每次重新连接拉全库</small>
          </span>
        </label>

        <div class="field field-inline">
          <label class="field-label" for="s-cache-refresh">自动对齐间隔</label>
          <select id="s-cache-refresh" v-model.number="form.cache.refreshMinutes" class="select select-mid" :disabled="!form.cache.enabled">
            <option :value="0">不自动对齐</option>
            <option :value="10">每 10 分钟</option>
            <option :value="30">每 30 分钟</option>
            <option :value="60">每 1 小时</option>
            <option :value="180">每 3 小时</option>
            <option :value="360">每 6 小时</option>
            <option :value="1440">每天一次</option>
          </select>
          <span class="field-hint">后台增量对齐 Komga；编辑器/刮削任务的变更会即时回源，无需等待</span>
        </div>

        <div v-if="cacheStatus" class="cache-status">
          <span class="cache-pill" :data-on="cacheStatus.ready">{{ cacheStatus.ready ? '已就绪' : '未预热' }}</span>
          <span class="cache-meta">{{ cacheStatus.libraryCount }} 库 · {{ cacheStatus.seriesCount }} 系列</span>
          <span v-if="cacheStatus.lastSync" class="cache-meta">
            上次对齐 {{ cacheAgeText(cacheStatus.lastSync) }}
          </span>
          <button
            class="btn btn-sm"
            type="button"
            :disabled="cacheStatus.refreshing"
            @click="cacheRefreshNow"
          >
            <span v-if="cacheStatus.refreshing" class="spinner" aria-hidden="true"></span>
            {{ cacheStatus.refreshing ? '对齐中…' : '立即对齐' }}
          </button>
        </div>
        <p class="field-hint">Komga 地址或密钥变更时缓存会自动清空重建，不会出现串库数据。</p>
      </section>

      <section class="card card-pad section">
        <h2 class="section-title">软件更新</h2>

        <label class="toggle-row">
          <input type="checkbox" v-model="form.update.autoCheck" />
          <span>
            <strong>启动时自动检查更新</strong>
            <small>启动 10 秒后检查一次，之后每 24 小时一次；检查走设置里的代理</small>
          </span>
        </label>

        <div v-if="updateStatus" class="update-status">
          <span class="num update-version">当前版本 v{{ updateStatus.current }}</span>
          <span v-if="updateStatus.latest" class="num update-version">最新 v{{ updateStatus.latest }}</span>
          <span v-if="updateStatus.checkedAt" class="update-meta">上次检查 {{ checkedAgoText(updateStatus.checkedAt) }}</span>
          <button class="btn btn-sm" type="button" :data-loading="updateChecking" :disabled="updateChecking || updateStatus.checking" @click="updateCheckNow">
            <span v-if="updateChecking || updateStatus.checking" class="spinner" aria-hidden="true"></span>
            {{ (updateChecking || updateStatus.checking) ? '检查中…' : '立即检查' }}
          </button>
        </div>

        <div v-if="updateStatus?.error && !updateStatus?.latest" class="update-error" :title="updateStatus.error">
          ⚠ {{ updateStatus.error }}
        </div>

        <div v-if="updateStatus?.hasUpdate" class="update-banner">
          <p class="update-banner-title">有新版本 v{{ updateStatus.latest }} 可用</p>
          <p class="update-banner-body">
            容器无法原地替换，请在宿主机执行：
            <code>docker compose pull &amp;&amp; docker compose up -d</code>
          </p>
          <a class="update-banner-link" :href="updateStatus.releaseUrl" target="_blank" rel="noopener">
            查看镜像发布页 ↗
          </a>
        </div>
        <p v-else class="field-hint">容器更新方式：宿主机 <code>docker compose pull &amp;&amp; docker compose up -d</code>，或启用 Watchtower 全自动。</p>
      </section>

      <section class="card card-pad section">
        <h2 class="section-title">代理</h2>

        <div class="field">
          <label class="field-label" for="s-proxy-protocol">协议</label>
          <select id="s-proxy-protocol" v-model="form.proxy.protocol" class="select select-mid">
            <option v-for="p in PROTOCOLS" :key="p.value" :value="p.value">{{ p.label }}</option>
          </select>
          <span class="field-hint">作用于 Bangumi API、封面下载与 BookWalker 下载；不影响 Komga</span>
        </div>

        <template v-if="form.proxy.protocol !== 'none'">
          <div class="proxy-grid">
            <div class="field">
              <label class="field-label" for="s-proxy-host">地址</label>
              <input id="s-proxy-host" v-model="form.proxy.host" class="input" type="text" placeholder="127.0.0.1" />
            </div>
            <div class="field">
              <label class="field-label" for="s-proxy-port">端口</label>
              <input id="s-proxy-port" v-model="form.proxy.port" class="input num" type="number" min="0" max="65535" placeholder="7890" />
            </div>
            <div class="field">
              <label class="field-label" for="s-proxy-user">用户名（可选）</label>
              <input id="s-proxy-user" v-model="form.proxy.username" class="input" type="text" autocomplete="off" />
            </div>
            <div class="field">
              <label class="field-label" for="s-proxy-pass">密码（可选）</label>
              <input
                id="s-proxy-pass"
                v-model="form.proxy.password"
                class="input"
                type="password"
                autocomplete="off"
                :placeholder="proxyPasswordMasked ? '已设置 · 留空保持不变' : ''"
              />
            </div>
          </div>
        </template>
      </section>

      <section class="card card-pad section">
        <h2 class="section-title">AI（OpenAI 兼容）</h2>

        <label class="toggle-row">
          <input type="checkbox" v-model="form.ai.enabled" />
          <span>
            <strong>启用 AI 能力</strong>
            <small>识别作品 / 智能选择 / 翻译简介 / 补写简介</small>
          </span>
        </label>

        <template v-if="form.ai.enabled">
          <div class="proxy-grid">
            <div class="field">
              <label class="field-label" for="s-ai-baseurl">Base URL</label>
              <input id="s-ai-baseurl" v-model="form.ai.baseURL" class="input" type="url" placeholder="https://api.deepseek.com" />
            </div>
            <div class="field">
              <label class="field-label" for="s-ai-model">模型</label>
              <input id="s-ai-model" v-model="form.ai.model" class="input" type="text" placeholder="deepseek-chat" />
            </div>
            <div class="field">
              <label class="field-label" for="s-ai-key">API Key</label>
              <input
                id="s-ai-key"
                v-model="form.ai.apiKey"
                class="input"
                type="password"
                autocomplete="off"
                :placeholder="aiKeyMasked ? '已设置 · 留空保持不变' : '粘贴 AI API Key'"
              />
            </div>
          </div>

          <div class="ai-toggles">
            <label class="toggle-row toggle-compact">
              <input type="checkbox" v-model="form.ai.identify" />
              <span>
                <strong>识别作品</strong>
                <small>搜不到时问 AI 日/中/英三名再重搜</small>
              </span>
            </label>
            <label class="toggle-row toggle-compact">
              <input type="checkbox" v-model="form.ai.smartPick" />
              <span>
                <strong>智能选择</strong>
                <small>多候选时让 AI 挑最匹配的</small>
              </span>
            </label>
            <label class="toggle-row toggle-compact">
              <input type="checkbox" v-model="form.ai.translateSummary" />
              <span>
                <strong>翻译简介</strong>
                <small>非中文简介 → 简体中文</small>
              </span>
            </label>
            <label class="toggle-row toggle-compact">
              <input type="checkbox" v-model="form.ai.writeSummary" />
              <span>
                <strong>补写简介</strong>
                <small>三源无简介时 AI 补写（80–160 字）</small>
              </span>
            </label>
          </div>
        </template>
      </section>

      <section class="card card-pad section">
        <h2 class="section-title">BookWalker 索引</h2>

        <div class="field">
          <label class="field-label" for="s-bw-url">目录库下载地址</label>
          <input id="s-bw-url" v-model="form.bookwalker.downloadURL" class="input" type="url" placeholder="https://data.bookwalker.com/bkwk-db.sqlite.zst" />
        </div>

        <label class="toggle-row">
          <input type="checkbox" v-model="form.bookwalker.writeExtras" />
          <span>
            <strong>写入扩展字段</strong>
            <small>类型 / 题材 / 出版品牌 / 阅读方向写入 Komga</small>
          </span>
        </label>

        <div class="bw-status" v-if="bwStatus">
          <span class="badge" :data-kind="bwStatus.building ? 'running' : bwStatus.indexed ? 'ok' : 'none'">
            {{ bwStatus.building ? '构建中…' : bwStatus.indexed ? '索引已就绪' : '未建立索引' }}
          </span>
          <span v-if="bwStatus.indexed || bwStatus.seriesCount" class="num bw-stats">
            {{ bwStatus.seriesCount ?? 0 }} 系列 · {{ bwStatus.volumeCount ?? 0 }} 卷 · format v{{ bwStatus.formatVersion ?? '—' }}
          </span>
          <span v-if="bwStatus.lastError" class="bw-error" :title="bwStatus.lastError">⚠ {{ bwStatus.lastError }}</span>
        </div>

        <div class="section-foot">
          <button class="btn" type="button" :data-loading="bwDownloading" :disabled="bwDownloading || bwStatus?.building" @click="bwDownload">
            {{ bwStatus?.building ? '构建中…' : '下载官方目录库' }}
          </button>
          <label class="btn cover-btn" :class="{ 'is-busy': bwImporting }">
            <span v-if="bwImporting" class="spinner" aria-hidden="true"></span>
            {{ bwImporting ? '导入中…' : '导入本地文件' }}
            <input type="file" accept=".zst,.sqlite,.db" class="visually-hidden" @change="bwImport" />
          </label>
        </div>
        <p class="field-hint">导入支持 .zst / .sqlite / .db（按文件头识别），构建进度自动轮询</p>
      </section>

      <!-- 数据源 · 标记 · 导出 -->
      <section class="card card-pad section">
        <h2 class="section-title">数据源 · 版本标记 · 导出</h2>

        <label class="toggle-row">
          <input type="checkbox" v-model="form.baikeEnabled" />
          <span>
            <strong>百度百科兜底</strong>
            <small>六源链末端：开放卡片接口补封面与概述</small>
          </span>
        </label>

        <label class="toggle-row">
          <input type="checkbox" v-model="form.mangaDexEnabled" />
          <span>
            <strong>MangaDex</strong>
            <small>公开 API，zh-Hans 标题，AI 翻译简介</small>
          </span>
        </label>

        <label class="toggle-row">
          <input type="checkbox" v-model="form.ehEnabled" />
          <span>
            <strong>E-Hentai（表站）</strong>
            <small>需要能访问 e-hentai.org（大陆需在代理里配置）</small>
            <small>标签按 12 条上限写入，中文标签自动翻译</small>
          </span>
        </label>

        <div class="sub-head sub-head--gap">版本标记黑白名单</div>
        <p class="field-hint">打标与刮削共用：文件夹标记命中白名单才打标，命中黑名单跳过</p>
        <div class="two-col">
          <div class="field">
            <label class="field-label" for="s-whitelist">白名单</label>
            <textarea id="s-whitelist" v-model="form.versionTagWhitelistText" class="input" rows="5" placeholder="每行一个词，如：&#10;完结&#10;汉化"></textarea>
          </div>
          <div class="field">
            <label class="field-label" for="s-blacklist">黑名单</label>
            <textarea id="s-blacklist" v-model="form.versionTagBlacklistText" class="input" rows="5" placeholder="每行一个词"></textarea>
          </div>
        </div>

        <div class="sub-head sub-head--gap">Mylar 导出</div>
        <div class="field">
          <label class="field-label" for="s-comics-dir">comicsDir（容器内路径）</label>
          <input id="s-comics-dir" v-model="form.comicsDir" class="input" type="text" placeholder="/comics（空 = 只支持 ZIP 下载）" />
          <span class="field-hint">设置后「Mylar 导出」可直接直写 series.json 到该目录</span>
        </div>
      </section>
    </form>
  </div>
</template>

<style scoped>
/* 满宽多列卡片网格：选项均匀铺开，不再单列下滚 */
.settings-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(380px, 1fr));
  gap: var(--s-4);
  align-items: start;
}

/* 让首列「连接」「刮削行为」等大卡占更合理的位置 */
@media (min-width: 1200px) {
  .settings-grid {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }
}

.section {
  display: flex;
  flex-direction: column;
  gap: var(--s-4);
}

.section-title {
  font-size: 14px;
  font-weight: 650;
  letter-spacing: 0.02em;
  color: var(--text-0);
  padding-bottom: var(--s-3);
  border-bottom: 1px solid var(--line-1);
}

/* 卡片内分组小标题 */
.sub-head {
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 0.07em;
  color: var(--text-2);
  padding-bottom: 6px;
  border-bottom: 1px dashed var(--line-1);
}

.sub-head--gap {
  margin-top: var(--s-2);
}

.settings-error {
  display: flex;
  flex-direction: column;
  gap: 12px;
  align-items: flex-start;
  color: var(--err);
}

.section-foot {
  display: flex;
  align-items: center;
  gap: 16px;
  flex-wrap: wrap;
}

.test-result {
  font-size: 13px;
}

.test-result[data-ok='true'] {
  color: var(--ok);
}

.test-result[data-ok='false'] {
  color: var(--err);
}

.cache-status {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  margin-top: 12px;
  padding: 10px 12px;
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
  background: var(--bg-inset);
}

.cache-pill {
  font-size: 12px;
  font-weight: 600;
  padding: 3px 10px;
  border-radius: var(--radius-full);
  border: 1px solid var(--line-1);
  color: var(--text-2);
}

.cache-pill[data-on='true'] {
  color: var(--ok);
  border-color: color-mix(in srgb, var(--ok) 40%, transparent);
}

.cache-meta {
  font-size: 12.5px;
  color: var(--text-1);
}

/* ---- 软件更新 ---- */
.update-status {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.update-version {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-0);
}

.update-meta {
  font-size: 12.5px;
  color: var(--text-2);
}

.update-error {
  font-size: 12.5px;
  color: var(--err);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.update-banner {
  padding: 12px 14px;
  border: 1px solid color-mix(in srgb, var(--ok) 40%, transparent);
  border-radius: var(--radius-m);
  background: var(--ok-soft);
}

.update-banner-title {
  font-size: 13.5px;
  font-weight: 650;
  color: var(--ok);
  margin-bottom: 4px;
}

.update-banner-body {
  font-size: 12.5px;
  color: var(--text-1);
  margin-bottom: 6px;
}

.update-banner-body code {
  padding: 1px 6px;
  border-radius: var(--radius-s);
  background: var(--bg-inset);
  font-size: 12px;
}

.update-banner-link {
  font-size: 12.5px;
  color: var(--accent, var(--text-0));
}


.field-inline {
  flex-direction: row;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.field-inline .field-label {
  flex-shrink: 0;
}

.input-narrow {
  width: 96px;
  flex-shrink: 0;
}

.select-mid {
  width: 180px;
}

/* 卡片内字段网格：地址占满一行，键值两列 */
.field-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--s-3);
}

.field-span {
  grid-column: 1 / -1;
}

.proxy-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--s-3);
}

.two-col {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--s-3);
}

.ai-toggles {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--s-2);
}

.toggle-row {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  cursor: pointer;
  padding: 12px;
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
  transition: border-color var(--t-fast);
}

.toggle-row:hover {
  border-color: var(--line-2);
}

.toggle-row input {
  margin-top: 3px;
}

.toggle-row strong {
  display: block;
  font-size: 13.5px;
  font-weight: 550;
}

.toggle-row small {
  display: block;
  color: var(--text-2);
  font-size: 12px;
  margin-top: 2px;
}

.toggle-compact {
  padding: 10px;
}

/* ---- BookWalker ---- */
.bw-status {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.bw-status .badge[data-kind='ok'] {
  color: var(--ok);
  background: var(--ok-soft);
  border-color: transparent;
}

.bw-status .badge[data-kind='running'] {
  color: var(--warn);
  background: var(--warn-soft);
  border-color: transparent;
}

.bw-status .badge[data-kind='none'] {
  color: var(--text-2);
  background: var(--bg-3);
  border-color: transparent;
}

.bw-stats {
  font-size: 12.5px;
  color: var(--text-1);
}

.bw-error {
  font-size: 12.5px;
  color: var(--err);
  max-width: 320px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ---- 文件上传按钮 ---- */
.cover-btn {
  cursor: pointer;
  position: relative;
}

.cover-btn.is-busy {
  pointer-events: none;
  opacity: 0.75;
}

.visually-hidden {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
  white-space: nowrap;
}

.form-error {
  font-size: 12.5px;
  color: var(--err);
  background: var(--err-soft);
  border-radius: var(--radius-s);
  padding: 8px 10px;
}

@media (max-width: 820px) {
  .proxy-grid,
  .two-col,
  .ai-toggles,
  .field-grid {
    grid-template-columns: 1fr;
  }

  .field-span {
    grid-column: auto;
  }
}
</style>
