using System.Net;
using System.Net.Sockets;
using System.Text;

namespace UnifiedRgb.Core.Net;

/// <summary>What the server needs from the app to answer clients. Keeps the
/// socket code in Core and the lighting decisions in the app, and makes the
/// whole server testable against a stub.</summary>
public interface IOpenRgbHost
{
    /// <summary>The devices to expose, in a stable order: a client addresses
    /// them by index and will keep using an index across packets.</summary>
    IReadOnlyList<IRgbDevice> Devices { get; }

    /// <summary>What a device is showing now, for the device blob.</summary>
    IReadOnlyList<Rgb> ColorsOf(IRgbDevice device);

    /// <summary>A client has taken this device: save what the user had and get
    /// our own effects off it.</summary>
    void BeginExternal(IRgbDevice device);

    /// <summary>Paint a client's frame. offset is where in the device the
    /// colours start, which is how a zone write is expressed.</summary>
    void PushExternal(IRgbDevice device, int offset, IReadOnlyList<Rgb> colors);

    /// <summary>The client is gone: put the user's lighting back.</summary>
    void EndExternal(IRgbDevice device);
}

/*-----------------------------------------------------------*\
| OpenRGB SDK server: lets anything written for OpenRGB drive  |
| our devices. Home Assistant, game mods, phone remotes and    |
| Stream Deck plugins all speak this and need no changes.      |
|                                                              |
| Loopback only unless the user opts in, because the protocol  |
| has no authentication whatsoever: whoever can reach the port |
| owns the lights.                                             |
|                                                              |
| One thread per client, blocking reads. Clients here number   |
| in the ones, and a thread parked on Read costs a stack; the  |
| alternative buys nothing and is harder to reason about.      |
\*-----------------------------------------------------------*/
public sealed class OpenRgbServer : IDisposable
{
    /// <summary>The port the ecosystem expects.</summary>
    public const int DefaultPort = 6742;

    /// <summary>Where we go when the bundled OpenRGB server already owns the
    /// default port.</summary>
    public const int AlternatePort = 6743;

    readonly IOpenRgbHost _host;

    /// <summary>Claims. Touched from every client thread and the sweep timer,
    /// so every access is under _gate, and host callbacks are made OUTSIDE it:
    /// restoring lighting marshals to the UI thread, and holding a lock across
    /// that is how a deadlock gets built.</summary>
    readonly ExternalOwnership<IRgbDevice> _owned;
    readonly object _gate = new();
    readonly List<Client> _clients = new();

    TcpListener? _listener;
    Thread? _accept;
    System.Threading.Timer? _sweep;
    volatile bool _stopping;
    int _nextClientId = 1;

    public int Port { get; private set; }
    public bool Running => _listener != null;

    /// <summary>Names the connected clients gave, for the settings line.</summary>
    public IReadOnlyList<string> ClientNames
    {
        get { lock (_gate) return _clients.Select(c => c.Name).ToList(); }
    }

    public int ClientCount { get { lock (_gate) return _clients.Count; } }

    /// <summary>Raised when a client connects, disconnects or names itself, so
    /// the settings line can follow along. Fired from a socket thread.</summary>
    public event Action? ClientsChanged;

    public OpenRgbServer(IOpenRgbHost host, double silenceSeconds = 5)
    {
        _host = host;
        _owned = new ExternalOwnership<IRgbDevice>(silenceSeconds);
    }

    /// <summary>Bind and start accepting. With no port given, tries the default
    /// first and then the alternate, so the bundled OpenRGB server owning 6742
    /// moves us rather than stopping us. Returns the port bound, or 0 if
    /// nothing was free.</summary>
    public int Start(bool listenOnLan, int port = 0)
    {
        if (_listener != null) return Port;
        var address = listenOnLan ? IPAddress.Any : IPAddress.Loopback;

        foreach (int candidate in port > 0 ? new[] { port } : new[] { DefaultPort, AlternatePort })
        {
            try
            {
                var listener = new TcpListener(address, candidate);
                listener.Start();
                _listener = listener;
                Port = candidate;
                break;
            }
            catch (SocketException) { /* in use: try the next */ }
        }
        if (_listener == null)
        {
            Log.Warn("orgb-server", $"ports {DefaultPort} and {AlternatePort} are both in use");
            return 0;
        }

        _stopping = false;
        _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "orgb-accept" };
        _accept.Start();
        // Claims lapse on silence, so something has to notice the silence.
        _sweep = new System.Threading.Timer(_ => SweepExpired(), null, 1000, 1000);

