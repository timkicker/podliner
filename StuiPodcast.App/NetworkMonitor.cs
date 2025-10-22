using System.Net.NetworkInformation;
using Serilog;
using StuiPodcast.App.UI;
using StuiPodcast.Core;
using Terminal.Gui;

namespace StuiPodcast.App;

// ==========================================================
// Network monitor (probing + hysteresis)
// ==========================================================
sealed class NetworkMonitor
{
    readonly AppData _data;
    readonly Shell _ui;
    readonly Func<Task> _saveAsync;

    static readonly HttpClient _probeHttp = new() { Timeout = TimeSpan.FromMilliseconds(1200) };

    volatile bool _probeRunning = false;
    int _ok = 0, _fail = 0;
    const int FAILS_FOR_OFFLINE = 4;
    const int SUCC_FOR_ONLINE   = 3;

    DateTimeOffset _lastFlip = DateTimeOffset.MinValue;
    static readonly TimeSpan _minDwell = TimeSpan.FromSeconds(15);

    DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    static readonly TimeSpan _heartbeatEvery = TimeSpan.FromMinutes(2);

    public NetworkMonitor(AppData data, Shell ui, Func<Task> saveAsync)
    {
        _data = data;
        _ui = ui;
        _saveAsync = saveAsync;
    }

    public void Start(out object? timerToken)
    {
        // first quick probe
        _ = Task.Run(async () =>
        {
            bool online = await QuickNetCheckAsync();
            _ok   = online ? 1 : 0;
            _fail = online ? 0 : 1;
            _lastFlip = DateTimeOffset.UtcNow;
            OnNetworkChanged(online);
        });

        try
        {
            NetworkChange.NetworkAvailabilityChanged += (s, e) => { TriggerProbe(); };
        } catch { }

        timerToken = Application.MainLoop.AddTimeout(NetProbeInterval(), _ =>
        {
            TriggerProbe();
            Application.MainLoop.AddTimeout(NetProbeInterval(), __ =>
            {
                TriggerProbe();
                return true;
            });
            return false;
        });
    }

    public static TimeSpan NetProbeInterval(AppData? data = null)
        => (data?.NetworkOnline ?? true) ? TimeSpan.FromSeconds(12) : TimeSpan.FromSeconds(5);

