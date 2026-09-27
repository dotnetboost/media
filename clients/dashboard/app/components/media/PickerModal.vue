<script setup lang="ts">
import type { MediaFile, MediaPage } from '~/types'

/**
 * The media library in a modal, for attaching a file to something.
 *
 * Two tabs: everything in the library, and what this entity already has. Both are the same
 * `MediaBrowser` the Media page is, so searching, filtering, sorting, paging and uploading all
 * behave identically here — there is no second, cut-down implementation to drift.
 */
const props = defineProps<{
  owner: { entityName: string, entityId: string }
  title?: string
  /**
   * Bumped by the host once an attach it was asked for has actually landed.
   *
   * The attach is the host's to make — it knows its own endpoints — and an emit cannot be awaited,
   * so refreshing straight after emitting would read the state from before the write. This is the
   * host saying "now".
   */
  version?: number
}>()

const emit = defineEmits<{
  attach: [file: MediaFile, role: string, replace: boolean]
  changed: []
}>()

const open = defineModel<boolean>('open', { default: false })

const tab = ref<'library' | 'attached'>('library')

/**
 * What the next attach is for. A role belongs to the link rather than to the file, so it is chosen
 * here, when the link is made, and not on the file itself.
 *
 * `primary` and `seo` replace whatever holds them — a product has one main image and one SEO image —
 * while `gallery` appends. That difference cannot live in the schema, because a rule that fits
 * `primary` would forbid a second gallery shot, so the picker states it.
 */
const roles = [
  { label: 'Gallery', value: 'gallery', replace: false, hint: 'Appended to the end of the gallery.' },
  { label: 'Primary', value: 'primary', replace: true, hint: 'Becomes the one main image, replacing whatever held that role.' },
  { label: 'SEO', value: 'seo', replace: true, hint: 'Becomes the one SEO image, replacing whatever held that role.' }
]

const role = ref('gallery')

const selectedRole = computed(() => roles.find(r => r.value === role.value) ?? roles[0]!)

const browse = useTemplateRef('browse')
const attachedBrowser = useTemplateRef('attachedBrowser')

/**
 * What is already attached **in the selected role**, so the browse tab can say so rather than
 * offering it again.
 *
 * Narrowed by role rather than by owner alone, because a file can hold several roles on one entity:
 * a gallery image is a perfectly good candidate for primary, and a badge that only knew the file was
 * attached somewhere would refuse to offer it.
 *
 * Asked of the media API directly rather than taken from whatever the host page calls its images.
 * The sample's product endpoint, for one, only surfaces the `product-images` category — a file
 * attached under any other category is genuinely attached but would be missing from that list,
 * and the browse tab would offer to attach it a second time.
 */
const attachedIds = ref<string[]>([])

async function loadAttached() {
  try {
    const page = await $fetch<MediaPage>('/api/media', {
      query: {
        ownerEntityName: props.owner.entityName,
        ownerEntityId: props.owner.entityId,
        role: role.value,
        take: 500
      }
    })

    attachedIds.value = page.items.map(file => file.id)
  } catch {
    // The browse tab simply offers everything. Attaching is idempotent — the same file in the same
    // role for the same entity is a no-op rather than an error or a second link — and a failed
    // count is not worth an error banner over the library itself.
    attachedIds.value = []
  }
}

// The badge means "already in this role", so changing the role changes which files carry it.
watch(role, () => loadAttached())

watch(() => props.version, () => reloadAll())

const tabs = computed(() => [
  { label: 'All files', value: 'library', icon: 'i-lucide-images' },
  { label: `As ${role.value} (${attachedIds.value.length})`, value: 'attached', icon: 'i-lucide-link' }
])

/** Both tabs show the same rows from different angles, so a change in one has to reach the other. */
async function reloadAll() {
  await loadAttached()
  browse.value?.reload()
  attachedBrowser.value?.reload()
  emit('changed')
}

function onAttach(file: MediaFile) {
  // No refresh here: the host has not written anything yet. `version` is what brings the tabs and
  // the badges up to date once it has.
  emit('attach', file, selectedRole.value.value, selectedRole.value.replace)
}

watch(open, (isOpen) => {
  if (isOpen) {
    loadAttached()
  }
})

onMounted(() => {
  if (open.value) {
    loadAttached()
  }
})
</script>

<template>
  <UModal
    v-model:open="open"
    :title="title ?? `Media for ${owner.entityName} ${owner.entityId}`"
    description="Attach a file that is already in the library, or upload a new one — it is stored and attached in one step. A file may belong to several entities at once; attaching it here does not take it off anything else."
    :ui="{ content: 'max-w-5xl' }"
  >
    <template #body>
      <div class="flex flex-col gap-4">
        <div class="flex flex-wrap items-end justify-between gap-3">
          <UTabs
            v-model="tab"
            :items="tabs"
            :content="false"
            size="sm"
          />

          <UFormField
            v-if="tab === 'library'"
            label="Attach as"
            :description="selectedRole.hint"
            :ui="{ description: 'text-xs' }"
          >
            <USelect
              v-model="role"
              :items="roles"
              value-key="value"
              size="sm"
              class="w-40"
            />
          </UFormField>
        </div>

        <!-- Both are kept mounted: switching tabs should not throw away a search you just typed. -->
        <MediaBrowser
          v-show="tab === 'library'"
          ref="browse"
          selectable
          compact
          :attached-ids="attachedIds"
          :upload-owner="owner"
          :upload-role="role"
          :page-size="9"
          @select="onAttach"
          @changed="reloadAll"
        />

        <MediaBrowser
          v-show="tab === 'attached'"
          ref="attachedBrowser"
          compact
          :owner="owner"
          :role="role"
          :upload-owner="owner"
          :upload-role="role"
          :page-size="9"
          @changed="reloadAll"
        />
      </div>
    </template>

    <template #footer>
      <div class="flex w-full justify-end">
        <UButton
          label="Done"
          color="neutral"
          variant="ghost"
          @click="open = false"
        />
      </div>
    </template>
  </UModal>
</template>
