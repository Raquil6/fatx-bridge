using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using FatxBridge.Core;

namespace FatxBridge.Windows;

/// <summary>
/// Standalone read-only viewer for an already-authorized <see cref="RawStorageInspector"/>.
/// It deliberately has no source-path picker and never opens or disposes the caller's stream.
/// </summary>
public partial class SectorViewerWindow : Window
{
    private enum NavigationKind
    {
        RawRange,
        Sector,
        Cluster
    }

    private readonly RawStorageInspector inspector;
    private readonly FatxVolumeMetadata? metadata;
    private RawStorageView? currentView;
    private NavigationKind navigationKind = NavigationKind.RawRange;
    private long currentOffset;
    private long currentCount = 256;
    private long currentSector;
    private long currentSectorCount = 1;
    private uint currentCluster;

    public SectorViewerWindow(RawStorageInspector inspector, FatxVolumeMetadata? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        this.inspector = inspector;
        this.metadata = metadata;

        InitializeComponent();
        ClusterAvailabilityText.Text = metadata is null
            ? "No validated FATX metadata supplied."
            : $"Validated cluster size: {checked((long)metadata.SectorsPerCluster * metadata.SectorSize):N0} bytes";
        ClusterNumberTextBox.Text = metadata is null
            ? "1"
            : metadata.RootFirstCluster.ToString(CultureInfo.InvariantCulture);
        ReadOnlyBannerText.Text =
            $"READ-ONLY • Source is caller-authorized and never modified. Authoritative source length: {inspector.SourceLength:N0} bytes; logical sector size: {inspector.LogicalSectorSize:N0} bytes.";
        ReadOnlyBannerText.ToolTip =
            "The caller owns source authorization and lifetime. This window only reads bounded ranges and can export the displayed bytes to a normal file.";
        Loaded += Viewer_Loaded;
    }

    /// <summary>Exports the currently displayed bytes with adjacent temporary-file publication.</summary>
    public async Task ExportCurrentBytesAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        RawStorageView view = currentView ?? throw new InvalidOperationException("Read a view before exporting bytes.");

        string destination = Path.GetFullPath(destinationPath);
        string? directory = Path.GetDirectoryName(destination);
        string fileName = Path.GetFileName(destination);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
            throw new ArgumentException("The export destination must name a file.", nameof(destinationPath));
        EnsureLocalDestination(destination);

