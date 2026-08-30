using System.IO;
using FatxBridge.Core;

namespace FatxBridge.Windows;

/// <summary>
/// Opens a selected legacy Xbox 360 <c>Xbox360\Data0000...</c> container as a
/// read-only segmented stream. No writes, mounts, or FAT32 changes are made.
/// </summary>
public sealed class Xbox360UsbContainerSource : IDisposable
{
    public const int MaximumSegmentCount = SegmentedReadOnlyStream.MaximumSegmentCount;
    public const long MaximumAggregateLength = 64L * 1024 * 1024 * 1024;
    private const int FileBufferSize = 64 * 1024;

    private readonly Xbox360UsbContainer container;
    private readonly bool readOnly = true;
    private bool disposed;

    private Xbox360UsbContainerSource(
        string selectedPath,
        string folderPath,
        IReadOnlyList<string> segmentPaths,
        Xbox360UsbContainer container)
    {
        SelectedPath = selectedPath;
        FolderPath = folderPath;
        SegmentPaths = segmentPaths;
        this.container = container;
    }

    public string SelectedPath { get; }
    public string FolderPath { get; }
    public IReadOnlyList<string> SegmentPaths { get; }
    public Xbox360UsbContainer Container
    {
        get
        {
            EnsureNotDisposed();
            return container;
        }
    }

    public SegmentedReadOnlyStream Stream => Container.Stream;
    public long Length => Container.Length;
    public Xbox360UsbConfiguration Configuration => Container.Configuration;
    public IReadOnlyList<FatxPartitionCandidate> Partitions => Container.Partitions;
    public IReadOnlyList<FatxPartitionCandidate> ValidatedPartitions => Partitions;
    public IReadOnlyList<Xbox360UsbDiagnostic> Diagnostics => Container.Diagnostics;
    public bool IsReadOnly => readOnly;

