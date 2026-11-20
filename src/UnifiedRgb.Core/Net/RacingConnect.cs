using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace UnifiedRgb.Core.Net;

/*-----------------------------------------------------------*\
| Connect to whichever of a host's addresses answers first.   |
|                                                             |
| .NET tries a host's addresses one at a time, in DNS order,  |
| and an address that drops the connection silently costs the |
| full OS connect timeout (21 s on Windows) before the next   |
| one is tried. Found 11/20/2026: GitHub's release download   |
| host has four IPv4 addresses and from one network three of  |
| them never answered, so the update's checksum fetch and its |
| download each sat 21 s or more on "Preparing" with nothing  |
| happening, while a browser on the same machine was instant. |
| Browsers race the addresses (Happy Eyeballs, RFC 8305): this |
| is that, simplified. The first address starts at once, each |
| next one a quarter second later or as soon as an attempt    |
| fails, families interleaved, and the first to connect wins. |
\*-----------------------------------------------------------*/
public static class RacingConnect
{
    /// <summary>How long one attempt gets alone before the next joins it.</summary>
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250);

    /// <summary>An HTTP handler that connects this way. Everything else is the
    /// default handler's: redirects, the system proxy, decompression off.</summary>
    public static SocketsHttpHandler Handler() => new()
    {
        ConnectCallback = (ctx, ct) => ConnectAsync(ctx.DnsEndPoint, ct),
        // Re-resolve now and then, so a long-lived client does not hold on to
        // a connection to an address that has since gone bad.
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    };

    public static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(endPoint.Host, out var literal)
            ? new[] { literal }
            : await Dns.GetHostAddressesAsync(endPoint.Host, ct).ConfigureAwait(false);
        var socket = await ConnectAsync(addresses, endPoint.Port, ct).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    /// <summary>The race itself, over addresses already resolved. Internal for
    /// the harness. Throws the last failure when no address connects.</summary>
    internal static async Task<Socket> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct)
    {
        if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);
        var order = Interleave(addresses);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<Socket>>();
        Exception? lastFailure = null;
        int next = 0;
        try
        {
            while (next < order.Count || pending.Count > 0)
            {
                if (next < order.Count) pending.Add(AttemptAsync(order[next++], port, stop.Token));
                // Until the next address is due (or, with none left, until one
                // of the attempts in flight ends).
                var due = Task.Delay(next < order.Count ? Stagger : Timeout.InfiniteTimeSpan, stop.Token);
                while (pending.Count > 0)
                {
                    var done = await Task.WhenAny(pending.Cast<Task>().Append(due)).ConfigureAwait(false);
                    if (done == due) break;   // the next address joins the race
                    var attempt = (Task<Socket>)done;
                    pending.Remove(attempt);
                    if (attempt.IsCompletedSuccessfully)
                    {
                        // The losers: cancelled below; one that connected in
                        // the same instant is closed rather than leaked.
                        foreach (var loser in pending)
                            _ = loser.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); },
                                                   TaskScheduler.Default);
                        return attempt.Result;
                    }
                    lastFailure = attempt.Exception?.GetBaseException() ?? lastFailure;
                    if (next < order.Count) break;   // a failure frees a slot: start the next at once
                }
                ct.ThrowIfCancellationRequested();
            }
        }
        finally { stop.Cancel(); }
        throw lastFailure ?? new SocketException((int)SocketError.TimedOut);
    }

    static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>IPv6 and IPv4 alternating, each family in DNS order (RFC 8305
    /// section 4), so a network with one broken family still reaches the other
    /// within a stagger.</summary>
    internal static List<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        var v6 = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily == AddressFamily.InterNetworkV6));
        var v4 = new Queue<IPAddress>(addresses.Where(a => a.AddressFamily != AddressFamily.InterNetworkV6));
        var order = new List<IPAddress>(addresses.Count);
        while (v6.Count > 0 || v4.Count > 0)
        {
            if (v6.Count > 0) order.Add(v6.Dequeue());
            if (v4.Count > 0) order.Add(v4.Dequeue());
        }
        return order;
    }
}
