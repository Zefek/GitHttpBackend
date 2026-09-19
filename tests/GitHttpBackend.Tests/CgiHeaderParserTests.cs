using System.Text;

namespace GitHttpBackend.Tests;

/// <summary>
/// The parser reads a binary stream by hand: it locates the header/body separator, accepts
/// both LF and CRLF forms, has to cope with the separator straddling a read boundary, and
/// hands the bytes it read past the separator on as the start of the body. It is exactly the
/// code where a later off-by-one would only ever surface as a corrupted packfile.
/// </summary>
public class CgiHeaderParserTests
{
    static async Task<CgiHeaderParser.Result> ParseAsync(byte[] input, int chunkSize = int.MaxValue)
        => await CgiHeaderParser.ReadAsync(new ChunkedStream(input, chunkSize), CancellationToken.None);

    static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    [Theory]
    [InlineData("\n\n")]
    [InlineData("\r\n\r\n")]
    public async Task Accepts_both_separator_forms(string separator)
    {
        var result = await ParseAsync(Bytes($"Status: 200 OK{separator}BODY"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("BODY", Encoding.ASCII.GetString(result.Leftover));
    }

    [Theory]
    [InlineData("\n\n")]
    [InlineData("\r\n\r\n")]
    public async Task Finds_a_separator_split_across_reads(string separator)
    {
        // One byte per read: every boundary inside the separator is exercised. Nothing past the
        // separator has been read yet, so the body has to come out of the inner stream — which
        // is the half of the contract the leftover buffer exists to make seamless.
        var stream = new ChunkedStream(
            Bytes($"Content-Type: application/x-git-upload-pack-result{separator}PACK"), chunkSize: 1);
        var result = await CgiHeaderParser.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("application/x-git-upload-pack-result", Assert.Single(result.Headers).Value);

        using var body = new ConcatStream(result.Leftover, stream);
        using var reader = new StreamReader(body, Encoding.ASCII);
        Assert.Equal("PACK", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task Reads_the_status_line_with_a_reason_phrase()
    {
        var result = await ParseAsync(Bytes("Status: 404 Not Found\n\n"));

        Assert.Equal(404, result.StatusCode);
        Assert.Equal("Not Found", result.ReasonPhrase);
    }

    [Fact]
    public async Task Reads_a_status_line_without_a_reason_phrase()
    {
        var result = await ParseAsync(Bytes("Status: 403\n\n"));

        Assert.Equal(403, result.StatusCode);
        Assert.Equal("OK", result.ReasonPhrase);   // the default is kept, not blanked
    }

    [Fact]
    public async Task Falls_back_to_200_for_a_malformed_status()
    {
        var result = await ParseAsync(Bytes("Status: nonsense\n\n"));

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("OK", result.ReasonPhrase);
    }

    [Fact]
    public async Task Ignores_a_header_line_with_no_colon()
    {
        var result = await ParseAsync(Bytes("garbage\nContent-Type: text/plain\n\n"));

        Assert.Equal("Content-Type", Assert.Single(result.Headers).Key);
    }

    [Fact]
    public async Task Status_is_not_passed_through_as_a_response_header()
    {
        var result = await ParseAsync(Bytes("Status: 304 Not Modified\nCache-Control: no-cache\n\n"));

        Assert.Equal(304, result.StatusCode);
        Assert.Equal("Cache-Control", Assert.Single(result.Headers).Key);
    }

    [Fact]
    public async Task Treats_everything_as_headers_when_EOF_arrives_before_a_separator()
    {
        var result = await ParseAsync(Bytes("Content-Type: text/plain\n"));

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(result.Leftover);
        Assert.Equal("text/plain", Assert.Single(result.Headers).Value);
    }

    [Fact]
    public async Task Empty_output_parses_as_an_empty_200()
    {
        var result = await ParseAsync([]);

        Assert.Equal(200, result.StatusCode);
        Assert.Empty(result.Headers);
        Assert.Empty(result.Leftover);
    }

    [Fact]
    public async Task Refuses_an_oversized_header_block()
    {
        // No separator anywhere, so the guard is the only thing that stops the read.
        var oversized = Encoding.ASCII.GetBytes(new string('x', 128 * 1024));

        await Assert.ThrowsAsync<InvalidOperationException>(() => ParseAsync(oversized, chunkSize: 4096));
    }

    [Fact]
    public async Task Body_bytes_that_arrive_with_the_headers_surface_intact()
    {
        // The realistic shape: headers and the first pack bytes land in one read, so the body
        // begins with bytes the parser has already taken off the stream.
        var payload = new byte[8192];
        Random.Shared.NextBytes(payload);

        var input = Bytes("Content-Type: application/x-git-upload-pack-result\r\n\r\n")
            .Concat(payload).ToArray();

        var stream = new ChunkedStream(input, chunkSize: int.MaxValue);
        var result = await CgiHeaderParser.ReadAsync(stream, CancellationToken.None);

        using var body = new ConcatStream(result.Leftover, stream);
        using var copy = new MemoryStream();
        await body.CopyToAsync(copy);

        Assert.Equal(payload, copy.ToArray());
    }

    /// <summary>Hands out at most <c>chunkSize</c> bytes per read, to force read boundaries.</summary>
    sealed class ChunkedStream : Stream
    {
        readonly byte[] _data;
        readonly int _chunkSize;
        int _position;

        public ChunkedStream(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, _chunkSize), _data.Length - _position);
            if (n <= 0)
                return 0;
            Array.Copy(_data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int n = Math.Min(Math.Min(buffer.Length, _chunkSize), _data.Length - _position);
            if (n <= 0)
                return ValueTask.FromResult(0);
            _data.AsMemory(_position, n).CopyTo(buffer);
            _position += n;
            return ValueTask.FromResult(n);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
