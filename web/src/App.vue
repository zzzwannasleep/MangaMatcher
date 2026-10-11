<script setup>
import { useRoute } from 'vue-router'
import AppShell from './components/AppShell.vue'

const route = useRoute()
</script>

<template>
  <router-view v-if="route.meta.bare" v-slot="{ Component }">
    <transition name="page" mode="out-in">
      <component :is="Component" :key="route.fullPath" />
    </transition>
  </router-view>
  <AppShell v-else>
    <router-view v-slot="{ Component }">
      <transition name="page" mode="out-in">
        <component :is="Component" :key="route.fullPath" />
      </transition>
    </router-view>
  </AppShell>
</template>
