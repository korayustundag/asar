using Asar.Constants;
using Asar.Models;
using Asar.Serialization;

namespace Asar.Core;

// ══════════════════════════════════════════════════════════════════════════════
// AsarPacker
//
// Converts an on-disk directory tree into an ASAR archive.
//
// Design
// ──────
// Rather than using AsarWriter (which buffers all content in streams), the
// packer takes a two-pass approach optimised for large directory trees:
//
//   Pass 1 – Walk:  Crawl the source directory tree, collect FileInfo for
//            every file that passes the include/exclude filters, and build
//            the AsarDirectoryEntry tree with sizes but placeholder offsets.
//
//   Pass 2 – Write: Serialise the header, then stream each file sequentially
//            into the output, updating offsets as we go.
//
// This avoids holding all file content in memory simultaneously.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Options that control which files are included when packing a directory.
/// </summary>
public sealed class AsarPackOptions
{
    /// <summary>
    /// Optional glob-style patterns.  Files whose virtual path matches any
    /// pattern are excluded from the archive.
    /// Leave empty to include everything.
    /// </summary>
    /// <example><c>["**/.git/**", "**/*.map"]</c></example>
    public List<string> ExcludePatterns { get; set; } = new();

    /// <summary>
    /// Optional set of glob-style patterns.  Only files whose virtual path
    /// matches at least one pattern are included.
    /// Leave empty to include all files not excluded by
    /// <see cref="ExcludePatterns"/>.
    /// </summary>
    public List<string> IncludePatterns { get; set; } = new();

    /// <summary>
    /// Virtual paths (relative to the source root) that should be stored
    /// in the companion <c>.asar.unpacked</c> directory rather than inside
    /// the archive itself.
    /// </summary>
    public HashSet<string> UnpackedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// File extensions (e.g. <c>".node"</c>) that are always marked as
    /// "unpacked" – native modules must reside outside the ASAR to be
    /// loaded by the Node.js native addon loader.
    /// </summary>
    public HashSet<string> UnpackedExtensions { get; set; }
        = new(StringComparer.OrdinalIgnoreCase) { ".node" };

    /// <summary>
    /// When <see langword="true"/> the packer copies unpacked files to the
    /// companion directory.  Defaults to <see langword="true"/>.
    /// </summary>
    public bool CopyUnpackedFiles { get; set; } = true;

    /// <summary>
    /// Maximum degree of parallelism used when copying unpacked files.
    /// Defaults to the number of logical processors.
    /// </summary>
    public int UnpackedCopyParallelism { get; set; } = Environment.ProcessorCount;
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Packs an on-disk directory into an ASAR archive.
/// </summary>
public static class AsarPacker
{
    // ══════════════════════════════════════════════════════════════════════
    // Public API
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Packs the contents of <paramref name="sourceDirectory"/> into an ASAR
    /// archive at <paramref name="outputPath"/>.
    /// </summary>
    /// <param name="sourceDirectory">
    /// The root directory whose contents will become the archive root.
    /// </param>
    /// <param name="outputPath">
    /// Path of the output <c>.asar</c> file.  Created or overwritten.
    /// </param>
    /// <param name="options">
    /// Optional filtering and unpacking options.  Pass <see langword="null"/>
    /// to use sensible defaults (include everything, unpack <c>.node</c> files).
    /// </param>
    public static void Pack(
        string           sourceDirectory,
        string           outputPath,
        AsarPackOptions? options = null)
    {
        options ??= new AsarPackOptions();

        // ── Step 1: Crawl the source directory tree ───────────────────────

        (AsarDirectoryEntry root, List<PackedFile> packedFiles)
            = WalkDirectory(sourceDirectory, options);

        // ── Step 2: Assign content offsets ────────────────────────────────

        AssignOffsets(packedFiles);

        // ── Step 3: Write the binary archive ──────────────────────────────

        WriteArchive(outputPath, root, packedFiles);

        // ── Step 4: Copy unpacked files to companion directory ────────────

        if (options.CopyUnpackedFiles)
        {
            CopyUnpackedFiles(sourceDirectory, outputPath, packedFiles, options);
        }
    }

