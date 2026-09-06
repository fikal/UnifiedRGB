using System.Net;
using System.Text;

namespace UnifiedRgb.Core.Games;

/*-----------------------------------------------------------*\
| Valve's Game State Integration: the game POSTs JSON to a     |
| local URL you register with a config file. This is the       |
| supported way in, so there is no memory reading, no injected |
| DLL and nothing that looks like a cheat to an anti-cheat.    |
|                                                              |
| Loopback only, always. The game runs on this machine, so     |
| there is no reason to accept a state update from anywhere    |
| else, and the token is a shared secret in a plain text file  |
| rather than anything worth trusting on a network.            |
\*-----------------------------------------------------------*/
public sealed class GsiServer : IDisposable
{
    public const int DefaultPort = 27180;

    /// <summary>How long without a POST before the game counts as gone. The
    /// config asks for a heartbeat every few seconds, so this only trips when
    /// the game has actually stopped.</summary>
    public const double SilenceSeconds = 10;

    HttpListener? _listener;
    Thread? _thread;
    volatile bool _stopping;

    long _lastPostTicks;
    GameState _state = GameState.Empty;

    public int Port { get; private set; }
    public string Token { get; private set; } = "";
    public bool Running => _listener != null;

    /// <summary>Latest state. Never null, so the render path has no branch.</summary>
    public GameState State => _state;

    /// <summary>True while the game is still posting. Goes false on its own
    /// once the game closes, which is what returns the effect to idle.</summary>
    public bool Connected =>
        _lastPostTicks != 0 &&
        (Environment.TickCount64 - Interlocked.Read(ref _lastPostTicks)) < SilenceSeconds * 1000;

    /// <summary>Seconds since the last update, or null if there has never been
    /// one. For the settings line.</summary>
    public double? SinceLastPost =>
        _lastPostTicks == 0 ? null
        : (Environment.TickCount64 - Interlocked.Read(ref _lastPostTicks)) / 1000.0;

    /// <summary>Raised on the first post after a quiet spell, so the UI can
    /// stop saying "waiting for game". Fired from the listener thread.</summary>
    public event Action? Connectedchanged;

    /// <summary>Bind, stepping to the next port if one is taken. Returns the
    /// port, or 0 if none was free. The token is the caller's, because the same
    /// string has to go in the config file the game reads.</summary>
    public int Start(string token, int preferredPort = DefaultPort)
    {
        if (_listener != null) return Port;
        Token = token ?? "";

        for (int port = preferredPort; port < preferredPort + 8; port++)
        {
            try
            {
                var listener = new HttpListener();
                // "localhost" rather than "+": a wildcard prefix needs an
                // elevated URL reservation and would accept from the network.
                listener.Prefixes.Add($"http://localhost:{port}/");
                listener.Start();
                _listener = listener;
                Port = port;
                break;
            }
            catch (HttpListenerException) { /* taken: try the next */ }
            catch (ObjectDisposedException) { }
        }
        if (_listener == null)
        {
            Log.Warn("gsi", $"no free port from {preferredPort}");
            return 0;
        }

        _stopping = false;
        _thread = new Thread(Loop) { IsBackground = true, Name = "gsi" };
        _thread.Start();
        Log.Info("gsi", $"listening on http://localhost:{Port}/");
        return Port;
    }

    public void Stop()
    {
        _stopping = true;
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        _lastPostTicks = 0;
        _state = GameState.Empty;
    }

    public void Dispose() => Stop();

    void Loop()
    {
        var listener = _listener;
        while (!_stopping && listener != null)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; }          // stopped
            try { Handle(ctx); }
            catch (Exception ex) { Log.Occasional("gsi-handle", "gsi", $"update failed: {ex.Message}"); }
        }
    }

    void Handle(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = reader.ReadToEnd();

        // The game does not read the response, but it does wait for one, and
        // its timeout is the frame budget it is willing to spend. Answer first
        // and think afterwards.
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentLength64 = 0;
        try { ctx.Response.Close(); } catch { }

        var parsed = GsiParser.Parse(body, Token);
        if (parsed == null)
        {
            // A wrong token is worth saying once: it is the difference between
            // "the game is not running" and "the config file is stale".
            Log.Occasional("gsi-reject", "gsi", "an update was rejected (bad token or unreadable JSON)");
            return;
        }

        bool wasConnected = Connected;
        _state = parsed;
        Interlocked.Exchange(ref _lastPostTicks, Math.Max(1, Environment.TickCount64));
        if (!wasConnected)
        {
            // The difference between "your config is wrong" and "you have not
            // started the game yet" is worth one line.
            Log.Info("gsi", "game connected");
            Connectedchanged?.Invoke();
        }
    }

    /// <summary>A token for a fresh install. Not a secret worth defending, but
    /// it should not be the same on every machine either: it is the only thing
    /// stopping another local program posting fake states.</summary>
    public static string NewToken() => Guid.NewGuid().ToString("N")[..16];
}