        Log.Info("orgb-server", $"listening on {(listenOnLan ? "0.0.0.0" : "127.0.0.1")}:{Port}");
        if (listenOnLan)
            Log.Warn("orgb-server", "listening on the LAN: this protocol has no authentication");
        return Port;
    }

    public void Stop()
    {
        _stopping = true;
        _sweep?.Dispose(); _sweep = null;
        try { _listener?.Stop(); } catch { }
        _listener = null;

        List<Client> clients;
        lock (_gate) { clients = new List<Client>(_clients); _clients.Clear(); }
        foreach (var c in clients) c.Close();

        List<IRgbDevice> held;
        lock (_gate) held = _owned.ReleaseAll();
        foreach (var device in held) SafeEnd(device);
        ClientsChanged?.Invoke();
    }

    /// <summary>Devices were re-detected: the instances clients were addressing
    /// are gone, so drop every claim and tell clients to re-read the list.</summary>
    public void DeviceListChanged()
    {
        lock (_gate) _owned.ReleaseAll();   // the old instances: nothing to restore onto
        Broadcast(OpenRgbProtocol.PktDeviceListUpdated, Array.Empty<byte>());
    }

    public void Dispose() => Stop();

    /*--- accept + client threads ---*/

    void AcceptLoop()
    {
        var listener = _listener;
        while (!_stopping && listener != null)
        {
            TcpClient tcp;
            try { tcp = listener.AcceptTcpClient(); }
            catch { break; }          // stopped, or the listener died

            var client = new Client(Interlocked.Increment(ref _nextClientId), tcp);
            lock (_gate) _clients.Add(client);
            ClientsChanged?.Invoke();

            var thread = new Thread(() => Serve(client)) { IsBackground = true, Name = "orgb-client" };
            thread.Start();
        }
    }

    void Serve(Client client)
    {
        try
        {
            client.Tcp.NoDelay = true;
            var stream = client.Tcp.GetStream();
            var header = new byte[OpenRgbProtocol.HeaderBytes];

            while (!_stopping)
            {
                if (!ReadExactly(stream, header, header.Length)) break;
                var parsed = OpenRgbProtocol.ReadHeader(header);
                if (parsed == null)
                {
                    // Not an SDK client. Something else found the port.
                    Log.Warn("orgb-server", "dropping a connection that is not speaking the protocol");
                    break;
                }
                var (deviceIndex, packetId, size) = parsed.Value;

                // A size field is attacker-controlled, and this port can be
                // opened to the LAN. Cap it well above any real packet: the
                // biggest is a full frame, four bytes per LED.
                if (size < 0 || size > 1 << 20)
                {
                    Log.Warn("orgb-server", $"dropping a client that asked for a {size} byte packet");
                    break;
                }
                var payload = size == 0 ? Array.Empty<byte>() : new byte[size];
                if (size > 0 && !ReadExactly(stream, payload, size)) break;

                Handle(client, stream, deviceIndex, packetId, payload);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            // A client going away is not news.
        }
        catch (Exception ex)
        {
            Log.Warn("orgb-server", $"client error: {ex.Message}");
        }
        finally
        {
            List<IRgbDevice> held;
            lock (_gate) { _clients.Remove(client); held = _owned.ReleaseClient(client.Id); }
            foreach (var device in held)
            {
                Log.Info("orgb-server", $"{device.Name}: '{client.Name}' disconnected, restoring your lighting");
                SafeEnd(device);
            }
            client.Close();
            ClientsChanged?.Invoke();
        }
    }

    void Handle(Client client, NetworkStream stream, uint deviceIndex, uint packetId, byte[] payload)
    {
        switch (packetId)
        {
            case OpenRgbProtocol.PktProtocolVersion:
            {
                uint theirs = payload.Length >= 4 ? BitConverter.ToUInt32(payload) : 0;
                client.Version = Math.Min(theirs, OpenRgbProtocol.MaxVersion);
                Send(stream, 0, OpenRgbProtocol.PktProtocolVersion,
                     BitConverter.GetBytes(client.Version));
                break;
            }

            case OpenRgbProtocol.PktSetClientName:
                client.Name = Encoding.ASCII.GetString(payload).TrimEnd('\0').Trim();
                if (client.Name.Length == 0) client.Name = "unnamed";
                Log.Info("orgb-server", $"client '{client.Name}' connected (protocol {client.Version})");
                ClientsChanged?.Invoke();
                break;

            case OpenRgbProtocol.PktControllerCount:
                Send(stream, 0, OpenRgbProtocol.PktControllerCount,
                     BitConverter.GetBytes(_host.Devices.Count));
                break;

            case OpenRgbProtocol.PktControllerData:
            {
                var device = DeviceAt(deviceIndex);
                if (device == null) break;
                var blob = OpenRgbProtocol.WriteDevice(device, _host.ColorsOf(device), client.Version);
                Send(stream, deviceIndex, OpenRgbProtocol.PktControllerData, blob);
                break;
            }

            case OpenRgbProtocol.PktUpdateLeds:
                ApplyWrite(client, deviceIndex, payload, zoneWrite: false);
                break;

            case OpenRgbProtocol.PktUpdateZoneLeds:
                ApplyWrite(client, deviceIndex, payload, zoneWrite: true);
                break;

            case OpenRgbProtocol.PktSetCustomMode:
                // We are always in the one mode we advertise. Clients send this
                // before writing colours, and expect no reply.
                break;

            case OpenRgbProtocol.PktResizeZone:
                // Our zones come from the hardware; a client cannot change them.
                Log.Occasional("orgb-server", "resize", "a client tried to resize a zone; ignored");
                break;
        }
    }

    /// <summary>Both LED writes, which differ only in a zone index up front.
    /// Layout: u32 payload length, [u32 zone], u16 count, then count colours.</summary>
    void ApplyWrite(Client client, uint deviceIndex, byte[] payload, bool zoneWrite)
    {
        var device = DeviceAt(deviceIndex);
        if (device == null) return;

        int o = 4;                                   // the duplicated length
        int zone = 0;
        if (zoneWrite)
        {
            if (payload.Length < o + 4) return;
            zone = BitConverter.ToInt32(payload, o); o += 4;
        }
        if (payload.Length < o + 2) return;
        int count = BitConverter.ToUInt16(payload, o); o += 2;
        if (count < 0 || payload.Length < o + count * 4) return;

        int offset = 0;
        if (zoneWrite)
        {
            var zones = OpenRgbProtocol.ZonesOf(device);
            if (zone < 0 || zone >= zones.Count) return;
            for (int i = 0; i < zone; i++) offset += zones[i].Count;
        }

        var colors = new Rgb[count];
        for (int i = 0; i < count; i++)
            colors[i] = OpenRgbProtocol.FromWire(BitConverter.ToUInt32(payload, o + i * 4));

        // Claiming has to happen before the paint, so the user's lighting is
        // saved before anything overwrites it.
        bool claimed;
        lock (_gate) claimed = _owned.Claim(device, client.Id, Now);
        if (claimed)
        {
            Log.Info("orgb-server", $"'{client.Name}' is now driving {device.Name}");
            try { _host.BeginExternal(device); }
            catch (Exception ex) { Log.Warn("orgb-server", $"begin {device.Name}: {ex.Message}"); }
        }
        try { _host.PushExternal(device, offset, colors); }
        catch (Exception ex) { Log.Warn("orgb-server", $"write {device.Name}: {ex.Message}"); }
    }

    void SweepExpired()
    {
        if (_stopping) return;
        List<IRgbDevice> lapsed;
        lock (_gate) lapsed = _owned.Expire(Now);
        foreach (var device in lapsed)
        {
            Log.Info("orgb-server", $"{device.Name}: client went quiet, restoring your lighting");
            SafeEnd(device);
        }
    }

    void SafeEnd(IRgbDevice device)
    {
        try { _host.EndExternal(device); }
        catch (Exception ex) { Log.Warn("orgb-server", $"restore {device.Name}: {ex.Message}"); }
    }

    IRgbDevice? DeviceAt(uint index)
    {
        var devices = _host.Devices;
        return index < devices.Count ? devices[(int)index] : null;
    }

    static double Now => Environment.TickCount64 / 1000.0;

    void Broadcast(uint packetId, byte[] payload)
    {
        List<Client> clients;
        lock (_gate) clients = new List<Client>(_clients);
        foreach (var c in clients)
        {
            try { Send(c.Tcp.GetStream(), 0, packetId, payload); }
            catch { /* it will fall out of the list on its own thread */ }
        }
    }

    static void Send(NetworkStream stream, uint device, uint packetId, byte[] payload)
    {
        var buf = new byte[OpenRgbProtocol.HeaderBytes + payload.Length];
        OpenRgbProtocol.WriteHeader(buf, device, packetId, payload.Length);
        payload.CopyTo(buf, OpenRgbProtocol.HeaderBytes);
        // One write: on a NoDelay socket a header/payload pair goes out as two
        // segments per packet.
        lock (stream) stream.Write(buf, 0, buf.Length);
    }

    static bool ReadExactly(NetworkStream stream, byte[] buf, int count)
    {
        int got = 0;
        while (got < count)
        {
            int n = stream.Read(buf, got, count - got);
            if (n <= 0) return false;
            got += n;
        }
        return true;
    }

    sealed class Client
    {
        public Client(int id, TcpClient tcp) { Id = id; Tcp = tcp; }
        public int Id { get; }
        public TcpClient Tcp { get; }
        public string Name = "unnamed";
        public uint Version;
        public void Close() { try { Tcp.Close(); } catch { } }
    }
}