    /// <summary>
    /// Asynchronously packs <paramref name="sourceDirectory"/> into an ASAR
    /// archive at <paramref name="outputPath"/>.
    /// </summary>
    public static async Task PackAsync(
        string            sourceDirectory,
        string            outputPath,
        AsarPackOptions?  options           = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AsarPackOptions();

        (AsarDirectoryEntry root, List<PackedFile> packedFiles)
            = WalkDirectory(sourceDirectory, options);

        AssignOffsets(packedFiles);

        await WriteArchiveAsync(outputPath, root, packedFiles, cancellationToken)
              .ConfigureAwait(false);

        if (options.CopyUnpackedFiles)
        {
            await CopyUnpackedFilesAsync(
                sourceDirectory, outputPath, packedFiles, options, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Pass 1: Directory walk
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Crawls <paramref name="sourceDirectory"/> and returns an
    /// <see cref="AsarDirectoryEntry"/> tree and a flat ordered list of
    /// files to pack.
    /// </summary>
    private static (AsarDirectoryEntry Root, List<PackedFile> Files)
        WalkDirectory(string sourceDirectory, AsarPackOptions options)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Source directory not found: '{sourceDirectory}'");
        }

        var packedFiles = new List<PackedFile>();
        var root        = new AsarDirectoryEntry(string.Empty, string.Empty);

        // Recursively walk from the source root.
        WalkRecursive(
            sourceDirectory,
            root,
            parentVirtualPath: string.Empty,
            sourceDirectory,
            options,
            packedFiles);

        return (root, packedFiles);
    }

    /// <summary>
    /// Recursive helper that walks a single directory and populates the entry
    /// tree and <paramref name="packedFiles"/> list.
    /// </summary>
    private static void WalkRecursive(
        string             physicalDir,
        AsarDirectoryEntry directoryEntry,
        string             parentVirtualPath,
        string             sourceRoot,
        AsarPackOptions    options,
        List<PackedFile>   packedFiles)
    {
        // ── Process subdirectories first (matches original asar tool behaviour) ──

        foreach (string subPhysical in Directory.GetDirectories(physicalDir)
                                                .OrderBy(p => p, StringComparer.Ordinal))
        {
            string name        = Path.GetFileName(subPhysical);
            string virtualPath = parentVirtualPath.Length == 0 ? name : $"{parentVirtualPath}/{name}";

            // Check exclusion filters.
            if (IsExcluded(virtualPath, options)) continue;

            var subEntry = new AsarDirectoryEntry(name, virtualPath);
            directoryEntry.AddChild(subEntry);

            WalkRecursive(subPhysical, subEntry, virtualPath, sourceRoot, options, packedFiles);
        }

        // ── Process files ──────────────────────────────────────────────────

        foreach (string physicalFile in Directory.GetFiles(physicalDir)
                                                 .OrderBy(p => p, StringComparer.Ordinal))
        {
            string name        = Path.GetFileName(physicalFile);
            string virtualPath = parentVirtualPath.Length == 0 ? name : $"{parentVirtualPath}/{name}";
            string extension   = Path.GetExtension(name);

            // Check exclusion / inclusion filters.
            if (IsExcluded(virtualPath, options)) continue;
            if (!IsIncluded(virtualPath, options)) continue;

            var info = new FileInfo(physicalFile);
            long size = info.Length;

            // Determine whether this file should be "unpacked".
            bool isUnpacked = options.UnpackedPaths.Contains(virtualPath)
                           || options.UnpackedExtensions.Contains(extension);

            var fileEntry = new AsarFileEntry(name, virtualPath, offset: 0, size)
            {
                IsUnpacked   = isUnpacked,
                // On POSIX, check if the executable bit is set.
                IsExecutable = IsExecutable(physicalFile),
            };

            directoryEntry.AddChild(fileEntry);

            packedFiles.Add(new PackedFile(physicalFile, fileEntry));
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Pass 2: Assign offsets
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Walks the flat ordered list of files and assigns each packed (non-
    /// unpacked) file its content offset relative to the content base.
    /// </summary>
    private static void AssignOffsets(List<PackedFile> files)
    {
        long currentOffset = 0;

        foreach (PackedFile pf in files)
        {
            if (pf.Entry.IsUnpacked)
            {
                // Unpacked files have no offset in the archive stream.
                pf.Entry.Offset = 0;
                continue;
            }

            pf.Entry.Offset = currentOffset;
            currentOffset  += pf.Entry.Size;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Pass 3: Write archive
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Writes the binary ASAR header followed by all packed file content.
    /// </summary>
    private static void WriteArchive(
        string             outputPath,
        AsarDirectoryEntry root,
        List<PackedFile>   files)
    {
        using var dest = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            AsarConstants.DefaultBufferSize,
            useAsync: false);

        // Serialise header section.
        var header = new AsarHeader(root, contentBaseOffset: 0, jsonHeaderSize: 0);
        AsarHeaderSerializer.Serialize(header, dest);

        // Write packed file content sequentially.
        byte[] buffer = new byte[AsarConstants.DefaultBufferSize];

        foreach (PackedFile pf in files)
        {
            if (pf.Entry.IsUnpacked) continue;

            using var src = new FileStream(
                pf.PhysicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                AsarConstants.DefaultBufferSize);

            CopyStream(src, dest, buffer);
        }
    }

    /// <summary>
    /// Asynchronously writes the binary ASAR header and all packed files.
    /// </summary>
    private static async Task WriteArchiveAsync(
        string             outputPath,
        AsarDirectoryEntry root,
        List<PackedFile>   files,
        CancellationToken  cancellationToken)
    {
        await using var dest = new FileStream(
            outputPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            AsarConstants.DefaultBufferSize,
            useAsync: true);

        var header = new AsarHeader(root, contentBaseOffset: 0, jsonHeaderSize: 0);
        AsarHeaderSerializer.Serialize(header, dest);

        byte[] buffer = new byte[AsarConstants.DefaultBufferSize];

        foreach (PackedFile pf in files)
        {
            if (pf.Entry.IsUnpacked) continue;
            cancellationToken.ThrowIfCancellationRequested();

            await using var src = new FileStream(
                pf.PhysicalPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                AsarConstants.DefaultBufferSize,
                useAsync: true);

            await CopyStreamAsync(src, dest, buffer, cancellationToken).ConfigureAwait(false);
        }

        await dest.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Pass 4: Copy unpacked files
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Copies files marked as "unpacked" to the companion
    /// <c>&lt;archive&gt;.unpacked</c> directory.
    /// </summary>
    private static void CopyUnpackedFiles(
        string           sourceRoot,
        string           outputPath,
        List<PackedFile> files,
        AsarPackOptions  options)
    {
        string unpackedDir = outputPath + AsarConstants.UnpackedDirectorySuffix;

        foreach (PackedFile pf in files.Where(f => f.Entry.IsUnpacked))
        {
            string destPath = Path.Combine(
                unpackedDir,
                pf.Entry.VirtualPath.Replace('/', Path.DirectorySeparatorChar));

            // Create intermediate directories as needed.
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            File.Copy(pf.PhysicalPath, destPath, overwrite: true);
        }
    }

    /// <summary>
    /// Asynchronously copies unpacked files with bounded parallelism.
    /// </summary>
    private static async Task CopyUnpackedFilesAsync(
        string            sourceRoot,
        string            outputPath,
        List<PackedFile>  files,
        AsarPackOptions   options,
        CancellationToken cancellationToken)
    {
        string          unpackedDir   = outputPath + AsarConstants.UnpackedDirectorySuffix;
        List<PackedFile> unpackedFiles = files.Where(f => f.Entry.IsUnpacked).ToList();

        using var throttle = new SemaphoreSlim(options.UnpackedCopyParallelism);

        IEnumerable<Task> tasks = unpackedFiles.Select(async pf =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string destPath = Path.Combine(
                    unpackedDir,
                    pf.Entry.VirtualPath.Replace('/', Path.DirectorySeparatorChar));

                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                await using var src  = File.OpenRead(pf.PhysicalPath);
                await using var dest = File.Create(destPath);
                await src.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Filter helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns <see langword="true"/> when the virtual path matches any
    /// entry in <see cref="AsarPackOptions.ExcludePatterns"/>.
    /// </summary>
    private static bool IsExcluded(string virtualPath, AsarPackOptions options)
    {
        if (options.ExcludePatterns.Count == 0) return false;

        foreach (string pattern in options.ExcludePatterns)
        {
            if (MatchesGlob(virtualPath, pattern)) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns <see langword="true"/> when the virtual path passes the
    /// include filter (or when no include filter is specified).
    /// </summary>
    private static bool IsIncluded(string virtualPath, AsarPackOptions options)
    {
        if (options.IncludePatterns.Count == 0) return true;

        foreach (string pattern in options.IncludePatterns)
        {
            if (MatchesGlob(virtualPath, pattern)) return true;
        }
        return false;
    }

    // ── Minimal glob matcher (supports * and ** wildcards) ────────────────

    /// <summary>
    /// A minimal glob matcher that supports <c>*</c> (any non-separator
    /// sequence) and <c>**</c> (any sequence including separators).
    /// Suitable for common Electron filter patterns.
    /// </summary>
    private static bool MatchesGlob(string path, string pattern)
    {
        // Convert glob to a simple regex-like match using recursive expansion.
        return GlobMatch(path.AsSpan(), pattern.AsSpan());
    }

    private static bool GlobMatch(ReadOnlySpan<char> path, ReadOnlySpan<char> pattern)
    {
        while (true)
        {
            if (pattern.IsEmpty) return path.IsEmpty;
            if (path.IsEmpty)    return IsAllStars(pattern);

            if (pattern.StartsWith("**".AsSpan()))
            {
                ReadOnlySpan<char> rest = pattern[2..].TrimStart('/');

                // ** matches zero or more path segments.
                if (rest.IsEmpty) return true;

                // Try matching the rest against every suffix of path.
                for (int i = 0; i <= path.Length; i++)
                {
                    if (GlobMatch(path[i..], rest)) return true;
                    if (i < path.Length && path[i] == '/') { }
                }
                return false;
            }

            if (pattern[0] == '*')
            {
                // * matches any non-separator sequence.
                int end = path.IndexOf('/');
                ReadOnlySpan<char> segment = end < 0 ? path : path[..end];

                for (int i = 0; i <= segment.Length; i++)
                {
                    if (GlobMatch(path[i..], pattern[1..])) return true;
                }
                return false;
            }

            if (pattern[0] != '?' && pattern[0] != path[0]) return false;

            path    = path[1..];
            pattern = pattern[1..];
        }
    }

    private static bool IsAllStars(ReadOnlySpan<char> s)
    {
        foreach (char c in s)
            if (c != '*' && c != '/') return false;
        return true;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Executable-bit detection (POSIX)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns <see langword="true"/> when the file at
    /// <paramref name="path"/> has an executable permission bit set on
    /// POSIX systems.  Always returns <see langword="false"/> on Windows.
    /// </summary>
    private static bool IsExecutable(string path)
    {
        // On non-POSIX systems executable bits are not meaningful.
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return false;

        try
        {
            // stat the file and check the Unix execute bits.
            // We use Mono.Posix or the newer System.IO.UnixFileMode API (net7+).
            var info = new FileInfo(path);

            // UnixFileMode is available in .NET 7+.
            UnixFileMode mode = info.UnixFileMode;
            return (mode & (UnixFileMode.UserExecute
                          | UnixFileMode.GroupExecute
                          | UnixFileMode.OtherExecute)) != 0;
        }
        catch
        {
            return false;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Stream helpers
    // ══════════════════════════════════════════════════════════════════════

    private static void CopyStream(Stream source, Stream destination, byte[] buffer)
    {
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            destination.Write(buffer, 0, read);
    }

    private static async Task CopyStreamAsync(
        Stream            source,
        Stream            destination,
        byte[]            buffer,
        CancellationToken cancellationToken)
    {
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
    }

    // ── Internal staging record ───────────────────────────────────────────

    private sealed record PackedFile(string PhysicalPath, AsarFileEntry Entry);
}