    /// <summary>
    /// Discovers direct children of a selected Data0000 file's folder or an
    /// explicitly selected Xbox360 folder. Only exact case-insensitive names
    /// Data followed by four ASCII decimal digits are considered.
    /// </summary>
    public static IReadOnlyList<string> DiscoverSegmentPaths(string selectedPath)
    {
        string fullPath = NormalizeLocalPath(selectedPath);
        FileAttributes selectedAttributes = File.GetAttributes(fullPath);
        RejectReparsePoint(fullPath, selectedAttributes);

        string folderPath;
        if ((selectedAttributes & FileAttributes.Directory) != 0)
        {
            string folderName = new DirectoryInfo(fullPath).Name;
            if (!folderName.Equals("Xbox360", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Select an Xbox360 folder or its Data0000 file.", nameof(selectedPath));
            folderPath = fullPath;
        }
        else
        {
            if (!TryParseSegmentName(Path.GetFileName(fullPath), out int selectedIndex) || selectedIndex != 0)
                throw new ArgumentException("Select the Data0000 file or its Xbox360 folder.", nameof(selectedPath));
            folderPath = Path.GetDirectoryName(fullPath)
                ?? throw new ArgumentException("The Data0000 file has no containing folder.", nameof(selectedPath));
        }

        FileAttributes folderAttributes = File.GetAttributes(folderPath);
        if ((folderAttributes & FileAttributes.Directory) == 0)
            throw new IOException("The Data0000 parent is not a directory.");
        RejectReparsePoint(folderPath, folderAttributes);

        var pathsByIndex = new SortedDictionary<int, string>();
        foreach (string entryPath in Directory.EnumerateFileSystemEntries(folderPath))
        {
            string name = Path.GetFileName(entryPath);
            if (!TryParseSegmentName(name, out int index)) continue;
            if (!pathsByIndex.TryAdd(index, entryPath))
                throw new IOException($"The container contains duplicate segment index {index:0000}.");
            if (pathsByIndex.Count > MaximumSegmentCount)
                throw new IOException($"The container contains more than {MaximumSegmentCount} segments.");

            FileAttributes attributes = File.GetAttributes(entryPath);
            RejectReparsePoint(entryPath, attributes);
            if ((attributes & FileAttributes.Directory) != 0)
                throw new IOException($"Container segment {name} is a directory.");
        }

        if (pathsByIndex.Count < 4 || !pathsByIndex.ContainsKey(0) ||
            !pathsByIndex.ContainsKey(1) || !pathsByIndex.ContainsKey(2) || !pathsByIndex.ContainsKey(3))
            throw new IOException("The container must contain Data0000 through Data0003.");

        int maximumIndex = pathsByIndex.Keys.Max();
        var result = new List<string>(pathsByIndex.Count);
        long totalLength = 0;
        for (int index = 0; index <= maximumIndex; index++)
        {
            if (!pathsByIndex.TryGetValue(index, out string? path))
                throw new IOException($"The container has a gap before Data{maximumIndex:0000} (missing Data{index:0000}).");

            FileInfo file = new(path);
            long length = file.Length;
            if (length < 0) throw new IOException($"Container segment {file.Name} reported a negative length.");
            totalLength = checked(totalLength + length);
            if (totalLength > MaximumAggregateLength)
                throw new IOException($"The container exceeds the {MaximumAggregateLength / (1024L * 1024 * 1024)} GiB safety limit.");
            result.Add(path);
        }

        return result.ToArray();
    }

    public static Xbox360UsbContainerSource Open(
        string selectedPath,
        Action<string>? diagnostic = null)
    {
        string fullPath = NormalizeLocalPath(selectedPath);
        IReadOnlyList<string> segmentPaths = DiscoverSegmentPaths(fullPath);
        string folderPath = (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0
            ? fullPath
            : Path.GetDirectoryName(fullPath)!;
        var segments = new List<Stream>(segmentPaths.Count);
        try
        {
            foreach (string segmentPath in segmentPaths)
            {
                diagnostic?.Invoke($"Opening read-only USB container segment {Path.GetFileName(segmentPath)}.");
                segments.Add(OpenSegment(segmentPath));
            }

            Xbox360UsbContainer container = new(segments, leaveOpen: false, diagnostic);
            if (container.Length > MaximumAggregateLength)
            {
                container.Dispose();
                throw new IOException("The opened container exceeds the aggregate-size safety limit.");
            }
            return new Xbox360UsbContainerSource(fullPath, folderPath, segmentPaths, container);
        }
        catch
        {
            foreach (Stream segment in segments)
            {
                try { segment.Dispose(); }
                catch { /* Preserve the original open/probe failure. */ }
            }
            throw;
        }
    }

    public static bool TryOpen(
        string selectedPath,
        out Xbox360UsbContainerSource? source,
        out string? error,
        Action<string>? diagnostic = null)
    {
        try
        {
            source = Open(selectedPath, diagnostic);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            source = null;
            error = exception.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        container.Dispose();
    }

    private static FileStream OpenSegment(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        FileBufferSize,
        FileOptions.None);

    private static string NormalizeLocalPath(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        if (selectedPath.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("The selected path contains a NUL character.", nameof(selectedPath));
        if (selectedPath.IndexOfAny(['*', '?']) >= 0)
            throw new ArgumentException("Wildcards are not accepted for a selected container path.", nameof(selectedPath));

        string fullPath;
        try { fullPath = Path.GetFullPath(selectedPath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The selected path is invalid.", nameof(selectedPath), exception);
        }

        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal) ||
            fullPath.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("UNC and device paths are not accepted for a local container.", nameof(selectedPath));

        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        if (root.Length == 0 || root.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("The selected path must be on a local drive.", nameof(selectedPath));
        try
        {
            if (new DriveInfo(root).DriveType == DriveType.Network)
                throw new ArgumentException("Network-mounted paths are not accepted for a local container.", nameof(selectedPath));
        }
        catch (IOException)
        {
            // A removable drive may not answer DriveInfo while it is being
            // inserted; the file/attribute checks below remain authoritative.
        }
        for (int index = root.Length; index < fullPath.Length; index++)
            if (fullPath[index] == ':')
                throw new ArgumentException("Alternate data stream paths are not accepted.", nameof(selectedPath));

        return fullPath;
    }

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reparse points are not accepted in the selected container path: {path}");

        string? current = (attributes & FileAttributes.Directory) != 0
            ? path
            : Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(current))
        {
            FileAttributes parentAttributes = File.GetAttributes(current);
            if ((parentAttributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse points are not accepted in the selected container path: {current}");
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent.Equals(current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
    }

    private static bool TryParseSegmentName(string? name, out int index)
    {
        index = -1;
        if (name is null || name.Length != 8 ||
            !name.StartsWith("Data", StringComparison.OrdinalIgnoreCase)) return false;
        int parsed = 0;
        for (int offset = 4; offset < name.Length; offset++)
        {
            char digit = name[offset];
            if (digit is < '0' or > '9') return false;
            parsed = checked(parsed * 10 + digit - '0');
        }
        index = parsed;
        return true;
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
