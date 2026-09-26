<script setup lang="ts">
import type { MediaFile } from '~/types'

/**
 * One file in full: a preview, everything the row records, and the fields that can be changed.
 *
 * The bytes, their size, their checksum and where they live are shown but not editable — the API
 * refuses to change them, because replacing content means a new upload.
 */
const props = defineProps<{
  file: MediaFile
  busy?: boolean
}>()

const emit = defineEmits<{
  save: [patch: { fileName: string, category: string, tags: string[] }]
  detach: []
  remove: []
}>()

const open = defineModel<boolean>('open', { default: false })

const fileName = ref(props.file.fileName)
const category = ref(props.file.category ?? '')
const tags = ref<string[]>([...props.file.tags])

// The slideover is reused for whichever file is selected, so its fields follow the selection.
watch(() => props.file, (file) => {
  fileName.value = file.fileName
  category.value = file.category ?? ''
  tags.value = [...file.tags]
}, { deep: true })

const dirty = computed(() =>
  fileName.value.trim() !== props.file.fileName
  || category.value.trim() !== (props.file.category ?? '')
  || tags.value.join(',') !== props.file.tags.join(','))

function save() {
  emit('save', {
    fileName: fileName.value.trim(),
    category: category.value.trim(),
    tags: tags.value
  })
}

/** Everything the row records that is not editable, in the order it is worth reading. */
const facts = computed(() => [
  { label: 'Type', value: props.file.contentType, mono: true },
  { label: 'Size', value: formatBytes(props.file.size) },
  { label: 'Uploaded', value: formatDate(props.file.createdAt) },
  { label: 'Updated', value: props.file.updatedAt ? formatDate(props.file.updatedAt) : '—' },
  { label: 'Uploaded by', value: props.file.uploadedBy ?? '—' },
  { label: 'Checksum', value: props.file.checksum ?? '—', mono: true, truncate: true },
  { label: 'Id', value: props.file.id, mono: true, truncate: true }
])
</script>

<template>
  <USlideover
    v-model:open="open"
    :title="file.fileName"
    description="Everything the media row records. The bytes, their size and where they live are fixed — replacing content means a new upload."
    :ui="{ content: 'sm:max-w-lg' }"
  >
    <template #body>
      <div class="flex flex-col gap-5">
        <div class="relative aspect-[16/9] w-full overflow-hidden rounded-lg border border-default">
          <MediaThumbnail :file="file" size="lg" class="absolute inset-0 size-full" />
        </div>

        <div class="flex flex-wrap gap-2">
          <UButton
            icon="i-lucide-external-link"
            color="neutral"
            variant="outline"
            size="sm"
            label="Open"
            :to="contentUrl(file.id)"
            target="_blank"
          />
          <UButton
            icon="i-lucide-download"
            color="neutral"
            variant="outline"
            size="sm"
            label="Download"
            :to="contentUrl(file.id, true)"
          />
          <UButton
            v-if="file.url"
            icon="i-lucide-link-2"
            color="neutral"
            variant="outline"
            size="sm"
            label="Direct link"
            :to="file.url"
            target="_blank"
            title="A signed, expiring link straight to the storage backend"
          />
        </div>

        <UFormField label="File name" description="The name the file is offered under. The stored path does not change.">
          <UInput v-model="fileName" class="w-full" />
        </UFormField>

        <UFormField label="Category" description="A coarse bucket. Changing it does not move the stored bytes.">
          <UInput v-model="category" placeholder="None" class="w-full" />
        </UFormField>

        <UFormField label="Tags" description="Up to 32, each 64 characters, no commas.">
          <UInputTags v-model="tags" placeholder="Add a tag…" class="w-full" />
        </UFormField>

        <UFormField v-if="file.ownerEntityName" label="Attached to">
          <div class="flex items-center gap-2">
            <UBadge
              color="primary"
              variant="subtle"
              icon="i-lucide-link"
              :label="`${file.ownerEntityName} ${file.ownerEntityId}`"
            />
            <UButton
              icon="i-lucide-unlink"
              color="neutral"
              variant="ghost"
              size="xs"
              label="Detach"
              :loading="busy"
              @click="emit('detach')"
            />
          </div>
        </UFormField>

        <USeparator />

        <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
          <template v-for="fact in facts" :key="fact.label">
            <dt class="text-muted">
              {{ fact.label }}
            </dt>
            <dd
              class="text-highlighted"
              :class="[fact.mono && 'font-mono text-xs', fact.truncate && 'truncate']"
              :title="fact.truncate ? fact.value : undefined"
            >
              {{ fact.value }}
            </dd>
          </template>
        </dl>

        <UAlert
          color="neutral"
          variant="subtle"
          icon="i-lucide-hard-drive"
          title="Where the bytes are"
          :description="`Not shown by the API's own responses, on purpose — the storage layout is not a client's business. This dashboard talks to the API as an administrator would.`"
        />
      </div>
    </template>

    <template #footer>
      <div class="flex w-full items-center gap-2">
        <UButton
          icon="i-lucide-trash-2"
          color="error"
          variant="ghost"
          label="Delete"
          :loading="busy"
          @click="emit('remove')"
        />
        <UButton
          class="ms-auto"
          color="neutral"
          variant="ghost"
          label="Close"
          @click="open = false"
        />
        <UButton
          icon="i-lucide-save"
          label="Save"
          :disabled="!dirty"
          :loading="busy"
          @click="save"
        />
      </div>
    </template>
  </USlideover>
</template>
