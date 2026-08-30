using System.IO;
using System.Media;

namespace FatxBridge.Windows;

/// <summary>The selected Airy Halo UI sound and its local on/off preference.</summary>
internal static class UiSound
{
    private const int SampleRate = 22_050;
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FatxBridge",
        "ui-sounds.txt");
    private static readonly Lazy<SoundPlayer> AiryHaloPlayer = new(CreateAiryHaloPlayer);
    private static readonly Lazy<SoundPlayer> ButtonClickPlayer = new(CreateButtonClickPlayer);
    private static bool enabled = LoadEnabled();

    public static bool Enabled
    {
        get => enabled;
        set
        {
            if (enabled == value) return;
            enabled = value;
            SaveEnabled(value);
        }
    }

    public static void Navigate() => Play(AiryHaloPlayer);
    public static void Activate() => Play(ButtonClickPlayer);

    private static void Play(Lazy<SoundPlayer> player)
    {
        if (!Enabled) return;
        try { player.Value.Play(); }
        catch (InvalidOperationException) { }
        catch (FileNotFoundException) { }
    }

    private static SoundPlayer CreateButtonClickPlayer()
    {
        const double seconds = 0.13;
        int sampleCount = checked((int)(SampleRate * seconds));
        var wave = new MemoryStream(44 + sampleCount * sizeof(short));
        using (var writer = CreateWaveWriter(wave, sampleCount))
        {
            for (int index = 0; index < sampleCount; index++)
            {
                double time = index / (double)SampleRate;
                double envelope = Math.Sin(Math.PI * index / sampleCount);
                double first = Math.Sin(2 * Math.PI * 520 * time);
                double second = time < 0.055 ? 0 : Math.Sin(2 * Math.PI * 780 * (time - 0.055));
                double sample = (first * (time < 0.07 ? 1 : 0.25) + 0.65 * second) * envelope * 0.20;
                writer.Write((short)(Math.Clamp(sample, -1, 1) * short.MaxValue));
            }
        }
        return LoadPlayer(wave);
    }

    private static SoundPlayer CreateAiryHaloPlayer()
    {
        const double seconds = 0.46;
        const double edgeCutoff = 220;
        const double peakCutoff = 1_420;
        const double lowRatio = 0.22;
        const double memory = 0.87;
        const double targetPeak = 0.11;
        const double envelopePower = 1.30;
        const double ringStart = 840;
        const double ringEnd = 1_450;
        const double ringMix = 0.11;
        const double ringDamping = 0.27;

        int sampleCount = checked((int)(SampleRate * seconds));
        var samples = new double[sampleCount];
        // Preserve the exact deterministic noise seed used by Sound Lab option 5.
        uint noiseState = 0x1C1F1850u;
        double fastStageOne = 0;
        double fastStageTwo = 0;
        double slowStageOne = 0;
        double slowStageTwo = 0;
        double previous = 0;
        double resonantLow = 0;
        double resonantBand = 0;
        double highest = 0;

        for (int index = 0; index < sampleCount; index++)
        {
            double progress = index / (double)(sampleCount - 1);
            double sweep = progress * progress * (3 - 2 * progress);
            double fastCutoff = edgeCutoff + (peakCutoff - edgeCutoff) * sweep;
            double slowCutoff = fastCutoff * lowRatio;
            double fastAlpha = 1 - Math.Exp(-2 * Math.PI * fastCutoff / SampleRate);
            double slowAlpha = 1 - Math.Exp(-2 * Math.PI * slowCutoff / SampleRate);
            double white = NextNoise(ref noiseState);

            fastStageOne += fastAlpha * (white - fastStageOne);
            fastStageTwo += fastAlpha * (fastStageOne - fastStageTwo);
            slowStageOne += slowAlpha * (white - slowStageOne);
            slowStageTwo += slowAlpha * (slowStageOne - slowStageTwo);
            double hollowAir = fastStageTwo - slowStageTwo;
            double smoothedAir = memory * previous + (1 - memory) * hollowAir;
            previous = smoothedAir;

            double ringFrequency = ringStart + (ringEnd - ringStart) * sweep;
            double resonantCoefficient = 2 * Math.Sin(Math.PI * ringFrequency / SampleRate);
            resonantLow += resonantCoefficient * resonantBand;
            double resonantHigh = white - resonantLow - ringDamping * resonantBand;
            resonantBand += resonantCoefficient * resonantHigh;

            double envelope = Math.Pow(Math.Sin(Math.PI * progress), envelopePower);
            double breath = 0.92 + 0.08 * Math.Sin(2 * Math.PI * (2.2 * progress + 0.35 * progress * progress));
            samples[index] = (smoothedAir + resonantBand * ringMix) * envelope * breath;
            highest = Math.Max(highest, Math.Abs(samples[index]));
        }

        double scale = highest > 0 ? targetPeak / highest : 0;
        var wave = new MemoryStream(44 + sampleCount * sizeof(short));
        using (var writer = CreateWaveWriter(wave, sampleCount))
        {
            foreach (double sample in samples)
                writer.Write((short)(Math.Clamp(sample * scale, -1, 1) * short.MaxValue));
        }
        return LoadPlayer(wave);
    }

    private static BinaryWriter CreateWaveWriter(MemoryStream wave, int sampleCount)
    {
        var writer = new BinaryWriter(wave, System.Text.Encoding.ASCII, leaveOpen: true);
        int dataLength = checked(sampleCount * sizeof(short));
        writer.Write("RIFF"u8);
        writer.Write(checked(36 + dataLength));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(checked(SampleRate * sizeof(short)));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataLength);
        return writer;
    }

    private static SoundPlayer LoadPlayer(MemoryStream wave)
    {
        wave.Position = 0;
        var player = new SoundPlayer(wave);
        player.Load();
        return player;
    }

    private static double NextNoise(ref uint state)
    {
        state = unchecked(state * 1_664_525u + 1_013_904_223u);
        return ((state >> 8) & 0x00FFFFFF) / 8_388_607.5 - 1;
    }

    private static bool LoadEnabled()
    {
        try
        {
            return !File.Exists(PreferencePath) ||
                !bool.TryParse(File.ReadAllText(PreferencePath).Trim(), out bool stored) || stored;
        }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void SaveEnabled(bool value)
    {
        try
        {
            string? directory = Path.GetDirectoryName(PreferencePath);
            if (directory is not null) Directory.CreateDirectory(directory);
            File.WriteAllText(PreferencePath, value.ToString());
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
