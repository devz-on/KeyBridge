using System.Net;
using System.Net.Sockets;
using System.Text;

const int port = 8787;
const string origin = "http://localhost:8787";
if (args.Length != 1) { Console.Error.WriteLine("Pass the tests/webauthn source directory as the only argument."); return 1; }
string root = Path.GetFullPath(args[0]);
var routes = new Dictionary<string, (string File, string Mime)>(StringComparer.Ordinal) {
    ["/"] = ("index.html", "text/html; charset=utf-8"),
    ["/index.html"] = ("index.html", "text/html; charset=utf-8"),
    ["/styles.css"] = ("styles.css", "text/css; charset=utf-8"),
    ["/app.mjs"] = ("app.mjs", "text/javascript; charset=utf-8"),
    ["/webauthn.mjs"] = ("webauthn.mjs", "text/javascript; charset=utf-8"),
    ["/cbor.mjs"] = ("cbor.mjs", "text/javascript; charset=utf-8")
};
if (routes.Values.Any(route => !File.Exists(Path.Combine(root, route.File)))) { Console.Error.WriteLine("Required diagnostic source files are missing."); return 1; }
using var lifetime = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; lifetime.Cancel(); };
var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
try
{
    listener.Start();
    Console.WriteLine($"KeyBridge diagnostic listening at {origin} (127.0.0.1 only). Press Ctrl+C to stop.");
    while (!lifetime.IsCancellationRequested)
    {
        using var client = await listener.AcceptTcpClientAsync(lifetime.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await using var stream = client.GetStream();
        try
        {
            string request = await ReadHeadersAsync(stream, timeout.Token);
            string[] lines = request.Split("\r\n", StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (lines.Length > 64 || first.Length != 3 || first[0].Length is < 1 or > 20 || first[0].Any(c => c is < 'A' or > 'Z') || first[1].Length > 2048 || first[2] is not ("HTTP/1.1" or "HTTP/1.0")) throw new InvalidDataException();
            string method = first[0], path = first[1];
            bool head = method == "HEAD";
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1).Where(line => line.Length > 0))
            {
                int colon = line.IndexOf(':');
                if (colon < 1 || line.AsSpan(0, colon).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-")) throw new InvalidDataException();
                if (!headers.TryAdd(line[..colon], line[(colon + 1)..].Trim())) throw new InvalidDataException();
            }
            if (!headers.TryGetValue("Host", out string? host) || !host.Equals("localhost:8787", StringComparison.OrdinalIgnoreCase) || (headers.TryGetValue("Origin", out string? requestOrigin) && requestOrigin != origin)) { await ReplyAsync(stream, 403, "Forbidden", head, timeout.Token); continue; }
            if (method is not ("GET" or "HEAD")) { await ReplyAsync(stream, 405, "Method Not Allowed", head, timeout.Token); continue; }
            if (headers.ContainsKey("Transfer-Encoding") || (headers.TryGetValue("Content-Length", out string? contentLength) && contentLength != "0")) throw new InvalidDataException();
            if (!routes.TryGetValue(path, out var route)) { await ReplyAsync(stream, 404, "Not Found", head, timeout.Token); continue; }
            // Only a fixed trusted filename is combined with the source root; no URL becomes a filesystem path.
            byte[] body = await File.ReadAllBytesAsync(Path.Combine(root, route.File), timeout.Token);
            await ReplyAsync(stream, 200, "OK", head, timeout.Token, body, route.Mime);
        }
        catch (InvalidDataException) { try { await ReplyAsync(stream, 400, "Bad Request", false, timeout.Token); } catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
    }
}
catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
catch (SocketException) { Console.Error.WriteLine("Cannot bind 127.0.0.1:8787. The diagnostic did not start; check whether that port is already occupied."); return 1; }
finally { listener.Stop(); }
return 0;

static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken ct)
{
    using var bytes = new MemoryStream();
    byte[] single = new byte[1];
    int tail = 0;
    while (bytes.Length < 8192)
    {
        if (await stream.ReadAsync(single, ct) == 0) throw new InvalidDataException();
        byte next = single[0];
        if ((next < 32 && next is not (9 or 10 or 13)) || next > 126) throw new InvalidDataException();
        bytes.WriteByte(next);
        tail = (tail << 8) | next;
        if (bytes.Length >= 4 && tail == 0x0d0a0d0a) return Encoding.ASCII.GetString(bytes.ToArray());
    }
    throw new InvalidDataException();
}

static async Task ReplyAsync(NetworkStream stream, int code, string reason, bool head, CancellationToken ct, byte[]? body = null, string mime = "text/plain; charset=utf-8")
{
    body ??= Encoding.UTF8.GetBytes($"{code} {reason}\n");
    const string csp = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'none'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
    string headers = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {mime}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nCross-Origin-Opener-Policy: same-origin\r\nContent-Security-Policy: {csp}\r\nPermissions-Policy: publickey-credentials-create=(self), publickey-credentials-get=(self)\r\nAllow: GET, HEAD\r\n\r\n";
    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
    if (!head) await stream.WriteAsync(body, ct);
    await stream.FlushAsync(ct);
}
