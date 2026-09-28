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
| **Products** | The sample's own entity, each with what it has attached, grouped by what it is for. Opening one gives it a page of its own — its columns, and **a box per role**: empty with a `+`, or holding the image with an `x` that takes it off this product. |
| **Overview** | A few counts, and the way in. |

## One browser, used twice

`components/media/Browser.vue` **is** the Media page, and it is also what the picker on the
products page shows in a modal.

```
pages/media.vue          →  MediaBrowser
pages/products/[id].vue  →  ProductRoleBox  →  MediaPickerModal  →  MediaBrowser   (browse tab, selectable)
                                                                 →  MediaBrowser   (attached tab, by owner and role)
```

That is worth stating because "the same functionality in the popup" is the kind of requirement
usually met by writing a second, cut-down browser that slowly diverges from the first. Here the
modal is the same component with `selectable` set: anything added to it appears in both, and
neither can drift.

Each instance owns its own listing state, so opening the picker cannot disturb what the page
behind it was showing.

## Detaching and deleting are different buttons

The distinction the library rests on is visible in the UI, because getting it wrong destroys data:

- **The `x` on a role box** detaches. The file stays in the library and stays attached to every other
  product using it. On the Media page the same thing is a click on one of a file's role chips, since
  each chip is one link.
- **Delete**, on the Media page, removes the file and every link to it. The confirmation says how
  many entities lose it and names them — *"It is attached to 2 entities, and all of them lose it:
  Product 2, Product 1."*

Attaching a file to a second product does not take it off the first, so there is nothing to warn
about there. The links a file has are read one file at a time, because a listing does not carry
them.

## Roles are boxes, not a dropdown

A role is a property of the **link**, so something has to choose one when the link is made. The
obvious control is a select beside the library — *attach as: primary / gallery / seo* — and it is the
wrong one three times over: it asks after the file has been picked, it answers a question the page
already knew the answer to, and nothing on screen says which roles the product is still short of.

The product page makes the roles the control instead. Each one the entity declares gets a box, and
an empty `primary` box is the product saying it has no main image without being asked:

| | |
|---|---|
| Single-valued (`primary`, `seo`) | Exactly one box. Full, it offers **replace**, which attaches with `replace: true` — the engine takes the role over from whatever held it, in one operation, and the displaced file is only detached. |
| Multi-valued (`gallery`) | A box per file, in the order the product put them in, with arrows to move one within the role, and a trailing empty box to append to. |

Clicking a box opens the picker with the role already fixed, so the modal renders **no role control
at all** — there is no second answer to keep in step with the first.

The list still **asks the entity** rather than assuming: `GET api/media/roles/{entityName}` returns
what that entity declared, so a product offers `primary / gallery / seo`, an invoice offers its own,
and a fourth role on the entity grows a fourth box with no change to the client. Whether attaching
replaces or appends comes from the same answer.

An entity that declares nothing comes back with an empty list. The page then falls back to the roles
its files are actually attached under — it cannot offer an empty box for a role nobody has named,
because there is no such list. The picker opened without a fixed role keeps its free-text box, which
is exactly what "not declared" means: any role will do.

The "Attached" badge counts the role being attached in, so an image already in the gallery is still
offered as a candidate for primary, which is a perfectly reasonable thing to want.

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
