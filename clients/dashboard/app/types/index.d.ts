/** One stored file, as `GET api/media` and friends return it. */
export interface MediaFile {
  id: string
  fileName: string
  contentType: string
  size: number
  checksum: string | null
  category: string | null
  tags: string[]
  metadata: Record<string, string>
  /**
   * Everything using this file, on `GET api/media/{id}` only. `null` in a listing, which does not
   * carry them — a page of files would need a query each to fill. Read the one file to see them.
   */
  attachments: MediaAttachment[] | null
  uploadedBy: string | null
  createdAt: string
  updatedAt: string | null
  /**
   * A direct link to the bytes, when the storage backend mints one. `null` for a backend that
   * does not — local disk, typically — in which case the content route is the way to the file.
   */
  url: string | null
}

/**
 * One entity's use of a file. A file may have many — the same image can be one product's main
 * photo and another's third gallery shot, which is why the role lives here and not on the file.
 */
export interface MediaAttachment {
  id: string
  ownerEntityName: string
  ownerEntityId: string
  /** What the entity uses the file for: `primary`, `gallery`, `seo`, or whatever the app means. */
  role: string
  /** Its position within that role. */
  sortOrder: number
  createdAt: string
}

/** One page of `GET api/media`. */
export interface MediaPage {
  items: MediaFile[]
  total: number
  skip: number
  take: number
}

/**
 * What a listing can be ordered by. Mirrors the API's `MediaSortBy`.
 *
 * `sortOrder` is the order an entity put its own files in, so it needs an owner filter; the API
 * refuses it without one rather than quietly ignoring it.
 */
export type MediaSortBy = 'createdAt' | 'fileName' | 'size' | 'sortOrder'

/** The filter, sort and page the browser is currently showing. */
export interface MediaQuery {
  search: string
  category: string
  tag: string
  contentType: string
  sortBy: MediaSortBy
  descending: boolean
  page: number
  pageSize: number
  /** Restricts the listing to one entity's files. Set by the picker on the products page. */
  ownerEntityName?: string
  ownerEntityId?: string
  /** Narrows that entity's files to one role. Needs an owner. */
  role?: string
  /** Only files nothing is using — for finding uploads nobody ever claimed. */
  unattached?: boolean
}

/** Body of `PATCH api/media/{id}`. Omitted properties are left alone. */
export interface MediaUpdate {
  fileName?: string
  category?: string
  tags?: string[]
  metadata?: Record<string, string>
}

/**
 * Body of `POST api/media/{id}/attachments`.
 *
 * `replace` is what makes a single-valued role single-valued: true takes the role over from
 * whatever held it, false appends as a gallery does. Nothing in the schema knows that `primary`
 * holds one file and `gallery` holds many, so the caller says which it means.
 */
export interface AttachRequest {
  ownerEntityName: string
  ownerEntityId: string
  role?: string
  replace?: boolean
}

/** One of a product's files, as the sample's own endpoints return it. */
export interface ProductImage {
  id: string
  fileName: string
  contentType: string
  size: number
  contentUrl: string
  /** What this product uses it for — `primary`, `gallery`, `seo`. */
  role: string
  /** Its position within that role. */
  sortOrder: number
}

/** A product, as `GET api/products` returns it. */
export interface Product {
  id: number
  sku: string
  name: string
  price: number
  images: ProductImage[]
}

/** RFC 7807, as the API's problem-details mapping writes it. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  instance?: string
  /** `ValidationProblemDetails` adds this. */
  errors?: Record<string, string[]>
  /** The API attaches these to some problems — `limit`, `size`, `detectedContentType`, … */
  [key: string]: unknown
}
