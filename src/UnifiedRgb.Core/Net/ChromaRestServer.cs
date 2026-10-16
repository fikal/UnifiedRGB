using System.Net;
using System.Text;
using System.Text.Json;
using UnifiedRgb.Core.Effects;

namespace UnifiedRgb.Core.Net;

/// <summary>The modern Chroma path: games (CS2, most 2023+ titles) talk to the
/// Razer Chroma SDK over a REST API on localhost:54235 - NOT the C++
/// RzChromaSDK64.dll our shim replaces (that's Wallpaper Engine's route). Razer
/// Synapse/Central normally serves this; when it's absent (no Razer gear) we
/// serve it ourselves and feed the frames to ChromaFeed, so the game lights ALL
/// our devices. No DLL, no signed-binary-in-a-system-path, no hijack shape.
///
/// Init:      POST /razer/chromasdk           -> {sessionid, uri}
/// Per frame: PUT  /razer/chromasdk/{id}/keyboard (etc.) with a CHROMA_CUSTOM/
///            _KEY/STATIC effect; the keyboard's 6x22 grid is the richest source.
/// Also:      PUT .../heartbeat, DELETE .../{id}.
///
/// Perf shape: requests are handled on the thread pool (the accept loop never
/// blocks behind a slow parse), bodies are read into a per-thread reusable
/// buffer and parsed as UTF-8 bytes (no per-frame body string), constant
/// responses are pre-encoded, and failed requests are aborted instead of left
/// hanging until the client times out.</summary>
public static class ChromaRestServer
{
    const int Port = 54235;
    static HttpListener? _listener;
    static int _port = Port;   // what the init reply points the client back at
    static int _session;
    static readonly object _lock = new();

    static readonly byte[] RespResult0 = Encoding.UTF8.GetBytes("{\"result\":0}");
    static readonly byte[] RespTick = Encoding.UTF8.GetBytes("{\"tick\":1}");
    static readonly LogBudget _initLog = new(5);   // a game inits once; a flood must not grow the log
    static readonly LogBudget _refusedLog = new(5); // a page can retry in a loop; same bound

    public static void Start() => Start(Port);

