<script setup lang="ts">
import type { MediaFile, Product } from '~/types'

/**
 * The sample's products, each with the files attached to it.
 *
 * Attaching is a `PATCH api/media/{id}` that sets the owner — nothing on the product changes, and
 * nothing in the database refers to the media row. That is the arrangement the whole library rests
 * on, and it is why deleting a product here also has to delete its files.
 */
useHead({ title: 'Products — DotNetBoost.Media' })

const toast = useToast()

const products = ref<Product[]>([])
const pending = ref(true)
const loadError = ref('')
const busy = ref<number | null>(null)

/** The product whose media picker is open. */
const picking = ref<Product | null>(null)
const deleting = ref<Product | null>(null)
const creating = ref(false)

const draft = reactive({ sku: '', name: '', price: 0 })

async function load() {
  pending.value = true
  loadError.value = ''

  try {
    products.value = await $fetch<Product[]>('/api/products')
  } catch (error) {
    loadError.value = problemMessage(error)
  } finally {
    pending.value = false
  }
}

const picked = computed(() => picking.value
  ? products.value.find(product => product.id === picking.value!.id) ?? picking.value
  : null)

const owner = computed(() => picked.value
  ? { entityName: 'Product', entityId: String(picked.value.id) }
  : { entityName: 'Product', entityId: '' })

async function onAttach(file: MediaFile) {
  const product = picked.value
  if (!product) {
    return
  }

  busy.value = product.id

  try {
    await $fetch(`/api/media/${file.id}`, {
      method: 'PATCH',
      body: { ownerEntityName: 'Product', ownerEntityId: String(product.id) }
    })

    toast.add({ title: `Attached ${file.fileName}`, icon: 'i-lucide-link', color: 'success' })
    await load()
  } catch (error) {
    toast.add({
      title: 'Could not attach the file',
      description: problemMessage(error),
      icon: 'i-lucide-triangle-alert',
      color: 'error'
    })
  } finally {
    busy.value = null
  }
}

async function create() {
  if (!draft.sku.trim() || !draft.name.trim()) {
    return
  }

  try {
    await $fetch('/api/products', {
      method: 'POST',
      body: { sku: draft.sku.trim(), name: draft.name.trim(), price: Number(draft.price) || 0 }
    })

    creating.value = false
    draft.sku = ''
    draft.name = ''
    draft.price = 0

    await load()
  } catch (error) {
    toast.add({
      title: 'Could not create the product',
      description: problemMessage(error),
      icon: 'i-lucide-triangle-alert',
      color: 'error'
    })
  }
}

async function onDelete() {
  const product = deleting.value
  if (!product) {
    return
  }

  busy.value = product.id

  try {
    await $fetch(`/api/products/${product.id}`, { method: 'DELETE' })

    toast.add({
      title: `Deleted ${product.name}`,
      description: `${product.images.length} attached file(s) were deleted with it.`,
      icon: 'i-lucide-trash-2',
      color: 'success'
    })

    deleting.value = null
    await load()
  } catch (error) {
    toast.add({
      title: 'Could not delete the product',
      description: problemMessage(error),
      icon: 'i-lucide-triangle-alert',
      color: 'error'
    })
  } finally {
    busy.value = null
  }
}

onMounted(load)
</script>

