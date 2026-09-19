namespace GitHttpBackend.Tests;

/// <summary>
/// ConcatStream joins the bytes read past the CGI header block to the rest of the backend's
/// stdout. A mistake here shows up as a packfile that is short or duplicated at one point,
/// so the prefix/buffer size relationships are worth pinning explicitly.
/// </summary>
public class ConcatStreamTests
{
    static byte[] Sequence(int length)
    {
        var bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }
        return bytes;
    }

    [Theory]
    [InlineData(0, 64)]      // no prefix at all
    [InlineData(16, 64)]     // prefix shorter than the read buffer
    [InlineData(64, 64)]     // prefix exactly the read buffer
    [InlineData(200, 64)]    // prefix longer than the read buffer, spanning several reads
    public async Task Async_read_yields_prefix_then_inner(int prefixLength, int bufferSize)
    {
        var prefix = Sequence(prefixLength);
        var tail = Sequence(300);

        using var stream = new ConcatStream(prefix, new MemoryStream(tail));
        var read = await ReadAllAsync(stream, bufferSize);

        Assert.Equal(prefix.Concat(tail), read);
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(16, 64)]
    [InlineData(64, 64)]
    [InlineData(200, 64)]
    public void Sync_read_yields_the_same_bytes_as_async(int prefixLength, int bufferSize)
    {
        var prefix = Sequence(prefixLength);
        var tail = Sequence(300);

        using var syncStream = new ConcatStream(prefix, new MemoryStream(tail));
        var syncBytes = ReadAll(syncStream, bufferSize);

        using var asyncStream = new ConcatStream(prefix, new MemoryStream(tail));
        var asyncBytes = ReadAllAsync(asyncStream, bufferSize).GetAwaiter().GetResult();

        Assert.Equal(syncBytes, asyncBytes);
    }

    [Fact]
    public void Does_not_dispose_the_inner_stream()
    {
        // The invoker owns the backend process and its stdout; the body stream is a view onto
        // it, and disposing it out from under the process would truncate the response.
        var inner = new MemoryStream(Sequence(10));
        var stream = new ConcatStream([], inner);

        stream.Dispose();

        Assert.True(inner.CanRead);
    }

    static byte[] ReadAll(Stream stream, int bufferSize)
    {
        using var output = new MemoryStream();
        var buffer = new byte[bufferSize];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }

    static async Task<byte[]> ReadAllAsync(Stream stream, int bufferSize)
    {
        using var output = new MemoryStream();
        var buffer = new byte[bufferSize];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            output.Write(buffer, 0, n);
        }
        return output.ToArray();
    }
}
