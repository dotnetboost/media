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
  ownerEntityName: string | null
  ownerEntityId: string | null
  uploadedBy: string | null
  createdAt: string
  updatedAt: string | null
  /**
   * A direct link to the bytes, when the storage backend mints one. `null` for a backend that
   * does not — local disk, typically — in which case the content route is the way to the file.
   */
  url: string | null
}

/** One page of `GET api/media`. */
export interface MediaPage {
  items: MediaFile[]
  total: number
  skip: number
  take: number
}

/** What a listing can be ordered by. Mirrors the API's `MediaSortBy`. */
export type MediaSortBy = 'createdAt' | 'fileName' | 'size'

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
}

/** Body of `PATCH api/media/{id}`. Omitted properties are left alone. */
export interface MediaUpdate {
  fileName?: string
  category?: string
  tags?: string[]
  metadata?: Record<string, string>
  ownerEntityName?: string
  ownerEntityId?: string
  clearOwner?: boolean
}

/** One of a product's files, as the sample's own endpoints return it. */
export interface ProductImage {
  id: string
  fileName: string
  contentType: string
  size: number
  contentUrl: string
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
