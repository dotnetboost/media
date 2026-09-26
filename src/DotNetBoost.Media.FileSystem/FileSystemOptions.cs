namespace DotNetBoost.Media.FileSystem;

/// <summary>
/// Where the local backend puts files, and whether anything can reach them over HTTP.
/// Configured through the <c>configure</c> argument of <c>UseFileSystem(...)</c>.
/// </summary>
public sealed class FileSystemOptions
{
    /// <summary>
    /// The directory every file is written under. Relative paths resolve against the process's
    /// working directory, which in a container is rarely what you meant — prefer an absolute path,
    /// or one built from <c>IHostEnvironment.ContentRootPath</c>.
    /// <para>
    /// Put it on a volume that outlives the process. A path inside the application directory is
    /// fine for development and loses every upload on the next deployment.
    /// </para>
    /// </summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// Creates <see cref="RootPath"/> at startup when it does not exist. Default: <c>true</c>.
    /// Turn it off where the directory is a mount that must already be there — then a missing one
    /// fails the boot instead of quietly writing to a fresh empty directory inside the container.
    /// </summary>
    public bool CreateRootIfMissing { get; set; } = true;

    /// <summary>
    /// The base URL a web server or CDN serves <see cref="RootPath"/> from, e.g.
    /// <c>https://cdn.example.com/media</c>. Set it and <c>IMediaManager.GetUrlAsync</c> returns a
    /// link; leave it unset — the default — and it returns <c>null</c>, which is the honest answer
    /// for files only this application can read.
    /// <para>
    /// <b>Anything served this way is public</b>: the URL is the file's storage path, it never
    /// expires, and nothing checks who is asking. Use it for genuinely public assets. For
    /// everything else leave it unset and serve downloads through the application, which
    /// <c>MapMediaEndpoints()</c> does with your own authorization in front.
    /// </para>
    /// </summary>
    public Uri? PublicBaseUrl { get; set; }

    /// <summary>
    /// Bytes copied at a time when reading or writing. Default: 80 KB, the value the BCL itself
    /// uses for stream copies — large enough to keep syscalls down, small enough to stay off the
    /// large object heap.
    /// </summary>
    public int BufferSize { get; set; } = 81920;
}
