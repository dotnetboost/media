namespace DotNetBoost.Media.Core.Models;

/// <summary>
/// A filter over the media table. Every property is optional and the set ones are ANDed, so the
/// default value means "every live file, newest first". Stores translate this into one statement
/// — it is deliberately small enough that each of them can.
/// </summary>
public sealed record MediaQuery
{
    /// <summary>The largest page a single query may return, whatever <see cref="Take"/> asks for.</summary>
    public const int MaxTake = 500;

    private readonly int _take = 50;
    private readonly int _skip;

    /// <summary>Only files attached to this entity type.</summary>
    public string? OwnerEntityName { get; init; }

    /// <summary>
    /// Only files attached to this entity instance. Meaningless without
    /// <see cref="OwnerEntityName"/>, and rejected without it — a bare key matches rows belonging
    /// to unrelated types that happen to share it.
    /// </summary>
    public string? OwnerEntityId { get; init; }

    /// <summary>Only files in this bucket.</summary>
    public string? Category { get; init; }

    /// <summary>Only files carrying this label. Matched as a whole tag, not a substring.</summary>
    public string? Tag { get; init; }

    /// <summary>
    /// Only files whose name contains this, case-insensitively. A plain substring match on
    /// <see cref="MediaFile.FileName"/> — what a search box in front of a media library needs, and
    /// deliberately no more than that.
    /// <para>
    /// It cannot use an index, being an unanchored <c>LIKE</c>. That is acceptable for an
    /// administrative search over a library of ordinary size, and not for a hot path on a large
    /// one; there, put a full-text index on the column and query it yourself.
    /// </para>
    /// </summary>
    public string? Search { get; init; }

    /// <summary>
    /// Only files whose media type starts with this, so <c>image/</c> selects every image and
    /// <c>image/png</c> only PNGs.
    /// </summary>
    public string? ContentTypePrefix { get; init; }

    /// <summary>Only files uploaded by this user.</summary>
    public string? UploadedBy { get; init; }

    /// <summary>Only files whose bytes hash to this. Exact, lowercase hex.</summary>
    public string? Checksum { get; init; }

    /// <summary>Only files created at or after this moment. UTC, like the stored value.</summary>
    public DateTime? CreatedAfter { get; init; }

    /// <summary>Only files created before this moment. UTC, like the stored value.</summary>
    public DateTime? CreatedBefore { get; init; }

    /// <summary>
    /// Include soft-deleted files. Default: <c>false</c> — a deleted file is gone as far as every
    /// ordinary read is concerned, and it takes saying so to see one.
    /// </summary>
    public bool IncludeDeleted { get; init; }

    /// <summary>Rows to skip, for paging. Negative values are rejected.</summary>
    public int Skip
    {
        get => _skip;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _skip = value;
        }
    }

    /// <summary>
    /// Rows to return. Default: 50, capped at <see cref="MaxTake"/> — a page size is a client's
    /// suggestion, and an unbounded one is how a listing endpoint becomes a way to exhaust the
    /// server's memory.
    /// </summary>
    public int Take
    {
        get => _take;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _take = Math.Min(value, MaxTake);
        }
    }

    /// <summary>What to order by. Default: when the file was created.</summary>
    public MediaSortBy SortBy { get; init; } = MediaSortBy.CreatedAt;

    /// <summary>
    /// Largest, latest or Z-to-A first. Default: <c>true</c>, which for the default
    /// <see cref="SortBy"/> means newest first — the order a media library is nearly always
    /// wanted in.
    /// </summary>
    public bool Descending { get; init; } = true;
}

/// <summary>
/// What a listing is ordered by. Every value maps to an indexed or trivially sortable column;
/// there is deliberately no "order by tag" or "order by owner", which would sort by a delimited
/// string and a key that is not meant to be read.
/// </summary>
public enum MediaSortBy
{
    /// <summary>When the file was uploaded. The default.</summary>
    CreatedAt,

    /// <summary>The file's name, ordinally.</summary>
    FileName,

    /// <summary>The size of the stored bytes.</summary>
    Size
}

/// <summary>One page of a listing, with the total behind it.</summary>
/// <param name="Items">The page's files, in the query's order.</param>
/// <param name="Total">How many files match the filter, ignoring <c>Skip</c> and <c>Take</c>.</param>
/// <param name="Skip">The offset this page starts at.</param>
/// <param name="Take">The page size that was applied, after capping.</param>
public sealed record MediaPage(IReadOnlyList<MediaFileInfo> Items, int Total, int Skip, int Take);
