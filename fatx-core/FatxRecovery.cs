namespace FatxBridge.Core;

/// <summary>Conservative status assigned to a deleted FATX directory entry.</summary>
public enum FatxDeletedEntryClassification
{
    InvalidMetadata,
    ZeroLength,
    ContiguousUnallocated,
    ClustersReusedOrOverwritten,
    Uncertain,
}

/// <summary>Raised when a deleted-file recovery operation cannot be made safely.</summary>
public sealed class FatxRecoveryException : IOException
{
    public FatxRecoveryException(string message) : base(message) { }
}

/// <summary>
/// A deleted 0xE5 directory slot and its best-effort recovery metadata.
/// FATX deletion removes the original name length and FAT chain links, so a
/// recovered name and a contiguous cluster range are hypotheses, not proof.
/// </summary>
public sealed record FatxDeletedEntryCandidate(
    string DirectoryPath,
    uint DirectoryFirstCluster,
    long SlotOffset,
    string RecoveredName,
    bool NameWasRecovered,
    bool OriginalNameLengthKnown,
    byte Attributes,
    uint FirstCluster,
    uint FileSize,
    uint CreationTimestamp,
    uint LastWriteTimestamp,
    uint LastAccessTimestamp,
    DateTimeOffset? CreationTime,
    DateTimeOffset? LastWriteTime,
    DateTimeOffset? LastAccessTime,
    FatxDeletedEntryClassification Classification,
    string ClassificationReason)
{
    /// <summary>Stable name for UI listings, either a plausible name or a synthetic label.</summary>
    public string DisplayName => RecoveredName;

    /// <summary>True only when the remaining name bytes formed a plausible ASCII FATX name.</summary>
    public bool HasPlausibleName => NameWasRecovered;

    /// <summary>Deleted slots never retain a trustworthy original name length.</summary>
    public bool IsOriginalNameLengthUnknown => !OriginalNameLengthKnown;

    public bool IsDirectory => (Attributes & 0x10) != 0;

    /// <summary>True only for a non-directory candidate whose complete range was free at scan time.</summary>
    public bool CanAttemptRead => Classification == FatxDeletedEntryClassification.ContiguousUnallocated && !IsDirectory;

    public bool IsContiguousUnallocatedCandidate =>
        Classification == FatxDeletedEntryClassification.ContiguousUnallocated;

    public long SourceSlotOffset => SlotOffset;
}

/// <summary>Read-only values decoded from a deleted FATX slot during validation.</summary>
internal readonly record struct DeletedRecoveryValidation(uint FirstCluster, uint FileSize);
