<script setup lang="ts">
useHead({ title: 'Media — DotNetBoost.Media' })
</script>

<template>
  <UDashboardPanel id="media">
    <template #header>
      <UDashboardNavbar title="Media">
        <template #leading>
          <UDashboardSidebarCollapse />
        </template>
      </UDashboardNavbar>
    </template>

    <template #body>
      <UPageCard
        title="Every uploaded file"
        description="One row per upload, whatever it is attached to. Search by name, narrow by category or tag, sort, and page — all of it in the query the API is asked, not in the rows already on screen."
        variant="naked"
      />

      <MediaBrowser :page-size="12" />
    </template>
  </UDashboardPanel>
</template>
