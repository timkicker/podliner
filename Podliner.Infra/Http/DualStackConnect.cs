using System.Net;
using System.Net.Sockets;

namespace Podliner.Infra.Http;

// Connecting the way curl and browsers do (RFC 8305, "Happy Eyeballs").
//
// Where IPv6 is broken, a connect to a host's IPv6 address hangs until the
// kernel gives up, minutes later. .NET tries a host's addresses one after
// another, so every request ran into its 30s timeout: changelog.com,
// Megaphone and Buzzsprout feeds all failed on a machine where curl fetched
// them in 4s. Here the next address, of the other family where there is
// one, starts after a short wait, and the first connection that stands
// wins. Every HttpClient podliner builds goes through Handler().
public static class DualStackConnect
{
    static readonly TimeSpan DefaultStagger = TimeSpan.FromMilliseconds(250);

    // A SocketsHttpHandler that connects this way; configure sets the rest.
    public static SocketsHttpHandler Handler(Action<SocketsHttpHandler>? configure = null)
    {
        var h = new SocketsHttpHandler
        {
            ConnectCallback = async (ctx, ct) =>
            {
                var host = ctx.DnsEndPoint.Host;
                var addrs = IPAddress.TryParse(host, out var literal)
                    ? new[] { literal }
                    : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                var socket = await ConnectAsync(Interleave(addrs),
                    (a, c) => ConnectOne(a, ctx.DnsEndPoint.Port, c), DefaultStagger, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        configure?.Invoke(h);
        return h;
    }

    // Start with the first address; every `stagger`, or at once when an
    // attempt fails, start the next. The first to connect wins and the
    // others are called off.
    internal static async Task<Socket> ConnectAsync(
        IReadOnlyList<IPAddress> addrs,
        Func<IPAddress, CancellationToken, Task<Socket>> connect,
        TimeSpan stagger,
        CancellationToken ct)
    {
        if (addrs.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<Socket>>();
        Exception? last = null;
        int next = 0;
        bool startNext = true;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (startNext && next < addrs.Count)
                    pending.Add(connect(addrs[next++], cts.Token));
                startNext = false;

                if (pending.Count == 0)
                    throw last ?? new SocketException((int)SocketError.HostUnreachable);

                var wait = next < addrs.Count
                    ? Task.Delay(stagger, cts.Token)
                    : Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
                var done = await Task.WhenAny(pending.Append<Task>(wait)).ConfigureAwait(false);

                if (done == wait)
                {
                    startNext = true;
                    continue;
                }

                var attempt = (Task<Socket>)done;
                pending.Remove(attempt);
                if (attempt.IsCompletedSuccessfully)
                    return attempt.Result;

                last = attempt.Exception?.InnerException ?? last;
                startNext = true;
            }
        }
        finally
        {
            cts.Cancel();
            // a loser that connected anyway must not leak its socket
            foreach (var p in pending)
                _ = p.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); },
                                   TaskScheduler.Default);
        }
    }

    // Families take turns, starting with whichever the resolver put first.
    internal static IReadOnlyList<IPAddress> Interleave(IReadOnlyList<IPAddress> addrs)
    {
        if (addrs.Count < 2) return addrs;
        var first = addrs[0].AddressFamily;
        var a = addrs.Where(x => x.AddressFamily == first).ToList();
        var b = addrs.Where(x => x.AddressFamily != first).ToList();
        var result = new List<IPAddress>(addrs.Count);
        for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count) result.Add(a[i]);
            if (i < b.Count) result.Add(b[i]);
        }
        return result;
    }

    static async Task<Socket> ConnectOne(IPAddress addr, int port, CancellationToken ct)
    {
        var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await s.ConnectAsync(new IPEndPoint(addr, port), ct).ConfigureAwait(false);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }
}
