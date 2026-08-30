using System.IO;
using System.Text.Json;
using FatxBridge.Core;

namespace FatxBridge.Windows;

internal sealed class VolumeLabelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FatxBridge", "volume-labels.json");
    private readonly Dictionary<string, string> labels;

    public VolumeLabelStore()
    {
        try
        {
            Dictionary<string, string>? loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(path));
            labels = loaded is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public string Get(DriveScanItem drive, FatxPartitionCandidate partition)
    {
        if (labels.TryGetValue(Key(drive, partition), out string? value)) return value;
        return Normalize($"FATX {partition.Name}");
    }

    public void Set(DriveScanItem drive, FatxPartitionCandidate partition, string label)
    {
        string normalized = Normalize(label);
        labels[Key(drive, partition)] = normalized;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(labels, JsonOptions));
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // A label can still be used for this mount when local preference storage is unavailable.
        }
    }

    public static string Normalize(string value)
    {
        const string invalid = "\\/:*?\"<>|";
        string normalized = new(value.Trim().Where(character => !char.IsControl(character) && !invalid.Contains(character)).ToArray());
        if (normalized.Length == 0) normalized = "FATX";
        return normalized.Length <= 32 ? normalized : normalized[..32];
    }

    private static string Key(DriveScanItem drive, FatxPartitionCandidate partition) =>
        $"{drive.SourceKind}|{Path.GetFullPath(drive.Path).ToUpperInvariant()}|{drive.Capacity:X}|{partition.Offset:X}|{partition.Metadata.SerialNumber:X8}";
}