<template>
  <UDashboardPanel id="products">
    <template #header>
      <UDashboardNavbar title="Products" :ui="{ right: 'gap-3' }">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>

        <template #right>
          <UButton
            icon="i-lucide-refresh-cw"
            color="neutral"
            variant="ghost"
            :loading="pending"
            aria-label="Refresh"
            @click="load"
          />
          <UButton icon="i-lucide-plus" label="New product" @click="creating = true" />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UPageCard
        title="Media attached to an entity"
        description="Nothing on Product mentions media — no column, no navigation, no migration. A file points at a product by name and key, which is why one table serves every entity, and why deleting a product has to delete its files explicitly."
        variant="naked"
      >
        <UBadge
          color="neutral"
          variant="subtle"
          class="w-fit font-mono"
          label="PATCH api/media/{id}"
        />
      </UPageCard>

      <UAlert
        v-if="loadError"
        color="error"
        variant="subtle"
        icon="i-lucide-plug-zap"
        title="Could not load the products"
        :description="loadError"
        :actions="[{ label: 'Try again', color: 'neutral', variant: 'outline', onClick: load }]"
      />

      <div v-else-if="pending && !products.length" class="grid gap-4 lg:grid-cols-2">
        <USkeleton v-for="n in 4" :key="n" class="h-44 rounded-lg" />
      </div>

      <div
        v-else-if="!products.length"
        class="flex flex-col items-center gap-3 rounded-lg border border-dashed border-default py-16 text-center"
      >
        <UIcon name="i-lucide-package-open" class="size-8 text-dimmed" />
        <div>
          <p class="font-medium text-highlighted">
            No products yet
          </p>
          <p class="text-sm text-muted">
            Create one, then attach files to it.
          </p>
        </div>
        <UButton icon="i-lucide-plus" label="New product" @click="creating = true" />
      </div>

      <div v-else class="grid gap-4 lg:grid-cols-2">
        <UCard v-for="product in products" :key="product.id" variant="subtle">
          <template #header>
            <div class="flex items-start gap-3">
              <div class="min-w-0 flex-1">
                <p class="truncate font-medium text-highlighted">
                  {{ product.name }}
                </p>
                <p class="font-mono text-xs text-muted">
                  {{ product.sku }}
                </p>
              </div>

              <UBadge color="neutral" variant="subtle" :label="`#${product.id}`" />

              <UButton
                icon="i-lucide-trash-2"
                color="neutral"
                variant="ghost"
                size="xs"
                :aria-label="`Delete ${product.name}`"
                @click="deleting = product"
              />
            </div>
          </template>

          <div class="flex flex-col gap-3">
            <div v-if="product.images.length" class="flex flex-wrap gap-2">
              <a
                v-for="image in product.images"
                :key="image.id"
                :href="contentUrl(image.id)"
                target="_blank"
                class="group relative size-16 overflow-hidden rounded-md border border-default"
                :title="`${image.fileName} · ${formatBytes(image.size)}`"
              >
                <MediaThumbnail
                  :file="{
                    id: image.id,
                    fileName: image.fileName,
                    contentType: image.contentType,
                    size: image.size,
                    checksum: null,
                    category: null,
                    tags: [],
                    metadata: {},
                    ownerEntityName: 'Product',
                    ownerEntityId: String(product.id),
                    uploadedBy: null,
                    createdAt: '',
                    updatedAt: null,
                    url: null
                  }"
                  size="sm"
                  class="size-full"
                />
              </a>
            </div>

            <p v-else class="text-sm text-muted">
              No files attached.
            </p>
          </div>

          <template #footer>
            <div class="flex items-center gap-2">
              <UButton
                icon="i-lucide-image-plus"
                label="Manage media"
                size="sm"
                :loading="busy === product.id"
                @click="picking = product"
              />
              <span class="text-xs text-muted">
                {{ product.images.length }} file(s)
              </span>
            </div>
          </template>
        </UCard>
      </div>

      <MediaPickerModal
        v-if="picked"
        :open="picking !== null"
        :owner="owner"
        :title="`Media for ${picked.name}`"
        @update:open="!$event && (picking = null)"
        @attach="onAttach"
        @changed="load"
      />

      <UModal
        v-model:open="creating"
        title="New product"
        description="Created through the sample's own POST api/products. Media is attached afterwards."
      >
        <template #body>
          <div class="flex flex-col gap-4">
            <UFormField label="SKU" required>
              <UInput v-model="draft.sku" placeholder="LAMP-1" class="w-full" />
            </UFormField>
            <UFormField label="Name" required>
              <UInput v-model="draft.name" placeholder="Desk lamp" class="w-full" />
            </UFormField>
            <UFormField label="Price">
              <UInputNumber
                v-model="draft.price"
                :min="0"
                :step="0.01"
                class="w-full"
              />
            </UFormField>
          </div>
        </template>

        <template #footer>
          <div class="flex w-full justify-end gap-2">
            <UButton
              label="Cancel"
              color="neutral"
              variant="ghost"
              @click="creating = false"
            />
            <UButton
              label="Create"
              :disabled="!draft.sku.trim() || !draft.name.trim()"
              @click="create"
            />
          </div>
        </template>
      </UModal>

      <ConfirmModal
        :open="deleting !== null"
        :title="`Delete ${deleting?.name}?`"
        :description="`Its ${deleting?.images.length ?? 0} attached file(s) are deleted with it. Nothing in the database cascades — the endpoint calls media.For(product).DeleteAllAsync() itself.`"
        confirm-label="Delete"
        :loading="busy === deleting?.id"
        @update:open="!$event && (deleting = null)"
        @confirm="onDelete"
      />
    </template>
  </UDashboardPanel>
</template>
