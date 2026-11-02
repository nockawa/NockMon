namespace NockMon.Core.Agents;

/// <summary>
/// Read side of an agent connection: fails once a line (an MCP message) exceeds <see cref="MaxLine"/> bytes. The MCP
/// SDK reads whole lines without a limit, so a client sending no newline would otherwise grow the elevated process's
/// memory without bound.
/// </summary>
internal sealed class LineLimitedStream(Stream inner) : Stream
{
    public const int MaxLine = 16 * 1024 * 1024;

    private long _sinceNewline;

    public override int Read(byte[] buffer, int offset, int count) => Count(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

    public override int Read(Span<byte> buffer) => Count(buffer[..inner.Read(buffer)]);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        return Count(buffer.Span[..read]);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private int Count(ReadOnlySpan<byte> data)
    {
        var newline = data.LastIndexOf((byte)'\n');
        _sinceNewline = newline >= 0 ? data.Length - newline - 1 : _sinceNewline + data.Length;
        if (_sinceNewline > MaxLine)
            throw new IOException($"Agent message over {MaxLine / (1024 * 1024)} MB: connection closed.");
        return data.Length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
