using System.Text;

namespace GitHttpBackend;

/// <summary>
/// Parses the CGI response header block emitted by git-http-backend on stdout:
/// text headers, then a blank line, then the binary body.
/// </summary>
internal static class CgiHeaderParser
{
    const int MaxHeaderBytes = 64 * 1024;

    public sealed record Result(
        int StatusCode,
        string ReasonPhrase,
        List<KeyValuePair<string, string>> Headers,
        byte[] Leftover);

    public static async Task<Result> ReadAsync(Stream stdout, CancellationToken ct)
    {
        var buf = new byte[4096];
        using var ms = new MemoryStream();
        int sepIndex = -1, sepLen = 0;

        while (true)
        {
            int n = await stdout.ReadAsync(buf.AsMemory(), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break; // EOF before a blank line — treat everything as headers
            }

            ms.Write(buf, 0, n);

            (sepIndex, sepLen) = FindSeparator(ms.GetBuffer(), (int)ms.Length);
            if (sepIndex >= 0)
            {
                break;
            }

            if (ms.Length > MaxHeaderBytes)
            {
                throw new InvalidOperationException("git-http-backend produced an oversized CGI header block.");
            }
        }

        byte[] all = ms.GetBuffer();
        int total = (int)ms.Length;
        int headerLen = sepIndex >= 0 ? sepIndex : total;
        int bodyStart = sepIndex >= 0 ? sepIndex + sepLen : total;

        var headerText = Encoding.ASCII.GetString(all, 0, headerLen);

        var leftover = new byte[total - bodyStart];
        Array.Copy(all, bodyStart, leftover, 0, leftover.Length);

        int status = 200;
        string reason = "OK";
        var headers = new List<KeyValuePair<string, string>>();

        foreach (var rawLine in headerText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (name.Equals("Status", StringComparison.OrdinalIgnoreCase))
                (status, reason) = ParseStatus(value, status, reason);
            else
                headers.Add(new KeyValuePair<string, string>(name, value));
        }

        return new Result(status, reason, headers, leftover);
    }

    // Finds the header/body separator: "\n\n" (len 2) or "\r\n\r\n" (len 4), whichever comes first.
    static (int index, int len) FindSeparator(byte[] b, int len)
    {
        for (int i = 0; i + 1 < len; i++)
        {
            if (b[i] == (byte)'\n' && b[i + 1] == (byte)'\n')
            {
                return (i, 2);
            }

            if (i + 3 < len && b[i] == (byte)'\r' && b[i + 1] == (byte)'\n'
                && b[i + 2] == (byte)'\r' && b[i + 3] == (byte)'\n')
            {
                return (i, 4);
            }
        }
        return (-1, 0);
    }

    static (int code, string reason) ParseStatus(string value, int defCode, string defReason)
    {
        int sp = value.IndexOf(' ');
        var codePart = sp < 0 ? value : value[..sp];
        if (int.TryParse(codePart, out var code))
        {
            return (code, sp < 0 ? defReason : value[(sp + 1)..]);
        }

        return (defCode, defReason);
    }
}