    void TriggerProbe()
    {
        if (_probeRunning) return;
        _probeRunning = true;

        _ = Task.Run(async () =>
        {
            try
            {
                var probeOnline = await QuickNetCheckAsync().ConfigureAwait(false);

                if (probeOnline) { _ok++;  _fail = 0; }
                else             { _fail++; _ok   = 0; }

                bool state   = _data.NetworkOnline;
                bool dwellOk = (DateTimeOffset.UtcNow - _lastFlip) >= _minDwell;

                bool flipToOn  = !state && probeOnline && _ok   >= SUCC_FOR_ONLINE && dwellOk;
                bool flipToOff =  state && !probeOnline && _fail >= FAILS_FOR_OFFLINE && dwellOk;

                Log.Information("net/decision prev={Prev} probe={Probe} ok={Ok} fail={Fail} dwellOk={DwellOk} flipOn={FlipOn} flipOff={FlipOff}",
                    state ? "online" : "offline",
                    probeOnline ? "online" : "offline",
                    _ok, _fail, dwellOk,
                    flipToOn, flipToOff);

                if (flipToOn || flipToOff)
                {
                    _lastFlip = DateTimeOffset.UtcNow;
                    LogNicsSnapshot();
                    Log.Information("net/state change → {State}", flipToOn ? "online" : "offline");
                    OnNetworkChanged(flipToOn);
                }
                else
                {
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastHeartbeat >= _heartbeatEvery)
                    {
                        _lastHeartbeat = now;
                        Log.Debug("net/steady state={State} ok={Ok} fail={Fail}",
                            _data.NetworkOnline ? "online" : "offline", _ok, _fail);
                    }
                }
            }
            finally { _probeRunning = false; }
        });
    }

    void OnNetworkChanged(bool online)
    {
        _data.NetworkOnline = online;

        Application.MainLoop?.Invoke(() =>
        {
            if (_ui == null) return;

            CommandRouter.ApplyList(_ui, _data);
            _ui.RefreshEpisodesForSelectedFeed(_data.Episodes);

            var nowId = _ui.GetNowPlayingId();
            if (nowId != null)
            {
                var ep = _data.Episodes.FirstOrDefault(x => x.Id == nowId);
                if (ep != null)
                    _ui.SetWindowTitle((!_data.NetworkOnline ? "[OFFLINE] " : "") + (ep.Title ?? "—"));
            }

            if (!online) _ui.ShowOsd("net: offline", 800);
        });

        _ = _saveAsync();
    }

    static async Task<bool> QuickNetCheckAsync()
    {
        bool anyUp = false;
        try { anyUp = NetworkInterface.GetIsNetworkAvailable(); Log.Verbose("net/probe nics-available={Avail}", anyUp); } catch { }

        var tcpOk =
            await TcpCheckAsync("1.1.1.1", 443, 900).ConfigureAwait(false) ||
            await TcpCheckAsync("8.8.8.8", 53, 900).ConfigureAwait(false);

        var httpOk =
            await HttpProbeAsync("http://connectivitycheck.gstatic.com/generate_204").ConfigureAwait(false) ||
            await HttpProbeAsync("http://www.msftconnecttest.com/connecttest.txt").ConfigureAwait(false);

        Log.Verbose("net/probe result tcp={TcpOk} http={HttpOk} anyNicUp={NicUp}",
            tcpOk, httpOk, anyUp);

        return (tcpOk || httpOk) && anyUp;
    }

    static async Task<bool> TcpCheckAsync(string hostOrIp, int port, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var sock = new System.Net.Sockets.Socket(
                System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream,
                System.Net.Sockets.ProtocolType.Tcp);
            var start = DateTime.UtcNow;
            await sock.ConnectAsync(hostOrIp, port, cts.Token).ConfigureAwait(false);
            var ms = (int)(DateTime.UtcNow - start).TotalMilliseconds;
            Log.Verbose("net/probe tcp ok {Host}:{Port} in {Ms}ms", hostOrIp, port, ms);
            return true;
        }
        catch (Exception ex)
        {
            Log.Verbose(ex, "net/probe tcp fail {Host}:{Port}", hostOrIp, port);
            return false;
        }
    }

    static async Task<bool> HttpProbeAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _probeHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            Log.Verbose("net/probe http {Url} {Code}", url, (int)resp.StatusCode);
            return ((int)resp.StatusCode) is >= 200 and < 400;
        }
        catch (Exception exHead)
        {
            try
            {
                using var resp = await _probeHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                Log.Verbose("net/probe http[GET] {Url} {Code} (HEAD failed: {Err})", url, (int)resp.StatusCode, exHead.GetType().Name);
                return ((int)resp.StatusCode) is >= 200 and < 400;
            }
            catch (Exception ex)
            {
                Log.Verbose(ex, "net/probe http fail {Url}", url);
                return false;
            }
        }
    }

    static void LogNicsSnapshot()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ip = nic.GetIPProperties();
                var ipv4 = string.Join(",", ip.UnicastAddresses
                                         .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                         .Select(a => a.Address.ToString()));
                var gw   = string.Join(",", ip.GatewayAddresses.Select(g => g.Address?.ToString() ?? ""));
                var dns  = string.Join(",", ip.DnsAddresses.Select(d => d.ToString()));
                Log.Information("net/nic name={Name} type={Type} op={Op} spd={Speed} ipv4=[{IPv4}] gw=[{Gw}] dns=[{Dns}]",
                    nic.Name, nic.NetworkInterfaceType, nic.OperationalStatus, nic.Speed, ipv4, gw, dns);
            }
        } catch (Exception ex) { Log.Debug(ex, "net/nic snapshot failed"); }
    }
}