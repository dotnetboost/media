using DotNetBoost.Media.Core.Models;
using System.Data;
using System.Data.Common;
using System.Globalization;

namespace DotNetBoost.Media.Dapper;

/// <summary>
/// Reads a <see cref="MediaFile"/> out of a data reader, column by column.
/// <para>
/// Hand-written rather than left to Dapper's automatic mapping, because the same logical column
/// arrives as a different CLR type depending on the engine. SQLite has no GUID and no date type:
/// the id comes back as a <c>string</c> and the timestamps as text, and Dapper's cast from
/// <c>string</c> to <see cref="Guid"/> simply fails. The usual fix — a global
/// <c>SqlMapper.AddTypeHandler</c> — changes how <see cref="Guid"/> is read for the whole
/// application, including code that has nothing to do with this library, which is not a
/// side effect a storage provider gets to have.
/// </para>
/// <para>
/// Ordinals are resolved once per reader rather than per row: the column set is the same for
/// every row, and <c>GetOrdinal</c> is a name lookup.
/// </para>
/// </summary>
internal static class MediaFileMapper
{
    /// <summary>Reads every row the reader has left.</summary>
    public static async Task<List<MediaFile>> ReadAllAsync(DbDataReader reader, CancellationToken ct)
    {
        var ordinals = new Ordinals(reader);
        var rows     = new List<MediaFile>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(Read(reader, ordinals));

        return rows;
    }

    /// <summary>Reads the first row, or <c>null</c> when there is none.</summary>
    public static async Task<MediaFile?> ReadFirstAsync(DbDataReader reader, CancellationToken ct)
    {
        var ordinals = new Ordinals(reader);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Read(reader, ordinals) : null;
    }

    private static MediaFile Read(DbDataReader row, Ordinals o) => new()
    {
        Id               = Guid(row, o.Id),
        FileName         = row.GetString(o.FileName),
        ContentType      = row.GetString(o.ContentType),
        Size             = row.GetInt64(o.Size),
        StorageProvider  = row.GetString(o.StorageProvider),
        StorageContainer = row.GetString(o.StorageContainer),
        StoragePath      = row.GetString(o.StoragePath),
        Checksum         = NullableString(row, o.Checksum),
        Category         = NullableString(row, o.Category),
        Tags             = NullableString(row, o.Tags),
        Metadata         = NullableString(row, o.Metadata),
        UploadedBy       = NullableString(row, o.UploadedBy),
        CreatedAt        = Utc(row, o.CreatedAt)!.Value,
        UpdatedAt        = Utc(row, o.UpdatedAt),
        DeletedAt        = Utc(row, o.DeletedAt)
    };

    /// <summary>
    /// The id, whether the engine hands back a <see cref="System.Guid"/> (SQL Server's
    /// <c>uniqueidentifier</c>, PostgreSQL's <c>uuid</c>) or text (SQLite).
    /// </summary>
    internal static Guid Guid(DbDataReader row, int ordinal)
    {
        var value = row.GetValue(ordinal);

        return value switch
        {
            Guid guid  => guid,
            string s   => System.Guid.Parse(s, CultureInfo.InvariantCulture),
            byte[] b   => new Guid(b),
            _          => throw new InvalidCastException(
                $"Cannot read a media id from a {value.GetType().Name}. The Id column should be uniqueidentifier " +
                "(SQL Server), uuid (PostgreSQL) or TEXT (SQLite).")
        };
    }

    /// <summary>
    /// A timestamp, forced to <see cref="DateTimeKind.Utc"/>.
    /// <para>
    /// Every value this library writes is UTC, but only PostgreSQL says so on the way back:
    /// SQL Server's <c>datetime2</c> and SQLite's text both return <c>Unspecified</c>, which
    /// serializes to JSON without a "Z" and is then read by every client as local time.
    /// </para>
    /// </summary>
    internal static DateTime? Utc(DbDataReader row, int ordinal)
    {
        if (row.IsDBNull(ordinal))
            return null;

        var value = row.GetValue(ordinal);

        var moment = value switch
        {
            DateTime dt        => dt,
            DateTimeOffset dto => dto.UtcDateTime,
            // SQLite, where the column is TEXT. Round-trip format, so it parses back exactly.
            string s           => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            _                  => throw new InvalidCastException(
                $"Cannot read a timestamp from a {value.GetType().Name}.")
        };

        return moment.Kind switch
        {
            DateTimeKind.Utc         => moment,
            DateTimeKind.Local       => moment.ToUniversalTime(),
            // Unspecified means the engine dropped the kind, not that the value is local: it was
            // written as UTC, so it is UTC.
            _                        => DateTime.SpecifyKind(moment, DateTimeKind.Utc)
        };
    }

