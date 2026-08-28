using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using FatxBridge.Core;

namespace FatxBridge.Windows;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private DriveScanItem? selectedDrive;
    private PartitionItem? selectedPartition;
    private bool isBusy;
    private long rootLoadGeneration;
    private string status = "Scanning local physical drives read-only…";
    private FatxWinFspMount? mount;
    private ElevatedDriveBroker? driveBroker;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += async (_, _) => await InitializeAsync();
        Closed += (_, _) => { Unmount(); driveBroker?.Dispose(); };
    }

    public ObservableCollection<DriveScanItem> Drives { get; } = [];
    public ObservableCollection<PartitionItem> Partitions { get; } = [];
    public ObservableCollection<DirectoryItem> RootEntries { get; } = [];
    public DriveScanItem? SelectedDrive { get => selectedDrive; set { selectedDrive = value; OnChanged(); OnChanged(nameof(CanOpen)); } }
    public PartitionItem? SelectedPartition { get => selectedPartition; set { selectedPartition = value; OnChanged(); OnChanged(nameof(CanMount)); OnChanged(nameof(CanMountReadWrite)); } }
    public bool IsBusy { get => isBusy; private set { isBusy = value; OnChanged(); OnChanged(nameof(IsNotBusy)); OnChanged(nameof(CanOpen)); OnChanged(nameof(CanNavigateBack)); OnChanged(nameof(CanMount)); OnChanged(nameof(CanMountReadWrite)); } }
    public bool IsNotBusy => !IsBusy && mount is null;
    public bool CanOpen => SelectedDrive is not null && !IsBusy;
    public bool CanMount => SelectedDrive is not null && SelectedPartition is not null && mount is null && !IsBusy;
    public bool CanMountReadWrite => CanMount && string.Equals(SelectedPartition!.Name, "Content", StringComparison.Ordinal);
    public bool IsMounted => mount is not null;
    public bool IsPartitionView => PartitionPanel.Visibility == Visibility.Visible;
    public bool CanNavigateBack => IsPartitionView && !IsBusy && mount is null;
    public string Status { get => status; private set { status = value; OnChanged(); } }

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

    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ResetToDriveView();
        Status = $"Scanning PhysicalDrive0–PhysicalDrive31 read-only… Log: {DriveScanner.LogPath}";
        try
        {
            if (driveBroker is null) throw new IOException("The physical-drive helper is not running.");
            DriveScanResult result = await driveBroker.ScanAsync();
            foreach (DriveScanItem item in result.Drives) Drives.Add(item);
            SelectedDrive = Drives.FirstOrDefault();
            Status = Drives.Count > 0 ? $"Found {Drives.Count} drive(s) with validated FATX partitions." :
                result.Diagnostics.Count > 0 ? string.Join(" ", result.Diagnostics.Take(3)) :
                $"No Xbox FATX drives were detected. Scan log: {DriveScanner.LogPath}";
        }
        catch (Exception exception)
        {
            Status = $"Drive scan failed safely: {exception.Message}";
        }
        finally { IsBusy = false; OnChanged(nameof(IsNotBusy)); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (driveBroker is null) await InitializeAsync(); else await RefreshAsync();
    }

    private void OpenDrive_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null || mount is not null) return;
        Partitions.Clear(); RootEntries.Clear();
        foreach (FatxPartitionCandidate candidate in SelectedDrive.Partitions) Partitions.Add(new PartitionItem(candidate));
        SelectedPartition = Partitions.FirstOrDefault();
        DrivePanel.Visibility = Visibility.Collapsed; PartitionPanel.Visibility = Visibility.Visible;
        OnChanged(nameof(IsPartitionView));
        OnChanged(nameof(CanNavigateBack));
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
        string path = SelectedDrive.Path;
        long capacity = SelectedDrive.Capacity;
        try
        {
            IReadOnlyList<FatxDirectoryEntry> entries = await Task.Run(() =>
            {
                if (driveBroker is null) throw new IOException("The physical-drive helper is not running.");
                using FileStream source = driveBroker.OpenAsync(path, readOnly: true).GetAwaiter().GetResult();
                return FatxVolume.Open(source, partition.Candidate.Offset, partition.Candidate.Length, capacity).ListRootDirectory();
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

    private async void MountReadOnly_Click(object sender, RoutedEventArgs e) => await MountAsync(readOnly: true);

    private async void MountReadWrite_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedDrive is null || SelectedPartition is null || !string.Equals(SelectedPartition.Name, "Content", StringComparison.Ordinal)) return;
        MessageBoxResult confirmation = MessageBox.Show(this,
            $"Experimental write access will mount {SelectedDrive.Path}, partition Content, as a Windows drive. " +
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
            if (driveBroker is null) throw new IOException("The physical-drive helper is not running.");
            FileStream stream = await driveBroker.OpenAsync(drive.Path, readOnly);
            mount = await Task.Run(() => FatxWinFspMount.MountOpened(stream, drive.Capacity, partition,
                readOnly, useDriveLetter: true));
            Status = $"Mounted {partition.Name} at {mount.MountPath} ({(readOnly ? "read-only" : "experimental read/write")}).";
            OnChanged(nameof(IsMounted)); OnChanged(nameof(CanMount)); OnChanged(nameof(CanMountReadWrite));
            OnChanged(nameof(IsNotBusy)); OnChanged(nameof(CanNavigateBack));
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
        finally { mount = null; OnChanged(nameof(IsMounted)); OnChanged(nameof(CanMount)); OnChanged(nameof(CanMountReadWrite)); OnChanged(nameof(IsNotBusy)); OnChanged(nameof(CanNavigateBack)); }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || mount is not null) return;
        PartitionPanel.Visibility = Visibility.Collapsed; DrivePanel.Visibility = Visibility.Visible; OnChanged(nameof(IsPartitionView));
        OnChanged(nameof(CanNavigateBack));
    }

    private void ResetToDriveView()
    {
        Interlocked.Increment(ref rootLoadGeneration);
        PartitionPanel.Visibility = Visibility.Collapsed;
        DrivePanel.Visibility = Visibility.Visible;
        Partitions.Clear(); RootEntries.Clear(); Drives.Clear();
        SelectedPartition = null; SelectedDrive = null;
        OnChanged(nameof(IsPartitionView));
        OnChanged(nameof(CanNavigateBack));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record PartitionItem(FatxPartitionCandidate Candidate)
{
    public string Name => Candidate.Name;
    public string Details => $"0x{Candidate.Offset:X} • {Candidate.Metadata.AllocationTable} • cluster {Candidate.Metadata.RootFirstCluster}";
}
public sealed record DirectoryItem(FatxDirectoryEntry Entry)
{
    public string Name => Entry.Name;
    public string Type => Entry.IsDirectory ? "Directory" : "File";
    public string Size => Entry.IsDirectory ? "—" : $"{Entry.FileSize:N0} bytes";
}
