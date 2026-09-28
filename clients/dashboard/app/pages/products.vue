<script setup lang="ts">
import type { MediaFile, Product, ProductImage } from '~/types'

/**
 * The sample's products, each with the files attached to it and what each one is used for.
 *
 * Attaching is a `POST api/media/{id}/attachments` — a link of its own, not a field on the file.
 * Nothing on the product changes and nothing in the database refers to the media row, which is the
 * arrangement the whole library rests on. It is also why one image can be several products' at
 * once: attaching it here takes it off nothing.
 *
 * Two different removals, and the difference matters. "Remove from product" detaches — the file
 * stays in the library and stays attached to every other product using it. Deleting the file, from
 * the Media page, takes it out of every product at once.
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

/** Bumped after every change to a product's media, so the open picker re-reads what it shows. */
const mediaVersion = ref(0)

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

/** Runs one call against a product, reports the outcome, and reloads. */
async function act(productId: number, call: () => Promise<unknown>, success: string, failure: string) {
  busy.value = productId

  try {
    await call()
    toast.add({ title: success, icon: 'i-lucide-check', color: 'success' })
    mediaVersion.value++
    await load()
  } catch (error) {
    toast.add({
      title: failure,
      description: problemMessage(error),
      icon: 'i-lucide-triangle-alert',
      color: 'error'
    })
  } finally {
    busy.value = null
  }
}

function onAttach(file: MediaFile, role: string, replace: boolean) {
  const product = picked.value
  if (!product) {
    return
  }

  return act(
    product.id,
    () => $fetch(`/api/media/${file.id}/attachments`, {
      method: 'POST',
      body: { ownerEntityName: 'Product', ownerEntityId: String(product.id), role, replace }
    }),
    `Attached ${file.fileName} as ${role}`,
    'Could not attach the file'
  )
}

/**
 * The product's images grouped by what it uses them for, primary first.
 *
 * A file the product uses twice — as its main image and its SEO picture — appears in both groups,
 * because the question each group answers is "what fills this role".
 */
function groups(product: Product) {
  const order = ['primary', 'gallery', 'seo']

  const byRole = new Map<string, ProductImage[]>()

  for (const image of product.images) {
    byRole.set(image.role, [...(byRole.get(image.role) ?? []), image])
  }

  return [...byRole.entries()]
    .map(([role, images]) => ({ role, images: [...images].sort((a, b) => a.sortOrder - b.sortOrder) }))
    .sort((a, b) => {
      const left = order.indexOf(a.role)
      const right = order.indexOf(b.role)

      // Roles the sample does not name sort after the ones it does, alphabetically among themselves.
      return (left < 0 ? order.length : left) - (right < 0 ? order.length : right)
        || a.role.localeCompare(b.role)
    })
}

/**
 * Makes one image the product's primary. The endpoint uses SetAsync, so it takes the role over from
 * whatever held it in one operation — and the displaced image is only detached, never deleted.
 */
function setPrimary(product: Product, image: ProductImage) {
  return act(
    product.id,
    () => $fetch(`/api/products/${product.id}/images/primary`, {
      method: 'PUT',
      body: { mediaFileId: image.id }
    }),
    `${image.fileName} is now the primary image`,
    'Could not set the primary image'
  )
}

/** Moves one gallery image one place earlier or later, and saves the whole order. */
function moveInGallery(product: Product, image: ProductImage, delta: -1 | 1) {
  const gallery = product.images
    .filter(i => i.role === 'gallery')
    .sort((a, b) => a.sortOrder - b.sortOrder)

  const from = gallery.findIndex(i => i.id === image.id)
  const to = from + delta

  if (from < 0 || to < 0 || to >= gallery.length) {
    return
  }

  const ids = gallery.map(i => i.id)
  ;[ids[from], ids[to]] = [ids[to]!, ids[from]!]

  return act(
    product.id,
    // Every id in the role, because a partial order has no meaning: the images left out would have
    // no defined position. The API refuses one.
    () => $fetch(`/api/products/${product.id}/images/gallery/order`, {
      method: 'PUT',
      body: { mediaFileIds: ids }
    }),
    'Gallery order saved',
    'Could not save the gallery order'
  )
}

