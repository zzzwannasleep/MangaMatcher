<script setup>
import { ref, reactive, computed, onMounted } from 'vue'
import { api } from '../api/client'
import { auth } from '../stores/auth'
import { notify } from '../stores/toast'
import EmptyState from '../components/ui/EmptyState.vue'
import Modal from '../components/ui/Modal.vue'

const users = ref([])
const libraries = ref([])
const loading = ref(true)
const loadError = ref('')

const modalOpen = ref(false)
const editingId = ref('') // '' = 新建
const saving = ref(false)
const form = reactive({ username: '', password: '', role: 'master', libraries: [] })
const formError = ref('')

const deletingId = ref('') // 两段式删除确认

const isEdit = computed(() => !!editingId.value)
const roleLabel = { admin: '管理员', master: '普通用户' }

async function load() {
  loading.value = true
  loadError.value = ''
  try {
    const [userList, libList] = await Promise.all([api('/api/users'), api('/api/libraries')])
    users.value = Array.isArray(userList) ? userList : []
    libraries.value = Array.isArray(libList) ? libList : []
  } catch (err) {
    loadError.value = err.message
    notify.error(err.message)
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingId.value = ''
  Object.assign(form, { username: '', password: '', role: 'master', libraries: [] })
  formError.value = ''
  modalOpen.value = true
}

function openEdit(user) {
  editingId.value = user.id
  Object.assign(form, {
    username: user.username,
    password: '',
    role: user.role || 'master',
    libraries: [...(user.libraries || [])],
  })
  formError.value = ''
  modalOpen.value = true
}

function toggleLibrary(id, checked) {
  const set = new Set(form.libraries)
  if (checked) set.add(id)
  else set.delete(id)
  form.libraries = [...set]
}

function libName(id) {
  return libraries.value.find((l) => l.id === id)?.name || id
}

async function save() {
  formError.value = ''
  if (!isEdit.value && !form.username.trim()) {
    formError.value = '请输入用户名'
    return
  }
  if (!isEdit.value && !form.password) {
    formError.value = '请输入初始密码'
    return
  }
  if (form.role === 'admin') form.libraries = []

  saving.value = true
  try {
    if (isEdit.value) {
      // admin 不需要库授权；传空数组避免伪造 id（["*"] 只是 /api/me 的展示约定）
      const patch = { role: form.role, libraries: form.role === 'admin' ? [] : form.libraries }
      if (form.password) patch.password = form.password
      await api(`/api/users/${editingId.value}`, { method: 'PATCH', body: patch })
      notify.success(`已更新用户 ${form.username}`)
    } else {
      await api('/api/users', {
        method: 'POST',
        body: {
          username: form.username.trim(),
          password: form.password,
          role: form.role,
          libraries: form.role === 'admin' ? [] : form.libraries,
        },
      })
      notify.success(`已创建用户 ${form.username}`)
    }
    modalOpen.value = false
    await load()
  } catch (err) {
    formError.value = err.message
  } finally {
    saving.value = false
  }
}

async function confirmDelete(user) {
  try {
    await api(`/api/users/${user.id}`, { method: 'DELETE' })
    notify.success(`已删除用户 ${user.username}`)
    deletingId.value = ''
    await load()
  } catch (err) {
    notify.error(err.message)
    deletingId.value = ''
  }
}

onMounted(load)
</script>

<template>
  <div>
    <header class="page-head">
      <div>
        <h1 class="page-title">用户管理</h1>
        <p class="page-sub">创建用户、分配角色与库授权</p>
      </div>
      <div class="page-actions">
        <button class="btn btn-primary" type="button" @click="openCreate">+ 新建用户</button>
      </div>
    </header>

    <div v-if="loading" class="loading-block">
      <span class="spinner" aria-hidden="true"></span>
      正在加载用户…
    </div>

    <EmptyState v-else-if="loadError" icon="⚠" title="加载失败" :hint="loadError">
      <button class="btn" type="button" @click="load">重试</button>
    </EmptyState>

    <EmptyState
      v-else-if="!users.length"
      icon="◍"
      title="还没有用户"
      hint="创建第一个 master 用户，为他分配可刮削的库"
    />

    <div v-else class="table-wrap">
      <table class="table">
        <thead>
          <tr>
            <th>用户名</th>
            <th>角色</th>
            <th>库授权</th>
            <th>创建时间</th>
            <th style="width: 200px"></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="user in users" :key="user.id">
            <td class="cell-user">
              {{ user.username }}
              <span v-if="user.id === auth.user?.id" class="badge badge--me">我</span>
            </td>
            <td>
              <span class="role-badge" :data-role="user.role">{{ roleLabel[user.role] || user.role }}</span>
            </td>
            <td class="cell-libs">
              <template v-if="user.role === 'admin'">
                <span class="lib-tag lib-tag-all">全部库</span>
              </template>
              <template v-else-if="user.libraries?.length">
                <span v-for="id in user.libraries" :key="id" class="lib-tag">{{ libName(id) }}</span>
              </template>
              <span v-else class="text-dim">未分配</span>
            </td>
            <td class="cell-time num">{{ user.createdAt ? new Date(user.createdAt).toLocaleString('zh-CN', { hour12: false }) : '—' }}</td>
            <td class="cell-actions">
              <button class="btn btn-sm" type="button" @click="openEdit(user)">编辑</button>
              <button
                v-if="user.id === auth.user?.id"
                class="btn btn-sm"
                type="button"
                disabled
                title="不能删除自己"
              >
                删除
              </button>
              <button
                v-else-if="deletingId !== user.id"
                class="btn btn-danger btn-sm"
                type="button"
                @click="deletingId = user.id"
              >
                删除
              </button>
              <template v-else>
                <button class="btn btn-danger btn-sm" type="button" @click="confirmDelete(user)">确认删除？</button>
                <button class="btn btn-ghost btn-sm" type="button" @click="deletingId = ''">取消</button>
              </template>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <Modal v-if="modalOpen" :title="isEdit ? `编辑用户 · ${form.username}` : '新建用户'" @close="modalOpen = false">
      <form class="form-grid" @submit.prevent="save">
        <div v-if="!isEdit" class="field">
          <label class="field-label" for="u-username">用户名</label>
          <input id="u-username" v-model="form.username" class="input" type="text" autocomplete="off" placeholder="master-01" />
        </div>

        <div class="field">
          <label class="field-label" for="u-password">
            {{ isEdit ? '新密码（留空 = 不修改）' : '密码' }}
          </label>
          <input
            id="u-password"
            v-model="form.password"
            class="input"
            type="password"
            :autocomplete="isEdit ? 'new-password' : 'off'"
            :placeholder="isEdit ? '••••••••' : '至少 6 位'"
          />
        </div>

        <div class="field">
          <span class="field-label">角色</span>
          <div class="role-picker">
            <label class="role-option" :data-active="form.role === 'master'">
              <input type="radio" value="master" v-model="form.role" name="role" />
              <span><strong>master</strong><small>仅能查看/刮削被分配的库</small></span>
            </label>
            <label class="role-option" :data-active="form.role === 'admin'">
              <input type="radio" value="admin" v-model="form.role" name="role" />
              <span><strong>admin</strong><small>用户管理与全局设置</small></span>
            </label>
          </div>
        </div>

        <div v-if="form.role === 'master'" class="field">
          <span class="field-label">库授权（可多选）</span>
          <div class="lib-checks">
            <label v-for="lib in libraries" :key="lib.id" class="lib-check">
              <input
                type="checkbox"
                :checked="form.libraries.includes(lib.id)"
                @change="toggleLibrary(lib.id, $event.target.checked)"
              />
              {{ lib.name }}
            </label>
            <p v-if="!libraries.length" class="field-hint">暂无可选的库</p>
          </div>
        </div>

        <p v-if="formError" class="form-error" role="alert">{{ formError }}</p>

        <div class="form-foot">
          <button class="btn" type="button" @click="modalOpen = false">取消</button>
          <button class="btn btn-primary" type="submit" :data-loading="saving" :disabled="saving">
            {{ saving ? '保存中…' : isEdit ? '保存修改' : '创建用户' }}
          </button>
        </div>
      </form>
    </Modal>
  </div>
</template>

<style scoped>
.cell-user {
  font-weight: 600;
}

.badge--me {
  margin-left: 8px;
  font-size: 11px;
  color: var(--accent-strong);
  background: var(--accent-soft);
  border-color: transparent;
}

.role-badge {
  font-size: 12px;
  padding: 4px 10px;
  border-radius: var(--radius-full);
  border: 1px solid var(--line-2);
  color: var(--text-1);
}

.role-badge[data-role='admin'] {
  color: var(--accent-strong);
  background: var(--accent-soft);
  border-color: transparent;
}

.cell-libs {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  max-width: 320px;
}

.lib-tag {
  font-size: 12px;
  padding: 3px 9px;
  border-radius: var(--radius-full);
  background: var(--bg-3);
  border: 1px solid var(--line-1);
  color: var(--text-1);
}

.lib-tag-all {
  color: var(--ok);
  background: var(--ok-soft);
  border-color: transparent;
}

.cell-time {
  font-size: 12.5px;
  color: var(--text-1);
  white-space: nowrap;
}

.cell-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  white-space: nowrap;
}

