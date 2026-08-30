using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FatxBridge.Core;
using Microsoft.Win32;

namespace FatxBridge.Windows;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private enum BladeKind { Storage, Browse, Tools, About }

    private DriveScanItem? selectedDrive;
    private DriveScanItem? browsedDrive;
    private BladeKind currentBlade = BladeKind.Storage;
    private bool browseTabAvailable;
    private bool browseTabAnimating;
    private PartitionItem? selectedPartition;
    private bool isBusy;
    private long rootLoadGeneration;
    private string status = "Scanning local physical drives read-only…";
    private FatxWinFspMount? mount;
    private ElevatedDriveBroker? driveBroker;
    private CancellationTokenSource? transferCancellation;
    private double transferProgress;
    private Visibility transferVisibility = Visibility.Collapsed;
    private bool canCancelTransfer;
    private string volumeLabel = "FATX";
    private readonly VolumeLabelStore volumeLabels = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        AddHandler(Button.ClickEvent, new RoutedEventHandler(Button_ClickSound));
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        BladeTabBar.SizeChanged += BladeTabBar_SizeChanged;
        Loaded += async (_, _) => await InitializeAsync();
        Closing += MainWindow_Closing;
        Closed += (_, _) => { Unmount(); driveBroker?.Dispose(); };
    }

    public ObservableCollection<DriveScanItem> Drives { get; } = [];
    public ObservableCollection<PartitionItem> Partitions { get; } = [];
    public ObservableCollection<DirectoryItem> RootEntries { get; } = [];
    public DriveScanItem? SelectedDrive { get => selectedDrive; set { selectedDrive = value; OnChanged(); OnChanged(nameof(CanOpen)); OnChanged(nameof(CanBackup)); OnChanged(nameof(CanRestore)); OnChanged(nameof(CanMount)); OnChanged(nameof(CanMountReadWrite)); OnChanged(nameof(CanUsePartitionTools)); RefreshVolumeLabel(); } }
    public PartitionItem? SelectedPartition { get => selectedPartition; set { selectedPartition = value; OnChanged(); OnChanged(nameof(CanMount)); OnChanged(nameof(CanMountReadWrite)); OnChanged(nameof(CanEditVolumeLabel)); OnChanged(nameof(CanUsePartitionTools)); RefreshVolumeLabel(); } }
    public bool IsBusy { get => isBusy; private set { isBusy = value; OnChanged(); OnChanged(nameof(IsNotBusy)); OnChanged(nameof(CanOpen)); OnChanged(nameof(CanBackup)); OnChanged(nameof(CanRestore)); OnChanged(nameof(CanMount)); OnChanged(nameof(CanMountReadWrite)); OnChanged(nameof(CanEditVolumeLabel)); OnChanged(nameof(CanUsePartitionTools)); } }
    public bool IsNotBusy => !IsBusy && mount is null;
    public bool CanOpen => SelectedDrive is not null && !IsBusy;
    public bool CanBackup => SelectedDrive is { IsPhysicalDevice: true } && mount is null && !IsBusy;
    public bool CanRestore => SelectedDrive is { IsPhysicalDevice: true } && mount is null && !IsBusy;
    public bool CanMount => SelectedDrive is { IsUsbContainer: false } && SelectedPartition is not null && mount is null && !IsBusy;
    public bool CanMountReadWrite => CanMount && SelectedDrive!.SourceSupportsWrite && SelectedPartition!.Candidate.SupportsWrite;
    public bool CanUsePartitionTools => SelectedDrive is not null && SelectedPartition is not null && mount is null && !IsBusy;
    public bool CanEditVolumeLabel => SelectedPartition is not null && mount is null && !IsBusy;
    public bool IsMounted => mount is not null;
    public bool IsPartitionView => PartitionPanel.Visibility == Visibility.Visible;
    public string Status { get => status; private set { status = value; OnChanged(); } }
    public string VolumeLabel { get => volumeLabel; set { volumeLabel = value; OnChanged(); } }
    public double TransferProgress { get => transferProgress; private set { transferProgress = value; OnChanged(); } }
    public Visibility TransferVisibility { get => transferVisibility; private set { transferVisibility = value; OnChanged(); } }
    public bool CanCancelTransfer { get => canCancelTransfer; private set { canCancelTransfer = value; OnChanged(); } }
    public bool SoundsEnabled
    {
        get => UiSound.Enabled;
        set
        {
            if (UiSound.Enabled == value) return;
            UiSound.Enabled = value;
            OnChanged();
            Status = value ? "Interface sounds enabled." : "Interface sounds disabled.";
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            Status = "Requesting access to scan physical Xbox drives…";
            driveBroker = await ElevatedDriveBroker.StartAsync();
            await RefreshAsync();
        }
        catch (Exception exception) { Status = $"Physical-drive access was not started: {exception.GetBaseException().Message}"; }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (TransferVisibility != Visibility.Visible) return;
        e.Cancel = true;
        if (CanCancelTransfer && transferCancellation is not null)
        {
            CanCancelTransfer = false;
            Status = "Cancelling backup before FATX Bridge closes…";
            transferCancellation.Cancel();
            return;
        }
        MessageBox.Show(this, "A raw-device restore and verification is in progress. FATX Bridge cannot close safely until it finishes.",
            "Restore in progress", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        DriveScanItem[] openImages = Drives.Where(item => item.IsImageFile).ToArray();
        IsBusy = true;
        ResetToDriveView();
        Status = $"Scanning PhysicalDrive0–PhysicalDrive31 read-only… Log: {DriveScanner.LogPath}";
        try
        {
            if (driveBroker is null) throw new IOException("The physical-drive helper is not running.");
            DriveScanResult result = await driveBroker.ScanAsync();
            foreach (DriveScanItem item in result.Drives) Drives.Add(item);
            foreach (DriveScanItem image in openImages) Drives.Add(image);
            SelectedDrive = Drives.FirstOrDefault();
            int archivedSecuritySectors = 0;
            foreach (DriveScanItem item in result.Drives.Where(item => item.StorageKind == FatxStorageKind.Xbox360HardDrive))
                if (await TryArchiveSecuritySectorAsync(item)) archivedSecuritySectors++;
            string archiveSuffix = archivedSecuritySectors == 0 ? string.Empty : $" Archived {archivedSecuritySectors} validated Xbox 360 security-sector backup(s).";
            Status = Drives.Count > 0 ? $"Found {Drives.Count} device(s) and image(s) with validated FATX partitions.{archiveSuffix}" :
                result.Diagnostics.Count > 0 ? string.Join(" ", result.Diagnostics.Take(3)) :
                $"No Xbox FATX drives were detected. Scan log: {DriveScanner.LogPath}";
        }
        catch (Exception exception)
        {
            foreach (DriveScanItem image in openImages) Drives.Add(image);
            SelectedDrive = Drives.FirstOrDefault();
            Status = $"Drive scan failed safely: {exception.Message}";
        }
        finally { IsBusy = false; OnChanged(nameof(IsNotBusy)); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (driveBroker is null) await InitializeAsync(); else await RefreshAsync();
    }

    private async void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        if (!IsNotBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Open a FATX disk or partition image",
            Filter = "FATX images (*.img;*.bin;*.fatx)|*.img;*.bin;*.fatx|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        IsBusy = true;
        Status = $"Validating image {dialog.FileName} read-only…";
        try
        {
            string imagePath = Path.GetFullPath(dialog.FileName);
            FatxStorageDetection detection;
            long length;
            await using (var image = new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                length = image.Length;
                detection = await Task.Run(() => FatxPartitionProbe.DetectImage(image, length))
                    ?? throw new FatxFormatException("The selected file does not contain a validated supported FATX layout.");
            }

            bool writable = CanOpenImageForWrite(imagePath);
            string kind = detection.Kind switch
            {
                FatxStorageKind.Xbox360HardDrive => "Xbox 360 disk",
                FatxStorageKind.OriginalXboxHardDrive => "Original Xbox HDD",
                FatxStorageKind.OriginalXboxMemoryUnit => "Original Xbox memory unit",
                FatxStorageKind.StandaloneFatxImage => "Standalone FATX volume",
                _ => "FATX",
            };
            DriveScanItem? old = Drives.FirstOrDefault(item => item.IsImageFile &&
                string.Equals(Path.GetFullPath(item.Path), imagePath, StringComparison.OrdinalIgnoreCase));
            if (old is not null) Drives.Remove(old);
            var item = new DriveScanItem($"{kind} image — {Path.GetFileName(imagePath)}", imagePath,
                length, detection.Partitions, DriveSourceKind.ImageFile, writable, StorageKind: detection.Kind);
            Drives.Add(item);
            SelectedDrive = item;
            bool archived = await TryArchiveSecuritySectorAsync(item);
            Status = $"Opened {Path.GetFileName(imagePath)} with {item.Partitions.Count} validated FATX partition(s)" +
                (writable ? "." : " (the image file is read-only).") +
                (archived ? " A validated Xbox 360 security-sector backup was archived automatically." : string.Empty);
            IsBusy = false;
            ShowSelectedDrive();
        }
        catch (Exception exception)
        {
            Status = $"Image open failed safely: {exception.GetBaseException().Message}";
        }
        finally { IsBusy = false; OnChanged(nameof(IsNotBusy)); }
    }

    private void OpenDataContainer_Click(object sender, RoutedEventArgs e)
    {
        if (!IsNotBusy) return;
        var dialog = new OpenFileDialog
        {
            Title = "Open an Xbox 360 legacy USB Data0000 container",
            Filter = "Xbox 360 Data0000|Data0000|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        IsBusy = true;
        Status = $"Validating {dialog.FileName} as a segmented Xbox 360 USB container…";
        try
        {
            using Xbox360UsbContainerSource source = Xbox360UsbContainerSource.Open(dialog.FileName);
            if (source.Partitions.Count == 0)
                throw new FatxFormatException("The Data0000 sequence did not contain a validated supported FATX partition.");
            string selectedPath = Path.GetFullPath(dialog.FileName);
            DriveScanItem? old = Drives.FirstOrDefault(item => item.IsUsbContainer &&
                string.Equals(Path.GetFullPath(item.Path), selectedPath, StringComparison.OrdinalIgnoreCase));
            if (old is not null) Drives.Remove(old);
            var item = new DriveScanItem(
                $"Xbox 360 USB container — {new DirectoryInfo(source.FolderPath).Name}",
                selectedPath,
                source.Length,
                source.Partitions,
                DriveSourceKind.Xbox360UsbContainer,
                SourceSupportsWrite: false,
                StorageKind: FatxStorageKind.Xbox360HardDrive);
            Drives.Add(item);
            SelectedDrive = item;
            Status = $"Opened {source.SegmentPaths.Count} contiguous Data files with {source.Partitions.Count} validated read-only FATX partition(s). " +
                $"Configuration: {source.Configuration.ValidationState}; declared capacity: {source.Configuration.DeviceCapacityBytes?.ToString("N0", CultureInfo.CurrentCulture) ?? "unknown"} bytes.";
            IsBusy = false;
            ShowSelectedDrive();
        }
        catch (Exception exception)
        {
            Status = $"Data0000 open failed safely: {exception.GetBaseException().Message}";
        }
        finally { IsBusy = false; OnChanged(nameof(IsNotBusy)); }
    }

    private async void CreateFatxImage_Click(object sender, RoutedEventArgs e)
    {
        if (!IsNotBusy) return;
        var optionsDialog = new FormatImageDialog { Owner = this };
        if (optionsDialog.ShowDialog() != true || optionsDialog.Request is not FormatImageRequest request) return;

        var destinationDialog = new SaveFileDialog
        {
            Title = "Create a new FATX image",
            Filter = "FATX image (*.fatx)|*.fatx|Disk image (*.img)|*.img|Binary image (*.bin)|*.bin",
            DefaultExt = ".fatx",
            AddExtension = true,
            FileName = "new-fatx-volume.fatx",
            OverwritePrompt = true,
        };
        if (destinationDialog.ShowDialog(this) != true) return;

        string finalPath = Path.GetFullPath(destinationDialog.FileName);
        string temporaryPath = finalPath + $".fatxbridge-format-{Guid.NewGuid():N}.tmp";
        BeginTransfer(cancellable: true);
        Status = $"Creating a new {FormatBytes(request.Length)} FATX image…";
        try
        {
            await using (var image = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess))
            {
                image.SetLength(request.Length);
                var options = new FatxFormatOptions(0, request.Length, request.ByteOrder, request.SectorSize,
                    request.SectorsPerCluster, request.Mode);
                var progress = new Progress<FatxFormatProgress>(value =>
                {
                    TransferProgress = value.Fraction * 100;
                    Status = $"Formatting image: {value.Stage} ({value.Fraction:P1})";
                });
                _ = await Task.Run(() => FatxFormatter.Format(image, options, progress, transferCancellation!.Token));
            }
            File.Move(temporaryPath, finalPath, overwrite: true);

            await using var reopened = new FileStream(finalPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 1024 * 1024, FileOptions.RandomAccess);
            FatxStorageDetection detection = FatxPartitionProbe.DetectImage(reopened, reopened.Length)
                ?? throw new FatxFormatException("The newly formatted image did not pass the final FATX probe.");
            var item = new DriveScanItem($"Standalone FATX volume — {Path.GetFileName(finalPath)}", finalPath,
                reopened.Length, detection.Partitions, DriveSourceKind.ImageFile, SourceSupportsWrite: true,
                StorageKind: detection.Kind);
            Drives.Add(item);
            SelectedDrive = item;
            Status = $"Created and validated {finalPath}.";
        }
        catch (OperationCanceledException)
        {
            Status = "Image creation cancelled safely; the incomplete temporary image was removed.";
        }
        catch (Exception exception)
        {
            Status = $"Image creation failed safely: {exception.GetBaseException().Message}";
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            EndTransfer();
        }
    }

    private async void SupportPackage_Click(object sender, RoutedEventArgs e)
    {
        if (!IsNotBusy) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export a redacted FATX Bridge support package",
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = $"FATXBridge-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        IsBusy = true;
        Status = "Collecting allowlisted logs and redacting private identifiers…";
        try
        {
            SupportPackageExportResult result = await SupportPackageExporter.ExportAsync(dialog.FileName);
            Status = $"Support package saved: {result.DestinationPath} — {result.FileCount} log(s), {result.TotalRedactions} redaction(s), {result.PackageSizeBytes:N0} bytes.";
        }
        catch (Exception exception)
        {
            Status = $"Support package export failed safely: {exception.GetBaseException().Message}";
        }
        finally { IsBusy = false; }
    }

    private void OpenDrive_Click(object sender, RoutedEventArgs e)
        => ShowSelectedDrive();

    private void StorageBlade_Click(object sender, RoutedEventArgs e) => NavigateToTab(BladeKind.Storage);

    private void BrowseBlade_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null)
        {
            Status = "Choose a validated source on the Storage tab before opening Browse.";
            ShowStorageBlade();
            return;
        }
        NavigateToTab(BladeKind.Browse);
    }

    private void ToolsBlade_Click(object sender, RoutedEventArgs e) => NavigateToTab(BladeKind.Tools);

    private void AboutBlade_Click(object sender, RoutedEventArgs e) => NavigateToTab(BladeKind.About);

    private void NavigateToTab(BladeKind target)
    {
        switch (target)
        {
            case BladeKind.Storage:
                ShowStorageBlade();
                break;
            case BladeKind.Browse when browseTabAvailable && SelectedDrive is not null:
                ShowSelectedDrive();
                break;
            case BladeKind.Tools:
                HideBladePanels();
                ToolsPanel.Visibility = Visibility.Visible;
                SetBladeHeader(BladeKind.Tools, "TOOLS", "Images, legacy USB containers, physical-drive imaging, and diagnostics—explained before use.", browseTabAvailable ? "3 / 4" : "2 / 3");
                NotifyBladeState();
                break;
            case BladeKind.About:
                HideBladePanels();
                AboutPanel.Visibility = Visibility.Visible;
                SetBladeHeader(BladeKind.About, "ABOUT", "What FATX Bridge does, what stays local, and which operations are intentionally unavailable.", browseTabAvailable ? "4 / 4" : "3 / 3");
                NotifyBladeState();
                break;
        }
    }

    private void ShowSelectedDrive()
    {
        if (SelectedDrive is null) return;
        RevealBrowseTab();
        if (!ReferenceEquals(browsedDrive, SelectedDrive) || Partitions.Count == 0 || SelectedPartition is null)
        {
            Partitions.Clear(); RootEntries.Clear();
            foreach (FatxPartitionCandidate candidate in SelectedDrive.Partitions) Partitions.Add(new PartitionItem(candidate));
            SelectedPartition = Partitions.FirstOrDefault();
            browsedDrive = SelectedDrive;
        }
        HideBladePanels();
        PartitionPanel.Visibility = Visibility.Visible;
        SetBladeHeader(BladeKind.Browse, "BROWSE", $"Explore validated partitions on {SelectedDrive.DisplayName}.", "2 / 4");
        NotifyBladeState();
    }

    private async void Partition_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedPartition is not null && SelectedDrive is not null) await LoadRootAsync(SelectedPartition);
    }

    private async Task LoadRootAsync(PartitionItem partition)
    {
        if (SelectedDrive is null) return;
        long generation = Interlocked.Increment(ref rootLoadGeneration);
        IsBusy = true; RootEntries.Clear(); Status = $"Reading {partition.Name} root directory read-only…";
        long capacity = SelectedDrive.Capacity;
        DriveScanItem drive = SelectedDrive;
        try
        {
            IReadOnlyList<FatxDirectoryEntry> entries = await Task.Run(async () =>
            {
                await using Stream source = await OpenSourceAsync(drive, readOnly: true);
                return FatxVolume.Open(source, partition.Candidate.Offset, partition.Candidate.Length, capacity,
                    partition.Candidate.Metadata.SectorSize).ListRootDirectory();
            });
            if (generation != Interlocked.Read(ref rootLoadGeneration) || !IsPartitionView || !ReferenceEquals(partition, SelectedPartition))
                return;
            foreach (FatxDirectoryEntry entry in entries) RootEntries.Add(new DirectoryItem(entry));
            Status = $"{partition.Name}: {entries.Count} root entries.";
        }
        catch (Exception exception)
        {
            if (generation == Interlocked.Read(ref rootLoadGeneration)) Status = $"Could not read this partition: {exception.Message}";
        }
        finally { if (generation == Interlocked.Read(ref rootLoadGeneration)) { IsBusy = false; OnChanged(nameof(IsNotBusy)); } }
    }

    private async void SectorViewer_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUsePartitionTools || SelectedDrive is null || SelectedPartition is null) return;
        DriveScanItem drive = SelectedDrive;
        FatxPartitionCandidate partition = SelectedPartition.Candidate;
        IsBusy = true;
        Status = $"Opening the read-only sector viewer for {partition.Name}…";
        try
        {
            await using Stream source = await OpenSourceAsync(drive, readOnly: true);
            var inspector = new RawStorageInspector(source, drive.Capacity, partition.Metadata.SectorSize);
            var window = new SectorViewerWindow(inspector, partition.Metadata) { Owner = this };
            window.ShowDialog();
            Status = "Sector viewer closed. The source was not modified.";
        }
        catch (Exception exception)
        {
            Status = $"Sector viewer could not be opened safely: {exception.GetBaseException().Message}";
        }
        finally { IsBusy = false; }
    }

    private async void RecoverDeleted_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUsePartitionTools || SelectedDrive is null || SelectedPartition is null) return;
        DriveScanItem drive = SelectedDrive;
        FatxPartitionCandidate partition = SelectedPartition.Candidate;
        IsBusy = true;
        Status = $"Scanning {partition.Name} for recoverable deleted directory slots read-only…";
        try
        {
            await using Stream source = await OpenSourceAsync(drive, readOnly: true);
            FatxVolume volume = FatxVolume.Open(source, partition.Offset, partition.Length, drive.Capacity,
                partition.Metadata.SectorSize);
            FatxDeletedEntryCandidate[] candidates = await Task.Run(() => EnumerateDeletedCandidates(volume));
            if (candidates.Length == 0)
            {
                Status = "No deleted FATX directory slots were found in the bounded scan.";
                MessageBox.Show(this, Status, "Deleted-file recovery", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var recoveryDialog = new DeletedRecoveryDialog(candidates) { Owner = this };
            if (recoveryDialog.ShowDialog() != true || recoveryDialog.SelectedCandidate is not FatxDeletedEntryCandidate candidate)
            {
                Status = $"Found {candidates.Length:N0} deleted candidate(s); nothing was exported.";
                return;
            }

            var destinationDialog = new SaveFileDialog
            {
                Title = "Export recovered bytes to a normal Windows file",
                FileName = SanitizeWindowsFileName(candidate.DisplayName),
                Filter = "All files (*.*)|*.*",
                OverwritePrompt = true,
            };
            if (destinationDialog.ShowDialog(this) != true)
            {
                Status = "Recovery export cancelled; the FATX source was not modified.";
                return;
            }

            string finalPath = Path.GetFullPath(destinationDialog.FileName);
            string temporaryPath = finalPath + $".fatxbridge-recovery-{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    long recovered = await Task.Run(() => volume.ExportDeletedFile(candidate, destination));
                    await destination.FlushAsync();
                    destination.Flush(flushToDisk: true);
                    Status = $"Recovered and validated {recovered:N0} candidate bytes; publishing export…";
                }
                File.Move(temporaryPath, finalPath, overwrite: true);
                Status = $"Recovered candidate bytes to {finalPath}. The FATX source was not modified.";
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            }
        }
        catch (Exception exception)
        {
            Status = $"Deleted-file recovery stopped safely: {exception.GetBaseException().Message}";
        }
        finally { IsBusy = false; }
    }

    private async void ContentMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (!CanUsePartitionTools || SelectedDrive is null || SelectedPartition is null) return;
        DriveScanItem drive = SelectedDrive;
        FatxPartitionCandidate partition = SelectedPartition.Candidate;
        IsBusy = true;
        Status = $"Scanning {partition.Name} for bounded CON/LIVE/PIRS metadata…";
        try
        {
            await using Stream source = await OpenSourceAsync(drive, readOnly: true);
            FatxVolume volume = FatxVolume.Open(source, partition.Offset, partition.Length, drive.Capacity,
                partition.Metadata.SectorSize);
            ContentMetadataItem[] packages = await Task.Run(() => EnumerateContentMetadata(volume));
            if (packages.Length == 0)
            {
                Status = "No recognized CON, LIVE, or PIRS package metadata was found in the bounded scan.";
                MessageBox.Show(this, Status, "Xbox 360 content metadata", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            new ContentMetadataDialog(packages) { Owner = this }.ShowDialog();
            Status = $"Found {packages.Length:N0} recognized package(s). No signatures or hash tables were cryptographically verified.";
        }
        catch (Exception exception)
        {
            Status = $"Content metadata scan stopped safely: {exception.GetBaseException().Message}";
        }
        finally { IsBusy = false; }
    }

    private async void MountReadOnly_Click(object sender, RoutedEventArgs e) => await MountAsync(readOnly: true);

    private async void MountReadWrite_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null || SelectedPartition is null || !SelectedDrive.SourceSupportsWrite ||
            !SelectedPartition.Candidate.SupportsWrite) return;
        MessageBoxResult confirmation = MessageBox.Show(this,
            $"Experimental write access will mount {SelectedDrive.Path}, partition {SelectedPartition.Name}, as a Windows drive. " +
            "Corruption or data loss is possible, including if power is lost. Continue only if you have a backup.",
            "Confirm experimental FATX write mount", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirmation == MessageBoxResult.Yes) await MountAsync(readOnly: false);
    }

    private async Task MountAsync(bool readOnly)
    {
        if (!CanMount || SelectedDrive is null || SelectedPartition is null) return;
        IsBusy = true;
        try
        {
            DriveScanItem drive = SelectedDrive; FatxPartitionCandidate partition = SelectedPartition.Candidate;
            string label = VolumeLabelStore.Normalize(VolumeLabel);
            volumeLabels.Set(drive, partition, label);
            Stream opened = await OpenSourceAsync(drive, readOnly);
            if (opened is not FileStream stream)
            {
                await opened.DisposeAsync();
                throw new NotSupportedException("Segmented Data0000 containers are available in the built-in read-only browser but are not mounted through WinFsp yet.");
            }
            mount = await Task.Run(() => FatxWinFspMount.MountOpened(stream, drive.Capacity, partition,
                readOnly, useDriveLetter: true, volumeLabel: label));
            Status = $"Mounted {partition.Name} as “{label}” at {mount.MountPath} ({(readOnly ? "read-only" : "experimental read/write")}).";
            OnChanged(nameof(IsMounted)); OnChanged(nameof(CanMount)); OnChanged(nameof(CanMountReadWrite));
            OnChanged(nameof(IsNotBusy)); OnChanged(nameof(CanEditVolumeLabel)); OnChanged(nameof(CanUsePartitionTools));
            OpenExplorer();
        }
        catch (Exception exception) { Status = $"Mount failed: {exception.Message}"; }
        finally { IsBusy = false; OnChanged(nameof(IsNotBusy)); }
    }

    private void OpenExplorer_Click(object sender, RoutedEventArgs e) => OpenExplorer();
    private void OpenExplorer()
    {
        if (mount is null) return;
        try
        {
            mount.OpenExplorer();
        }
        catch (Exception exception) { Status = $"Mounted at {mount.MountPath} but Explorer could not be opened: {exception.Message}"; }
    }
    private void Unmount_Click(object sender, RoutedEventArgs e) => Unmount();
    private void Unmount()
    {
        if (mount is null) return;
        string mountPath = mount.MountPath;
        try { mount.Dispose(); Status = $"Unmounted {mountPath}"; }
        catch (Exception exception) { Status = $"Unmount of {mountPath} reported: {exception.Message}"; }
        finally { mount = null; OnChanged(nameof(IsMounted)); OnChanged(nameof(CanMount)); OnChanged(nameof(CanMountReadWrite)); OnChanged(nameof(IsNotBusy)); OnChanged(nameof(CanEditVolumeLabel)); OnChanged(nameof(CanUsePartitionTools)); }
    }

    private async void BackupDrive_Click(object sender, RoutedEventArgs e)
    {
        if (!CanBackup || SelectedDrive is null) return;
        DriveScanItem drive = SelectedDrive;
        var dialog = new SaveFileDialog
        {
            Title = $"Back up {drive.Path}",
            Filter = "Raw disk image (*.img)|*.img|Binary image (*.bin)|*.bin|All files (*.*)|*.*",
            FileName = $"FATXBridge-{SourceLeafName(drive.Path)}-{DateTime.Now:yyyyMMdd-HHmmss}.img",
            AddExtension = true,
            DefaultExt = ".img",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        string finalPath = Path.GetFullPath(dialog.FileName);
        string temporaryPath = finalPath + $".fatxbridge-partial-{Guid.NewGuid():N}";
        BeginTransfer(cancellable: true);
        Status = $"Starting verified backup of {drive.Path}…";
        try
        {
            await using Stream source = await OpenSourceAsync(drive, readOnly: true, transferCancellation!.Token);
            await using var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            destination.SetLength(drive.Capacity);
            IProgress<RawImageProgress> progress = CreateTransferProgress("Backing up", "Verifying backup");
            RawImageResult result = await RawImageOperations.CopyAndVerifyAsync(source, destination, drive.Capacity,
                progress, transferCancellation.Token);
            destination.Close();
            File.Move(temporaryPath, finalPath, overwrite: true);
            string manifest = $"{result.Sha256} *{Path.GetFileName(finalPath)}{Environment.NewLine}";
            try
            {
                await File.WriteAllTextAsync(finalPath + ".sha256.txt", manifest);
                Status = $"Backup complete and verified: {finalPath} — SHA-256 {result.Sha256}";
            }
            catch (Exception manifestException)
            {
                Status = $"Backup complete and verified: {finalPath} — SHA-256 {result.Sha256}. " +
                    $"The optional checksum sidecar could not be saved: {manifestException.GetBaseException().Message}";
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Backup cancelled safely; the incomplete temporary image was removed.";
        }
        catch (Exception exception)
        {
            Status = $"Backup failed safely: {exception.GetBaseException().Message}";
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            EndTransfer();
        }
    }

    private async void RestoreImage_Click(object sender, RoutedEventArgs e)
    {
        if (!CanRestore || SelectedDrive is null) return;
        DriveScanItem drive = SelectedDrive;
        var dialog = new OpenFileDialog
        {
            Title = $"Choose a complete raw image to restore to {drive.Path}",
            Filter = "Raw disk images (*.img;*.bin;*.fatx)|*.img;*.bin;*.fatx|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        string imagePath;
        long imageLength;
        try
        {
            imagePath = Path.GetFullPath(dialog.FileName);
            imageLength = new FileInfo(imagePath).Length;
        }
        catch (Exception exception)
        {
            Status = $"Restore image could not be opened: {exception.GetBaseException().Message}";
            return;
        }
        if (imageLength != drive.Capacity)
        {
            Status = $"Restore refused: image size {imageLength:N0} bytes does not exactly match {drive.Path} capacity {drive.Capacity:N0} bytes.";
            MessageBox.Show(this, Status, "Image capacity mismatch", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var confirmation = new RestoreConfirmationWindow(imagePath, drive.Path, drive.Capacity) { Owner = this };
        if (confirmation.ShowDialog() != true) return;

        bool restored = false;
        string? verifiedHash = null;
        BeginTransfer(cancellable: false);
        Status = $"Restoring {imagePath} to {drive.Path}. Do not disconnect either device…";
        try
        {
            await using var source = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using Stream destination = await OpenSourceAsync(drive, readOnly: false);
            IProgress<RawImageProgress> progress = CreateTransferProgress("Restoring", "Verifying restored device");
            RawImageResult result = await RawImageOperations.CopyAndVerifyAsync(source, destination, drive.Capacity,
                progress, CancellationToken.None);
            verifiedHash = result.Sha256;
            restored = true;
        }
        catch (Exception exception)
        {
            Status = $"Restore failed. The target may now contain an incomplete image and should not be used until restored again: {exception.GetBaseException().Message}";
            MessageBox.Show(this, Status, "Restore incomplete", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { EndTransfer(); }

        if (restored)
        {
            await RefreshAsync();
            Status = $"Restore completed and the entire target verified successfully. SHA-256 {verifiedHash}";
        }
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCancelTransfer || transferCancellation is null) return;
        CanCancelTransfer = false;
        Status = "Cancelling backup after the current block…";
        transferCancellation.Cancel();
    }

    private void BeginTransfer(bool cancellable)
    {
        transferCancellation?.Dispose();
        transferCancellation = cancellable ? new CancellationTokenSource() : null;
        IsBusy = true;
        TransferProgress = 0;
        TransferVisibility = Visibility.Visible;
        CanCancelTransfer = cancellable;
    }

    private void EndTransfer()
    {
        transferCancellation?.Dispose();
        transferCancellation = null;
        CanCancelTransfer = false;
        TransferVisibility = Visibility.Collapsed;
        TransferProgress = 0;
        IsBusy = false;
    }

    private Progress<RawImageProgress> CreateTransferProgress(string copyText, string verifyText) =>
        new Progress<RawImageProgress>(progress =>
        {
            TransferProgress = progress.Fraction * 100;
            string action = progress.Stage == RawImageStage.Copying ? copyText : verifyText;
            Status = $"{action}: {FormatBytes(progress.BytesProcessed)} / {FormatBytes(progress.TotalBytes)} ({progress.Fraction:P1})";
        });

    private async Task<Stream> OpenSourceAsync(DriveScanItem drive, bool readOnly,
        CancellationToken cancellationToken = default)
    {
        if (drive.IsUsbContainer)
        {
            if (!readOnly) throw new NotSupportedException("Xbox 360 Data0000 containers are read-only in this release.");
            IReadOnlyList<string> paths = Xbox360UsbContainerSource.DiscoverSegmentPaths(drive.Path);
            var segments = new List<Stream>(paths.Count);
            try
            {
                foreach (string path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    segments.Add(new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess));
                }
                return new SegmentedReadOnlyStream(segments, leaveOpen: false);
            }
            catch
            {
                foreach (Stream segment in segments) segment.Dispose();
                throw;
            }
        }
        if (drive.IsImageFile)
        {
            return new FileStream(drive.Path, FileMode.Open, readOnly ? FileAccess.Read : FileAccess.ReadWrite,
                readOnly ? FileShare.ReadWrite : FileShare.Read, 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
        }
        if (driveBroker is null) throw new IOException("The physical-drive helper is not running.");
        return await driveBroker.OpenAsync(drive.Path, readOnly, cancellationToken);
    }

    private static bool CanOpenImageForWrite(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return stream.CanWrite;
        }
        catch { return false; }
    }

    private async Task<bool> TryArchiveSecuritySectorAsync(DriveScanItem drive)
    {
        if (drive.StorageKind != FatxStorageKind.Xbox360HardDrive || drive.IsUsbContainer) return false;
        try
        {
            await using Stream source = await OpenSourceAsync(drive, readOnly: true);
            Xbox360SecuritySectorResult inspection = await Task.Run(() => Xbox360SecuritySector.Detect(source, drive.Capacity));
            return new SecuritySectorArchive().Archive(inspection) is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Securityless BadStorage disks and unreadable legacy HDDSS regions are supported.
            // A best-effort archive failure must never hide an otherwise valid FATX drive.
            return false;
        }
    }

    private static FatxDeletedEntryCandidate[] EnumerateDeletedCandidates(FatxVolume volume)
    {
        const int maximumDirectories = 10_000;
        const int maximumCandidates = 50_000;
        var candidates = new List<FatxDeletedEntryCandidate>();
        var pending = new Stack<string>();
        pending.Push("/");
        int directories = 0;
        while (pending.Count > 0)
        {
            string path = pending.Pop();
            if (++directories > maximumDirectories)
                throw new IOException($"Deleted-file scan stopped at the {maximumDirectories:N0}-directory safety limit.");
            candidates.AddRange(volume.EnumerateDeletedEntries(path));
            if (candidates.Count > maximumCandidates)
                throw new IOException($"Deleted-file scan stopped at the {maximumCandidates:N0}-candidate safety limit.");
            foreach (FatxDirectoryEntry entry in volume.EnumerateDirectory(path).Where(entry => entry.IsDirectory))
                pending.Push(CombineFatxPath(path, entry.Name));
        }
        return candidates.ToArray();
    }

    private static ContentMetadataItem[] EnumerateContentMetadata(FatxVolume volume)
    {
        const int maximumEntries = 100_000;
        const int maximumPackages = 20_000;
        var packages = new List<ContentMetadataItem>();
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push(("/", 0));
        int visited = 0;
        while (pending.Count > 0)
        {
            (string path, int depth) = pending.Pop();
            foreach (FatxDirectoryEntry entry in volume.EnumerateDirectory(path))
            {
                if (++visited > maximumEntries)
                    throw new IOException($"Content scan stopped at the {maximumEntries:N0}-entry safety limit.");
                string childPath = CombineFatxPath(path, entry.Name);
                if (entry.IsDirectory)
                {
                    if (depth < 32) pending.Push((childPath, depth + 1));
                    continue;
                }
                if (entry.FileSize < StfsMetadata.MinimumMetadataBytes) continue;

                try
                {
                    int count = checked((int)Math.Min((long)entry.FileSize, StfsMetadata.MaximumHeaderRead));
                    var header = new byte[count];
                    int read = volume.ReadFile(entry, 0, header);
                    if (read != header.Length) Array.Resize(ref header, read);
                    StfsMetadataResult result = StfsMetadata.Parse(header);
                    if (result.IsPresent && result.Metadata is not null)
                    {
                        packages.Add(new ContentMetadataItem(childPath, result.Metadata));
                        if (packages.Count >= maximumPackages)
                            throw new IOException($"Content scan stopped at the {maximumPackages:N0}-package safety limit.");
                    }
                }
                catch (FatxFormatException)
                {
                    // A corrupt individual file cannot escape its validated FATX bounds and
                    // does not prevent metadata inspection of unrelated files.
                }
            }
        }
        return packages.ToArray();
    }

    private static string CombineFatxPath(string parent, string name) => parent == "/" ? $"/{name}" : $"{parent}/{name}";

    private static string SanitizeWindowsFileName(string name)
    {
        string value = string.IsNullOrWhiteSpace(name) ? "recovered-file.bin" : name;
        foreach (char invalid in Path.GetInvalidFileNameChars()) value = value.Replace(invalid, '_');
        return value.Length == 0 ? "recovered-file.bin" : value;
    }

    private void RefreshVolumeLabel()
    {
        if (SelectedDrive is null || SelectedPartition is null) return;
        VolumeLabel = volumeLabels.Get(SelectedDrive, SelectedPartition.Candidate);
    }

    private static string FormatBytes(long bytes) => bytes >= 1024L * 1024 * 1024
        ? $"{bytes / 1024d / 1024d / 1024d:0.00} GiB"
        : $"{bytes / 1024d / 1024d:0.00} MiB";

    private static string SourceLeafName(string path)
    {
        string leaf = Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(leaf) ? path.TrimEnd('\\').Split('\\').Last() : leaf;
    }

    private void ResetToDriveView()
    {
        Interlocked.Increment(ref rootLoadGeneration);
        HideBrowseTab();
        ShowStorageBlade();
        Partitions.Clear(); RootEntries.Clear(); Drives.Clear();
        browsedDrive = null;
        SelectedPartition = null; SelectedDrive = null;
    }

    private void ShowStorageBlade()
    {
        HideBladePanels();
        DrivePanel.Visibility = Visibility.Visible;
        SetBladeHeader(BladeKind.Storage, "STORAGE", "Choose a detected Xbox drive, memory unit, or previously opened image.", browseTabAvailable ? "1 / 4" : "1 / 3");
        NotifyBladeState();
    }

    private void RevealBrowseTab()
    {
        if (browseTabAvailable) return;

        browseTabAvailable = true;
        browseTabAnimating = true;
        BrowseTabHost.IsHitTestVisible = true;
        BrowseTabHost.BeginAnimation(FrameworkElement.WidthProperty, null);
        BrowseTabLabel.BeginAnimation(OpacityProperty, null);
        BrowseTabHost.Width = 0;
        BrowseTabLabel.Opacity = 0;

        double targetWidth = Math.Max(150, BladeTabBar.ActualWidth / 4);
        var expansion = new DoubleAnimation(0, targetWidth, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        expansion.Completed += (_, _) =>
        {
            BrowseTabHost.BeginAnimation(FrameworkElement.WidthProperty, null);
            BrowseTabHost.Width = targetWidth;
            browseTabAnimating = false;
        };
        BrowseTabHost.BeginAnimation(FrameworkElement.WidthProperty, expansion);

        var labelFade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            BeginTime = TimeSpan.FromMilliseconds(150),
            FillBehavior = FillBehavior.Stop,
        };
        labelFade.Completed += (_, _) =>
        {
            BrowseTabLabel.BeginAnimation(OpacityProperty, null);
            BrowseTabLabel.Opacity = 1;
        };
        BrowseTabLabel.BeginAnimation(OpacityProperty, labelFade);
    }

    private void HideBrowseTab()
    {
        browseTabAvailable = false;
        browseTabAnimating = false;
        BrowseTabHost.BeginAnimation(FrameworkElement.WidthProperty, null);
        BrowseTabLabel.BeginAnimation(OpacityProperty, null);
        BrowseTabHost.Width = 0;
        BrowseTabLabel.Opacity = 0;
        BrowseTabHost.IsHitTestVisible = false;
    }

    private void BladeTabBar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!browseTabAvailable || browseTabAnimating) return;
        double targetWidth = Math.Max(150, BladeTabBar.ActualWidth / 4);
        if (Math.Abs(BrowseTabHost.Width - targetWidth) > 0.5) BrowseTabHost.Width = targetWidth;
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right) || Keyboard.FocusedElement is TextBox or ComboBox) return;

        BladeKind[] tabs = browseTabAvailable
            ? [BladeKind.Storage, BladeKind.Browse, BladeKind.Tools, BladeKind.About]
            : [BladeKind.Storage, BladeKind.Tools, BladeKind.About];
        int currentIndex = Array.IndexOf(tabs, currentBlade);
        if (currentIndex < 0) currentIndex = 0;
        int direction = e.Key == Key.Right ? 1 : -1;
        int nextIndex = (currentIndex + direction + tabs.Length) % tabs.Length;
        NavigateToTab(tabs[nextIndex]);
        e.Handled = true;
    }

    private static void Button_ClickSound(object sender, RoutedEventArgs e)
    {
        if (e.Source is not Button button) return;
        string? tag = button.Tag as string;
        if (string.Equals(tag, "NavigationTab", StringComparison.Ordinal)) return;
        UiSound.Activate();
    }

    private void HideBladePanels()
    {
        DrivePanel.Visibility = Visibility.Collapsed;
        PartitionPanel.Visibility = Visibility.Collapsed;
        ToolsPanel.Visibility = Visibility.Collapsed;
        AboutPanel.Visibility = Visibility.Collapsed;
    }

    private void SetBladeHeader(BladeKind blade, string heading, string intro, string position)
    {
        if (currentBlade != blade) UiSound.Navigate();
        currentBlade = blade;
        BladeHeading.Text = heading;
        BladeIntro.Text = intro;
        BladePosition.Text = position;
        StorageBladeTab.Opacity = blade == BladeKind.Storage ? 1 : 0.72;
        BrowseBladeTab.Opacity = blade == BladeKind.Browse ? 1 : 0.72;
        ToolsBladeTab.Opacity = blade == BladeKind.Tools ? 1 : 0.72;
        AboutBladeTab.Opacity = blade == BladeKind.About ? 1 : 0.72;
    }

    private void NotifyBladeState()
    {
        OnChanged(nameof(IsPartitionView));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record PartitionItem(FatxPartitionCandidate Candidate)
{
    public string Name => Candidate.Name;
    public string Details => $"0x{Candidate.Offset:X} • {Candidate.Metadata.ByteOrder} • {Candidate.Metadata.SectorSize}-byte sectors • {Candidate.Metadata.AllocationTable} • cluster {Candidate.Metadata.RootFirstCluster}";
}
public sealed record DirectoryItem(FatxDirectoryEntry Entry)
{
    public string Name => Entry.Name;
    public string Type => Entry.IsDirectory ? "Directory" : "File";
    public string Size => Entry.IsDirectory ? "—" : $"{Entry.FileSize:N0} bytes";
}
