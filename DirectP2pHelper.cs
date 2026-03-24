using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Via;

/// <summary>
/// Manages a single direct P2P transfer session: starts a local croc relay,
/// opens router ports via UPnP/NAT-PMP, adds a temporary Windows Firewall rule,
/// and cleans up everything on Dispose.
/// All encryption (PAKE + ECDH + AES-256-GCM) remains intact — croc handles
/// it at the protocol level regardless of which relay is used.
/// </summary>
internal sealed class DirectP2pHelper : IDisposable
{
    public const int RelayBasePort = 9009;
    private static readonly int[] RelayPorts = [9009, 9010, 9011, 9012, 9013];
    private const string FirewallRuleName = "VIA x4 Direct P2P";

    // ── Logging ──────────────────────────────────────────────────────────────
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VIA x4", "logs");
    private static readonly string LogFile = Path.Combine(LogDir, "via.log");
    private static readonly object _logLock = new();
    private static void Log(string msg)
    {
        try
        {
            if (!Directory.Exists(LogDir)) Directory.CreateDirectory(LogDir);
            lock (_logLock) File.AppendAllText(LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [P2P] {msg}\n");
        }
        catch { }
    }

    private readonly string _crocPath;
    private Process? _relayProc;

    // UPnP state — service type MUST match what was discovered so SOAP action headers are correct
    private string? _upnpControlUrl;
    private string? _upnpServiceType;
    private readonly List<int> _upnpMappedPorts = new();

    // NAT-PMP state
    private IPAddress? _natPmpGateway;
    private readonly List<int> _natPmpMappedPorts = new();

    private bool _firewallRuleAdded;
    private volatile bool _disposed;
    private bool _monitoringNetwork;
    private System.Threading.Timer? _leaseRenewalTimer;
    private CancellationTokenSource? _renewalCts;

    // ── Public properties ─────────────────────────────────────────────────────

    /// <summary>Public IP of the sender. May be a LAN IP if port mapping failed.</summary>
    public string PublicIp { get; private set; } = "";

    /// <summary>How the public IP was resolved: "UPnP", "NAT-PMP", or "LAN".</summary>
    public string IpSource { get; private set; } = "";

    /// <summary>True when port mapping failed — relay is reachable on LAN only.</summary>
    public bool IsLanOnly { get; private set; }

    /// <summary>True when the external IP is non-routable (CGNAT, double-NAT, private).</summary>
    public bool IsBehindCgnat { get; private set; }

    /// <summary>True when a VPN adapter was detected active during setup.</summary>
    public bool VpnDetected { get; private set; }

    /// <summary>True when the primary connection is WiFi and port mapping failed — likely guest/public/hotel network.</summary>
    public bool RestrictedNetwork { get; private set; }

    /// <summary>True when the Windows Firewall rule could not be added (requires elevation).</summary>
    public bool FirewallRuleFailed { get; private set; }

    /// <summary>True when IPv6 is being used as the connection method (globally routable, no port mapping needed).</summary>
    public bool UsingIpv6 { get; private set; }

    /// <summary>Human-readable summary of what happened during setup.</summary>
    public string SetupSummary { get; private set; } = "";

    /// <summary>Fired when the network address changes during an active transfer.</summary>
    public event EventHandler? NetworkChanged;

    /// <summary>Fired when the local relay process exits unexpectedly mid-transfer.</summary>
    public event EventHandler? RelayDied;

    public DirectP2pHelper(string crocPath) => _crocPath = crocPath;

    // ── Static pre-checks ─────────────────────────────────────────────────────

    /// <summary>Returns false if there is no IPv4 gateway (IPv6-only / no internet).</summary>
    public static bool HasIpv4Gateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up &&
                            i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Any(i => i.GetIPProperties().GatewayAddresses
                    .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                              !g.Address.Equals(IPAddress.Any)));
        }
        catch { return false; }
    }

    // ── Setup ────────────────────────────────────────────────────────────────

    public async Task<string?> SetupAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        // Pre-check: do we have an IPv4 gateway or IPv6 connectivity?
        if (!HasIpv4Gateway() && GetPublicIpv6() == null)
        {
            Log("No IPv4 gateway or IPv6 address found — Direct P2P requires network connectivity");
            return "No network gateway found. Direct P2P requires an IPv4 or IPv6 internet connection.";
        }

        // Detect VPN (warn but don't abort — may still work on LAN)
        VpnDetected = DetectVpn();
        if (VpnDetected) Log("Warning: VPN adapter detected — Direct P2P may not work as expected");

        // 1. Start local relay
        Log("Starting local croc relay...");
        var relayErr = await StartRelayAsync(ct);
        if (relayErr != null) { Log($"Relay FAILED: {relayErr}"); return relayErr; }
        Log($"Relay ready on port {RelayBasePort} ({sw.ElapsedMilliseconds}ms)");

        // 2. Firewall rule (delete stale first)
        AddFirewallRule();

        // 3. Local IP (routing-table based — picks correct interface)
        var localIp = GetLocalIp();
        var gateway = GetDefaultGateway();
        Log($"Local IP: {localIp}  Gateway: {gateway}");

        // 4. Port mapping — UPnP, NAT-PMP, and PCP in parallel
        var upnpTask   = TryUpnpAsync(localIp, gateway, ct);
        var natPmpTask = TryNatPmpAsync(gateway, ct);
        var pcpTask    = TryPcpAsync(gateway, localIp, ct);
        await Task.WhenAll(upnpTask, natPmpTask, pcpTask);

        // 5. Start lease renewal timer if any ports were mapped (covers UPnP, NAT-PMP, and PCP)
        if (_upnpMappedPorts.Count > 0 || _natPmpMappedPorts.Count > 0 || _pcpMappedPorts.Count > 0)
        {
            _renewalCts = new CancellationTokenSource();
            _leaseRenewalTimer = new System.Threading.Timer(
                _ => _ = RenewLeasesAsync(),
                null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        }

        // 6. Resolve external IP (fully local — router queries only, no external servers)
        await ResolveExternalIpAsync(localIp, ct);

        // 7. Detect CGNAT / double-NAT
        if (!string.IsNullOrEmpty(PublicIp) && !IsRoutablePublicIp(PublicIp))
        {
            IsBehindCgnat = true;
            IsLanOnly = true;
            Log($"CGNAT detected: {PublicIp} is not a routable public IP");
        }

        // 8. Start monitoring for network changes
        StartNetworkMonitoring();

        sw.Stop();
        bool portsMapped = _upnpMappedPorts.Count > 0 || _natPmpMappedPorts.Count > 0 || _pcpMappedPorts.Count > 0;
        if (portsMapped && !IsBehindCgnat)
        {
            var method = _upnpMappedPorts.Count > 0 ? "UPnP" : _natPmpMappedPorts.Count > 0 ? "NAT-PMP" : "PCP";
            var totalMapped = _upnpMappedPorts.Count + _natPmpMappedPorts.Count + _pcpMappedPorts.Count;
            SetupSummary = $"Direct P2P — {method} mapped {totalMapped} ports ({sw.ElapsedMilliseconds}ms)";
        }
        else
        {
            // IPv6 fallback — globally routable, no port mapping needed
            var ipv6 = GetPublicIpv6();
            if (ipv6 != null && !IsBehindCgnat)
            {
                PublicIp = ipv6;
                IpSource = "IPv6";
                UsingIpv6 = true;
                SetupSummary = $"Direct P2P — IPv6 globally routable ({sw.ElapsedMilliseconds}ms)";
                Log($"IPv6 fallback: using {ipv6} (no port mapping needed)");
            }
            else
            {
                IsLanOnly = true;
                // Detect restricted (guest/public/hotel) WiFi — UPnP+NAT-PMP+PCP failed on a wireless interface
                if (!portsMapped && IsPrimaryInterfaceWiFi())
                {
                    RestrictedNetwork = true;
                    Log("Restricted network: WiFi + no port mapping — likely guest/public/hotel network");
                }
                SetupSummary = $"Direct P2P (LAN only) — using LAN IP ({sw.ElapsedMilliseconds}ms)";
            }
        }
        if (FirewallRuleFailed) SetupSummary += " [firewall rule failed]";
        Log(SetupSummary);
        return null; // success — relay is running
    }

    // ── VPN detection ─────────────────────────────────────────────────────────

    private static bool DetectVpn()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up)
                .Any(i =>
                    i.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                    i.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    i.Description.Contains("VPN",       StringComparison.OrdinalIgnoreCase) ||
                    i.Description.Contains("TAP",       StringComparison.OrdinalIgnoreCase) ||
                    i.Description.Contains("TUN",       StringComparison.OrdinalIgnoreCase) ||
                    i.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                    i.Description.Contains("OpenVPN",   StringComparison.OrdinalIgnoreCase) ||
                    i.Description.Contains("NordVPN",   StringComparison.OrdinalIgnoreCase) ||
                    i.Description.Contains("ExpressVPN",StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    // ── WiFi / restricted network detection ────────────────────────────────────

    /// <summary>
    /// Returns true if the primary outbound interface is WiFi.
    /// Combined with UPnP/NAT-PMP failure, this indicates a guest/public/hotel network
    /// that blocks port mapping — Direct P2P will only work on the same LAN.
    /// </summary>
    private static bool IsPrimaryInterfaceWiFi()
    {
        try
        {
            string? localIp = null;
            try
            {
                using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                sock.Connect("8.8.8.8", 80);
                localIp = ((IPEndPoint)sock.LocalEndPoint!).Address.ToString();
            }
            catch { return false; }

            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == localIp))
                    return iface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            }
        }
        catch { }
        return false;
    }

    // ── Network change monitoring ──────────────────────────────────────────────

    private void StartNetworkMonitoring()
    {
        if (_monitoringNetwork) return;
        _monitoringNetwork = true;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    private void StopNetworkMonitoring()
    {
        if (!_monitoringNetwork) return;
        _monitoringNetwork = false;
        NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Log("Network address changed — notifying application");
        NetworkChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnRelayProcessExited(object? sender, EventArgs e)
    {
        if (_disposed) return; // intentional kill during cleanup — ignore
        Log("Relay process exited unexpectedly");
        RelayDied?.Invoke(this, EventArgs.Empty);
    }

    // ── IP validation ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true only for routable public IPv4 addresses.
    /// Rejects RFC 1918 (private), RFC 6598 (CGNAT), loopback, link-local, and 0.0.0.0.
    /// </summary>
    public static bool IsRoutablePublicIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return false;
        if (!IPAddress.TryParse(ip, out var addr)) return false;
        if (addr.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = addr.GetAddressBytes();
        return !(
            b[0] == 0   ||                                        // 0.0.0.0/8
            b[0] == 10  ||                                        // 10.0.0.0/8
            b[0] == 127 ||                                        // 127.0.0.0/8 loopback
            (b[0] == 100 && b[1] >= 64  && b[1] <= 127) ||       // 100.64.0.0/10 CGNAT (RFC 6598)
            (b[0] == 169 && b[1] == 254) ||                       // 169.254.0.0/16 link-local
            (b[0] == 172 && b[1] >= 16  && b[1] <= 31) ||        // 172.16.0.0/12
            (b[0] == 192 && b[1] == 168)                          // 192.168.0.0/16
        );
    }

    private static bool IsUsableIp(string? ip)
        => !string.IsNullOrEmpty(ip) && ip != "0.0.0.0" && IPAddress.TryParse(ip, out _);

    // ── Port mapping orchestration ───────────────────────────────────────────

    private async Task<bool> TryUpnpAsync(string localIp, IPAddress? gateway, CancellationToken ct)
    {
        try
        {
            Log("UPnP: SSDP discovery...");
            var disco = await DiscoverUpnpAsync(gateway, ct);
            if (disco == null) { Log("UPnP: No IGD device found"); return false; }

            _upnpControlUrl  = disco.Value.controlUrl;
            _upnpServiceType = disco.Value.serviceType;
            Log($"UPnP: {_upnpServiceType} @ {_upnpControlUrl}");

            var tasks = RelayPorts.Select(p =>
                UpnpMapPortAsync(_upnpControlUrl, _upnpServiceType, p, localIp, ct)).ToArray();
            var results = await Task.WhenAll(tasks);
            for (int i = 0; i < results.Length; i++)
                if (results[i]) _upnpMappedPorts.Add(RelayPorts[i]);

            Log($"UPnP: Mapped {_upnpMappedPorts.Count}/{RelayPorts.Length} ports");
            return _upnpMappedPorts.Count > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"UPnP: Exception — {ex.Message}"); return false; }
    }

    private async Task<bool> TryNatPmpAsync(IPAddress? gateway, CancellationToken ct)
    {
        if (gateway == null) { Log("NAT-PMP: No gateway"); return false; }
        try
        {
            _natPmpGateway = gateway;
            Log($"NAT-PMP: Probing {gateway}...");

            var tasks = RelayPorts.Select(p =>
                NatPmpAddWithRetryAsync(gateway, p, ct)).ToArray();
            var results = await Task.WhenAll(tasks);
            for (int i = 0; i < results.Length; i++)
                if (results[i]) _natPmpMappedPorts.Add(RelayPorts[i]);

            Log(_natPmpMappedPorts.Count > 0
                ? $"NAT-PMP: Mapped {_natPmpMappedPorts.Count}/{RelayPorts.Length} ports"
                : "NAT-PMP: Router did not respond (unsupported)");
            return _natPmpMappedPorts.Count > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"NAT-PMP: Exception — {ex.Message}"); return false; }
    }

    // ── External IP resolution (router queries only — no external servers) ────

    private async Task ResolveExternalIpAsync(string localIp, CancellationToken ct)
    {
        if (_upnpMappedPorts.Count > 0 && _upnpControlUrl != null && _upnpServiceType != null)
        {
            try
            {
                var ip = await UpnpGetExternalIpAsync(_upnpControlUrl, _upnpServiceType, ct);
                if (IsUsableIp(ip)) { PublicIp = ip!; IpSource = "UPnP"; Log($"External IP (UPnP): {ip}"); return; }
            }
            catch { }
        }
        if (_natPmpMappedPorts.Count > 0 && _natPmpGateway != null)
        {
            try
            {
                var ip = await NatPmpGetExternalIpAsync(_natPmpGateway, ct);
                if (IsUsableIp(ip)) { PublicIp = ip!; IpSource = "NAT-PMP"; Log($"External IP (NAT-PMP): {ip}"); return; }
            }
            catch { }
        }
        if (_pcpMappedPorts.Count > 0 && IsUsableIp(_pcpExternalIp))
        {
            PublicIp = _pcpExternalIp!;
            IpSource = "PCP";
            Log($"External IP (PCP): {_pcpExternalIp}");
            return;
        }
        // No external servers — use LAN IP
        PublicIp = localIp;
        IpSource = "LAN";
        Log($"External IP: router did not return one, using LAN IP {localIp}");
    }

    // ── Port mapping lease renewal ─────────────────────────────────────────────

    private async Task RenewLeasesAsync()
    {
        if (_disposed) return;
        var ct = _renewalCts?.Token ?? CancellationToken.None;

        try
        {
            // Renew UPnP leases
            if (_upnpMappedPorts.Count > 0 && _upnpControlUrl != null && _upnpServiceType != null)
            {
                var localIp = GetLocalIp();
                int renewed = 0;
                foreach (var port in _upnpMappedPorts.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    var ok = await UpnpMapPortAsync(_upnpControlUrl, _upnpServiceType, port, localIp, ct);
                    if (ok) renewed++;
                }
                Log($"UPnP: lease renewal — {renewed}/{_upnpMappedPorts.Count} ports refreshed");
            }

            // Renew NAT-PMP leases (requested with lifetime=3600s, renew at 30min)
            if (_natPmpMappedPorts.Count > 0 && _natPmpGateway != null)
            {
                int renewed = 0;
                foreach (var port in _natPmpMappedPorts.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    var ok = await NatPmpAddWithRetryAsync(_natPmpGateway, port, ct);
                    if (ok) renewed++;
                }
                Log($"NAT-PMP: lease renewal — {renewed}/{_natPmpMappedPorts.Count} ports refreshed");
            }

            // Renew PCP leases
            if (_pcpMappedPorts.Count > 0 && _natPmpGateway != null)
            {
                var localIp = GetLocalIp();
                int renewed = 0;
                foreach (var port in _pcpMappedPorts.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    var result = await PcpMapPortAsync(_natPmpGateway, localIp, port, 3600, ct);
                    if (result.ok) renewed++;
                }
                Log($"PCP: lease renewal — {renewed}/{_pcpMappedPorts.Count} ports refreshed");
            }
        }
        catch (OperationCanceledException) { Log("Lease renewal cancelled (dispose)"); }
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel any in-progress lease renewal so it stops immediately
        try { _renewalCts?.Cancel(); } catch { }
        _renewalCts?.Dispose();
        _renewalCts = null;
        _leaseRenewalTimer?.Dispose();
        _leaseRenewalTimer = null;
        StopNetworkMonitoring();

        // Kill relay process
        try { if (_relayProc != null && !_relayProc.HasExited) _relayProc.Kill(true); } catch { }
        try { _relayProc?.Dispose(); } catch { }
        _relayProc = null;

        // Remove port mappings — collect tasks and wait briefly so they complete before process exits
        var cleanupTasks = new List<Task>();

        if (_upnpMappedPorts.Count > 0 && _upnpControlUrl != null && _upnpServiceType != null)
        {
            var url = _upnpControlUrl; var svc = _upnpServiceType;
            var ports = _upnpMappedPorts.ToList();
            cleanupTasks.Add(Task.Run(async () =>
            {
                foreach (var p in ports) await UpnpDeleteMappingAsync(url, svc, p);
            }));
        }

        if (_natPmpMappedPorts.Count > 0 && _natPmpGateway != null)
        {
            var gw = _natPmpGateway; var ports = _natPmpMappedPorts.ToList();
            cleanupTasks.Add(Task.Run(async () =>
            {
                foreach (var p in ports) await NatPmpDeleteMappingAsync(gw, p);
            }));
        }

        if (_pcpMappedPorts.Count > 0 && _natPmpGateway != null)
        {
            var gw = _natPmpGateway; var localIp = GetLocalIp();
            var ports = _pcpMappedPorts.ToList();
            cleanupTasks.Add(Task.Run(async () =>
            {
                foreach (var p in ports) await PcpDeleteMappingAsync(gw, localIp, p);
            }));
        }

        // Wait up to 3 seconds for port mapping cleanup to complete
        if (cleanupTasks.Count > 0)
        {
            try { Task.WaitAll(cleanupTasks.ToArray(), TimeSpan.FromSeconds(3)); }
            catch { }
            Log($"Port mapping cleanup: {cleanupTasks.Count(t => t.IsCompletedSuccessfully)}/{cleanupTasks.Count} completed");
        }

        // Remove firewall rule
        if (_firewallRuleAdded) { RemoveFirewallRule(); _firewallRuleAdded = false; }
        Log("Direct P2P cleanup complete");
    }

    // ── Relay process ────────────────────────────────────────────────────────

    private async Task<string?> StartRelayAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _crocPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("relay");
            _relayProc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _relayProc.Exited += OnRelayProcessExited;
            _relayProc.Start();

            // Poll until relay accepts TCP on port 9009 (up to 5 s)
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (_relayProc.HasExited)
                {
                    string stderr = "";
                    try { stderr = (await _relayProc.StandardError.ReadToEndAsync(ct)).Trim(); } catch { }
                    Log($"Relay exited early. stderr: {stderr}");
                    if (stderr.Contains("address already in use", StringComparison.OrdinalIgnoreCase) ||
                        stderr.Contains("bind:", StringComparison.OrdinalIgnoreCase))
                        return $"Port {RelayBasePort} is already in use by another application";
                    return string.IsNullOrEmpty(stderr)
                        ? "Relay process exited unexpectedly — croc may be blocked by antivirus or another security tool"
                        : $"Relay failed: {stderr[..Math.Min(200, stderr.Length)]}";
                }

                try
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, RelayBasePort, ct);
                    return null; // relay is ready
                }
                catch (OperationCanceledException) { throw; }
                catch { }

                await Task.Delay(200, ct);
            }

            Log("Relay: TCP probe timed out but process is running, proceeding");
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return $"Could not start relay: {ex.Message}"; }
    }

    // ── Windows Firewall ─────────────────────────────────────────────────────

    private void AddFirewallRule()
    {
        try
        {
            RemoveFirewallRule(); // always remove stale rule first

            // Try non-elevated first (works if app is already admin)
            if (TryAddFirewallRule(elevated: false))
            {
                _firewallRuleAdded = true;
                Log("Firewall: TCP 9009-9013 inbound rule added (all profiles)");
                return;
            }

            // Non-elevated failed — retry with UAC elevation prompt
            Log("Firewall: non-elevated failed, requesting admin elevation...");
            if (TryAddFirewallRule(elevated: true))
            {
                _firewallRuleAdded = true;
                Log("Firewall: TCP 9009-9013 inbound rule added (elevated)");
                return;
            }

            FirewallRuleFailed = true;
            Log("Firewall: rule could not be added even with elevation");
        }
        catch (Exception ex)
        {
            FirewallRuleFailed = true;
            Log($"Firewall: add rule failed — {ex.Message}");
        }
    }

    private bool TryAddFirewallRule(bool elevated)
    {
        try
        {
            var args = $"advfirewall firewall add rule name=\"{FirewallRuleName}\" " +
                       $"protocol=TCP dir=in localport={RelayBasePort}-{RelayPorts[^1]} " +
                       $"action=allow program=\"{_crocPath}\" profile=any";

            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = args,
                UseShellExecute = elevated,
                WindowStyle = elevated ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                CreateNoWindow = !elevated,
            };

            if (elevated)
                psi.Verb = "runas";
            else
            {
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
            }

            using var proc = Process.Start(psi);
            proc?.WaitForExit(10000);
            return proc?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user denied UAC prompt
            Log("Firewall: user denied UAC elevation");
            return false;
        }
        catch (Exception ex)
        {
            Log($"Firewall: {(elevated ? "elevated" : "non-elevated")} attempt — {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// If a previous VIA session crashed and left a croc relay running on port 9009,
    /// this method detects and kills it. Only kills processes named "croc" or "via".
    /// </summary>
    public static async Task KillZombieRelayAsync()
    {
        try
        {
            // Quick probe — if nothing answers, we're clean
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(600);
            await tcp.ConnectAsync(IPAddress.Loopback, RelayBasePort, cts.Token);
        }
        catch
        {
            return; // Nothing listening on 9009 — nothing to do
        }
        Log($"Zombie detected on port {RelayBasePort} — attempting cleanup");
        await Task.Run(() => KillProcessOnPort(RelayBasePort));
    }

    private static void KillProcessOnPort(int port)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netstat",
                Arguments = "-ano",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            using var proc = Process.Start(psi)!;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);

            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains($":{port}") || !line.Contains("LISTENING")) continue;
                var parts = line.Trim().Split([' '], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !int.TryParse(parts[^1], out int pid) || pid <= 0) continue;
                try
                {
                    var target = Process.GetProcessById(pid);
                    var name = target.ProcessName;
                    if (name.Contains("croc", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("via",  StringComparison.OrdinalIgnoreCase))
                    {
                        target.Kill(true);
                        Log($"Killed zombie {name} (PID {pid}) on port {port}");
                    }
                    else
                    {
                        Log($"Port {port} held by '{name}' (PID {pid}) — not a VIA process, leaving it");
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>Removes the Direct P2P firewall rule. Safe to call even if no rule exists.</summary>
    public static void RemoveFirewallRule()
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = "netsh", UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "advfirewall", "firewall", "delete", "rule",
                $"name={FirewallRuleName}" })
                psi.ArgumentList.Add(arg);
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }

    // ── Network helpers ──────────────────────────────────────────────────────

    private static string GetLocalIp()
    {
        // UDP connect trick — no packets sent; OS picks the correct outbound interface
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Connect("8.8.8.8", 80);
            return ((IPEndPoint)sock.LocalEndPoint!).Address.ToString();
        }
        catch { }
        // Fallback: iterate interfaces, skip virtual adapters
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (IsVirtualAdapter(iface)) continue;
                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                        return addr.Address.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    private static IPAddress? GetDefaultGateway()
    {
        // Step 1: use the same UDP connect trick as GetLocalIp() to find the outbound
        // local IP — this correctly reflects the OS routing table even when multiple
        // interfaces are active (WiFi + Ethernet, VMs, etc.).
        string? localIp = null;
        try
        {
            using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sock.Connect("8.8.8.8", 80);
            localIp = ((IPEndPoint)sock.LocalEndPoint!).Address.ToString();
        }
        catch { }

        if (localIp != null)
        {
            // Step 2: find the interface that owns this IP and return its gateway.
            // This guarantees UPnP/NAT-PMP probe the same router the OS routes through.
            try
            {
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (iface.OperationalStatus != OperationalStatus.Up) continue;
                    var props = iface.GetIPProperties();
                    if (!props.UnicastAddresses.Any(a => a.Address.ToString() == localIp)) continue;
                    var gw = props.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (gw != null) return gw.Address;
                }
            }
            catch { }
        }

        // Fallback: first non-virtual interface with an IPv4 gateway
        try
        {
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (IsVirtualAdapter(iface)) continue;
                foreach (var gw in iface.GetIPProperties().GatewayAddresses)
                    if (gw.Address.AddressFamily == AddressFamily.InterNetwork)
                        return gw.Address;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Filters out VMware, VirtualBox, Hyper-V, Docker, and loopback adapters.</summary>
    private static bool IsVirtualAdapter(NetworkInterface iface)
    {
        if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) return true;
        var desc = iface.Description;
        return desc.Contains("VMware",     StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("vEthernet",  StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Hyper-V",    StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Docker",     StringComparison.OrdinalIgnoreCase) ||
               desc.Contains("Loopback",   StringComparison.OrdinalIgnoreCase) ||
               iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel;
    }

    // ── XML safety helper ─────────────────────────────────────────────────────

    /// <summary>
    /// Parses XML with DTD processing disabled and no external resolver.
    /// Prevents XXE injection from malicious router SOAP responses.
    /// </summary>
    private static XDocument SafeParseXml(string xml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 512_000
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }

    // ── UPnP SSDP Discovery ─────────────────────────────────────────────────

    private static readonly string[] SsdpTargets =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    /// <summary>
    /// Discovers UPnP IGD device on the LAN. Validates that any found device
    /// comes from the default gateway IP to prevent rogue-device hijacking.
    /// </summary>
    private static async Task<(string controlUrl, string serviceType)?> DiscoverUpnpAsync(
        IPAddress? expectedGateway, CancellationToken ct)
    {
        const string ssdpAddr = "239.255.255.250";
        const int ssdpPort = 1900;
        var locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var udp = new UdpClient();
            udp.Client.ReceiveTimeout = 2500;

            foreach (var st in SsdpTargets)
            {
                var query = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\n" +
                    $"HOST: {ssdpAddr}:{ssdpPort}\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n" +
                    $"ST: {st}\r\n\r\n");
                try { await udp.SendAsync(query, query.Length, ssdpAddr, ssdpPort); } catch { }
            }

            var deadline = DateTime.UtcNow.AddSeconds(2.5);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var inner = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    inner.CancelAfter(remaining);
                    var recv = await udp.ReceiveAsync(inner.Token);

                    // Security: only accept SSDP responses from the default gateway
                    if (expectedGateway != null &&
                        !recv.RemoteEndPoint.Address.Equals(expectedGateway))
                    {
                        Log($"UPnP: Discarding SSDP response from {recv.RemoteEndPoint.Address} (expected {expectedGateway})");
                        continue;
                    }

                    var text = Encoding.ASCII.GetString(recv.Buffer);
                    var locLine = text.Split('\n')
                        .FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase));
                    if (locLine != null)
                    {
                        var loc = locLine["LOCATION:".Length..].Trim().TrimEnd('\r');
                        if (!string.IsNullOrEmpty(loc)) locations.Add(loc);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { throw; }
                catch { break; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        Log($"UPnP: {locations.Count} SSDP location(s) found");
        if (locations.Count == 0) return null;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        foreach (var loc in locations)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var xml = await http.GetStringAsync(loc, ct);
                var result = ParseControlUrl(xml, loc);
                if (result != null) return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log($"UPnP: Failed to fetch {loc} — {ex.Message}"); }
        }
        return null;
    }

    private static (string controlUrl, string serviceType)? ParseControlUrl(string xml, string locationUrl)
    {
        try
        {
            var doc = SafeParseXml(xml);
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";
            string[] serviceTypes =
            [
                "urn:schemas-upnp-org:service:WANIPConnection:2",
                "urn:schemas-upnp-org:service:WANIPConnection:1",
                "urn:schemas-upnp-org:service:WANPPPConnection:1",
            ];
            foreach (var st in serviceTypes)
            {
                var svc = doc.Descendants(ns + "service")
                    .FirstOrDefault(s => s.Element(ns + "serviceType")?.Value == st);
                if (svc == null) continue;
                var path = svc.Element(ns + "controlURL")?.Value;
                if (string.IsNullOrEmpty(path)) continue;
                string controlUrl = path.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? path
                    : new Uri(new Uri(locationUrl), path).ToString();
                return (controlUrl, st);
            }
        }
        catch (Exception ex) { Log($"UPnP: XML parse error — {ex.Message}"); }
        return null;
    }

    // ── UPnP Port Mapping ────────────────────────────────────────────────────

    /// <summary>
    /// Maps a single port with full error recovery:
    /// • Pre-deletes stale mapping (avoids error 718)
    /// • Retries with permanent lease on error 725
    /// • Retries after re-delete on error 718
    /// </summary>
    private static async Task<bool> UpnpMapPortAsync(
        string controlUrl, string svcType, int port, string localIp, CancellationToken ct)
    {
        try { await UpnpDeleteMappingAsync(controlUrl, svcType, port); } catch { }

        var (ok, errCode) = await UpnpAddCoreAsync(controlUrl, svcType, port, localIp, 3600, ct);
        if (ok) return true;

        if (errCode == 725) // OnlyPermanentLeasesSupported
        {
            Log($"UPnP: Port {port} — error 725, retrying with permanent lease");
            (ok, _) = await UpnpAddCoreAsync(controlUrl, svcType, port, localIp, 0, ct);
            if (ok) return true;
        }
        if (errCode == 718) // ConflictInMappingEntry
        {
            Log($"UPnP: Port {port} — error 718 conflict, delete + retry");
            try { await UpnpDeleteMappingAsync(controlUrl, svcType, port); } catch { }
            await Task.Delay(150, ct);
            (ok, _) = await UpnpAddCoreAsync(controlUrl, svcType, port, localIp, 3600, ct);
            if (ok) return true;
        }

        if (errCode != 0) Log($"UPnP: Port {port} — SOAP error {errCode}");
        return false;
    }

    private static async Task<(bool ok, int errCode)> UpnpAddCoreAsync(
        string controlUrl, string svcType, int port, string localIp, int lease, CancellationToken ct)
    {
        const string action = "AddPortMapping";
        var body = $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"
                        s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:{action} xmlns:u="{svcType}">
                  <NewRemoteHost></NewRemoteHost>
                  <NewExternalPort>{port}</NewExternalPort>
                  <NewProtocol>TCP</NewProtocol>
                  <NewInternalPort>{port}</NewInternalPort>
                  <NewInternalClient>{localIp}</NewInternalClient>
                  <NewEnabled>1</NewEnabled>
                  <NewPortMappingDescription>VIA x4</NewPortMappingDescription>
                  <NewLeaseDuration>{lease}</NewLeaseDuration>
                </u:{action}>
              </s:Body>
            </s:Envelope>
            """;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
            req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
            req.Headers.Add("SOAPAction", $"\"{svcType}#{action}\"");
            var resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode) return (true, 0);
            try
            {
                var xml = await resp.Content.ReadAsStringAsync(ct);
                var doc = SafeParseXml(xml);
                var code = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "errorCode")?.Value;
                if (code != null && int.TryParse(code, out int ec)) return (false, ec);
            }
            catch { }
            return (false, 0);
        }
        catch { return (false, 0); }
    }

    private static async Task UpnpDeleteMappingAsync(string controlUrl, string svcType, int port)
    {
        const string action = "DeletePortMapping";
        var body = $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"
                        s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:{action} xmlns:u="{svcType}">
                  <NewRemoteHost></NewRemoteHost>
                  <NewExternalPort>{port}</NewExternalPort>
                  <NewProtocol>TCP</NewProtocol>
                </u:{action}>
              </s:Body>
            </s:Envelope>
            """;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
            req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
            req.Headers.Add("SOAPAction", $"\"{svcType}#{action}\"");
            await http.SendAsync(req);
        }
        catch { }
    }

    private static async Task<string?> UpnpGetExternalIpAsync(
        string controlUrl, string svcType, CancellationToken ct)
    {
        const string action = "GetExternalIPAddress";
        var body = $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"
                        s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body>
                <u:{action} xmlns:u="{svcType}"/>
              </s:Body>
            </s:Envelope>
            """;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var req = new HttpRequestMessage(HttpMethod.Post, controlUrl);
            req.Content = new StringContent(body, Encoding.UTF8, "text/xml");
            req.Headers.Add("SOAPAction", $"\"{svcType}#{action}\"");
            var resp = await http.SendAsync(req, ct);
            var xml = await resp.Content.ReadAsStringAsync(ct);
            var doc = SafeParseXml(xml);
            return doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress")?.Value?.Trim();
        }
        catch { return null; }
    }

    // ── NAT-PMP (RFC 6886) ───────────────────────────────────────────────────

    /// <summary>
    /// RFC 6886 exponential retry: 250ms, 500ms, 1000ms.
    /// Validates that the response comes from the expected gateway (prevents spoofing).
    /// </summary>
    private static async Task<bool> NatPmpAddWithRetryAsync(
        IPAddress gateway, int port, CancellationToken ct)
    {
        int[] retryMs = [250, 500, 1000];
        foreach (var timeout in retryMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var udp = new UdpClient();
                var req = BuildNatPmpMapRequest(port, 3600);
                var gwEp = new IPEndPoint(gateway, 5351);
                await udp.SendAsync(req, req.Length, gwEp);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                var resp = await udp.ReceiveAsync(cts.Token);

                // Security: only accept response from the expected gateway
                if (!resp.RemoteEndPoint.Address.Equals(gateway))
                {
                    Log($"NAT-PMP: Discarding response from {resp.RemoteEndPoint.Address} (expected {gateway})");
                    continue;
                }

                if (resp.Buffer.Length >= 16 && resp.Buffer[0] == 0 && resp.Buffer[1] == 130)
                {
                    int result = (resp.Buffer[2] << 8) | resp.Buffer[3];
                    if (result == 0) return true;
                    Log($"NAT-PMP: Port {port} error code {result}");
                    return false; // router error — no point retrying
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            catch (OperationCanceledException) { throw; }
            catch { return false; }
        }
        return false;
    }

    private static byte[] BuildNatPmpMapRequest(int port, uint lifetime)
    {
        var req = new byte[12];
        req[1] = 2; // opcode: map TCP
        req[4] = (byte)(port >> 8);   req[5] = (byte)(port & 0xFF);
        req[6] = (byte)(port >> 8);   req[7] = (byte)(port & 0xFF);
        req[8]  = (byte)(lifetime >> 24); req[9]  = (byte)(lifetime >> 16);
        req[10] = (byte)(lifetime >> 8);  req[11] = (byte)(lifetime & 0xFF);
        return req;
    }

    private static async Task NatPmpDeleteMappingAsync(IPAddress gateway, int port)
    {
        try
        {
            using var udp = new UdpClient();
            var req = BuildNatPmpMapRequest(port, 0); // lifetime=0 deletes
            await udp.SendAsync(req, req.Length, new IPEndPoint(gateway, 5351));
            await Task.Delay(100);
        }
        catch { }
    }

    private static async Task<string?> NatPmpGetExternalIpAsync(IPAddress gateway, CancellationToken ct)
    {
        try
        {
            using var udp = new UdpClient();
            var req = new byte[] { 0, 0 }; // version=0, opcode=0
            await udp.SendAsync(req, req.Length, new IPEndPoint(gateway, 5351));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            var resp = await udp.ReceiveAsync(cts.Token);

            // Security: validate source is the expected gateway
            if (!resp.RemoteEndPoint.Address.Equals(gateway)) return null;

            if (resp.Buffer.Length >= 12 &&
                resp.Buffer[0] == 0 && resp.Buffer[1] == 128 &&
                resp.Buffer[2] == 0 && resp.Buffer[3] == 0)
            {
                return new IPAddress(resp.Buffer[8..12]).ToString();
            }
        }
        catch { }
        return null;
    }

    // ── PCP (RFC 6887) — successor to NAT-PMP ────────────────────────────────

    private readonly List<int> _pcpMappedPorts = new();
    private string? _pcpExternalIp;

    private async Task<bool> TryPcpAsync(IPAddress? gateway, string localIp, CancellationToken ct)
    {
        if (gateway == null) { Log("PCP: No gateway"); return false; }
        try
        {
            Log($"PCP: Probing {gateway}...");
            var tasks = RelayPorts.Select(p =>
                PcpMapPortAsync(gateway, localIp, p, 3600, ct)).ToArray();
            var results = await Task.WhenAll(tasks);
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i].ok)
                {
                    _pcpMappedPorts.Add(RelayPorts[i]);
                    if (_pcpExternalIp == null && results[i].externalIp != null)
                        _pcpExternalIp = results[i].externalIp;
                }
            }

            Log(_pcpMappedPorts.Count > 0
                ? $"PCP: Mapped {_pcpMappedPorts.Count}/{RelayPorts.Length} ports"
                : "PCP: Router did not respond (unsupported)");
            return _pcpMappedPorts.Count > 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"PCP: Exception — {ex.Message}"); return false; }
    }

    /// <summary>
    /// Sends a PCP MAP request (RFC 6887 §11.1).
    /// PCP uses the same port 5351 as NAT-PMP but version=2 and a 60-byte request.
    /// Returns (ok, externalIp) — externalIp extracted from the MAP response bytes 44-59.
    /// </summary>
    private static async Task<(bool ok, string? externalIp)> PcpMapPortAsync(
        IPAddress gateway, string localIp, int port, uint lifetime, CancellationToken ct)
    {
        // RFC 6887 §11.2: reuse the same nonce across retransmissions so the
        // router recognizes retries vs. new requests
        var nonce = new byte[12];
        System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);

        int[] retryMs = [250, 500, 1000];
        foreach (var timeout in retryMs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var udp = new UdpClient();
                var req = BuildPcpMapRequest(localIp, port, lifetime, nonce);
                await udp.SendAsync(req, req.Length, new IPEndPoint(gateway, 5351));

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(timeout);
                var resp = await udp.ReceiveAsync(cts.Token);

                if (!resp.RemoteEndPoint.Address.Equals(gateway)) continue;

                // PCP response: version=2, R=1 (bit 7 of byte 0), opcode=1 (MAP)
                if (resp.Buffer.Length >= 60 &&
                    (resp.Buffer[0] & 0x7F) == 2 &&    // version 2
                    (resp.Buffer[0] & 0x80) != 0 &&     // response bit set
                    resp.Buffer[1] == 1)                 // opcode MAP
                {
                    int result = resp.Buffer[3]; // result code in byte 3
                    if (result == 0)
                    {
                        // Extract assigned external address from bytes 44-59 (IPv6-mapped IPv4)
                        string? extIp = null;
                        try
                        {
                            var extAddr = new IPAddress(resp.Buffer[44..60]);
                            if (extAddr.IsIPv4MappedToIPv6)
                                extIp = extAddr.MapToIPv4().ToString();
                            else if (extAddr.AddressFamily == AddressFamily.InterNetwork)
                                extIp = extAddr.ToString();
                        }
                        catch { }
                        return (true, extIp);
                    }
                    Log($"PCP: Port {port} result code {result}");
                    return (false, null);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
            catch (OperationCanceledException) { throw; }
            catch { return (false, null); }
        }
        return (false, null);
    }

    /// <summary>
    /// Builds a 60-byte PCP MAP request per RFC 6887 §11.1.
    /// Header (24 bytes) + MAP opcode data (36 bytes).
    /// The nonce parameter should be reused across retries of the same mapping (§11.2).
    /// </summary>
    private static byte[] BuildPcpMapRequest(string localIp, int port, uint lifetime, byte[]? nonce = null)
    {
        var req = new byte[60];
        // Header (24 bytes)
        req[0] = 2;                // version = 2
        req[1] = 1;                // opcode = MAP (1)
        // bytes 2-3: reserved
        // bytes 4-7: requested lifetime (big-endian)
        req[4] = (byte)(lifetime >> 24); req[5] = (byte)(lifetime >> 16);
        req[6] = (byte)(lifetime >> 8);  req[7] = (byte)(lifetime & 0xFF);
        // bytes 8-23: client IP as IPv4-mapped IPv6 (::ffff:x.x.x.x)
        if (IPAddress.TryParse(localIp, out var addr))
        {
            var mapped = addr.MapToIPv6().GetAddressBytes();
            Array.Copy(mapped, 0, req, 8, 16);
        }
        // MAP opcode data (36 bytes starting at offset 24)
        // bytes 24-35: mapping nonce (reused across retries per RFC 6887 §11.2)
        if (nonce == null)
        {
            nonce = new byte[12];
            System.Security.Cryptography.RandomNumberGenerator.Fill(nonce);
        }
        Array.Copy(nonce, 0, req, 24, 12);
        req[36] = 6;               // protocol = TCP (6)
        // bytes 37-39: reserved
        // bytes 40-41: internal port (big-endian)
        req[40] = (byte)(port >> 8); req[41] = (byte)(port & 0xFF);
        // bytes 42-43: suggested external port (same as internal)
        req[42] = (byte)(port >> 8); req[43] = (byte)(port & 0xFF);
        // bytes 44-59: suggested external address (all zeros = let router choose)
        return req;
    }

    private async Task PcpDeleteMappingAsync(IPAddress gateway, string localIp, int port)
    {
        try
        {
            using var udp = new UdpClient();
            var req = BuildPcpMapRequest(localIp, port, 0); // lifetime=0 deletes
            await udp.SendAsync(req, req.Length, new IPEndPoint(gateway, 5351));
            await Task.Delay(100);
        }
        catch { }
    }

    // ── Connection quality probe ──────────────────────────────────────────────

    /// <summary>
    /// Measures TCP connect latency to a relay endpoint. Returns RTT in milliseconds,
    /// or -1 if unreachable. Used on the receiver side to show connection quality.
    /// </summary>
    public static async Task<long> ProbeRelayLatencyAsync(string host, int port, CancellationToken ct = default)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(3000);
            await tcp.ConnectAsync(host, port, cts.Token);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        catch { return -1; }
    }

    /// <summary>
    /// Returns a human-readable quality label based on RTT.
    /// </summary>
    public static string LatencyLabel(long ms) => ms switch
    {
        < 0   => "Unreachable",
        < 30  => "Excellent",
        < 80  => "Good",
        < 150 => "Fair",
        _     => "Poor"
    };

    // ── IPv6 support ─────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first routable IPv6 address (non-link-local, non-loopback)
    /// on the primary outbound interface, or null if IPv6 is not available.
    /// IPv6 addresses are globally routable — no port mapping needed.
    /// </summary>
    public static string? GetPublicIpv6()
    {
        try
        {
            // Use UDP connect trick for IPv6 to find the correct outbound interface
            string? localIpv6 = null;
            try
            {
                using var sock = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                sock.Connect("2001:4860:4860::8888", 80); // Google DNS IPv6
                var ep = (IPEndPoint)sock.LocalEndPoint!;
                if (!ep.Address.IsIPv6LinkLocal && !IPAddress.IsLoopback(ep.Address))
                    localIpv6 = ep.Address.ToString();
            }
            catch { }

            if (localIpv6 != null) return localIpv6;

            // Fallback: scan interfaces for a global IPv6 address
            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (IsVirtualAdapter(iface)) continue;
                foreach (var addr in iface.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                        !addr.Address.IsIPv6LinkLocal &&
                        !IPAddress.IsLoopback(addr.Address))
                        return addr.Address.ToString();
                }
            }
        }
        catch { }
        return null;
    }
}