.text-dim {
  color: var(--text-2);
  font-size: 12.5px;
}

.role-picker {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: var(--s-2);
}

.role-option {
  display: flex;
  gap: 10px;
  align-items: flex-start;
  padding: 12px;
  border: 1px solid var(--line-2);
  border-radius: var(--radius-m);
  cursor: pointer;
  transition: border-color var(--t-fast), background-color var(--t-fast);
}

.role-option[data-active='true'] {
  border-color: var(--accent-line);
  background: var(--accent-soft);
}

.role-option input {
  margin-top: 3px;
}

.role-option strong {
  display: block;
  font-size: 13.5px;
}

.role-option small {
  display: block;
  color: var(--text-2);
  font-size: 12px;
  margin-top: 2px;
}

.lib-checks {
  display: flex;
  flex-wrap: wrap;
  gap: var(--s-2);
  padding: 12px;
  border: 1px solid var(--line-1);
  border-radius: var(--radius-m);
  background: var(--bg-inset);
  max-height: 180px;
  overflow-y: auto;
}

.lib-check {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  border-radius: var(--radius-full);
  border: 1px solid var(--line-2);
  font-size: 13px;
  cursor: pointer;
  transition: border-color var(--t-fast), background-color var(--t-fast);
}

.lib-check:hover {
  border-color: var(--accent-line);
}

.form-error {
  font-size: 12.5px;
  color: var(--err);
  background: var(--err-soft);
  border-radius: var(--radius-s);
  padding: 8px 10px;
}

.form-foot {
  display: flex;
  justify-content: flex-end;
  gap: var(--s-2);
  margin-top: var(--s-1);
}

@media (max-width: 640px) {
  .role-picker {
    grid-template-columns: 1fr;
  }
}
</style>