/**
 * Takes an image off this product without deleting it. It stays in the library and stays attached
 * to every other product using it.
 */
function detachImage(product: Product, image: ProductImage) {
  return act(
    product.id,
    () => $fetch(`/api/products/${product.id}/images/${image.id}`, {
      method: 'DELETE',
      query: { role: image.role }
    }),
    `Removed ${image.fileName} from ${product.name}`,
    'Could not remove the image'
  )
}

/** The actions offered on one of a product's images. */
function imageActions(product: Product, image: ProductImage) {
  const gallery = product.images
    .filter(i => i.role === 'gallery')
    .sort((a, b) => a.sortOrder - b.sortOrder)

  const at = gallery.findIndex(i => i.id === image.id)

  return [
    [
      {
        label: 'Open',
        icon: 'i-lucide-external-link',
        to: contentUrl(image.id),
        target: '_blank'
      },
      ...(image.role === 'primary'
        ? []
        : [{
            label: 'Make primary',
            icon: 'i-lucide-star',
            onSelect: () => setPrimary(product, image)
          }]),
      ...(image.role === 'gallery' && at > 0
        ? [{
            label: 'Move earlier',
            icon: 'i-lucide-arrow-left',
            onSelect: () => moveInGallery(product, image, -1)
          }]
        : []),
      ...(image.role === 'gallery' && at >= 0 && at < gallery.length - 1
        ? [{
            label: 'Move later',
            icon: 'i-lucide-arrow-right',
            onSelect: () => moveInGallery(product, image, 1)
          }]
        : [])
    ],
    [
      {
        label: 'Remove from product',
        icon: 'i-lucide-unlink',
        color: 'error' as const,
        onSelect: () => detachImage(product, image)
      }
    ]
  ]
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
      // The endpoint deletes the files this product alone was using and merely detaches the ones
      // other products still reference, so a count of its links would overstate what went.
      description: 'Files no other product was using were deleted with it; shared ones were only detached.',
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
        description="Nothing on Product mentions media — no column, no navigation, no migration. An attachment row points at a product by name and key, so one image can belong to several products at once, and each of them decides what it uses the image for. Deleting a product deletes the files it alone was using and unlinks the rest."
        variant="naked"
      >
        <UBadge
          color="neutral"
          variant="subtle"
          class="w-fit font-mono"
          label="POST api/media/{id}/attachments"
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
            <!-- Grouped by role rather than shown as one heap: which image is the main one, and what
                 order the gallery is in, is the whole point of the attachment carrying a role. -->
            <div
              v-for="group in groups(product)"
              :key="group.role"
              class="flex flex-col gap-1.5"
            >
              <div class="flex items-center gap-2">
                <UBadge
                  :color="group.role === 'primary' ? 'primary' : 'neutral'"
                  variant="subtle"
                  size="sm"
                  :label="group.role"
                />
                <span v-if="group.role === 'gallery'" class="text-xs text-dimmed">
                  in order
                </span>
              </div>

              <div class="flex flex-wrap gap-2">
                <UDropdownMenu
                  v-for="image in group.images"
                  :key="`${group.role}-${image.id}`"
                  :items="imageActions(product, image)"
                >
                  <button
                    type="button"
                    class="relative size-16 overflow-hidden rounded-md border border-default transition hover:border-inverted/30"
                    :class="group.role === 'primary' && 'ring-2 ring-primary ring-offset-1 ring-offset-default'"
                    :title="`${image.fileName} · ${formatBytes(image.size)}`"
                    :aria-label="`Actions for ${image.fileName}`"
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
                        attachments: null,
                        uploadedBy: null,
                        createdAt: '',
                        updatedAt: null,
                        url: null
                      }"
                      size="sm"
                      class="size-full"
                    />
                  </button>
                </UDropdownMenu>
              </div>
            </div>

            <p v-if="!product.images.length" class="text-sm text-muted">
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
        :version="mediaVersion"
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