    private static string? NullableString(DbDataReader row, int ordinal) =>
        row.IsDBNull(ordinal) ? null : row.GetString(ordinal);

    /// <summary>Column positions, resolved once per reader.</summary>
    private sealed class Ordinals
    {
        public Ordinals(DbDataReader row)
        {
            Id               = row.GetOrdinal(nameof(MediaFile.Id));
            FileName         = row.GetOrdinal(nameof(MediaFile.FileName));
            ContentType      = row.GetOrdinal(nameof(MediaFile.ContentType));
            Size             = row.GetOrdinal(nameof(MediaFile.Size));
            StorageProvider  = row.GetOrdinal(nameof(MediaFile.StorageProvider));
            StorageContainer = row.GetOrdinal(nameof(MediaFile.StorageContainer));
            StoragePath      = row.GetOrdinal(nameof(MediaFile.StoragePath));
            Checksum         = row.GetOrdinal(nameof(MediaFile.Checksum));
            Category         = row.GetOrdinal(nameof(MediaFile.Category));
            Tags             = row.GetOrdinal(nameof(MediaFile.Tags));
            Metadata         = row.GetOrdinal(nameof(MediaFile.Metadata));
            UploadedBy       = row.GetOrdinal(nameof(MediaFile.UploadedBy));
            CreatedAt        = row.GetOrdinal(nameof(MediaFile.CreatedAt));
            UpdatedAt        = row.GetOrdinal(nameof(MediaFile.UpdatedAt));
            DeletedAt        = row.GetOrdinal(nameof(MediaFile.DeletedAt));
        }

        public int Id { get; }
        public int FileName { get; }
        public int ContentType { get; }
        public int Size { get; }
        public int StorageProvider { get; }
        public int StorageContainer { get; }
        public int StoragePath { get; }
        public int Checksum { get; }
        public int Category { get; }
        public int Tags { get; }
        public int Metadata { get; }
        public int UploadedBy { get; }
        public int CreatedAt { get; }
        public int UpdatedAt { get; }
        public int DeletedAt { get; }
    }
}

/// <summary>
/// Reads a <see cref="MediaAttachment"/> out of a data reader, column by column, for the same
/// reason <see cref="MediaFileMapper"/> does it by hand: SQLite returns the ids as text and the
/// timestamp as text, and a global Dapper type handler would change how every Guid in the
/// application is read.
/// </summary>
internal static class MediaAttachmentMapper
{
    /// <summary>Reads every row the reader has left.</summary>
    public static async Task<List<MediaAttachment>> ReadAllAsync(DbDataReader reader, CancellationToken ct)
    {
        var ordinals = new Ordinals(reader);
        var rows     = new List<MediaAttachment>();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(Read(reader, ordinals));

        return rows;
    }

    private static MediaAttachment Read(DbDataReader row, Ordinals o) => new()
    {
        Id              = MediaFileMapper.Guid(row, o.Id),
        MediaFileId     = MediaFileMapper.Guid(row, o.MediaFileId),
        OwnerEntityName = row.GetString(o.OwnerEntityName),
        OwnerEntityId   = row.GetString(o.OwnerEntityId),
        Role            = row.GetString(o.Role),
        SortOrder       = row.GetInt32(o.SortOrder),
        CreatedAt       = MediaFileMapper.Utc(row, o.CreatedAt)!.Value
    };

    /// <summary>Column positions, resolved once per reader.</summary>
    private sealed class Ordinals
    {
        public Ordinals(DbDataReader row)
        {
            Id              = row.GetOrdinal(nameof(MediaAttachment.Id));
            MediaFileId     = row.GetOrdinal(nameof(MediaAttachment.MediaFileId));
            OwnerEntityName = row.GetOrdinal(nameof(MediaAttachment.OwnerEntityName));
            OwnerEntityId   = row.GetOrdinal(nameof(MediaAttachment.OwnerEntityId));
            Role            = row.GetOrdinal(nameof(MediaAttachment.Role));
            SortOrder       = row.GetOrdinal(nameof(MediaAttachment.SortOrder));
            CreatedAt       = row.GetOrdinal(nameof(MediaAttachment.CreatedAt));
        }

        public int Id { get; }
        public int MediaFileId { get; }
        public int OwnerEntityName { get; }
        public int OwnerEntityId { get; }
        public int Role { get; }
        public int SortOrder { get; }
        public int CreatedAt { get; }
    }
}
