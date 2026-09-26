namespace DotNetBoost.Media.AmazonS3;

/// <summary>
/// Which bucket the S3 backend writes to, and how it hands out links. Configured through the
/// <c>configure</c> argument of <c>UseAmazonS3(...)</c>.
/// </summary>
public sealed class AmazonS3Options
{
    /// <summary>The bucket every file is written to.</summary>
    public string BucketName { get; set; } = "media";

    /// <summary>
    /// A prefix every path is written under, e.g. <c>tenant-a/</c>. Lets several applications, or
    /// several tenants, share one bucket without being able to see each other's objects through
    /// this library. Empty by default.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Verifies the bucket exists at startup. Default: <c>true</c>.
    /// <para>
    /// It only ever checks. Unlike the Azure backend it will not create the bucket: on S3 that is
    /// a region-bound, account-level, name-globally-unique decision with public-access and
    /// encryption settings attached, and a library guessing at those is how a bucket ends up
    /// world-readable. Create it in your infrastructure code.
    /// </para>
    /// </summary>
    public bool ValidateBucketAtStartup { get; set; } = true;

    /// <summary>
    /// Whether <c>GetUrlAsync</c> mints a time-limited pre-signed URL. Default: <c>true</c>, so
    /// clients download straight from S3 rather than through the application.
    /// </summary>
    public bool GeneratePresignedUrls { get; set; } = true;

    /// <summary>
    /// A CDN (CloudFront, typically) in front of the bucket, e.g. <c>https://cdn.example.com</c>.
    /// The host of a generated URL is rewritten to this; the path and any signature are kept.
    /// <para>
    /// An S3 signature is bound to the host it was computed for, so a rewritten pre-signed URL
    /// will NOT authenticate against CloudFront unless the distribution is configured to forward
    /// to the origin's host. Use this with <see cref="GeneratePresignedUrls"/> off, for a
    /// distribution that does its own access control.
    /// </para>
    /// </summary>
    public Uri? PublicBaseUrl { get; set; }

    /// <summary>
    /// The storage class new objects are written with, e.g. <c>STANDARD_IA</c> or
    /// <c>INTELLIGENT_TIERING</c>. <c>null</c> — the default — leaves it to the bucket, which is
    /// where a lifecycle policy usually belongs anyway.
    /// </summary>
    public string? StorageClass { get; set; }

    /// <summary>
    /// Server-side encryption for new objects: <c>AES256</c> for SSE-S3, <c>aws:kms</c> for
    /// SSE-KMS. <c>null</c> — the default — uses whatever the bucket's own default encryption
    /// says, which every bucket has had since 2023.
    /// </summary>
    public string? ServerSideEncryption { get; set; }
}
