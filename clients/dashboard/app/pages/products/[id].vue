<script setup lang="ts">
import type { MediaFile, MediaRole, Product, ProductImage } from '~/types'

/**
 * One product, and the files it uses — a box per role rather than a heap with a dropdown over it.
 *
 * The role is a property of the link, so something has to choose it when the link is made. The
 * picker used to ask afterwards, in a "Attach as" select next to a grid of every file in the
 * library: you picked the file first and named its purpose second, and nothing on screen said
 * which roles the product was still short of. Here the boxes are the question. An empty
 * `primary` box is the product telling you it has no main image, and clicking it opens the
 * library already committed to that role.
 *
 * The roles are read from the API, not written down here. `Product` declares them with
 * `[MediaRole]` attributes and `GET api/media/roles/Product` hands them over, so a fourth role on
 * the entity grows a fourth box with no change to this page.
 *
 * Nothing on this page is a draft: an attach or a detach is a request of its own, landed before
 * the box redraws. Only the name, SKU and price are edited and saved, and saving them cannot
 * touch the media — a product's files are attachment rows pointing at it, not columns on it.
 */
const route = useRoute()
const router = useRouter()
const toast = useToast()

const id = computed(() => Number(route.params.id))

const product = ref<Product | null>(null)
const pending = ref(true)
const loadError = ref('')

/** The media id an attach or detach is running on, for a per-box spinner. */
const busy = ref<string | null>(null)
const saving = ref(false)

/** Bumped after every change, so the open picker re-reads which files already hold the role. */
const mediaVersion = ref(0)

/** The role the open picker is attaching for — and `null` when no picker is open. */
const picking = ref<string | null>(null)

const owner = computed(() => ({ entityName: 'Product', entityId: String(id.value) }))

const details = reactive({ sku: '', name: '', price: 0 })

useHead(() => ({ title: `${product.value?.name ?? 'Product'} — DotNetBoost.Media` }))

/**
 * The roles this entity declares, in the order it declares them.
 *
 * An entity that declares none comes back empty; the page then falls back to the roles its files
 * are actually attached under, so it still shows what the product has even when nothing enforces
 * a vocabulary. It cannot offer an empty box for a role nobody has named — there is no such list.
 */
const declared = ref<MediaRole[]>([])

async function loadRoles() {
  try {
    declared.value = await $fetch<MediaRole[]>('/api/media/roles/Product')
  } catch {
    // Not worth blocking the page over: the fallback below still draws every attached file.
    declared.value = []
  }
}

async function load() {
  pending.value = true
  loadError.value = ''

  try {
    const loaded = await $fetch<Product>(`/api/products/${id.value}`)

    product.value = loaded
    details.sku = loaded.sku
    details.name = loaded.name
    details.price = loaded.price
  } catch (error) {
    loadError.value = problemMessage(error)
    product.value = null
  } finally {
    pending.value = false
  }
}

/**
 * One row per role: the images in it, and whether it can hold more than one.
 *
 * A single-valued role gets exactly one box — filled or empty — because that is what "single" means
 * on screen. A multi-valued one gets a box per file plus a trailing empty one to append to.
 */
const rows = computed(() => {
  const images = product.value?.images ?? []

  const held = (role: string) => images
    .filter(image => image.role === role)
    .sort((a, b) => a.sortOrder - b.sortOrder)

  const names = declared.value.length
    ? declared.value.map(role => role.name)
    // Nothing declared: show what is attached, which is all this page can honestly know about.
    : [...new Set(images.map(image => image.role))].sort()

  return names.map((name) => {
    const role = declared.value.find(r => r.name === name)

    return {
      name,
      multiple: role?.multiple ?? true,
      images: held(name)
    }
  })
})

const total = computed(() => product.value?.images.length ?? 0)

const dirty = computed(() => !!product.value && (
  details.sku !== product.value.sku
  || details.name !== product.value.name
  || Number(details.price) !== product.value.price
))

