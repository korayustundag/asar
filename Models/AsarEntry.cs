namespace Asar.Models;

// ══════════════════════════════════════════════════════════════════════════════
// Domain model for ASAR archive entries
//
// The ASAR header JSON describes a virtual filesystem tree.  Every node in
// that tree is an AsarEntry.  Leaf nodes (files) are AsarFileEntry instances;
// interior nodes (directories) are AsarDirectoryEntry instances.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Discriminated-union tag that identifies the concrete type of an
/// <see cref="AsarEntry"/> without requiring a runtime type-check.
/// </summary>
public enum AsarEntryKind
{
    /// <summary>The entry represents a regular file.</summary>
    File,

    /// <summary>The entry represents a directory.</summary>
    Directory
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Abstract base for every node in the ASAR virtual-filesystem tree.
/// </summary>
public abstract class AsarEntry
{
    // ── Identity ─────────────────────────────────────────────────────────

    /// <summary>The simple name of this entry (no path separators).</summary>
    public string Name { get; set; }

    /// <summary>
    /// The full virtual path inside the archive, using forward slashes.
    /// The root directory's path is an empty string.
    /// </summary>
    public string VirtualPath { get; set; }

    /// <summary>Whether this is a file or a directory.</summary>
    public abstract AsarEntryKind Kind { get; }

    // ── Flags ────────────────────────────────────────────────────────────

    /// <summary>
    /// When <see langword="true"/> the entry's bytes are stored in the
    /// companion <c>.asar.unpacked</c> directory rather than inside the
    /// archive stream itself.
    /// </summary>
    public bool IsUnpacked { get; set; }

    // ── Navigation ───────────────────────────────────────────────────────

    /// <summary>
    /// Reference to the parent directory, or <see langword="null"/> for the
    /// root node.
    /// </summary>
    public AsarDirectoryEntry? Parent { get; internal set; }

    // ── Construction ─────────────────────────────────────────────────────

    /// <summary>Initialises the entry with its display name and virtual path.</summary>
    protected AsarEntry(string name, string virtualPath)
    {
        Name        = name;
        VirtualPath = virtualPath;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Returns <see langword="true"/> if the entry is a file.</summary>
    public bool IsFile => Kind == AsarEntryKind.File;

    /// <summary>Returns <see langword="true"/> if the entry is a directory.</summary>
    public bool IsDirectory => Kind == AsarEntryKind.Directory;

    /// <inheritdoc/>
    public override string ToString() => $"[{Kind}] {VirtualPath}";
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Represents a file entry inside an ASAR archive.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Offset"/> is relative to the <em>content base</em> – the
/// first byte that comes immediately after the (padded) header section.
/// Actual position in the stream = contentBase + Offset.
/// </para>
/// <para>
/// When <see cref="AsarEntry.IsUnpacked"/> is <see langword="true"/>,
/// <see cref="Offset"/> has no meaningful value and the bytes must be read
/// from the companion unpacked directory.
/// </para>
/// </remarks>
public sealed class AsarFileEntry : AsarEntry
{
    /// <inheritdoc/>
    public override AsarEntryKind Kind => AsarEntryKind.File;

    // ── Data location inside the archive stream ───────────────────────────

    /// <summary>
    /// Byte offset of the file's content relative to the content base of
    /// the archive.  Stored as <c>long</c> to support archives larger than
    /// 2 GiB without issues.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>Byte length of the file's content.</summary>
    public long Size { get; set; }

    // ── Optional metadata ─────────────────────────────────────────────────

    /// <summary>
    /// When <see langword="true"/> the file should be marked executable on
    /// POSIX systems after extraction.
    /// </summary>
    public bool IsExecutable { get; set; }

    /// <summary>
    /// Optional integrity descriptor (algorithm + hash) stored by
    /// Electron's asar tool.  May be <see langword="null"/> for archives
    /// created without integrity information.
    /// </summary>
    public AsarIntegrity? Integrity { get; set; }

    // ── Construction ─────────────────────────────────────────────────────

    /// <summary>Creates a new file entry with the given name and virtual path.</summary>
    public AsarFileEntry(string name, string virtualPath) : base(name, virtualPath) { }

    /// <summary>
    /// Creates a fully-initialised file entry.
    /// </summary>
    public AsarFileEntry(string name, string virtualPath, long offset, long size)
        : base(name, virtualPath)
    {
        Offset = offset;
        Size   = size;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Represents a directory entry inside an ASAR archive.
/// </summary>
public sealed class AsarDirectoryEntry : AsarEntry
{
    /// <inheritdoc/>
    public override AsarEntryKind Kind => AsarEntryKind.Directory;

    /// <summary>
    /// Child entries keyed by their simple name.
    /// Insertion order is preserved (Dictionary in .NET maintains insertion
    /// order since .NET Core 3+, matching JS object key enumeration).
    /// </summary>
    public Dictionary<string, AsarEntry> Children { get; }
        = new(StringComparer.Ordinal);

    // ── Construction ─────────────────────────────────────────────────────

    /// <summary>Creates a new directory entry with the given name and virtual path.</summary>
    public AsarDirectoryEntry(string name, string virtualPath) : base(name, virtualPath) { }

    // ── Child management ─────────────────────────────────────────────────

    /// <summary>
    /// Adds a child entry to this directory, linking the parent pointer.
    /// </summary>
    /// <param name="entry">The child to add.</param>
    /// <exception cref="ArgumentException">
    /// Thrown when a child with the same name already exists.
    /// </exception>
    public void AddChild(AsarEntry entry)
    {
        // Link the parent so callers can navigate upward.
        entry.Parent = this;
        Children.Add(entry.Name, entry);
    }

    /// <summary>
    /// Attempts to add a child entry.  Returns <see langword="false"/> if a
    /// child with the same name already exists.
    /// </summary>
    public bool TryAddChild(AsarEntry entry)
    {
        if (Children.ContainsKey(entry.Name))
            return false;

        entry.Parent = this;
        Children[entry.Name] = entry;
        return true;
    }

    /// <summary>
    /// Removes the child with the specified name.
    /// Returns <see langword="true"/> when the child was found and removed.
    /// </summary>
    public bool RemoveChild(string name) => Children.Remove(name);

    /// <summary>
    /// Tries to get a child by simple name.
    /// </summary>
    public bool TryGetChild(string name, out AsarEntry? entry)
        => Children.TryGetValue(name, out entry);
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Optional integrity record attached to a file entry by Electron's asar tool.
/// Preserved verbatim during read/write so the integrity chain is not broken.
/// </summary>
public sealed class AsarIntegrity
{
    /// <summary>Hash algorithm name, e.g. <c>"SHA256"</c>.</summary>
    public string Algorithm { get; set; } = string.Empty;

    /// <summary>Hex-encoded hash of the file's content.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Block size used for chunked hashing (optional).</summary>
    public int? BlockSize { get; set; }

    /// <summary>Per-block hashes (optional, used by Electron's integrity check).</summary>
    public List<string>? Blocks { get; set; }
}