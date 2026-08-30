using System.Windows;
using System.IO;

namespace FatxBridge.Windows;

public partial class RestoreConfirmationWindow : Window
{
    private readonly string phrase;

    public RestoreConfirmationWindow(string imagePath, string targetPath, long capacity)
    {
        InitializeComponent();
        string targetName = targetPath.TrimEnd('\\').Split('\\').Last();
        phrase = $"RESTORE {targetName.ToUpperInvariant()}";
        DetailsText.Text = $"Image: {imagePath}{Environment.NewLine}Target: {targetPath}{Environment.NewLine}Bytes: {capacity:N0}";
        InstructionText.Text = $"Type {phrase} to confirm the exact target:";
    }

    private void ConfirmationText_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        RestoreButton.IsEnabled = string.Equals(ConfirmationText.Text.Trim(), phrase, StringComparison.Ordinal);

    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
    private void Restore_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