    /// <summary>Listen on <paramref name="port"/>. A parameter only for the
    /// harness, which cannot take :54235 while the app itself is running.
    /// True when listening (now or already).</summary>
    internal static bool Start(int port)
    {
        if (_listener != null) return true;
        lock (_lock)
        {
            if (_listener != null) return true;
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://localhost:{port}/");
                // HTTP.sys matches a prefix on the Host header: a Chroma host that
                // spells the documented base URL as 127.0.0.1 got no answer at all.
                // Loopback either way; nothing on the LAN is reached by this.
                l.Prefixes.Add($"http://127.0.0.1:{port}/");
                l.Start();
                _port = port;
                _listener = l;
                new Thread(() => Loop(l)) { IsBackground = true, Name = "chroma-rest" }.Start();
                Log.Info("chroma", $"REST server listening on :{port}");
                return true;
            }
            catch (Exception ex)
            {
                // Port busy = Razer Central already owns it (then Razer's own SDK
                // handles games; our shim/pipe still covers Wallpaper Engine).
                Log.Warn("chroma", $"REST server not started ({ex.Message}) - Razer Central may own :{port}");
                return false;
            }
        }
    }

    /// <summary>Release the port and stop the accept loop. Without this the
    /// HTTP.sys registration lived for the whole process, and a Razer install
    /// starting later could never claim :54235 back.</summary>
    public static void Stop()
    {
        lock (_lock)
        {
            var l = _listener;
            _listener = null;
            if (l == null) return;
            try { l.Stop(); l.Close(); } catch { }   // already faulted or closed; the field is nulled above either way
            Log.Info("chroma", "REST server stopped");
        }
    }

    static void Loop(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = l.GetContext(); }
            catch { break; }   // the listener was stopped underneath us: that IS the exit signal
            // Off-thread: a slow frame parse must not delay the next accept.
            ThreadPool.QueueUserWorkItem(static c =>
            {
                var context = (HttpListenerContext)c!;
                try { Handle(context); }
                catch (Exception ex)
                {
                    Log.Occasional("chroma-rest", "chroma", $"handle: {ex.Message}");
                    try { context.Response.Abort(); } catch { }   // don't leave the client hanging
                }
            }, ctx);
        }
    }

    // Per-worker reusable body buffer: game frames arrive at render rate and a
    // fresh string + byte[] per request added up to a few hundred KB/s.
    [ThreadStatic] static byte[]? _bodyBuf;

    static void Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        // Loopback only, in code: the HTTP.sys prefix matches the Host header,
        // not the connecting address, so a LAN request sent with
        // "Host: localhost" was served. The GSI listener checks the same.
        if (!req.IsLocal)
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
            return;
        }
        // A web page must not drive the lights: a text/plain POST is a CORS
        // "simple" request, sent with no preflight, and its body was applied
        // before the browser ever looked for a CORS header in the reply. Pages
        // are told apart by their Origin (OriginAllowed has the rules); the
        // native clients send none, and the browser-hosted ones that worked
        // before - extensions, NW.js and Electron games, Overwolf apps - send
        // an origin no web page can.
        string? origin = req.Headers["Origin"];
        if (!OriginAllowed(origin))
        {
            if (_refusedLog.Allow()) Log.Warn("chroma", $"REST: refused a request from a web page ({Printable(origin)})");
            ctx.Response.StatusCode = 403;
            ctx.Response.ContentLength64 = 0;
            ctx.Response.Close();
            return;
        }
        string path = (req.Url?.AbsolutePath ?? "").TrimEnd('/');

        int bodyLen = 0;
        byte[]? body = null;
        if (req.HasEntityBody)
        {
            body = _bodyBuf ??= new byte[64 * 1024];
            int read;
            while (bodyLen < body.Length &&
                   (read = req.InputStream.Read(body, bodyLen, body.Length - bodyLen)) > 0)
                bodyLen += read;
        }

        // Init: POST /razer/chromasdk
        if (req.HttpMethod == "POST" && path.EndsWith("/razer/chromasdk", StringComparison.Ordinal))
        {
            int id = Interlocked.Increment(ref _session);
            if (_initLog.Allow()) Log.Info("chroma", $"REST init: session {id} ({AppTitle(body, bodyLen)})");
            Json(ctx, Encoding.UTF8.GetBytes(
                $"{{\"sessionid\":{id},\"uri\":\"http://localhost:{_port}/razer/chromasdk/{id}\"}}"));
            return;
        }

        int marker = path.IndexOf("/razer/chromasdk/", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var seg = path[(marker + "/razer/chromasdk/".Length)..]
                .Split('/', StringSplitOptions.RemoveEmptyEntries);   // [id] or [id, device]

            if (req.HttpMethod == "DELETE") { Json(ctx, RespResult0); return; }
            if (seg.Length >= 2 && seg[1].Equals("heartbeat", StringComparison.OrdinalIgnoreCase))
            { Json(ctx, RespTick); return; }
            if (seg.Length >= 2 && body != null) TryApply(body, bodyLen, seg[1]);
            Json(ctx, RespResult0);
            return;
        }

        Json(ctx, RespResult0);
    }

    /// <summary>Whether a request carrying this Origin header may be served.
    /// Refused: what a web page can send - an http(s) origin on any host but
    /// this machine's loopback, and the opaque "null" (a sandboxed iframe or a
    /// data: document forces that one, so it cannot be told from an attack).
    /// Served: no Origin at all (the SDK's C++/C# wrappers, game engines' HTTP
    /// stacks); any other scheme, which only an extension or a packaged app
    /// can be (chrome-extension, moz-extension, safari-web-extension,
    /// overwolf-extension, NW.js/Electron app:// and file://); and
    /// http(s)://localhost, 127.x.x.x and [::1], which is a local process
    /// serving its own page (DNS rebinding arrives with the attacker's own
    /// origin, not ours). Anything that is not a bare scheme://host[:port],
    /// as browsers serialise an origin, is refused rather than guessed at;
    /// two headers folded into one ("a, b") are refused that way too.</summary>
    internal static bool OriginAllowed(string? origin)
    {
        if (origin == null) return true;
        if (origin.Equals("null", StringComparison.OrdinalIgnoreCase)) return false;
        int sep = origin.IndexOf("://", StringComparison.Ordinal);
        if (sep <= 0 || !char.IsAsciiLetter(origin[0])) return false;
        string scheme = origin[..sep];
        foreach (char c in scheme)
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.')) return false;
        // The host[:port] part: printable ASCII (browsers send IDN hosts as
        // punycode), and no path, query, credentials or second value.
        foreach (char c in origin.AsSpan(sep + 3))
            if (c <= ' ' || c >= 0x7F || c is '/' or '\\' or '?' or '#' or '@' or ',') return false;
        bool web = scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        if (!web) return true;
        // The host is split out by hand, not with Uri: Uri rewrites a host
        // named "loopback" to "localhost" (and IsLoopback says yes to it), and
        // a DNS search suffix can resolve that name to anywhere on the network.
        string authority = origin[(sep + 3)..], host, port;
        if (authority.StartsWith('['))
        {
            int close = authority.IndexOf(']');
            if (close < 0) return false;
            host = authority[1..close];
            port = authority[(close + 1)..];
        }
        else
        {
            int colon = authority.IndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            port = colon < 0 ? "" : authority[colon..];
        }
        if (port.Length > 0 && (port.Length is < 2 or > 6 || port[0] != ':' || port.AsSpan(1).ContainsAnyExceptInRange('0', '9')))
            return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>Parse a device effect and push its colors to the feed, routed by
    /// device: the keyboard's CHROMA_CUSTOM 6x22 grid (type 1) is the richest
    /// source, ChromaLink's 1-D strip fills the type-2 slot, and every other
    /// device (mouse, headset, mousepad, keypad, the apply-by-id /effect
    /// endpoint) only marks the host alive. Their 9x7 / 1x1 / body-less frames
    /// used to land in the keyboard slot too, so a game driving several Razer
    /// device classes per frame flickered every LED between pictures and black.</summary>
    static void TryApply(byte[] body, int len, string device)
    {
        int type;
        if (device.Equals("keyboard", StringComparison.OrdinalIgnoreCase)) type = 1;
        else if (device.Equals("chromalink", StringComparison.OrdinalIgnoreCase)) type = 2;
        else { ChromaFeed.Touch(); return; }
        try
        {
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(body, 0, len));
            var root = doc.RootElement;
            string effect = root.TryGetProperty("effect", out var e) ? e.GetString() ?? "" : "";

            if (effect.Contains("CUSTOM", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("param", out var param))
            {
                // param is a 2D int array (keyboard), a 1D int array (ChromaLink),
                // or { color: [...], key: [[...]] }.
                var rows = param.ValueKind == JsonValueKind.Object && param.TryGetProperty("color", out var col) ? col : param;
                if (rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0)
                {
                    if (rows[0].ValueKind == JsonValueKind.Array)
                    {
                        int r = rows.GetArrayLength(), c = rows[0].GetArrayLength();
                        if (r > 0 && c > 0 && r * c <= 4096)
                        {
                            // The grid must be a fresh array: it's published by
                            // reference to the render threads.
                            var grid = new Rgb[r * c];
                            for (int y = 0; y < r; y++)
                            {
                                var rowArr = rows[y];
                                for (int x = 0; x < c && x < rowArr.GetArrayLength(); x++)
                                    grid[y * c + x] = FromChroma(rowArr[x].GetInt32());
                            }
                            ChromaFeed.PushGrid(grid, r, c, type);
                            return;
                        }
                    }
                    else if (rows[0].ValueKind == JsonValueKind.Number)
                    {
                        int c = Math.Min(rows.GetArrayLength(), 64);   // ChromaLink is 5; cap a hostile body
                        var grid = new Rgb[c];
                        for (int x = 0; x < c; x++) grid[x] = FromChroma(rows[x].GetInt32());
                        ChromaFeed.PushGrid(grid, 1, c, type);
                        return;
                    }
                }
            }

            if (effect.Contains("STATIC", StringComparison.OrdinalIgnoreCase))
            {
                int color = 0;
                if (root.TryGetProperty("param", out var p))
                {
                    if (p.ValueKind == JsonValueKind.Number) color = p.GetInt32();
                    else if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("color", out var cc) && cc.ValueKind == JsonValueKind.Number)
                        color = cc.GetInt32();
                }
                ChromaFeed.PushGrid(new[] { FromChroma(color) }, 1, 1, type);
                return;
            }

            // NONE / unrecognised: mark connected with black so the effect stops
            // showing its "waiting" breath while a host is clearly present.
            ChromaFeed.PushGrid(new[] { new Rgb(0, 0, 0) }, 1, 1, type);
        }
        catch { }   // a malformed effect body from a game: drop the frame, keep serving
    }

    // Chroma color int = COLORREF 0x00BBGGRR (R low byte).
    static Rgb FromChroma(int c) => new((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));

    // Internal (not private) so the console harness can pin the log-forgery guard.
    internal static string AppTitle(byte[]? body, int len)
    {
        if (body == null || len == 0) return "?";
        try
        {
            using var d = JsonDocument.Parse(new ReadOnlyMemory<byte>(body, 0, len));
            string title = d.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "?" : "?";
            return Printable(title);
        }
        catch { return "?"; }   // unparseable request body; "?" is what the log line prints
    }

    /// <summary>Peer-controlled text headed for the log (and the support
    /// bundle): no control characters (an embedded newline forges a log line),
    /// at most 64 characters, "?" when nothing is left.</summary>
    static string Printable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "?";
        var sb = new StringBuilder(Math.Min(text.Length, 64));
        foreach (char ch in text)
        {
            if (sb.Length >= 64) break;
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        return sb.Length == 0 ? "?" : sb.ToString();
    }

    static void Json(HttpListenerContext ctx, byte[] bytes)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }
}
