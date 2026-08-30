using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FatxBridge.Core;

namespace FatxBridge.Windows;

public sealed record FormatImageRequest(
    long Length,
    FatxByteOrder ByteOrder,
    int SectorSize,
    uint SectorsPerCluster,
    FatxFormatMode Mode);

public sealed class FormatImageDialog : Window
{
    private readonly TextBox sizeText = new() { Text = "512", MinWidth = 150 };
    private readonly ComboBox byteOrder = new() { ItemsSource = new[] { "Original Xbox / little-endian", "Xbox 360 / big-endian" }, SelectedIndex = 0 };
    private readonly ComboBox sectorSize = new() { ItemsSource = new[] { 512, 4096 }, SelectedIndex = 0 };
    private readonly ComboBox clusterSectors = new() { ItemsSource = new uint[] { 2, 4, 8, 16, 32, 64, 128 }, SelectedIndex = 2 };
    private readonly ComboBox mode = new() { ItemsSource = new[] { FatxFormatMode.Quick, FatxFormatMode.Full }, SelectedIndex = 0 };

    public FormatImageDialog()
    {
        Title = "Create FATX image";
        Width = 520;
        Height = 430;
        MinWidth = 460;
        MinHeight = 390;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#0E1116");
        Foreground = Brush("#EEF4F0");

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "CREATE A NEW FATX IMAGE", FontSize = 20, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "Only a new image file is formatted. Physical disks are deliberately unavailable in this tool.",
            Foreground = Brush("#8FE3B0"), Margin = new Thickness(0, 8, 0, 18), TextWrapping = TextWrapping.Wrap
        });
        AddField(panel, "Image size (MiB)", sizeText);
        AddField(panel, "Console byte order", byteOrder);
        AddField(panel, "Logical sector size", sectorSize);
        AddField(panel, "Sectors per cluster", clusterSectors);
        AddField(panel, "Format mode", mode);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var create = new Button { Content = "Choose destination…", IsDefault = true, Padding = new Thickness(14, 7, 14, 7) };
        create.Click += Create_Click;
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }

    public FormatImageRequest? Request { get; private set; }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (!long.TryParse(sizeText.Text, NumberStyles.None, CultureInfo.InvariantCulture, out long mebibytes) ||
            mebibytes is < 16 or > 2_097_152)
        {
            MessageBox.Show(this, "Enter an image size from 16 MiB through 2 TiB.", "Invalid image size", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Request = new FormatImageRequest(
            checked(mebibytes * 1024 * 1024),
            byteOrder.SelectedIndex == 1 ? FatxByteOrder.BigEndian : FatxByteOrder.LittleEndian,
            (int)(sectorSize.SelectedItem ?? 512),
            (uint)(clusterSectors.SelectedItem ?? 8U),
            (FatxFormatMode)(mode.SelectedItem ?? FatxFormatMode.Quick));
        DialogResult = true;
    }

    private static void AddField(Panel parent, string label, Control control)
    {
        control.Margin = new Thickness(0, 4, 0, 10);
        control.Padding = new Thickness(7, 5, 7, 5);
        control.Background = Brush("#202631");
        control.Foreground = Brush("#EEF4F0");
        parent.Children.Add(new TextBlock { Text = label, Foreground = Brush("#A9B7B0") });
        parent.Children.Add(control);
    }

    private static Brush Brush(string value) => (Brush)new BrushConverter().ConvertFromString(value)!;
}

public sealed record DeletedRecoveryListItem(FatxDeletedEntryCandidate Candidate)
{
    public string Summary => $"{Candidate.DisplayName}  •  {Candidate.FileSize:N0} bytes  •  {Candidate.Classification}";
}

public sealed class DeletedRecoveryDialog : Window
{
    private readonly ListBox list = new();

