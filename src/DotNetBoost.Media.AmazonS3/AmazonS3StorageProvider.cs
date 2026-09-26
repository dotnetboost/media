using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using DotNetBoost.Media.AmazonS3;
using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;
using System.Net;

namespace DotNetBoost.Media.AmazonS3;

/// <summary>
/// Stores files as objects in an Amazon S3 bucket — or in anything that speaks the S3 API, which
/// is most object stores: MinIO, Cloudflare R2, DigitalOcean Spaces, Backblaze B2. Uploads stream
/// through the SDK's transfer utility, which switches to a multipart upload for large files on its
/// own; downloads can be handed to clients as pre-signed URLs.
/// </summary>
public sealed class AmazonS3StorageProvider : IMediaStorageProvider, IMediaStorageHealthCheck
{
    private readonly IAmazonS3 _s3;
    private readonly AmazonS3Options _options;
    private readonly string _prefix;

    /// <summary>Creates the backend over an existing S3 client.</summary>
    /// <param name="s3">The client to use. Its region and credentials are the backend's.</param>
    /// <param name="options">Bucket, prefix, URL behaviour and object settings.</param>
    public AmazonS3StorageProvider(IAmazonS3 s3, AmazonS3Options options)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BucketName, nameof(options));

        _s3      = s3;
        _options = options;
        _prefix  = NormalizePrefix(options.KeyPrefix);
    }

    /// <inheritdoc/>
    public string Name => "AmazonS3";

    /// <inheritdoc/>
    public string Container => _options.BucketName;

    /// <inheritdoc/>
    public async Task<StoredObject> SaveAsync(MediaWrite write, CancellationToken ct = default)
    {
        var path = MediaPaths.Validate(write.Path);

        var request = new TransferUtilityUploadRequest
        {
            BucketName  = _options.BucketName,
            Key         = Key(path),
            InputStream = write.Content,
            ContentType = write.ContentType,
            // The upload stream is forward-only and its length is not known up front, so the
            // transfer utility must not try to seek it to find one.
            AutoCloseStream   = false,
            AutoResetStreamPosition = false
        };

        if (_options.StorageClass is { } storageClass)
            request.StorageClass = S3StorageClass.FindValue(storageClass);

        if (_options.ServerSideEncryption is { } encryption)
            request.ServerSideEncryptionMethod = ServerSideEncryptionMethod.FindValue(encryption);

        try
        {
            using var transfer = new TransferUtility(_s3);
            await transfer.UploadAsync(request, ct).ConfigureAwait(false);

            // S3 does not report the object's length in the upload response either; the engine's
            // measuring stream already has the number.
            return new StoredObject(_options.BucketName, path, Size: 0, Checksum: null);
        }
        catch (AmazonS3Exception ex)
        {
            throw new MediaStorageException(
                $"Could not upload object '{Key(path)}' to bucket '{_options.BucketName}': {ex.Message}",
                ex,
                Name);
        }
    }

    /// <inheritdoc/>
    public async Task<Stream> OpenReadAsync(string container, string path, CancellationToken ct = default)
    {
        var safe = MediaPaths.Validate(path);

        try
        {
            var response = await _s3.GetObjectAsync(
                new GetObjectRequest { BucketName = container, Key = Key(safe) },
                ct).ConfigureAwait(false);

            // The response stream owns the underlying HTTP connection; the caller disposes it
            // through MediaContent.
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (IsMissing(ex))
        {
            throw new MediaContentMissingException(Name, safe);
        }
        catch (AmazonS3Exception ex)
        {
            throw new MediaStorageException($"Could not read object '{Key(safe)}' from bucket '{container}': {ex.Message}", ex, Name);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(string container, string path, CancellationToken ct = default)
    {
        var safe = MediaPaths.Validate(path);

        // S3's DELETE is idempotent and reports success for a key that was never there, so the
        // existence check is what makes the return value mean what the contract says.
        var existed = await ExistsAsync(container, safe, ct).ConfigureAwait(false);

        try
        {
            await _s3.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = container, Key = Key(safe) },
                ct).ConfigureAwait(false);

            return existed;
        }
        catch (AmazonS3Exception ex)
        {
            throw new MediaStorageException($"Could not delete object '{Key(safe)}' from bucket '{container}': {ex.Message}", ex, Name);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> ExistsAsync(string container, string path, CancellationToken ct = default)
    {
        try
        {
            await _s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = container, Key = Key(MediaPaths.Validate(path)) },
                ct).ConfigureAwait(false);

            return true;
        }
        catch (AmazonS3Exception ex) when (IsMissing(ex))
        {
            return false;
        }
        catch (AmazonS3Exception ex)
        {
            throw new MediaStorageException($"Could not check object '{path}' in bucket '{container}': {ex.Message}", ex, Name);
        }
    }

    /// <inheritdoc/>
    public async Task<Uri?> GetUrlAsync(string container, string path, MediaUrlOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var key = Key(MediaPaths.Validate(path));

        if (!_options.GeneratePresignedUrls)
        {
            // Only a CDN in front of the bucket makes an unsigned URL fetchable; the bucket's own
            // object URL would 403 for anyone without credentials.
            return _options.PublicBaseUrl is { } cdn ? Combine(cdn, key) : null;
        }

        var request = new GetPreSignedUrlRequest
        {
            BucketName = container,
            Key        = key,
            Verb       = HttpVerb.GET,
            Expires    = DateTime.UtcNow.Add(options.ExpiresIn ?? TimeSpan.FromHours(1))
        };

        if (options.AsAttachment)
        {
            var fileName = MediaPaths.SanitizeFileName(options.DownloadFileName);
            request.ResponseHeaderOverrides.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        }

        try
        {
            var url = await _s3.GetPreSignedURLAsync(request).ConfigureAwait(false);
            return Rewrite(new Uri(url));
        }
        catch (AmazonS3Exception ex)
        {
            throw new MediaStorageException($"Could not pre-sign a URL for '{key}' in bucket '{container}': {ex.Message}", ex, Name);
        }
    }

    /// <inheritdoc/>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!_options.ValidateBucketAtStartup)
            return;

        try
        {
            // A zero-result list rather than a HEAD on the bucket: it needs only s3:ListBucket,
            // which is a permission this backend needs anyway, and it works against the S3-
            // compatible stores whose HeadBucket support is patchy.
            await _s3.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = _options.BucketName, MaxKeys = 1, Prefix = _prefix },
                ct).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex)
        {
            throw new MediaStorageException(
                $"Could not reach S3 bucket '{_options.BucketName}': {ex.Message}. Check the bucket name, the " +
                "region on the client, and that the credentials allow s3:ListBucket on it. This backend does " +
                "not create buckets — create it in your infrastructure code.",
                ex,
                Name);
        }
    }

    private string Key(string path) => _prefix.Length == 0 ? path : _prefix + path;

    private static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return string.Empty;

        var trimmed = prefix.Replace('\\', '/').Trim('/');
        return trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>
    /// Whether an S3 error means "not there". <c>NoSuchKey</c> on a GET, a bare 404 on the HEAD
    /// behind <c>GetObjectMetadataAsync</c> — which carries no error code at all, because a HEAD
    /// response has no body to put one in.
    /// </summary>
    private static bool IsMissing(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.NotFound
        || string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.Ordinal);

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

    private static Uri Combine(Uri baseUrl, string key)
    {
        var root    = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        var escaped = string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
        return new Uri(root, escaped);
    }
}
