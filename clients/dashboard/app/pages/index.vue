<script setup lang="ts">
import type { MediaPage } from '~/types'

const { apiReferenceUrl } = useRuntimeConfig().public

const online = ref<boolean | null>(null)
const loading = ref(true)
const totals = reactive({ files: 0, bytes: 0, images: 0, attached: 0 })

/**
 * A few numbers for the landing page, from the listing endpoint the dashboard already uses.
 *
 * Each figure is one `take=1` call read for its `total` — the API has no statistics endpoint, and
 * inventing one for a sample's overview would be the wrong place to add surface. The bytes are
 * the exception: they need the rows, so they are summed over the first page only and labelled as
 * such rather than presented as a library total.
 */
async function probe() {
  loading.value = true

  try {
    const [all, images, attached, recent] = await Promise.all([
      $fetch<MediaPage>('/api/media', { query: { take: 1 } }),
      $fetch<MediaPage>('/api/media', { query: { take: 1, contentType: 'image/' } }),
      $fetch<MediaPage>('/api/media', { query: { take: 1, ownerEntityName: 'Product' } }),
      $fetch<MediaPage>('/api/media', { query: { take: 50 } })
    ])

    totals.files = all.total
    totals.images = images.total
    totals.attached = attached.total
    totals.bytes = recent.items.reduce((sum, file) => sum + file.size, 0)

    online.value = true
  } catch {
    online.value = false
  } finally {
    loading.value = false
  }
}

const cards = computed(() => [
  { label: 'Files', value: String(totals.files), icon: 'i-lucide-files' },
  { label: 'Images', value: String(totals.images), icon: 'i-lucide-image' },
  { label: 'Attached to a product', value: String(totals.attached), icon: 'i-lucide-link' },
  { label: 'Size of the newest 50', value: formatBytes(totals.bytes), icon: 'i-lucide-hard-drive' }
])

onMounted(probe)
</script>

<template>
  <UDashboardPanel id="home">
    <template #header>
      <UDashboardNavbar title="Overview" :ui="{ right: 'gap-3' }">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <UBadge
            :color="loading ? 'neutral' : online ? 'success' : 'error'"
            variant="subtle"
            :icon="loading ? 'i-lucide-loader-circle' : online ? 'i-lucide-plug' : 'i-lucide-plug-zap'"
            :label="loading ? 'Connecting…' : online ? 'API connected' : 'API unreachable'"
          />

          <UButton
            icon="i-lucide-refresh-cw"
            color="neutral"
            variant="ghost"
            :loading="loading"
            aria-label="Refresh"
            @click="probe"
          />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UPageCard
        title="Files, with a row that says where they are"
        description="This dashboard reads and writes through the endpoints MapMediaEndpoints() maps. Uploads stream to whichever storage backend the API is configured with — local disk, Azure Blob Storage or Amazon S3 — while one table records what each file is and what it belongs to."
        variant="naked"
        class="mb-2"
      >
        <UButton
          label="Open API reference"
          icon="i-lucide-book-open"
          color="neutral"
          variant="outline"
          :to="apiReferenceUrl"
          target="_blank"
          class="w-fit lg:ms-auto"
        />
      </UPageCard>

      <UPageGrid class="sm:grid-cols-2 lg:grid-cols-4">
        <UPageCard
          v-for="card in cards"
          :key="card.label"
          :icon="card.icon"
          variant="subtle"
        >
          <p class="text-sm text-muted">
            {{ card.label }}
          </p>
          <USkeleton v-if="loading" class="mt-1 h-7 w-16" />
          <p v-else class="text-2xl font-semibold text-highlighted">
            {{ card.value }}
          </p>
        </UPageCard>
      </UPageGrid>

      <UPageGrid class="lg:grid-cols-2">
        <UPageCard
          title="Media"
          description="Browse everything uploaded. Search by name, narrow by category or tag, sort by date, name or size, and page through it. Upload from here too."
          icon="i-lucide-images"
          to="/media"
          variant="subtle"
          spotlight
        >
          <template #footer>
            <UBadge
              color="neutral"
              variant="subtle"
              class="font-mono"
              label="api/media"
            />
          </template>
        </UPageCard>

        <UPageCard
          title="Products"
          description="The sample's own entity. Each product can have files attached — pick from the library or upload a new one, in the same browser this dashboard's Media page uses."
          icon="i-lucide-package"
          to="/products"
          variant="subtle"
          spotlight
        >
          <template #footer>
            <UBadge
              color="neutral"
              variant="subtle"
              class="font-mono"
              label="api/products"
            />
          </template>
        </UPageCard>
      </UPageGrid>
    </template>
  </UDashboardPanel>
</template>
