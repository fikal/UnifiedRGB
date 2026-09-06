namespace UnifiedRgb.Core.Net;

/// <summary>Who is driving which device while an SDK client is connected.
///
/// The rule is deliberately blunt: writing to a device claims it, and the claim
/// lapses when the writer goes quiet or disconnects. There is no locking or
/// negotiation, because the clients this serves (a Home Assistant integration,
/// a game mod, a Stream Deck button) do not have a way to ask for one and would
/// not release it if they crashed.
///
/// The clock is injected so the whole thing is testable without waiting five
/// seconds per case.</summary>
public sealed class ExternalOwnership<TDevice> where TDevice : notnull
{
    /// <summary>How long a claim survives with no further writes. Long enough
    /// that a client updating a few times a second keeps it, short enough that
    /// a crashed one gives the lights back while the user is still wondering.</summary>
    public double SilenceSeconds { get; }

    readonly Dictionary<TDevice, (int Client, double At)> _owners = new();

    public ExternalOwnership(double silenceSeconds = 5) => SilenceSeconds = silenceSeconds;

    public int Count => _owners.Count;
    public bool IsOwned(TDevice device) => _owners.ContainsKey(device);
    public int? OwnerOf(TDevice device) => _owners.TryGetValue(device, out var o) ? o.Client : null;

    /// <summary>A client wrote to a device. True the first time, which is the
    /// caller's cue to save the user's lighting and stop its own effects on it;
    /// later writes just push the deadline out.</summary>
    public bool Claim(TDevice device, int client, double now)
    {
        // A second client writing to a device takes it over rather than being
        // refused: last writer wins is at least predictable, and the protocol
        // has no way to tell the loser anything anyway.
        bool isNew = !_owners.TryGetValue(device, out var held) || held.Client != client;
        _owners[device] = (client, now);
        return isNew;
    }

    /// <summary>Devices whose owner has gone quiet, released as a side effect.
    /// The caller restores each one.</summary>
    public List<TDevice> Expire(double now)
    {
        var done = new List<TDevice>();
        foreach (var (device, held) in _owners)
            if (now - held.At >= SilenceSeconds) done.Add(device);
        foreach (var d in done) _owners.Remove(d);
        return done;
    }

    /// <summary>A client disconnected: everything it held is released at once,
    /// without waiting out the silence timer.</summary>
    public List<TDevice> ReleaseClient(int client)
    {
        var done = new List<TDevice>();
        foreach (var (device, held) in _owners)
            if (held.Client == client) done.Add(device);
        foreach (var d in done) _owners.Remove(d);
        return done;
    }

    /// <summary>Everything, for shutdown and for a rescan that replaces the
    /// device instances these keys refer to.</summary>
    public List<TDevice> ReleaseAll()
    {
        var done = new List<TDevice>(_owners.Keys);
        _owners.Clear();
        return done;
    }
}
