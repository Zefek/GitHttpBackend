namespace GitHttpBackend;

/// <summary>
/// Read-only stream that first yields a buffered prefix (the bytes read past the CGI
/// header block while parsing headers), then continues reading from an inner stream
/// (the backend's stdout). Does not own or dispose the inner stream.
/// </summary>
internal sealed class ConcatStream : Stream
{
    readonly byte[] _prefix;
    readonly Stream _inner;
    int _prefixPos;

    public ConcatStream(byte[] prefix, Stream inner)
    {
        _prefix = prefix;
        _inner = inner;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_prefixPos < _prefix.Length)
        {
            int n = Math.Min(count, _prefix.Length - _prefixPos);
            Array.Copy(_prefix, _prefixPos, buffer, offset, n);
            _prefixPos += n;
            return n;
        }
        return _inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_prefixPos < _prefix.Length)
        {
            int n = Math.Min(buffer.Length, _prefix.Length - _prefixPos);
            _prefix.AsMemory(_prefixPos, n).CopyTo(buffer);
            _prefixPos += n;
            return n;
        }
        return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
