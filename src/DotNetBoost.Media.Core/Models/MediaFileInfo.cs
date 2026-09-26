using System.Collections.ObjectModel;

namespace DotNetBoost.Media.Core.Models;

/// <summary>
/// An immutable snapshot of one stored file — what <see cref="Interfaces.IMediaManager"/> hands
/// back, and what an application returns from its own endpoints. Cheap to cache and pass around,
/// and it cannot be mutated into a state the store never saw.
/// </summary>
public sealed record MediaFileInfo
{
    /// <summary>The file's identity.</summary>
    public required Guid Id { get; init; }

    /// <summary>The name it was uploaded under.</summary>
    public required string FileName { get; init; }

    /// <summary>The media type it is served as.</summary>
    public required string ContentType { get; init; }

    /// <summary>Size of the stored bytes.</summary>
    public required long Size { get; init; }

    /// <summary>The backend holding the bytes.</summary>
    public required string StorageProvider { get; init; }

    /// <summary>The container, bucket or root directory inside that backend.</summary>
    public required string StorageContainer { get; init; }

    /// <summary>The path to the bytes inside the container.</summary>
    public required string StoragePath { get; init; }

    /// <summary>Lowercase hex SHA-256 of the bytes, when one was computed.</summary>
    public string? Checksum { get; init; }

    /// <summary>The entity this file is attached to, or <c>null</c>.</summary>
    public string? OwnerEntityName { get; init; }

    /// <summary>That entity's key, or <c>null</c>.</summary>
    public string? OwnerEntityId { get; init; }

    /// <summary>The bucket the application sorted it into, or <c>null</c>.</summary>
    public string? Category { get; init; }

    /// <summary>Its labels, already split — never the stored comma-delimited form.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Whatever the application kept alongside it.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Who uploaded it, as the application identifies people.</summary>
    public string? UploadedBy { get; init; }

    /// <summary>When the row was created. Always UTC.</summary>
    public required DateTime CreatedAt { get; init; }

    /// <summary>When its metadata last changed. Always UTC.</summary>
    public DateTime? UpdatedAt { get; init; }

    /// <summary>When it was deleted, or <c>null</c> while it is live. Always UTC.</summary>
    public DateTime? DeletedAt { get; init; }

    /// <summary>True once the file has been deleted.</summary>
    public bool IsDeleted => DeletedAt is not null;

    /// <summary>Snapshots a stored <see cref="MediaFile"/>.</summary>
    public static MediaFileInfo From(MediaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        return new MediaFileInfo
        {
            Id               = file.Id,
            FileName         = file.FileName,
            ContentType      = file.ContentType,
            Size             = file.Size,
            StorageProvider  = file.StorageProvider,
            StorageContainer = file.StorageContainer,
            StoragePath      = file.StoragePath,
            Checksum         = file.Checksum,
            OwnerEntityName  = file.OwnerEntityName,
            OwnerEntityId    = file.OwnerEntityId,
            Category         = file.Category,
            Tags             = MediaTags.Split(file.Tags),
            Metadata         = MediaMetadata.Parse(file.Metadata),
            UploadedBy       = file.UploadedBy,
            CreatedAt        = file.CreatedAt,
            UpdatedAt        = file.UpdatedAt,
            DeletedAt        = file.DeletedAt
        };
    }
}
