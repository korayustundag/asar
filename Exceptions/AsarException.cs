namespace Asar.Exceptions;

// ══════════════════════════════════════════════════════════════════════════════
// Exception hierarchy
// All ASAR-specific errors derive from AsarException so callers can catch the
// whole family with a single catch block when desired.
// ══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Base class for all exceptions thrown by the Asar library.
/// </summary>
public class AsarException : IOException
{
    /// <inheritdoc/>
    public AsarException(string message) : base(message) { }

    /// <inheritdoc/>
    public AsarException(string message, Exception innerException)
        : base(message, innerException) { }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Thrown when the binary header of an ASAR file cannot be parsed –
/// e.g. the magic bytes are missing, a field is out of range, or the
/// JSON header is malformed.
/// </summary>
public sealed class AsarHeaderException : AsarException
{
    /// <inheritdoc/>
    public AsarHeaderException(string message) : base(message) { }

    /// <inheritdoc/>
    public AsarHeaderException(string message, Exception innerException)
        : base(message, innerException) { }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Thrown when a requested path does not exist inside the archive.
/// </summary>
public sealed class AsarEntryNotFoundException : AsarException
{
    /// <summary>The virtual path that could not be located.</summary>
    public string EntryPath { get; }

    /// <inheritdoc/>
    public AsarEntryNotFoundException(string entryPath)
        : base($"Entry not found inside the ASAR archive: '{entryPath}'")
    {
        EntryPath = entryPath;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Thrown when an operation that targets a file entry is called on a
/// directory entry, or vice-versa.
/// </summary>
public sealed class AsarEntryTypeMismatchException : AsarException
{
    /// <summary>The virtual path that caused the mismatch.</summary>
    public string EntryPath { get; }

    /// <inheritdoc/>
    public AsarEntryTypeMismatchException(string entryPath, string expectedType)
        : base($"Entry '{entryPath}' is not a {expectedType}.")
    {
        EntryPath = entryPath;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Thrown when an entry that is flagged as "unpacked" is accessed in a way
/// that requires reading its bytes from the archive stream (which holds no
/// data for unpacked entries).
/// </summary>
public sealed class AsarUnpackedException : AsarException
{
    /// <summary>The virtual path of the unpacked entry.</summary>
    public string EntryPath { get; }

    /// <inheritdoc/>
    public AsarUnpackedException(string entryPath)
        : base($"Entry '{entryPath}' is unpacked and its data is stored " +
               $"outside the archive in the companion '.unpacked' directory.")
    {
        EntryPath = entryPath;
    }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Thrown when a modification or write operation is attempted on an archive
/// that was opened in read-only mode.
/// </summary>
public sealed class AsarReadOnlyException : AsarException
{
    /// <inheritdoc/>
    public AsarReadOnlyException()
        : base("The archive was opened in read-only mode and cannot be modified.") { }
}

// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Thrown when a safety limit is exceeded – e.g. the header size exceeds
/// <see cref="Asar.Constants.AsarConstants.MaxHeaderSize"/>.
/// </summary>
public sealed class AsarLimitExceededException : AsarException
{
    /// <inheritdoc/>
    public AsarLimitExceededException(string message) : base(message) { }
}