        string temporary = Path.Combine(directory, $".{fileName}.fatxbridge-{Guid.NewGuid():N}.tmp");
        bool published = false;
        try
        {
            byte[] bytes = view.Bytes;
            await using (var output = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                options: FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(true);
                await output.FlushAsync(cancellationToken).ConfigureAwait(true);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
            published = true;
        }
        finally
        {
            if (!published)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void Viewer_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Viewer_Loaded;
        ReadRawAt(0, currentCount);
    }

    private void ReadOffset_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadNonNegativeLong(OffsetTextBox.Text, "absolute byte offset", out long offset) ||
            !TryReadNonNegativeLong(CountTextBox.Text, "byte count", out long count)) return;
        ReadRawAt(offset, count);
    }

    private void ReadSector_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadNonNegativeLong(SectorNumberTextBox.Text, "logical sector number", out long sector) ||
            !TryReadNonNegativeLong(SectorCountTextBox.Text, "sector count", out long count)) return;
        ReadSectorAt(sector, count);
    }

    private void ReadCluster_Click(object sender, RoutedEventArgs e)
    {
        if (metadata is null)
        {
            SetStatus("Cluster navigation is unavailable until the caller supplies validated FATX metadata.");
            return;
        }
        if (!uint.TryParse(ClusterNumberTextBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out uint cluster) || cluster == 0)
        {
            SetStatus("Enter a positive FATX cluster number.");
            return;
        }
        ReadClusterAt(cluster);
    }

    private void Previous_Click(object sender, RoutedEventArgs e)
    {
        if (currentView is null)
        {
            SetStatus("Read a view before using navigation.");
            return;
        }

        try
        {
            switch (navigationKind)
            {
                case NavigationKind.RawRange:
                    long rawStep = Math.Max(1, currentCount);
                    ReadRawAt(currentOffset > rawStep ? currentOffset - rawStep : 0, currentCount);
                    break;
                case NavigationKind.Sector:
                    if (currentSector == 0) { SetStatus("Already at the first logical sector."); return; }
                    ReadSectorAt(currentSector - 1, currentSectorCount);
                    break;
                case NavigationKind.Cluster:
                    if (currentCluster <= 1) { SetStatus("Already at the first FATX cluster."); return; }
                    ReadClusterAt(currentCluster - 1);
                    break;
            }
        }
        catch (OverflowException exception) { SetStatus($"Navigation rejected: {exception.Message}"); }
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (currentView is null)
        {
            SetStatus("Read a view before using navigation.");
            return;
        }

        try
        {
            switch (navigationKind)
            {
                case NavigationKind.RawRange:
                    long rawStep = Math.Max(1, currentCount);
                    if (currentOffset > long.MaxValue - rawStep)
                    {
                        SetStatus("Next offset would overflow.");
                        return;
                    }
                    ReadRawAt(currentOffset + rawStep, currentCount);
                    break;
                case NavigationKind.Sector:
                    if (currentSector == long.MaxValue) { SetStatus("Next sector would overflow."); return; }
                    ReadSectorAt(currentSector + 1, currentSectorCount);
                    break;
                case NavigationKind.Cluster:
                    if (metadata is null || (long)currentCluster >= metadata.ClusterCount || currentCluster == uint.MaxValue)
                    {
                        SetStatus("Already at the last validated FATX cluster.");
                        return;
                    }
                    ReadClusterAt(currentCluster + 1);
                    break;
            }
        }
        catch (OverflowException exception) { SetStatus($"Navigation rejected: {exception.Message}"); }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (currentView is null)
        {
            ReadRawAt(0, currentCount);
            return;
        }

        switch (navigationKind)
        {
            case NavigationKind.RawRange: ReadRawAt(currentOffset, currentCount); break;
            case NavigationKind.Sector: ReadSectorAt(currentSector, currentSectorCount); break;
            case NavigationKind.Cluster: ReadClusterAt(currentCluster); break;
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (currentView is null)
        {
            SetStatus("Read a view before exporting bytes.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export displayed read-only bytes",
            Filter = "Binary files (*.bin)|*.bin|All files (*.*)|*.*",
            DefaultExt = ".bin",
            AddExtension = true,
            FileName = "fatx-view.bin",
            CheckPathExists = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await ExportCurrentBytesAsync(dialog.FileName);
            SetStatus($"Exported {currentView.Length:N0} displayed bytes. The inspection source was not modified.");
        }
        catch (OperationCanceledException) { SetStatus("Export cancelled; no completed export was published."); }
        catch (IOException exception) { SetStatus($"Export failed; no completed export was published: {exception.Message}"); }
        catch (UnauthorizedAccessException exception) { SetStatus($"Export denied; no completed export was published: {exception.Message}"); }
    }

    private void ReadRawAt(long offset, long count)
    {
        try
        {
            RawStorageView view = inspector.ReadRange(offset, count);
            currentView = view;
            navigationKind = NavigationKind.RawRange;
            currentOffset = offset;
            currentCount = count;
            OffsetTextBox.Text = offset.ToString(CultureInfo.InvariantCulture);
            CountTextBox.Text = count.ToString(CultureInfo.InvariantCulture);
            Display(view);
        }
        catch (ArgumentException exception) { SetStatus($"Raw range rejected: {exception.Message}"); }
        catch (IOException exception) { SetStatus($"Raw range could not be read: {exception.Message}"); }
        catch (OverflowException exception) { SetStatus($"Raw range rejected: {exception.Message}"); }
    }

    private void ReadSectorAt(long sector, long count)
    {
        try
        {
            RawStorageView view = inspector.ReadSector(sector, count);
            currentView = view;
            navigationKind = NavigationKind.Sector;
            currentSector = sector;
            currentSectorCount = count;
            SectorNumberTextBox.Text = sector.ToString(CultureInfo.InvariantCulture);
            SectorCountTextBox.Text = count.ToString(CultureInfo.InvariantCulture);
            Display(view);
        }
        catch (ArgumentException exception) { SetStatus($"Sector range rejected: {exception.Message}"); }
        catch (IOException exception) { SetStatus($"Sector range could not be read: {exception.Message}"); }
        catch (OverflowException exception) { SetStatus($"Sector range rejected: {exception.Message}"); }
    }

    private void ReadClusterAt(uint cluster)
    {
        try
        {
            RawStorageView view = inspector.ReadFatxCluster(metadata!, cluster);
            currentView = view;
            navigationKind = NavigationKind.Cluster;
            currentCluster = cluster;
            ClusterNumberTextBox.Text = cluster.ToString(CultureInfo.InvariantCulture);
            Display(view);
        }
        catch (ArgumentException exception) { SetStatus($"Cluster range rejected: {exception.Message}"); }
        catch (IOException exception) { SetStatus($"Cluster range could not be read: {exception.Message}"); }
        catch (OverflowException exception) { SetStatus($"Cluster range rejected: {exception.Message}"); }
    }

    private void Display(RawStorageView view)
    {
        DumpTextBox.Text = view.HexDump;
        string relative = view.PartitionRelativeOffset is long partitionRelative
            ? $"0x{partitionRelative:X16} ({partitionRelative:N0})"
            : "not available (no partition context)";
        string context = view.Kind switch
        {
            RawStorageViewKind.Sector => $"Sector {view.SectorNumber:N0}",
            RawStorageViewKind.FatxCluster => $"FATX cluster {view.ClusterNumber:N0}",
            RawStorageViewKind.FatxHeader => "FATX header",
            RawStorageViewKind.FatxAllocationTable => "FATX allocation table",
            _ => "Raw range"
        };
        string cap = view.IsTruncated
            ? $" Requested {view.RequestedLength:N0} bytes; display capped at {RawStorageInspector.MaximumViewBytes:N0}."
            : string.Empty;
        string end = view.IsAtEnd ? " At authoritative source end." : string.Empty;
        SetStatus($"READ-ONLY • {context}. Absolute offset: 0x{view.AbsoluteOffset:X16} ({view.AbsoluteOffset:N0}); length: {view.Length:N0}; partition-relative offset: {relative}.{cap}{end}");
    }

    private bool TryReadNonNegativeLong(string? text, string label, out long value)
    {
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
        {
            SetStatus($"Enter a non-negative {label}.");
            return false;
        }
        return true;
    }

    private static void EnsureLocalDestination(string destination)
    {
        if (destination.StartsWith("\\\\", StringComparison.Ordinal) || destination.StartsWith("//", StringComparison.Ordinal))
            throw new IOException("The viewer exports only to a local file path; network shares are not used.");

        string? root = Path.GetPathRoot(destination);
        if (string.IsNullOrEmpty(root)) return;
        DriveType driveType;
        try
        {
            driveType = new DriveInfo(root).DriveType;
        }
        catch (ArgumentException exception)
        {
            throw new IOException("The viewer could not verify that the export destination is local.", exception);
        }
        catch (IOException exception)
        {
            throw new IOException("The viewer could not verify that the export destination is local.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new IOException("The viewer could not verify that the export destination is local.", exception);
        }
        if (driveType == DriveType.Network)
            throw new IOException("The viewer exports only to a local file path; network drives are not used.");
    }

    private void SetStatus(string message) => StatusText.Text = message;
}
