using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;

namespace DotNetBoost.Media.AzureBlobStorage;

/// <summary>
/// Stores files as blobs in an Azure Storage container. Uploads stream straight through to
/// storage, downloads can be handed to the client as a time-limited SAS URL so the bytes never
/// pass through the application, and the container is private unless you make it otherwise.
/// </summary>
public sealed class AzureBlobStorageProvider : IMediaStorageProvider, IMediaStorageHealthCheck
{
    private readonly BlobServiceClient _service;
    private readonly AzureBlobOptions _options;

    /// <summary>Creates the backend over an existing service client.</summary>
    /// <param name="service">The account to write to.</param>
    /// <param name="options">Container name, upload tuning and URL behaviour.</param>
    public AzureBlobStorageProvider(BlobServiceClient service, AzureBlobOptions options)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ContainerName, nameof(options));

        _service = service;
        _options = options;
    }

    /// <inheritdoc/>
    public string Name => "AzureBlobStorage";

    /// <inheritdoc/>
    public string Container => _options.ContainerName;

    /// <inheritdoc/>
    public async Task<StoredObject> SaveAsync(MediaWrite write, CancellationToken ct = default)
    {
        var path = MediaPaths.Validate(write.Path);
        var blob = Blob(_options.ContainerName, path);

        try
        {
            await blob.UploadAsync(
                write.Content,
                new BlobUploadOptions
                {
                    HttpHeaders = new BlobHttpHeaders { ContentType = write.ContentType },
                    TransferOptions = new global::Azure.Storage.StorageTransferOptions
                    {
                        InitialTransferSize = _options.BlockSize,
                        MaximumTransferSize = _options.BlockSize,
                        MaximumConcurrency  = _options.MaxConcurrency
                    }
                },
                ct).ConfigureAwait(false);

            // The upload response carries no length, and asking Azure for one costs a round trip
            // per upload. Returning 0 tells the engine to use what its own measuring stream
            // counted, which is the same number and already in hand.
            return new StoredObject(_options.ContainerName, path, Size: 0, Checksum: null);
        }
        catch (RequestFailedException ex)
        {
            throw new MediaStorageException(
                $"Could not upload blob '{path}' to container '{_options.ContainerName}': {ex.Message}",
                ex,
                Name);
        }
    }

    /// <inheritdoc/>
    public async Task<Stream> OpenReadAsync(string container, string path, CancellationToken ct = default)
    {
        var safe = MediaPaths.Validate(path);
        var blob = Blob(container, safe);

        try
        {
            return await blob.OpenReadAsync(new BlobOpenReadOptions(allowModifications: false), ct)
                .ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new MediaContentMissingException(Name, safe);
        }
        catch (RequestFailedException ex)
        {
            throw new MediaStorageException($"Could not read blob '{safe}' from container '{container}': {ex.Message}", ex, Name);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string container, string path, CancellationToken ct = default)
    {
        var safe = MediaPaths.Validate(path);

        try
        {
            var response = await Blob(container, safe)
                .DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: ct)
                .ConfigureAwait(false);

            return response.Value;
        }
        catch (RequestFailedException ex)
        {
            throw new MediaStorageException($"Could not delete blob '{safe}' from container '{container}': {ex.Message}", ex, Name);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string container, string path, CancellationToken ct = default)
    {
        try
        {
            return await Blob(container, MediaPaths.Validate(path)).ExistsAsync(ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex)
        {
            throw new MediaStorageException($"Could not check blob '{path}' in container '{container}': {ex.Message}", ex, Name);
        }
    }

    /// <inheritdoc/>
    public Task<Uri?> GetUrlAsync(string container, string path, MediaUrlOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var blob = Blob(container, MediaPaths.Validate(path));

        if (!_options.GenerateSasUrls)
            return Task.FromResult<Uri?>(Rewrite(blob.Uri));

        // No signing credential — a token credential without an account key, typically. A URL
        // without a SAS would 404 for an anonymous client against a private container, so saying
        // "there is no direct link" is the truthful answer and the caller falls back to streaming
        // through the application.
        if (!blob.CanGenerateSasUri)
            return Task.FromResult<Uri?>(null);

        var builder = new BlobSasBuilder(BlobSasPermissions.Read, DateTimeOffset.UtcNow.Add(options.ExpiresIn ?? TimeSpan.FromHours(1)))
        {
            BlobContainerName = blob.BlobContainerName,
            BlobName          = blob.Name,
            Resource          = "b",
            // A minute of leeway for clock skew between this host and the storage service. Without
            // it a link can be rejected as not-yet-valid the instant after it is minted.
            StartsOn          = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        if (options.AsAttachment)
        {
            var fileName = MediaPaths.SanitizeFileName(options.DownloadFileName);
            builder.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        }

        return Task.FromResult<Uri?>(Rewrite(blob.GenerateSasUri(builder)));
    }

    /// <inheritdoc/>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        var container = _service.GetBlobContainerClient(_options.ContainerName);

        try
        {
            if (_options.CreateContainerIfMissing)
            {
                // PublicAccessType.None, explicitly. The SDK's default is already None, but the
                // access level of the container holding every uploaded file is worth stating.
                await container.CreateIfNotExistsAsync(PublicAccessType.None, cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            if (!await container.ExistsAsync(ct).ConfigureAwait(false))
            {
                throw new MediaStorageException(
                    $"Blob container '{_options.ContainerName}' does not exist, and CreateContainerIfMissing " +
                    "is off. Create it before starting the application.",
                    Name);
            }
        }
        catch (RequestFailedException ex)
        {
            throw new MediaStorageException(
                $"Could not reach blob container '{_options.ContainerName}' on '{_service.AccountName}': {ex.Message}. " +
                "Check the connection string or credential, and that the identity has access to the container.",
                ex,
                Name);
        }
    }

    private BlobClient Blob(string container, string path) =>
        _service.GetBlobContainerClient(container).GetBlobClient(path);

    /// <summary>
    /// Puts a generated URL behind the configured CDN or custom domain, keeping the path and any
    /// SAS token. A no-op when none is configured.
    /// </summary>
    private Uri Rewrite(Uri uri)
    {
        if (_options.PublicBaseUrl is not { } baseUrl)
            return uri;

        return new UriBuilder(uri)
        {
            Scheme = baseUrl.Scheme,
            Host   = baseUrl.Host,
            Port   = baseUrl.IsDefaultPort ? -1 : baseUrl.Port
        }.Uri;
    }
}
