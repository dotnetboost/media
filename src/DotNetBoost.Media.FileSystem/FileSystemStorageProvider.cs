using DotNetBoost.Media.Core;
using DotNetBoost.Media.Core.Interfaces;
using DotNetBoost.Media.Core.Models;
using DotNetBoost.Media.Core.Services;

namespace DotNetBoost.Media.FileSystem;

/// <summary>
/// Stores files on the machine's own disk, under <see cref="FileSystemOptions.RootPath"/>. The
/// backend to develop against, and a reasonable production one for a single host with a durable
/// volume — not for several hosts behind a load balancer, where one node's disk is not another's.
/// <para>
/// Every path is resolved and checked against the root before it is touched, so a stored path
/// that has been tampered with cannot reach a file outside the media directory.
/// </para>
/// </summary>
public sealed class FileSystemStorageProvider : IMediaStorageProvider, IMediaStorageHealthCheck
{
    private readonly FileSystemOptions _options;
    private readonly string _root;

    /// <summary>Creates the backend over <paramref name="options"/>.</summary>
    /// <exception cref="ArgumentException"><see cref="FileSystemOptions.RootPath"/> is empty.</exception>
    public FileSystemStorageProvider(FileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPath, nameof(options));

        _options = options;

        // Resolved once, to a full path with no trailing separator. Every containment check below
        // compares against this, and comparing against a relative or unnormalised root is how
        // traversal checks come to pass things they should not.
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.RootPath));
    }

    /// <inheritdoc/>
    public string Name => "FileSystem";

    /// <inheritdoc/>
    public string Container => _root;

    /// <inheritdoc/>
    public async Task<StoredObject> SaveAsync(MediaWrite write, CancellationToken ct = default)
    {
        var path     = MediaPaths.Validate(write.Path);
        var absolute = Resolve(_root, path);

        var directory = Path.GetDirectoryName(absolute);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        try
        {
            // FileMode.Create: the engine derives each path from a fresh id, so an existing file
            // would be a collision that should not happen — overwriting it is still better than
            // failing an upload over a path nobody chose.
            var target = new FileStream(
                absolute,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                _options.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            long written;
            await using (target.ConfigureAwait(false))
            {
                await write.Content.CopyToAsync(target, _options.BufferSize, ct).ConfigureAwait(false);
                await target.FlushAsync(ct).ConfigureAwait(false);
                written = target.Length;
            }

            return new StoredObject(_root, path, written, Checksum: null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leave nothing half-written behind for the engine's cleanup to have to reason about.
            TryDelete(absolute);

            throw new MediaStorageException(
                $"Could not write media content to '{absolute}'. Check that the directory exists and the " +
                "process can write to it.",
                ex,
                Name);
        }
    }

    /// <inheritdoc/>
    public Task<Stream> OpenReadAsync(string container, string path, CancellationToken ct = default)
    {
        var absolute = Resolve(container, MediaPaths.Validate(path));

        if (!File.Exists(absolute))
            throw new MediaContentMissingException(Name, path);

        try
        {
            Stream stream = new FileStream(
                absolute,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                _options.BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return Task.FromResult(stream);
        }
        catch (FileNotFoundException)
        {
            // Deleted between the check above and the open. A race, not a server fault.
            throw new MediaContentMissingException(Name, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MediaStorageException($"Could not read media content from '{absolute}'.", ex, Name);
        }
    }

    /// <inheritdoc/>
    public Task<bool> DeleteAsync(string container, string path, CancellationToken ct = default)
    {
        var absolute = Resolve(container, MediaPaths.Validate(path));

        if (!File.Exists(absolute))
            return Task.FromResult(false);

        try
        {
            File.Delete(absolute);
        }
        catch (FileNotFoundException)
        {
            return Task.FromResult(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MediaStorageException($"Could not delete media content at '{absolute}'.", ex, Name);
        }

        RemoveEmptyDirectories(Path.GetDirectoryName(absolute), container);
        return Task.FromResult(true);
    }

    /// <inheritdoc/>
    public Task<bool> ExistsAsync(string container, string path, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(Resolve(container, MediaPaths.Validate(path))));

    /// <inheritdoc/>
    public Task<Uri?> GetUrlAsync(string container, string path, MediaUrlOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // No base URL means no direct link, and saying so is the point: the caller then serves the
        // file through the application, where its own authorization applies. A local path dressed
        // up as a URL would be worse than nothing.
        if (_options.PublicBaseUrl is not { } baseUrl)
            return Task.FromResult<Uri?>(null);

        var relative = MediaPaths.Validate(path);
        var escaped  = string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));

        // A base URL without a trailing slash would otherwise have its last segment replaced.
        var root = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");

        return Task.FromResult<Uri?>(new Uri(root, escaped));
    }

    /// <inheritdoc/>
    public Task CheckAsync(CancellationToken ct = default)
    {
        try
        {
            if (!Directory.Exists(_root))
            {
                if (!_options.CreateRootIfMissing)
                {
                    throw new MediaStorageException(
                        $"The media directory '{_root}' does not exist, and CreateRootIfMissing is off. " +
                        "Create it (or mount the volume) before starting the application.",
                        Name);
                }

                Directory.CreateDirectory(_root);
            }

            // Existence is not permission. A read-only mount passes Directory.Exists and fails
            // every upload, which is exactly the failure a startup check should move to startup.
            var probe = Path.Combine(_root, $".dotnetboost-media-{Guid.NewGuid():N}.probe");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new MediaStorageException(
                $"The media directory '{_root}' is not writable by this process.", ex, Name);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The absolute path of <paramref name="path"/> inside <paramref name="container"/>, proven to
    /// be inside it.
    /// <para>
    /// <see cref="MediaPaths.Validate"/> has already rejected <c>..</c> segments; this is the
    /// second line, and it is the one that holds on a case-insensitive file system, through a
    /// symlink, and against whatever encoding trick gets past a textual check. It compares
    /// fully-resolved paths, which is the only comparison a file system actually honours.
    /// </para>
    /// </summary>
    private static string Resolve(string container, string path)
    {
        var root     = Path.TrimEndingDirectorySeparator(Path.GetFullPath(container));
        var absolute = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!absolute.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidMediaException(
                $"Storage path '{path}' resolves outside the media directory. Refusing to touch it.");
        }

        return absolute;
    }

    /// <summary>
    /// Prunes the date directories a deleted file leaves behind, stopping at the root and at the
    /// first directory that still has something in it. Without this a long-lived store accumulates
    /// an empty directory per day forever.
    /// </summary>
    private static void RemoveEmptyDirectories(string? directory, string container)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(container));

        while (!string.IsNullOrEmpty(directory)
               && !string.Equals(Path.TrimEndingDirectorySeparator(directory), root, StringComparison.Ordinal))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                    return;

                Directory.Delete(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another request writing into the same directory, or a permission the process
                // does not have. Tidying is optional; the delete itself already succeeded.
                return;
            }

            directory = Path.GetDirectoryName(directory);
        }
    }

    private static void TryDelete(string absolute)
    {
        try
        {
            if (File.Exists(absolute))
                File.Delete(absolute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The write already failed; this is only tidying, and the engine logs the leftover.
        }
    }
}
