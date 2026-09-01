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
    static int _session;
    static readonly object _lock = new();

    static readonly byte[] RespResult0 = Encoding.UTF8.GetBytes("{\"result\":0}");
    static readonly byte[] RespTick = Encoding.UTF8.GetBytes("{\"tick\":1}");

    public static void Start()
    {
        if (_listener != null) return;
        lock (_lock)
        {
            if (_listener != null) return;
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add($"http://localhost:{Port}/");
                l.Start();
                _listener = l;
                new Thread(() => Loop(l)) { IsBackground = true, Name = "chroma-rest" }.Start();
                Log.Info("chroma", $"REST server listening on :{Port}");
            }
            catch (Exception ex)
            {
                // Port busy = Razer Central already owns it (then Razer's own SDK
                // handles games; our shim/pipe still covers Wallpaper Engine).
                Log.Warn("chroma", $"REST server not started ({ex.Message}) - Razer Central may own :{Port}");
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
            try { l.Stop(); l.Close(); } catch { }
            Log.Info("chroma", "REST server stopped");
        }
    }

    static void Loop(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = l.GetContext(); }
            catch { break; }
            // Off-thread: a slow frame parse must not delay the next accept.
            ThreadPool.QueueUserWorkItem(static c =>
            {
                var context = (HttpListenerContext)c!;
                try { Handle(context); }
                catch (Exception ex)
                {
                    Log.Occasional("chroma", "rest", $"handle: {ex.Message}");
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
        if (req.HttpMethod == "POST" && path.EndsWith("/razer/chromasdk"))
        {
            int id = Interlocked.Increment(ref _session);
            Log.Info("chroma", $"REST init: session {id} ({AppTitle(body, bodyLen)})");
            Json(ctx, Encoding.UTF8.GetBytes(
                $"{{\"sessionid\":{id},\"uri\":\"http://localhost:{Port}/razer/chromasdk/{id}\"}}"));
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
            if (seg.Length >= 2 && body != null) TryApply(body, bodyLen);
            Json(ctx, RespResult0);
            return;
        }

        Json(ctx, RespResult0);
    }

    /// <summary>Parse a device effect and push its colors to the shared grid. The
    /// keyboard's CHROMA_CUSTOM 6x22 grid is the richest; STATIC fills one color.</summary>
    static void TryApply(byte[] body, int len)
    {
        try
        {
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(body, 0, len));
            var root = doc.RootElement;
            string effect = root.TryGetProperty("effect", out var e) ? e.GetString() ?? "" : "";

            if (effect.Contains("CUSTOM", StringComparison.OrdinalIgnoreCase) && root.TryGetProperty("param", out var param))
            {
                // param is either a 2D int array, or { color: [[...]], key: [[...]] }.
                var rows = param.ValueKind == JsonValueKind.Object && param.TryGetProperty("color", out var col) ? col : param;
                if (rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 && rows[0].ValueKind == JsonValueKind.Array)
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
                        ChromaFeed.PushGrid(grid, r, c);
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
                ChromaFeed.PushGrid(new[] { FromChroma(color) }, 1, 1);
                return;
            }

            // NONE / unrecognised: mark connected with black so the effect stops
            // showing its "waiting" breath while a host is clearly present.
            ChromaFeed.PushGrid(new[] { new Rgb(0, 0, 0) }, 1, 1);
        }
        catch { }
    }

    // Chroma color int = COLORREF 0x00BBGGRR (R low byte).
    static Rgb FromChroma(int c) => new((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));

    static string AppTitle(byte[]? body, int len)
    {
        if (body == null || len == 0) return "?";
        try
        {
            using var d = JsonDocument.Parse(new ReadOnlyMemory<byte>(body, 0, len));
            return d.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "?" : "?";
        }
        catch { return "?"; }
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
