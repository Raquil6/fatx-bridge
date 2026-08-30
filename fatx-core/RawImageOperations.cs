using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;

namespace FatxBridge.Core;

public enum RawImageStage
{
    Copying,
    Verifying,
}

public sealed record RawImageProgress(RawImageStage Stage, long BytesProcessed, long TotalBytes)
{
    public double Fraction => TotalBytes == 0 ? 1 : Math.Clamp((double)BytesProcessed / TotalBytes, 0, 1);
}

public sealed record RawImageResult(long BytesCopied, string Sha256);

/// <summary>Exact-length, sector-agnostic imaging primitives for files and raw devices.</summary>
public static class RawImageOperations
{
    private const int BufferSize = 4 * 1024 * 1024;

    public static async Task<RawImageResult> CopyAndVerifyAsync(
        Stream source,
        Stream destination,
        long length,
        IProgress<RawImageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (ReferenceEquals(source, destination))
            throw new ArgumentException("The imaging source and destination must be different streams.", nameof(destination));
        if (!source.CanRead || !source.CanSeek)
            throw new ArgumentException("The imaging source must be readable and seekable.", nameof(source));
        if (!destination.CanRead || !destination.CanWrite || !destination.CanSeek)
            throw new ArgumentException("The imaging destination must be readable, writable, and seekable.", nameof(destination));
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        cancellationToken.ThrowIfCancellationRequested();
        source.Position = 0;
        destination.Position = 0;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var copiedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await CopyExactAsync(source, destination, length, buffer, copiedHash,
                RawImageStage.Copying, progress, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (destination is FileStream file) file.Flush(flushToDisk: true);

            string expectedHash = Convert.ToHexString(copiedHash.GetHashAndReset());
            destination.Position = 0;
            using var verifiedHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await ReadExactAsync(destination, length, buffer, verifiedHash,
                RawImageStage.Verifying, progress, cancellationToken).ConfigureAwait(false);
            string actualHash = Convert.ToHexString(verifiedHash.GetHashAndReset());
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expectedHash), Convert.FromHexString(actualHash)))
                throw new IOException("Image verification failed: the destination hash does not match the copied bytes.");

            return new RawImageResult(length, actualHash);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task CopyExactAsync(
        Stream source,
        Stream destination,
        long length,
        byte[] buffer,
        IncrementalHash hash,
        RawImageStage stage,
        IProgress<RawImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new RawImageProgress(stage, 0, length));
        long processed = 0;
        var reportTimer = Stopwatch.StartNew();
        while (processed < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = checked((int)Math.Min(buffer.Length, length - processed));
            int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException($"The imaging source ended after {processed} of {length} bytes.");
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            processed += read;
            ReportProgress(stage, processed, length, reportTimer, progress);
        }
        progress?.Report(new RawImageProgress(stage, processed, length));
    }

    private static async Task ReadExactAsync(
        Stream source,
        long length,
        byte[] buffer,
        IncrementalHash hash,
        RawImageStage stage,
        IProgress<RawImageProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new RawImageProgress(stage, 0, length));
        long processed = 0;
        var reportTimer = Stopwatch.StartNew();
        while (processed < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = checked((int)Math.Min(buffer.Length, length - processed));
            int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException($"The verification source ended after {processed} of {length} bytes.");
            hash.AppendData(buffer, 0, read);
            processed += read;
            ReportProgress(stage, processed, length, reportTimer, progress);
        }
        progress?.Report(new RawImageProgress(stage, processed, length));
    }

    private static void ReportProgress(
        RawImageStage stage,
        long processed,
        long length,
        Stopwatch reportTimer,
        IProgress<RawImageProgress>? progress)
    {
        if (progress is null || (processed != length && reportTimer.ElapsedMilliseconds < 150)) return;
        progress.Report(new RawImageProgress(stage, processed, length));
        reportTimer.Restart();
    }
}
