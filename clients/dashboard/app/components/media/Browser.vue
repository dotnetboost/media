<script setup lang="ts">
import type { MediaFile } from '~/types'

/**
 * The media library: filter, sort, page, upload, inspect, delete.
 *
 * This is the whole of the Media page, and it is also what the picker on the products page shows
 * in a modal — "the same functionality" there is not a second implementation, it is this one with
 * `selectable` set. Anything added here appears in both.
 *
 * It owns its own `useMediaLibrary`, so the picker's query is independent of the page's: opening
 * the picker cannot disturb what the page behind it was showing.
 */
const props = withDefaults(defineProps<{
  /** Restricts the listing to one entity's files. */
  owner?: { entityName: string, entityId: string }
  /** Uploads land attached to this entity. */
  uploadOwner?: { entityName: string, entityId: string }
  /** Shows an "Attach" action on each file instead of opening its details. */
  selectable?: boolean
  /** Ids already attached, shown as such rather than offered again. */
  attachedIds?: string[]
  /** Fewer columns and a tighter filter bar, for the modal. */
  compact?: boolean
  pageSize?: number
}>(), {
  attachedIds: () => [],
  pageSize: 12
})

const emit = defineEmits<{
  select: [file: MediaFile]
  changed: []
}>()

const owner = computed(() => props.owner)

const library = useMediaLibrary({ owner, pageSize: props.pageSize })
const {
  query, items, total, pending, loadError, busy,
  pageCount, isFiltered, categoryOptions, tagOptions,
  load, upload, update, detach, remove, reset
} = library

const uploading = ref(false)
const selected = ref<MediaFile | null>(null)
const deleting = ref<MediaFile | null>(null)

/** The selected file, re-read from the current page so the slideover follows a save. */
const selectedFile = computed(() => selected.value
  ? items.value.find(file => file.id === selected.value!.id) ?? selected.value
  : null)

const attached = (file: MediaFile) => props.attachedIds.includes(file.id)

const range = computed(() => {
  if (!total.value) {
    return ''
  }

  const from = (query.page - 1) * query.pageSize + 1
  const to = Math.min(query.page * query.pageSize, total.value)

  return `${from}–${to} of ${total.value}`
})

async function onUpload(input: { file: File, category?: string, tags?: string[] }) {
  const result = await upload({ ...input, owner: props.uploadOwner })

  if (result) {
    uploading.value = false
    emit('changed')
  }
}

async function onSave(patch: { fileName: string, category: string, tags: string[] }) {
  if (!selectedFile.value) {
    return
  }

  await update(selectedFile.value.id, {
    fileName: patch.fileName,
    // An emptied box means "no category", and the API needs an empty string to say so.
    category: patch.category,
    tags: patch.tags
  })

  emit('changed')
}

async function onDetach() {
  if (!selectedFile.value) {
    return
  }

  await detach(selectedFile.value.id)
  emit('changed')
}

async function onDelete() {
  const file = deleting.value
  if (!file) {
    return
  }

  await remove(file)

  deleting.value = null
  if (selected.value?.id === file.id) {
    selected.value = null
  }

  emit('changed')
}

function onSelect(file: MediaFile) {
  emit('select', file)
}

// The picker opens the browser inside a modal that is mounted once and reused, so the parent
// needs a way to reload it when something outside changed.
defineExpose({ reload: load })

onMounted(load)
</script>

<template>
  <div class="flex flex-col gap-4">
    <div class="flex flex-col gap-2 lg:flex-row lg:items-center">
      <MediaFilters
        :query="query"
        :category-options="categoryOptions"
        :tag-options="tagOptions"
        :is-filtered="isFiltered"
        :pending="pending"
        :compact="compact"
        class="flex-1"
        @update="Object.assign(query, $event)"
        @reset="reset"
      />

      <div class="flex items-center gap-2 lg:shrink-0">
        <UButton
          icon="i-lucide-refresh-cw"
          color="neutral"
          variant="ghost"
          :loading="pending"
          aria-label="Refresh"
          @click="load"
        />
        <UButton
          icon="i-lucide-upload"
          label="Upload"
          class="flex-1 justify-center lg:flex-none"
          @click="uploading = true"
        />
      </div>
    </div>

    <UAlert
      v-if="loadError"
      color="error"
      variant="subtle"
      icon="i-lucide-plug-zap"
      title="Could not load the media library"
      :description="loadError"
      :actions="[{ label: 'Try again', color: 'neutral', variant: 'outline', onClick: load }]"
    />

    <template v-else>
      <div class="flex flex-wrap items-center gap-2 text-sm text-muted">
        <UBadge
          color="neutral"
          variant="subtle"
          class="font-mono"
          label="GET api/media"
        />
        <span v-if="range">{{ range }}</span>
        <span v-if="isFiltered" class="text-dimmed">· filtered</span>
      </div>

      <div v-if="pending && !items.length" :class="['grid gap-4', compact ? 'sm:grid-cols-2 lg:grid-cols-3' : 'sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4']">
        <USkeleton v-for="n in pageSize" :key="n" class="h-56 rounded-lg" />
      </div>

      <div
        v-else-if="!items.length"
        class="flex flex-col items-center gap-3 rounded-lg border border-dashed border-default py-16 text-center"
      >
        <UIcon name="i-lucide-image-off" class="size-8 text-dimmed" />
        <div>
          <p class="font-medium text-highlighted">
            {{ isFiltered ? 'Nothing matches this filter' : 'No files yet' }}
          </p>
          <p class="text-sm text-muted">
            {{ isFiltered ? 'Try a different search, category or tag.' : 'Upload one to get started.' }}
          </p>
        </div>
        <UButton
          v-if="isFiltered"
          icon="i-lucide-filter-x"
          color="neutral"
          variant="outline"
          label="Clear filters"
          @click="reset"
        />
        <UButton
          v-else
          icon="i-lucide-upload"
          label="Upload a file"
          @click="uploading = true"
        />
      </div>

      <div
        v-else
        :class="['grid gap-4', compact ? 'sm:grid-cols-2 lg:grid-cols-3' : 'sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4']"
      >
        <MediaCard
          v-for="file in items"
          :key="file.id"
          :file="file"
          :selectable="selectable"
          :attached="attached(file)"
          :busy="busy === file.id"
          @open="selected = file"
          @select="onSelect(file)"
        />
      </div>

      <div v-if="pageCount > 1" class="flex justify-center pt-1">
        <UPagination
          v-model:page="query.page"
          :total="total"
          :items-per-page="query.pageSize"
          :sibling-count="compact ? 0 : 1"
        />
      </div>
    </template>

    <MediaUploadModal
      v-model:open="uploading"
      :owner="uploadOwner"
      :default-category="query.category || undefined"
      :busy="pending"
      @upload="onUpload"
    />

    <MediaDetailSlideover
      v-if="selectedFile"
      :open="selected !== null"
      :file="selectedFile"
      :busy="busy === selectedFile.id"
      @update:open="!$event && (selected = null)"
      @save="onSave"
      @detach="onDetach"
      @remove="deleting = selectedFile"
    />

    <ConfirmModal
      :open="deleting !== null"
      :title="`Delete ${deleting?.fileName}?`"
      description="The bytes are removed from the storage backend. With soft delete on — the default — the row is kept as a record that the file existed, but the content is gone either way."
      confirm-label="Delete"
      :loading="busy === deleting?.id"
      @update:open="!$event && (deleting = null)"
      @confirm="onDelete"
    />
  </div>
</template>
