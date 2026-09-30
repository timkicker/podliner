using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Podliner.Infra.Http;
using Xunit;

namespace Podliner.Infra.Tests.Http;

// Where IPv6 is broken, connecting to a host's IPv6 address hangs until the
// kernel gives up. .NET tried the addresses one after another, so every
// request ran into the 30s timeout: changelog.com, Megaphone and Buzzsprout
// feeds all failed on a machine where curl got them in 4s, because curl
// starts the next address after a short wait (RFC 8305, "Happy Eyeballs").
public sealed class DualStackConnectTests
{
    static readonly IPAddress V6 = IPAddress.Parse("2001:db8::1");
    static readonly IPAddress V4 = IPAddress.Parse("192.0.2.1");
    static readonly IPAddress V4b = IPAddress.Parse("192.0.2.2");

    sealed class FakeConnect
    {
        public readonly Dictionary<IPAddress, TimeSpan?> Delay = new();   // null: never connects
        public readonly Dictionary<IPAddress, bool> Fails = new();
        public readonly List<IPAddress> Started = new();
        public readonly List<IPAddress> Cancelled = new();

        public async Task<Socket> Connect(IPAddress a, CancellationToken ct)
        {
            lock (Started) Started.Add(a);
            try
            {
                await Task.Delay(Delay.GetValueOrDefault(a) ?? Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                lock (Cancelled) Cancelled.Add(a);
                throw;
            }
            if (Fails.GetValueOrDefault(a)) throw new SocketException((int)SocketError.ConnectionRefused);
            return new Socket(a.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { };
        }
    }

    static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task A_hanging_ipv6_address_does_not_hold_up_ipv4()
    {
        var f = new FakeConnect();
        f.Delay[V6] = null;
        f.Delay[V4] = TimeSpan.FromMilliseconds(10);

        var sw = Stopwatch.StartNew();
        using var s = await DualStackConnect.ConnectAsync(new[] { V6, V4 }, f.Connect, Stagger, default);

        // v6 never connects: coming back at all means v4 won. The bound only
        // leaves room for a starved thread pool (3.5s seen on two cores).
        s.AddressFamily.Should().Be(AddressFamily.InterNetwork);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
        SpinWait.SpinUntil(() => { lock (f.Cancelled) return f.Cancelled.Contains(V6); }, 5000);
        f.Cancelled.Should().Contain(V6, "the losing attempt is called off");
    }

    [Fact]
    public async Task A_working_first_address_is_used_without_trying_others()
    {
        var f = new FakeConnect();
        f.Delay[V6] = TimeSpan.FromMilliseconds(5);

        // a stagger far above the 5 ms, so a busy machine cannot start v4
        using var s = await DualStackConnect.ConnectAsync(new[] { V6, V4 }, f.Connect, TimeSpan.FromSeconds(2), default);

        s.AddressFamily.Should().Be(AddressFamily.InterNetworkV6);
        f.Started.Should().Equal(V6);
    }

    [Fact]
    public async Task A_refused_address_moves_on_at_once()
    {
        var f = new FakeConnect();
        f.Delay[V6] = TimeSpan.Zero; f.Fails[V6] = true;
        f.Delay[V4] = TimeSpan.FromMilliseconds(5);

        var sw = Stopwatch.StartNew();
        using var s = await DualStackConnect.ConnectAsync(new[] { V6, V4 }, f.Connect, TimeSpan.FromSeconds(60), default);

        s.AddressFamily.Should().Be(AddressFamily.InterNetwork);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "a refusal does not wait for the 60s stagger");
    }

    [Fact]
    public async Task When_every_address_fails_the_error_comes_through()
    {
        var f = new FakeConnect();
        f.Delay[V6] = TimeSpan.Zero; f.Fails[V6] = true;
        f.Delay[V4] = TimeSpan.Zero; f.Fails[V4] = true;

        var act = () => DualStackConnect.ConnectAsync(new[] { V6, V4 }, f.Connect, Stagger, default);

        await act.Should().ThrowAsync<SocketException>();
    }

    [Fact]
    public async Task Cancelling_stops_a_connect_that_hangs_everywhere()
    {
        var f = new FakeConnect();
        f.Delay[V6] = null; f.Delay[V4] = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var act = () => DualStackConnect.ConnectAsync(new[] { V6, V4 }, f.Connect, Stagger, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Families_take_turns_starting_with_the_first()
    {
        var order = DualStackConnect.Interleave(new[]
        {
            V6, IPAddress.Parse("2001:db8::2"), V4, V4b,
        });

        order.Should().Equal(V6, V4, IPAddress.Parse("2001:db8::2"), V4b);
    }

    [Fact]
    public async Task A_real_handler_reaches_a_local_server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            var st = c.GetStream();
            var buf = new byte[4096];
            await st.ReadAsync(buf);
            var body = "ok"u8.ToArray();
            await st.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"));
            await st.WriteAsync(body);
        });

        using var http = new HttpClient(DualStackConnect.Handler());
        (await http.GetStringAsync($"http://localhost:{port}/")).Should().Be("ok");
        listener.Stop();
    }
}
