// https://nuxt.com/docs/api/configuration/nuxt-config
export default defineNuxtConfig({
  modules: [
    '@nuxt/eslint',
    '@nuxt/ui',
    '@vueuse/nuxt'
  ],

  // Client-rendered single-page app. Nitro is still used, but only to proxy the
  // API (see server/api/[...path].ts) — never to render pages.
  ssr: false,

  devtools: {
    enabled: true
  },

  css: ['~/assets/css/main.css'],

  runtimeConfig: {
    // Base address of the API: the media endpoints and the sample's product endpoints. Read by
    // the proxy handler only, so the browser never sees it. Override with NUXT_MEDIA_API_URL.
    mediaApiUrl: 'http://localhost:5180',

    public: {
      // Linked from the sidebar — the API's own Scalar reference.
      // Override with NUXT_PUBLIC_API_REFERENCE_URL.
      apiReferenceUrl: 'http://localhost:5180/scalar',

      // MediaOptions.MaxFileSizeBytes on the API. The upload form refuses a file over this before
      // sending it, so the user is told immediately rather than after the bytes have gone up and
      // come back as a 413. The API enforces it either way — this is courtesy, not a control.
      // Override with NUXT_PUBLIC_MAX_UPLOAD_BYTES.
      maxUploadBytes: 26214400
    }
  },

  compatibilityDate: '2026-06-30',

  eslint: {
    config: {
      stylistic: {
        commaDangle: 'never',
        braceStyle: '1tbs'
      }
    }
  }
})
