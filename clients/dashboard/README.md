# DotNetBoost.Media — Dashboard

A [Nuxt 4](https://nuxt.com) single-page client for the REST endpoints that
`DotNetBoost.Media.API` maps. A sidebar links to **Media** — the whole library, searchable,
filterable, sortable and paged — and to **Products**, the sample's own entity, where files are
attached to something.

Built on the [Nuxt UI dashboard template](https://github.com/nuxt-ui-templates/dashboard)
(collapsible sidebar, command palette, light/dark mode, theme picker), the same one
`DotNetBoost.Settings` and `DotNetBoost.MultiLingual` use.

## Setup

```bash
npm install
```

## Development

The dashboard needs the API running. Easiest is the Aspire AppHost, which starts both:

```bash
dotnet run --project ../../aspire/DotNetBoost.Media.AppHost
```

To run them separately, from the repository root:

```bash
dotnet run --project samples/SampleApp --urls http://localhost:5180
```

Then, in `clients/dashboard`:

```bash
npm run dev
```

The dashboard is at `http://localhost:3000`.

## Configuration

Copy `.env.example` to `.env` to point the dashboard at a different API.

| Variable | Default | Purpose |
|---|---|---|
| `NUXT_MEDIA_API_URL` | `http://localhost:5180` | Base address of the API |
| `NUXT_PUBLIC_API_REFERENCE_URL` | `http://localhost:5180/scalar` | Target of the sidebar's "API reference" link |
| `NUXT_PUBLIC_MAX_UPLOAD_BYTES` | `26214400` | The API's `MaxFileSizeBytes`, so the upload form can refuse an oversized file before sending it |

The browser never calls the API directly: `server/api/[...path].ts` proxies `/api/**` through
Nitro to `NUXT_MEDIA_API_URL`. That keeps the API free of CORS configuration, makes the target a
deploy-time setting rather than a build-time one, and means an upload streams through rather than
being buffered here.

It also means the sample's own routes have to live under `/api` — which is why
`samples/SampleApp` maps `/api/products` rather than `/products`.

## The one browser, used twice

`components/media/Browser.vue` **is** the Media page, and it is also what the picker on the
products page shows in a modal. "The same functionality in the popup" is not a second
implementation with the same features — it is this component with `selectable` set, so anything
added to it appears in both and neither can drift from the other.

```
pages/media.vue          →  MediaBrowser
pages/products.vue       →  MediaPickerModal  →  MediaBrowser  (browse tab, selectable)
                                              →  MediaBrowser  (attached tab, filtered by owner)
```

Each browser owns its own `useMediaLibrary`, so the picker's search and page are independent of
the page's — opening the picker cannot disturb what the page behind it was showing.

## Everything is filtered by the API

The search box, the category, the tag, the kind, the sort and the page all go into the query
string of `GET api/media`. Nothing is filtered in the browser.

That distinction matters the moment there is a second page: filtering the rows already on screen
searches one page and calls it the library. It is also why `DotNetBoost.Media` gained `search`,
`sortBy` and `descending` — a dashboard that pages cannot honestly offer a search box without
them.

The one exception is the category and tag **suggestions**, which are collected from the rows the
dashboard has loaded so far. The API has no distinct-values endpoint, so they are a convenience,
not a complete list — both filters are comboboxes that accept free text, so a value that has not
been seen yet can still be typed.

## How it maps to the API

| File | Role |
|---|---|
| `app/composables/useMediaLibrary.ts` | One listing: its query, its page, and the calls that change it |
| `app/components/media/Browser.vue` | The library — filters, grid, pagination, upload, details |
| `app/components/media/Filters.vue` | Search, category, tag, kind, sort |
| `app/components/media/Card.vue` | One file in the grid |
| `app/components/media/Thumbnail.vue` | The image, or an icon for its type |
| `app/components/media/UploadModal.vue` | `POST api/media`, multipart |
| `app/components/media/DetailSlideover.vue` | `PATCH api/media/{id}` and delete |
| `app/components/media/PickerModal.vue` | The library in a modal, for attaching |
| `app/pages/media.vue` | The Media page |
| `app/pages/products.vue` | The sample's products, and the attach button |
| `app/utils/format.ts` | Bytes, dates, icons, content URLs |
| `server/api/[...path].ts` | Proxy to the .NET API |

## Notes

- **Thumbnails are fetched through the proxy**, not through a file's `url`. A backend URL is
  signed and expires, so it would 403 in a long-open tab; the proxied route is stable and passes
  through whatever authorization the API has in front of it.
- **SVG is shown as an icon, not inline.** An SVG is an image the browser renders by executing it,
  and the library's own docs say to serve user uploads from another origin for exactly that reason.
- **The dashboard is an administrative client.** It talks to endpoints that
  `MapMediaEndpoints()` maps anonymously; a deployment puts authorization in front of both.
