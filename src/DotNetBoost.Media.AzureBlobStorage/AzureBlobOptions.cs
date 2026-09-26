namespace DotNetBoost.Media.AzureBlobStorage;

/// <summary>
/// Which container the Azure backend writes to, and how it hands out links. Configured through
/// the <c>configure</c> argument of <c>UseAzureBlobStorage(...)</c>.
/// </summary>
public sealed class AzureBlobOptions
{
    /// <summary>
    /// The blob container every file is written to. Azure's own rules apply: 3–63 characters,
    /// lowercase letters, digits and dashes.
    /// </summary>
    public string ContainerName { get; set; } = "media";

    /// <summary>
    /// Creates the container at startup when it does not exist, as a PRIVATE container. Default:
    /// <c>true</c>.
    /// <para>
    /// Private is not negotiable here. A public container makes every upload readable by anyone
    /// with the URL, for as long as it exists, and that is not something a library should be able
    /// to arrange by default. If you want public blobs, set the access level yourself in the
    /// portal or your infrastructure code, deliberately.
    /// </para>
    /// </summary>
    public bool CreateContainerIfMissing { get; set; } = true;

    /// <summary>
    /// Bytes per block when uploading. Default: 4 MB. The SDK splits anything larger than this
    /// into blocks and uploads them in parallel; smaller files go in a single request.
    /// </summary>
    public int BlockSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Blocks uploaded at once for a file large enough to be split. Default: 4.
    /// </summary>
    public int MaxConcurrency { get; set; } = 4;

    /// <summary>
    /// Whether <c>GetUrlAsync</c> mints a time-limited SAS URL. Default: <c>true</c> — the point
    /// of the Azure backend for most applications, since it lets clients download straight from
    /// storage without the bytes passing through the application.
    /// <para>
    /// It needs a credential that can sign: a connection string or account key, or a user
    /// delegation key obtained from Entra ID. With a token credential that cannot sign, signing is
    /// skipped and <c>GetUrlAsync</c> returns <c>null</c> rather than an unusable link.
    /// </para>
    /// </summary>
    public bool GenerateSasUrls { get; set; } = true;

    /// <summary>
    /// A CDN or custom domain in front of the container, e.g. <c>https://cdn.example.com</c>.
    /// The host of a generated URL is rewritten to this; the path and any SAS token are kept.
    /// </summary>
    public Uri? PublicBaseUrl { get; set; }
}