/** Runs one call, reports the outcome, and reloads the product. */
async function act(key: string | null, call: () => Promise<unknown>, success: string, failure: string) {
  busy.value = key

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

/**
 * Attaches the picked file in the role the box that opened the picker stands for.
 *
 * `replace` is what makes a single-valued role single-valued: the engine takes the role over from
 * whatever held it, in one operation, and the displaced file is only detached — it stays in the
 * library and stays attached to every other product using it.
 */
function onAttach(file: MediaFile, role: string, replace: boolean) {
  return act(
    file.id,
    () => $fetch(`/api/media/${file.id}/attachments`, {
      method: 'POST',
      body: { ownerEntityName: 'Product', ownerEntityId: String(id.value), role, replace }
    }),
    `${file.fileName} is now this product's ${role}`,
    'Could not attach the file'
  )
}

/** Takes one image out of one role. The file itself is untouched. */
function onDetach(image: ProductImage) {
  return act(
    image.id,
    () => $fetch(`/api/products/${id.value}/images/${image.id}`, {
      method: 'DELETE',
      query: { role: image.role }
    }),
    `Removed ${image.fileName} from ${image.role}`,
    'Could not remove the image'
  )
}

/**
 * Moves one image one place within its role, and saves the whole order.
 *
 * The media endpoint rather than the sample's gallery-specific one, so a second multi-valued role
 * on `Product` would reorder without another route.
 */
function onMove(role: string, image: ProductImage, delta: -1 | 1) {
  const held = rows.value.find(row => row.name === role)?.images ?? []

  const from = held.findIndex(i => i.id === image.id)
  const to = from + delta

  if (from < 0 || to < 0 || to >= held.length) {
    return
  }

  const ids = held.map(i => i.id)
  ;[ids[from], ids[to]] = [ids[to]!, ids[from]!]

  return act(
    image.id,
    // Every id in the role, because a partial order has no meaning: the images left out would have
    // no defined position. The API refuses one.
    () => $fetch('/api/media/attachments/order', {
      method: 'PUT',
      body: {
        ownerEntityName: 'Product',
        ownerEntityId: String(id.value),
        role,
        mediaFileIds: ids
      }
    }),
    `${role} order saved`,
    'Could not save the order'
  )
}

async function save() {
  if (!dirty.value || !details.sku.trim() || !details.name.trim()) {
    return
  }

  saving.value = true

  try {
    await $fetch(`/api/products/${id.value}`, {
      method: 'PUT',
      body: { sku: details.sku.trim(), name: details.name.trim(), price: Number(details.price) || 0 }
    })

    toast.add({ title: 'Saved', icon: 'i-lucide-check', color: 'success' })
    await load()
  } catch (error) {
    toast.add({
      title: 'Could not save the product',
      description: problemMessage(error),
      icon: 'i-lucide-triangle-alert',
      color: 'error'
    })
  } finally {
    saving.value = false
  }
}

function revert() {
  if (product.value) {
    details.sku = product.value.sku
    details.name = product.value.name
    details.price = product.value.price
  }
}

// Navigating from one product to the next reuses this page, so the id is what to watch.
watch(id, load)

onMounted(async () => {
  await Promise.all([loadRoles(), load()])
})
</script>

<template>
  <UDashboardPanel id="product-edit">
    <template #header>
      <UDashboardNavbar :title="product?.name ?? 'Edit product'" :ui="{ right: 'gap-3' }">
        <template #leading>
          <UButton
            icon="i-lucide-arrow-left"
            color="neutral"
            variant="ghost"
            aria-label="Back to products"
            @click="router.push('/products')"
          />
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
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UAlert
        v-if="loadError"
        color="error"
        variant="subtle"
        icon="i-lucide-plug-zap"
        title="Could not load the product"
        :description="loadError"
        :actions="[
          { label: 'Try again', color: 'neutral', variant: 'outline', onClick: load },
          { label: 'Back to products', color: 'neutral', variant: 'ghost', to: '/products' }
        ]"
      />

      <div v-else-if="pending && !product" class="flex flex-col gap-4">
        <USkeleton class="h-40 rounded-lg" />
        <USkeleton class="h-64 rounded-lg" />
      </div>

      <template v-else-if="product">
        <!-- `shrink-0` on both: the panel body is a scrolling flex column, so a card left to shrink
             gives up height rather than letting the body scroll — and a `UCard` clips its overflow,
             which on a phone cut the price field off the bottom of the details card. -->
        <UCard variant="subtle" class="shrink-0">
          <template #header>
            <div class="flex items-center gap-3">
              <div class="min-w-0 flex-1">
                <p class="font-medium text-highlighted">
                  Details
                </p>
                <p class="text-sm text-muted">
                  The product's own columns. Nothing here mentions media.
                </p>
              </div>
              <UBadge color="neutral" variant="subtle" :label="`#${product.id}`" />
            </div>
          </template>

          <div class="grid gap-4 sm:grid-cols-3">
            <UFormField label="SKU" required>
              <UInput v-model="details.sku" class="w-full" />
            </UFormField>
            <UFormField label="Name" required>
              <UInput v-model="details.name" class="w-full" />
            </UFormField>
            <UFormField label="Price">
              <UInputNumber
                v-model="details.price"
                :min="0"
                :step="0.01"
                class="w-full"
              />
            </UFormField>
          </div>

          <template #footer>
            <div class="flex items-center justify-end gap-2">
              <UButton
                label="Revert"
                color="neutral"
                variant="ghost"
                :disabled="!dirty || saving"
                @click="revert"
              />
              <UButton
                label="Save details"
                icon="i-lucide-save"
                :loading="saving"
                :disabled="!dirty || !details.sku.trim() || !details.name.trim()"
                @click="save"
              />
            </div>
          </template>
        </UCard>

        <UCard variant="subtle" class="shrink-0">
          <template #header>
            <!-- `basis-64` rather than `flex-1` alone: a flex item with a zero basis never
                 outgrows its line, so `flex-wrap` never fires and the badge squeezes the prose to
                 one word a line on a phone instead of dropping below it. -->
            <div class="flex flex-wrap items-center gap-3">
              <div class="min-w-0 flex-1 basis-64">
                <p class="font-medium text-highlighted">
                  Media
                </p>
                <p class="text-sm text-muted">
                  A box per role. Fill an empty one from the library; the
                  <span class="font-mono text-xs">x</span> takes a file off this product without
                  deleting it — it stays in the library and stays attached to every other product
                  using it.
                </p>
              </div>
              <UBadge
                color="neutral"
                variant="subtle"
                class="font-mono"
                label="POST api/media/{id}/attachments"
              />
            </div>
          </template>

          <div class="flex flex-col gap-5">
            <div
              v-for="row in rows"
              :key="row.name"
              class="flex flex-col gap-2"
            >
              <div class="flex items-center gap-2">
                <UBadge
                  :color="row.name === 'primary' ? 'primary' : 'neutral'"
                  variant="subtle"
                  size="sm"
                  :label="row.name"
                />
                <span class="text-xs text-dimmed">
                  {{ row.multiple
                    ? `${row.images.length} file(s), in order`
                    : row.images.length ? 'one file' : 'empty' }}
                </span>
              </div>

              <div class="flex flex-wrap gap-3">
                <ProductRoleBox
                  v-for="(image, index) in row.images"
                  :key="`${row.name}-${image.id}`"
                  :image="image"
                  :role="row.name"
                  :highlight="row.name === 'primary'"
                  :replaceable="!row.multiple"
                  :can-move-earlier="row.multiple && index > 0"
                  :can-move-later="row.multiple && index < row.images.length - 1"
                  :busy="busy === image.id"
                  @pick="picking = row.name"
                  @detach="onDetach(image)"
                  @move="onMove(row.name, image, $event)"
                />

                <!-- A single-valued role that is full has no empty box: there is nowhere for a
                     second file to go, and the replace button on the one it holds is the way to
                     change it. -->
                <ProductRoleBox
                  v-if="row.multiple || !row.images.length"
                  :role="row.name"
                  @pick="picking = row.name"
                />
              </div>
            </div>

            <p v-if="!rows.length" class="text-sm text-muted">
              This entity declares no roles and has no files attached.
            </p>
          </div>

          <template #footer>
            <div class="flex flex-wrap items-center gap-2 text-xs text-muted">
              <UIcon name="i-lucide-info" class="size-4 shrink-0" />
              <span>
                {{ total }} file(s) attached. The roles come from
                <span class="font-mono">GET api/media/roles/Product</span>, which reads the
                <span class="font-mono">[MediaRole]</span> attributes on the entity — a role added
                there grows a box here.
              </span>
            </div>
          </template>
        </UCard>

        <MediaPickerModal
          :open="picking !== null"
          :owner="owner"
          :fixed-role="picking ?? undefined"
          :version="mediaVersion"
          :title="`Pick the ${picking} for ${product.name}`"
          @update:open="!$event && (picking = null)"
          @attach="onAttach"
          @changed="load"
        />
      </template>
    </template>
  </UDashboardPanel>
</template>
