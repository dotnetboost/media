/**
 * Proxies every `/api/**` call through to the API — the media endpoints and the sample's product
 * endpoints alike, which is why both live under `/api` on the server.
 *
 * The browser therefore only ever talks to this app's own origin, which keeps the API free of
 * CORS configuration and lets the target move between environments by changing
 * `NUXT_MEDIA_API_URL` alone. It also means an upload streams through Nitro rather than being
 * buffered here: `proxyRequest` forwards the request body as it arrives.
 */
export default defineEventHandler(async (event) => {
  const { mediaApiUrl } = useRuntimeConfig(event)
  const path = getRouterParam(event, 'path') ?? ''
  const { search } = getRequestURL(event)

  const target = `${mediaApiUrl.replace(/\/$/, '')}/api/${path}${search}`

  return proxyRequest(event, target)
})
