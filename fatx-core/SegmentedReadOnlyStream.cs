namespace FatxBridge.Core;

/// <summary>
/// Presents a sequence of readable, seekable streams as one read-only stream.
/// The wrapper owns and disposes the supplied streams unless <paramref name="leaveOpen"/>
/// is <see langword="true"/>.
/// </summary>
public sealed class SegmentedReadOnlyStream : Stream
{
    private readonly Stream[] segments;
    private readonly long[] starts;
    private readonly long[] lengths;
    private readonly bool leaveOpen;
    private readonly long totalLength;
    private long position;
    private bool disposed;

    public SegmentedReadOnlyStream(IReadOnlyList<Stream> segments, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(segments);
        this.leaveOpen = leaveOpen;

        var seen = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
        try
        {
            if (segments.Count > MaximumSegmentCount)
                throw new ArgumentOutOfRangeException(nameof(segments),
                    $"A segmented stream cannot contain more than {MaximumSegmentCount} segments.");

            this.segments = new Stream[segments.Count];
            starts = new long[segments.Count];
            lengths = new long[segments.Count];

            long total = 0;
            for (int index = 0; index < segments.Count; index++)
            {
                Stream segment = segments[index] ?? throw new ArgumentException(
                    "A segmented stream cannot contain a null stream.", nameof(segments));
                if (!seen.Add(segment))
                    throw new ArgumentException("A segmented stream cannot contain the same stream twice.", nameof(segments));
                if (!segment.CanRead || !segment.CanSeek)
                    throw new ArgumentException("Every segment must be readable and seekable.", nameof(segments));

                long length = segment.Length;
                if (length < 0)
                    throw new IOException("A segment reported a negative length.");

                this.segments[index] = segment;
                starts[index] = total;
                lengths[index] = length;
                total = checked(total + length);
            }

            totalLength = total;
        }
        catch
        {
            if (!leaveOpen)
            {
                foreach (Stream segment in seen)
                {
                    try { segment.Dispose(); }
                    catch { /* Preserve the constructor's validation failure. */ }
                }
            }
            throw;
        }
    }

    public SegmentedReadOnlyStream(IEnumerable<Stream> segments, bool leaveOpen = false)
        : this(segments is IReadOnlyList<Stream> list ? list : segments.ToArray(), leaveOpen)
    {
    }

    public const int MaximumSegmentCount = 4096;

    public int SegmentCount
    {
        get
        {
            EnsureNotDisposed();
            return segments.Length;
        }
    }

    public bool OwnsSegments => !leaveOpen;

    public override bool CanRead => !disposed;
    public override bool CanSeek => !disposed;
    public override bool CanWrite => false;
    public override long Length
    {
        get
        {
            EnsureNotDisposed();
            return totalLength;
        }
    }

    public override long Position
    {
        get
        {
            EnsureNotDisposed();
            return position;
        }
        set
        {
            EnsureNotDisposed();
            SetPosition(value);
        }
    }

    public override void Flush() => EnsureNotDisposed();

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The read range is outside the destination buffer.", nameof(buffer));
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        EnsureNotDisposed();
        if (buffer.IsEmpty || position == Length) return 0;

        int totalRead = 0;
        while (totalRead < buffer.Length && position < Length)
        {
            int index = FindSegment(position);
            if (index >= segments.Length) break;

            long segmentOffset = checked(position - starts[index]);
            long segmentRemaining = checked(lengths[index] - segmentOffset);
            if (segmentRemaining <= 0)
            {
                position = checked(starts[index] + lengths[index]);
                continue;
            }

            int requested = checked((int)Math.Min((long)(buffer.Length - totalRead), segmentRemaining));
            Stream segment = segments[index];
            segment.Position = segmentOffset;
            int read = segment.Read(buffer.Slice(totalRead, requested));
            if (read < 0 || read > requested)
                throw new IOException("A segmented stream segment returned an invalid read count.");
            if (read == 0)
            {
                // A seekable file stream should not return zero before its end,
                // but advancing prevents a broken removable-media wrapper from
                // trapping the caller in an infinite loop.
                position = checked(starts[index] + lengths[index]);
                continue;
            }

            totalRead += read;
            position = checked(position + read);
        }

        return totalRead;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        new(ReadAsyncCore(buffer, cancellationToken));

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The read range is outside the destination buffer.", nameof(buffer));
        return ReadAsyncCore(buffer.AsMemory(offset, count), cancellationToken);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        EnsureNotDisposed();
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        SetPosition(target);
        return position;
    }

    public override void SetLength(long value) => throw new NotSupportedException(
        "A segmented read-only stream has a fixed length.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException(
        "A segmented read-only stream cannot be written.");

    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException(
        "A segmented read-only stream cannot be written.");

    protected override void Dispose(bool disposing)
    {
        if (!disposing || disposed)
        {
            base.Dispose(disposing);
            return;
        }

        disposed = true;
        if (!leaveOpen)
        {
            Exception? first = null;
            foreach (Stream segment in segments)
            {
                try { segment.Dispose(); }
                catch (Exception exception)
                {
                    first ??= exception;
                }
            }
            base.Dispose(disposing);
            if (first is not null) throw first;
            return;
        }

        base.Dispose(disposing);
    }

    private async Task<int> ReadAsyncCore(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        if (buffer.IsEmpty || position == Length) return 0;

        int totalRead = 0;
        while (totalRead < buffer.Length && position < Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int index = FindSegment(position);
            if (index >= segments.Length) break;

            long segmentOffset = checked(position - starts[index]);
            long segmentRemaining = checked(lengths[index] - segmentOffset);
            if (segmentRemaining <= 0)
            {
                position = checked(starts[index] + lengths[index]);
                continue;
            }

            int requested = checked((int)Math.Min((long)(buffer.Length - totalRead), segmentRemaining));
            Stream segment = segments[index];
            segment.Position = segmentOffset;
            int read = await segment.ReadAsync(buffer.Slice(totalRead, requested), cancellationToken)
                .ConfigureAwait(false);
            if (read < 0 || read > requested)
                throw new IOException("A segmented stream segment returned an invalid read count.");
            if (read == 0)
            {
                position = checked(starts[index] + lengths[index]);
                continue;
            }

            totalRead += read;
            position = checked(position + read);
        }

        return totalRead;
    }

    private int FindSegment(long at)
    {
        int low = 0;
        int high = starts.Length - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            if (starts[middle] <= at) low = middle + 1;
            else high = middle - 1;
        }

        int index = high;
        while (index >= 0 && index < segments.Length)
        {
            long end = checked(starts[index] + lengths[index]);
            if (at < end) return index;
            index++;
        }
        return segments.Length;
    }

    private void SetPosition(long value)
    {
        if (value < 0 || value > Length)
            throw new IOException("The requested position is outside the segmented stream.");
        position = value;
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
