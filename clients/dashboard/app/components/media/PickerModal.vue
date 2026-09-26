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
}>()

const emit = defineEmits<{
  attach: [file: MediaFile]
  changed: []
}>()

const open = defineModel<boolean>('open', { default: false })

const tab = ref<'library' | 'attached'>('library')

const browse = useTemplateRef('browse')
const attachedBrowser = useTemplateRef('attachedBrowser')

/**
 * What is already attached, so the browse tab can say so rather than offering it again.
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
        take: 500
      }
    })

    attachedIds.value = page.items.map(file => file.id)
  } catch {
    // The browse tab simply offers everything; the API refuses a duplicate attach anyway, and a
    // failed count is not worth an error banner over the library itself.
    attachedIds.value = []
  }
}

const tabs = computed(() => [
  { label: 'All files', value: 'library', icon: 'i-lucide-images' },
  { label: `Attached (${attachedIds.value.length})`, value: 'attached', icon: 'i-lucide-link' }
])

/** Both tabs show the same rows from different angles, so a change in one has to reach the other. */
async function reloadAll() {
  await loadAttached()
  browse.value?.reload()
  attachedBrowser.value?.reload()
  emit('changed')
}

async function onAttach(file: MediaFile) {
  emit('attach', file)
  await loadAttached()
  attachedBrowser.value?.reload()
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
    description="Attach a file that is already in the library, or upload a new one — it is stored and attached in one step."
    :ui="{ content: 'max-w-5xl' }"
  >
    <template #body>
      <div class="flex flex-col gap-4">
        <UTabs
          v-model="tab"
          :items="tabs"
          :content="false"
          size="sm"
        />

        <!-- Both are kept mounted: switching tabs should not throw away a search you just typed. -->
        <MediaBrowser
          v-show="tab === 'library'"
          ref="browse"
          selectable
          compact
          :attached-ids="attachedIds"
          :upload-owner="owner"
          :page-size="9"
          @select="onAttach"
          @changed="reloadAll"
        />

        <MediaBrowser
          v-show="tab === 'attached'"
          ref="attachedBrowser"
          compact
          :owner="owner"
          :upload-owner="owner"
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