    public DeletedRecoveryDialog(IEnumerable<FatxDeletedEntryCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        Title = "Deleted-file recovery";
        Width = 760;
        Height = 500;
        MinWidth = 580;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.White;

        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = "Only candidates whose entire guessed contiguous cluster range is currently free can be exported. FATX deletion destroys the original chain, so recovery is best-effort.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
        });

        list.DisplayMemberPath = nameof(DeletedRecoveryListItem.Summary);
        list.ItemsSource = candidates.Select(candidate => new DeletedRecoveryListItem(candidate)).ToArray();
        list.SelectedIndex = list.Items.Count > 0 ? 0 : -1;
        Grid.SetRow(list, 1);
        grid.Children.Add(list);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var export = new Button { Content = "Export selected…", IsDefault = true, Padding = new Thickness(14, 7, 14, 7) };
        export.Click += Export_Click;
        buttons.Children.Add(export);
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(10, 0, 0, 0) });
        Grid.SetRow(buttons, 2);
        grid.Children.Add(buttons);
        Content = grid;
    }

    public FatxDeletedEntryCandidate? SelectedCandidate { get; private set; }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (list.SelectedItem is not DeletedRecoveryListItem item)
        {
            MessageBox.Show(this, "Select a deleted entry first.", "Nothing selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!item.Candidate.CanAttemptRead)
        {
            MessageBox.Show(this, item.Candidate.ClassificationReason, "Candidate cannot be exported safely", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SelectedCandidate = item.Candidate;
        DialogResult = true;
    }
}

public sealed record ContentMetadataItem(string Path, StfsMetadata Metadata)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Metadata.GetDisplayName(StfsLanguage.English))
        ? Path
        : Metadata.GetDisplayName(StfsLanguage.English)!;

    public string Summary => $"{DisplayName}  •  {Metadata.SignatureKind}  •  Title ID {Metadata.TitleId:X8}";

    public string Details =>
        $"FATX path: {Path}{Environment.NewLine}" +
        $"Package: {Metadata.SignatureKind} ({Metadata.Magic}){Environment.NewLine}" +
        $"Title: {Metadata.TitleName}{Environment.NewLine}" +
        $"Publisher: {Metadata.PublisherName}{Environment.NewLine}" +
        $"Title ID: {Metadata.TitleId:X8}{Environment.NewLine}" +
        $"Media ID: {Metadata.MediaId:X8}{Environment.NewLine}" +
        $"Content type: 0x{Metadata.ContentType:X8}{Environment.NewLine}" +
        $"Content size: {Metadata.ContentSize:N0} bytes{Environment.NewLine}" +
        $"Version: {Metadata.Version:X8}; base {Metadata.BaseVersion:X8}{Environment.NewLine}" +
        $"Disc: {Metadata.DiscNumber}/{Metadata.DiscInSet}; platform {Metadata.Platform}; executable type {Metadata.ExecutableType}{Environment.NewLine}" +
        "Signature and package hash tables were not cryptographically verified.";
}

public sealed class ContentMetadataDialog : Window
{
    private readonly TextBox details = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };

    public ContentMetadataDialog(IReadOnlyList<ContentMetadataItem> packages)
    {
        ArgumentNullException.ThrowIfNull(packages);
        Title = "Xbox 360 content metadata";
        Width = 900;
        Height = 580;
        MinWidth = 700;
        MinHeight = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { Margin = new Thickness(20) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var banner = new TextBlock
        {
            Text = $"Read-only bounded STFS metadata scan • {packages.Count:N0} recognized CON/LIVE/PIRS package(s)",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetColumnSpan(banner, 2);
        grid.Children.Add(banner);

        var list = new ListBox { ItemsSource = packages, DisplayMemberPath = nameof(ContentMetadataItem.Summary), Margin = new Thickness(0, 0, 12, 0) };
        list.SelectionChanged += (_, _) => details.Text = (list.SelectedItem as ContentMetadataItem)?.Details ?? string.Empty;
        Grid.SetRow(list, 1);
        grid.Children.Add(list);
        details.Padding = new Thickness(10);
        details.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Grid.SetRow(details, 1);
        Grid.SetColumn(details, 1);
        grid.Children.Add(details);

        var close = new Button { Content = "Close", IsCancel = true, Padding = new Thickness(14, 7, 14, 7), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(close, 2);
        Grid.SetColumnSpan(close, 2);
        grid.Children.Add(close);
        Content = grid;
        if (packages.Count > 0) list.SelectedIndex = 0;
    }
}
