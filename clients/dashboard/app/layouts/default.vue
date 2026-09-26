<script setup lang="ts">
import type { NavigationMenuItem } from '@nuxt/ui'

const { apiReferenceUrl } = useRuntimeConfig().public

// Registers the `g-h` / `g-m` / `g-p` navigation shortcuts.
useDashboard()

const open = ref(false)

const close = () => {
  open.value = false
}

const links = [[{
  label: 'Overview',
  icon: 'i-lucide-house',
  to: '/',
  onSelect: close
}, {
  label: 'Media',
  icon: 'i-lucide-images',
  to: '/media',
  onSelect: close
}, {
  label: 'Products',
  icon: 'i-lucide-package',
  to: '/products',
  onSelect: close
}], [{
  label: 'API reference',
  icon: 'i-lucide-book-open',
  to: apiReferenceUrl,
  target: '_blank'
}, {
  label: 'Documentation',
  icon: 'i-simple-icons-github',
  to: 'https://github.com/dotnetboost/media',
  target: '_blank'
}]] satisfies NavigationMenuItem[][]

const groups = computed(() => [{
  id: 'links',
  label: 'Go to',
  items: links.flat()
}])
</script>

<template>
  <UDashboardGroup unit="rem">
    <UDashboardSidebar
      id="default"
      v-model:open="open"
      collapsible
      resizable
      class="bg-elevated/25"
      :ui="{ footer: 'lg:border-t lg:border-default' }"
    >
      <template #header="{ collapsed }">
        <AppBrand :collapsed="collapsed" />
      </template>

      <template #default="{ collapsed }">
        <UDashboardSearchButton :collapsed="collapsed" class="bg-transparent ring-default" />

        <UNavigationMenu
          :collapsed="collapsed"
          :items="links[0]"
          orientation="vertical"
          tooltip
          popover
        />

        <UNavigationMenu
          :collapsed="collapsed"
          :items="links[1]"
          orientation="vertical"
          tooltip
          class="mt-auto"
        />
      </template>

      <template #footer="{ collapsed }">
        <AppearanceMenu :collapsed="collapsed" />
      </template>
    </UDashboardSidebar>

    <UDashboardSearch :groups="groups" />

    <slot />
  </UDashboardGroup>
</template>
