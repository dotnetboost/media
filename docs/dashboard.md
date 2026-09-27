# Dashboard

`clients/dashboard` is a [Nuxt 4](https://nuxt.com) single-page client for the endpoints
`MapMediaEndpoints()` maps. It is the same template `DotNetBoost.Settings` and
`DotNetBoost.MultiLingual` use — Nuxt UI's dashboard layout, a collapsible sidebar, a command
palette, light and dark mode.

```bash
dotnet run --project aspire/DotNetBoost.Media.AppHost
```

The AppHost starts the API, its database and the dashboard together, and prints a clickable URL
for each. To run them separately, see
[clients/dashboard/README.md](../clients/dashboard/README.md).

## What is in it

| | |
|---|---|
| **Media** | The whole library. Search by file name, narrow by category, tag or kind, sort by date, name or size, page through it, upload, inspect, rename, retag, delete. Opening one file shows **every entity using it**, each with its roles and its own detach button. |
| **Products** | The sample's own entity. Each product's media is grouped by what it is for — the primary image, the gallery in its own order, anything else after — with make-primary, move, and remove-from-product on each. The button opens the library in a modal to attach a file that already exists, or upload a new one straight onto it. |
| **Overview** | A few counts, and the way in. |

## One browser, used twice

`components/media/Browser.vue` **is** the Media page, and it is also what the picker on the
products page shows in a modal.

```
pages/media.vue      →  MediaBrowser
pages/products.vue   →  MediaPickerModal  →  MediaBrowser   (browse tab, selectable)
                                          →  MediaBrowser   (attached tab, filtered by owner and role)
```

That is worth stating because "the same functionality in the popup" is the kind of requirement
usually met by writing a second, cut-down browser that slowly diverges from the first. Here the
modal is the same component with `selectable` set: anything added to it appears in both, and
neither can drift.

Each instance owns its own listing state, so opening the picker cannot disturb what the page
behind it was showing.

## Detaching and deleting are different buttons

The distinction the library rests on is visible in the UI, because getting it wrong destroys data:

- **Remove from product** detaches. The file stays in the library and stays attached to every other
  product using it. On the Media page the same thing is a click on one of a file's role chips, since
  each chip is one link.
- **Delete**, on the Media page, removes the file and every link to it. The confirmation says how
  many entities lose it and names them — *"It is attached to 2 entities, and all of them lose it:
  Product 2, Product 1."*

Attaching a file to a second product does not take it off the first, so there is nothing to warn
about there. The links a file has are read one file at a time, because a listing does not carry
them.

## Roles in the picker

The picker chooses what the next attach is **for**, with the cardinality in the verb: `gallery`
appends, `primary` and `seo` replace whatever held the role. The "Attached" badge counts the
selected role, so an image already in the gallery is still offered as a candidate for primary —
which is a perfectly reasonable thing to want.

## Everything is filtered by the API

The search box, the category, the tag, the kind, the sort and the page all go into the query
string of `GET api/media`. Nothing is filtered in the browser.

The distinction matters the moment there is a second page: filtering the rows already on screen
searches one page and calls it the library. It is also why `MediaQuery` has `Search`, `SortBy` and
`Descending` at all — a dashboard that pages cannot honestly offer a search box without them. See
[querying.md](querying.md).

The one exception is the category and tag **suggestions**, collected from the rows the dashboard
has loaded so far. The API has no distinct-values endpoint, so they are a convenience rather than
a complete list; both filters accept free text, so a value that has not been seen yet can still be
typed.

## It only ever talks to its own origin

`server/api/[...path].ts` proxies `/api/**` through Nitro to `NUXT_MEDIA_API_URL`. That keeps the
API free of CORS configuration, makes its address a deploy-time setting rather than a build-time
one, and lets an upload stream through rather than being buffered in the middle.

It is also why the sample maps `/api/products` rather than `/products`: one proxy reaches the
library's endpoints and the sample's alike.

Thumbnails go through that same proxy rather than through a file's `url`. A backend URL is signed
and expires, so it would start returning 403 in a long-open tab; the proxied route is stable and
passes through whatever authorization sits in front of the API.

## Before deploying one

The dashboard is an administrative client for endpoints that `MapMediaEndpoints()` maps
**anonymously**. Put authorization in front of both — see
[validation-and-security.md](validation-and-security.md#authorization). Nothing in the dashboard
is a substitute for it: it is a browser client, and every call it makes is one anybody could make
by hand.

## Configuration

| Variable | Default | |
|---|---|---|
| `NUXT_MEDIA_API_URL` | `http://localhost:5180` | Base address of the API |
| `NUXT_PUBLIC_API_REFERENCE_URL` | `http://localhost:5180/scalar` | The sidebar's "API reference" link |
| `NUXT_PUBLIC_MAX_UPLOAD_BYTES` | `26214400` | The API's `MaxFileSizeBytes`, so the form refuses an oversized file before sending it. Courtesy, not a control — the API enforces the real limit either way. |

Under the AppHost all three are injected, pointing at whichever ports Aspire allocated.
