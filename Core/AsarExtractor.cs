using Asar.Constants;
using Asar.Exceptions;
using Asar.Models;

namespace Asar.Core;

// ══════════════════════════════════════════════════════════════════════════════
// AsarExtractor
//
// Extracts one file, a subtree, or the entire archive to disk.
//
// Design
// ──────
// • All extraction paths go through the AsarReader so the same random-access
//   stream is reused across all extract calls – no extra file handles.
// • Extracting a single file is O(fileSize): the reader seeks directly to
//   the content offset and streams the bytes to disk.
// • Extracting the whole archive is a sequential pass over all files in
//   header order, which is optimal for spinning-disk I/O.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Options that control extraction behaviour.
/// </summary>
public sealed class AsarExtractOptions
{
    /// <summary>
    /// When <see langword="true"/> existing files in the output directory
    /// are overwritten.  Defaults to <see langword="true"/>.
    /// </summary>
    public bool Overwrite { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> (POSIX only) the executable bit is
    /// applied to extracted files that have <see cref="AsarFileEntry.IsExecutable"/>
    /// set.  Has no effect on Windows.  Defaults to <see langword="true"/>.
    /// </summary>
    public bool PreserveExecutableBit { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/> the companion <c>.asar.unpacked</c>
    /// directory is also copied to <paramref name="outputDirectory"/> when
    /// it exists.  Defaults to <see langword="true"/>.
    /// </summary>
    public bool ExtractUnpacked { get; set; } = true;

    /// <summary>
    /// Maximum degree of parallelism for file extraction.
    /// Set to 1 for strictly sequential extraction (safest for HDDs).
    /// Defaults to <see cref="Environment.ProcessorCount"/>.
    /// </summary>
    public int Parallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Optional progress callback invoked after each file is extracted.
    /// Receives the virtual path of the extracted entry.
    /// </summary>
    public Action<string>? OnFileExtracted { get; set; }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Extracts files from an ASAR archive to disk.
/// </summary>
public static class AsarExtractor
{
    // ══════════════════════════════════════════════════════════════════════
    // Extract single file
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts a single file from the archive to <paramref name="outputPath"/>.
    /// This is an O(fileSize) random-access read – the extractor seeks
    /// directly to the file's content without scanning the archive.
    /// </summary>
    /// <param name="reader">An open <see cref="AsarReader"/>.</param>
    /// <param name="virtualPath">Virtual path of the file inside the archive.</param>
    /// <param name="outputPath">Destination path on disk.</param>
    /// <param name="options">Extraction options.</param>
    public static void ExtractFile(
        AsarReader         reader,
        string             virtualPath,
        string             outputPath,
        AsarExtractOptions? options = null)
    {
        options ??= new AsarExtractOptions();

        AsarFileEntry entry = reader.GetFileEntry(virtualPath);

        if (!options.Overwrite && File.Exists(outputPath))
            return;

        // Create any missing parent directories.
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (entry.IsUnpacked)
        {
            // For unpacked files, copy from the companion directory.
            ExtractUnpackedFile(reader.ArchivePath, entry, outputPath, options);
        }
        else
        {
            // For packed files, stream directly from the archive.
            // Random-access seek is performed inside AsarEntryStream.
            using Stream src  = reader.OpenEntryStream(virtualPath);
            using var    dest = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                AsarConstants.DefaultBufferSize);

            src.CopyTo(dest, AsarConstants.DefaultBufferSize);
        }

        // Apply executable bit on POSIX when requested.
        if (options.PreserveExecutableBit && entry.IsExecutable)
            ApplyExecutableBit(outputPath);

        options.OnFileExtracted?.Invoke(virtualPath);
    }

    /// <summary>
    /// Asynchronously extracts a single file.
    /// </summary>
    public static async Task ExtractFileAsync(
        AsarReader          reader,
        string              virtualPath,
        string              outputPath,
        AsarExtractOptions? options           = null,
        CancellationToken   cancellationToken = default)
    {
        options ??= new AsarExtractOptions();

        AsarFileEntry entry = reader.GetFileEntry(virtualPath);

        if (!options.Overwrite && File.Exists(outputPath))
            return;

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (entry.IsUnpacked)
        {
            await ExtractUnpackedFileAsync(
                reader.ArchivePath, entry, outputPath, options, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await using Stream src  = reader.OpenEntryStream(virtualPath);
            await using var    dest = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                AsarConstants.DefaultBufferSize,
                useAsync: true);

            await src.CopyToAsync(dest, AsarConstants.DefaultBufferSize, cancellationToken)
                     .ConfigureAwait(false);
        }

        if (options.PreserveExecutableBit && entry.IsExecutable)
            ApplyExecutableBit(outputPath);

        options.OnFileExtracted?.Invoke(virtualPath);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Extract subtree / entire archive
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Extracts the entire archive to <paramref name="outputDirectory"/>.
    /// </summary>
    /// <param name="reader">An open <see cref="AsarReader"/>.</param>
    /// <param name="outputDirectory">
    /// Destination directory.  Created if it does not exist.
    /// </param>
    /// <param name="options">Extraction options.</param>
    public static void ExtractAll(
        AsarReader          reader,
        string              outputDirectory,
        AsarExtractOptions? options = null)
    {
        ExtractDirectory(reader, virtualPath: string.Empty, outputDirectory, options);
    }

    /// <summary>
    /// Asynchronously extracts the entire archive to
    /// <paramref name="outputDirectory"/>.
    /// </summary>
    public static Task ExtractAllAsync(
        AsarReader          reader,
        string              outputDirectory,
        AsarExtractOptions? options           = null,
        CancellationToken   cancellationToken = default)
        => ExtractDirectoryAsync(reader, virtualPath: string.Empty, outputDirectory, options, cancellationToken);

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts all files under the directory at <paramref name="virtualPath"/>
    /// into <paramref name="outputDirectory"/>, preserving the sub-tree
    /// structure.
    /// </summary>
    public static void ExtractDirectory(
        AsarReader          reader,
        string              virtualPath,
        string              outputDirectory,
        AsarExtractOptions? options = null)
    {
        options ??= new AsarExtractOptions();
        Directory.CreateDirectory(outputDirectory);

        // Collect all files under the given subtree.
        AsarDirectoryEntry rootEntry = string.IsNullOrEmpty(virtualPath)
            ? reader.Header.Root
            : reader.GetDirectoryEntry(virtualPath);

        List<AsarFileEntry> files = CollectFiles(rootEntry);

        if (options.Parallelism <= 1)
        {
            // Sequential extraction (optimal for HDD).
            foreach (AsarFileEntry file in files)
            {
                string relativePath = GetRelativePath(file.VirtualPath, virtualPath);
                string outputPath   = Path.Combine(
                    outputDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));

                ExtractFile(reader, file.VirtualPath, outputPath, options);
            }
        }
        else
        {
            // Parallel extraction (better for SSD / network shares).
            Parallel.ForEach(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = options.Parallelism },
                file =>
                {
                    string relativePath = GetRelativePath(file.VirtualPath, virtualPath);
                    string outputPath   = Path.Combine(
                        outputDirectory,
                        relativePath.Replace('/', Path.DirectorySeparatorChar));

                    ExtractFile(reader, file.VirtualPath, outputPath, options);
                });
        }
    }

    /// <summary>
    /// Asynchronously extracts a subtree.
    /// </summary>
    public static async Task ExtractDirectoryAsync(
        AsarReader          reader,
        string              virtualPath,
        string              outputDirectory,
        AsarExtractOptions? options           = null,
        CancellationToken   cancellationToken = default)
    {
        options ??= new AsarExtractOptions();
        Directory.CreateDirectory(outputDirectory);

        AsarDirectoryEntry rootEntry = string.IsNullOrEmpty(virtualPath)
            ? reader.Header.Root
            : reader.GetDirectoryEntry(virtualPath);

        List<AsarFileEntry> files = CollectFiles(rootEntry);

        using var throttle = new SemaphoreSlim(Math.Max(1, options.Parallelism));

        IEnumerable<Task> tasks = files.Select(async file =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string relativePath = GetRelativePath(file.VirtualPath, virtualPath);
                string outputPath   = Path.Combine(
                    outputDirectory,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));

                await ExtractFileAsync(
                    reader, file.VirtualPath, outputPath, options, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Convenience factory overloads (open + extract + close)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Opens the ASAR at <paramref name="archivePath"/>, extracts all files
    /// to <paramref name="outputDirectory"/>, and closes the archive.
    /// </summary>
    public static void ExtractAll(
        string              archivePath,
        string              outputDirectory,
        AsarExtractOptions? options = null)
    {
        using var reader = new AsarReader(archivePath);
        ExtractAll(reader, outputDirectory, options);
    }

    /// <summary>
    /// Opens the ASAR at <paramref name="archivePath"/>, asynchronously
    /// extracts all files, and closes the archive.
    /// </summary>
    public static async Task ExtractAllAsync(
        string              archivePath,
        string              outputDirectory,
        AsarExtractOptions? options           = null,
        CancellationToken   cancellationToken = default)
    {
        await using var reader = new AsarReader(archivePath);
        await ExtractAllAsync(reader, outputDirectory, options, cancellationToken)
              .ConfigureAwait(false);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Internal helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns a flat list of every <see cref="AsarFileEntry"/> under
    /// <paramref name="dir"/> in depth-first order.
    /// </summary>
    private static List<AsarFileEntry> CollectFiles(AsarDirectoryEntry dir)
    {
        var result = new List<AsarFileEntry>();
        CollectFilesRecursive(dir, result);
        return result;
    }

    private static void CollectFilesRecursive(AsarDirectoryEntry dir, List<AsarFileEntry> result)
    {
        foreach (AsarEntry child in dir.Children.Values)
        {
            if (child is AsarFileEntry file)
                result.Add(file);
            else if (child is AsarDirectoryEntry subDir)
                CollectFilesRecursive(subDir, result);
        }
    }

    /// <summary>
    /// Returns the portion of <paramref name="fullVirtualPath"/> that is
    /// relative to <paramref name="baseVirtualPath"/>.
    /// </summary>
    private static string GetRelativePath(string fullVirtualPath, string baseVirtualPath)
    {
        if (string.IsNullOrEmpty(baseVirtualPath)) return fullVirtualPath;

        if (fullVirtualPath.StartsWith(baseVirtualPath, StringComparison.Ordinal))
        {
            string relative = fullVirtualPath[baseVirtualPath.Length..];
            return relative.TrimStart('/');
        }

        return fullVirtualPath;
    }

    /// <summary>
    /// Copies an unpacked file from the companion directory to the output.
    /// </summary>
    private static void ExtractUnpackedFile(
        string             archivePath,
        AsarFileEntry      entry,
        string             outputPath,
        AsarExtractOptions options)
    {
        string unpackedDir  = archivePath + AsarConstants.UnpackedDirectorySuffix;
        string sourcePath   = Path.Combine(
            unpackedDir,
            entry.VirtualPath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(sourcePath))
        {
            throw new AsarUnpackedException(entry.VirtualPath);
        }

        File.Copy(sourcePath, outputPath, overwrite: options.Overwrite);
    }

    private static async Task ExtractUnpackedFileAsync(
        string             archivePath,
        AsarFileEntry      entry,
        string             outputPath,
        AsarExtractOptions options,
        CancellationToken  cancellationToken)
    {
        string unpackedDir = archivePath + AsarConstants.UnpackedDirectorySuffix;
        string sourcePath  = Path.Combine(
            unpackedDir,
            entry.VirtualPath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(sourcePath))
            throw new AsarUnpackedException(entry.VirtualPath);

        await using var src  = File.OpenRead(sourcePath);
        await using var dest = new FileStream(
            outputPath,
            options.Overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            AsarConstants.DefaultBufferSize,
            useAsync: true);

        await src.CopyToAsync(dest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the Unix execute bits on <paramref name="path"/> when running on
    /// a POSIX system.  No-op on Windows.
    /// </summary>
    private static void ApplyExecutableBit(string path)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        try
        {
            var info = new FileInfo(path);
            // Add user+group+other execute bits to existing permissions.
            UnixFileMode newMode = info.UnixFileMode
                                 | UnixFileMode.UserExecute
                                 | UnixFileMode.GroupExecute
                                 | UnixFileMode.OtherExecute;
            info.UnixFileMode = newMode;
        }
        catch
        {
            // Best-effort; ignore permission errors.
        }
    }
}