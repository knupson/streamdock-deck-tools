// Wake-on-LAN and reachability checks.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StreamDeckAudio
{
    public sealed class HostState
    {
        public bool Up;
        public int Milliseconds;
    }

    public static class WakeOnLan
    {
        /// <summary>
        /// Sends the magic packet: six 0xFF bytes followed by the MAC sixteen
        /// times. It goes to the limited broadcast address and to every local
        /// subnet's directed broadcast, because which of the two a router or
        /// switch forwards varies.
        /// </summary>
        public static bool Send(string mac, int port, string extraTarget)
        {
            byte[] address = ParseMac(mac);
            if (address == null)
            {
                Log.Write("WOL: cannot parse MAC '" + mac + "'");
                return false;
            }

            var packet = new byte[102];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int repeat = 0; repeat < 16; repeat++)
                Array.Copy(address, 0, packet, 6 + repeat * 6, 6);

            var targets = new List<IPAddress> { IPAddress.Broadcast };
            targets.AddRange(DirectedBroadcasts());

            IPAddress extra;
            if (!string.IsNullOrEmpty(extraTarget) && IPAddress.TryParse(extraTarget, out extra) &&
                !targets.Contains(extra))
                targets.Add(extra);

            int sent = 0;
            foreach (IPAddress target in targets)
            {
                try
                {
                    using (var socket = new UdpClient { EnableBroadcast = true })
                        socket.Send(packet, packet.Length, new IPEndPoint(target, port <= 0 ? 9 : port));
                    sent++;
                }
                catch (Exception ex)
                {
                    Log.Write("WOL: " + target + " failed: " + ex.Message);
                }
            }

            Log.Write("WOL: sent to " + sent + " of " + targets.Count + " targets, port " + port);
            return sent > 0;
        }

        // x.y.z.255 for each up IPv4 interface: a packet aimed at the subnet
        // reaches machines a limited broadcast sometimes does not.
        private static List<IPAddress> DirectedBroadcasts()
        {
            var found = new List<IPAddress>();
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                    if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                    foreach (UnicastIPAddressInformation info in adapter.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (info.IPv4Mask == null) continue;

                        byte[] ip = info.Address.GetAddressBytes();
                        byte[] mask = info.IPv4Mask.GetAddressBytes();
                        var broadcast = new byte[4];
                        for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | (byte)~mask[i]);

                        var candidate = new IPAddress(broadcast);
                        if (!found.Contains(candidate)) found.Add(candidate);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("WOL: enumerating interfaces failed: " + ex.Message);
            }
            return found;
        }

        private static byte[] ParseMac(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;

            string digits = Regex.Replace(mac, "[^0-9A-Fa-f]", string.Empty);
            if (digits.Length != 12) return null;

            var address = new byte[6];
            for (int i = 0; i < 6; i++)
            {
                int value;
                if (!int.TryParse(digits.Substring(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                                  System.Globalization.CultureInfo.InvariantCulture, out value))
                    return null;
                address[i] = (byte)value;
            }
            return address;
        }
    }

    public static class HostProbe
    {
        /// <summary>
        /// Whether the host answers, and how long it took. A TCP port is checked
        /// instead of ICMP when one is given: plenty of machines drop pings but
        /// still serve SSH, and a closed firewall would otherwise read as asleep.
        /// </summary>
        public static HostState Check(string host, int tcpPort, int timeoutMs)
        {
            var state = new HostState();
            if (string.IsNullOrEmpty(host)) return state;

            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (tcpPort > 0)
                {
                    using (var client = new TcpClient())
                    {
                        IAsyncResult attempt = client.BeginConnect(host, tcpPort, null, null);
                        state.Up = attempt.AsyncWaitHandle.WaitOne(timeoutMs) && client.Connected;
                        if (state.Up) client.EndConnect(attempt);
                    }
                }
                else
                {
                    using (var ping = new Ping())
                    {
                        PingReply reply = ping.Send(host, timeoutMs);
                        state.Up = reply != null && reply.Status == IPStatus.Success;
                        if (state.Up) state.Milliseconds = (int)reply.RoundtripTime;
                    }
                }
            }
            catch (Exception)
            {
                state.Up = false; // unresolvable, unreachable or refused
            }

            if (state.Up && state.Milliseconds == 0)
                state.Milliseconds = Math.Max(1, (int)clock.ElapsedMilliseconds);

            return state;
        }
    }

    /// <summary>
    /// Finds a machine whose DNS name went stale (new DHCP lease, cable instead
    /// of Wi-Fi). Identity is the SSH host key already trusted in known_hosts,
    /// not the address: every host on the local subnets with the port open is
    /// asked for its key, and the one that matches is the machine. Connections
    /// to the address found pass -o HostKeyAlias=name so ssh still checks the
    /// key against the entry for the name.
    /// </summary>
    public static class HostFinder
    {
        private const int ReachMs = 800, ScanConnectMs = 300;
        private static readonly TimeSpan RescanEvery = TimeSpan.FromMinutes(2);
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, string> Found =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> ScannedAt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The address last found for the host, or null. Never blocks.</summary>
        public static string Cached(string host)
        {
            if (string.IsNullOrEmpty(host)) return null;
            lock (Gate) { string ip; return Found.TryGetValue(host, out ip) ? ip : null; }
        }

        /// <summary>
        /// Where to connect: the name itself when it answers, otherwise the
        /// address that carries its host key. Blocks for up to a few seconds;
        /// call it off the plugin thread.
        /// </summary>
        public static string Locate(string host, int port)
        {
            if (string.IsNullOrEmpty(host) || Reachable(host, port, ReachMs)) return host;

            string cached = Cached(host);
            if (cached != null && Reachable(cached, port, ReachMs)) return cached;

            lock (Gate)
            {
                DateTime last;
                if (ScannedAt.TryGetValue(host, out last) && DateTime.UtcNow - last < RescanEvery) return host;
                ScannedAt[host] = DateTime.UtcNow;
            }

            HashSet<string> keys = KnownKeys(host);
            if (keys.Count == 0) return host;

            string match = null;
            Parallel.ForEach(OpenHosts(port), new ParallelOptions { MaxDegreeOfParallelism = 16 }, (ip, loop) =>
            {
                if (!KeyMatches(ip, port, host)) return;
                match = ip;
                loop.Stop();
            });
            if (match == null) return host;
            lock (Gate) Found[host] = match;
            Log.Write("remote: " + host + " found at " + match + " by its host key");
            return match;
        }

        private static bool Reachable(string host, int port, int timeoutMs)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    IAsyncResult attempt = client.BeginConnect(host, port, null, null);
                    if (!attempt.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    client.EndConnect(attempt);
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        // "type base64" pairs listed for the name in ~/.ssh/known_hosts (plain
        // entries only; hashed ones cannot be matched back to a name).
        private static HashSet<string> KnownKeys(string host)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                           ".ssh", "known_hosts");
                if (!File.Exists(path)) return keys;
                foreach (string line in File.ReadAllLines(path))
                {
                    string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 3 || parts[0].StartsWith("#", StringComparison.Ordinal)) continue;
                    foreach (string name in parts[0].Split(','))
                    {
                        string bare = name.StartsWith("[", StringComparison.Ordinal)
                            ? name.Substring(1, Math.Max(0, name.IndexOf(']') - 1)) : name;
                        if (string.Equals(bare, host, StringComparison.OrdinalIgnoreCase))
                            keys.Add(parts[1] + " " + parts[2]);
                    }
                }
            }
            catch (Exception ex) { Log.Write("remote: known_hosts unreadable: " + ex.Message); }
            return keys;
        }

        // Every address on the machine's own IPv4 subnets (/22 or smaller) that
        // accepts a connection on the port.
        private static List<string> OpenHosts(int port)
        {
            var candidates = new List<string>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation address in nic.GetIPProperties().UnicastAddresses)
                {
                    if (address.Address.AddressFamily != AddressFamily.InterNetwork || address.IPv4Mask == null) continue;
                    uint ip = ToUInt(address.Address), mask = ToUInt(address.IPv4Mask);
                    uint size = ~mask;
                    if (size == 0 || size > 1023) continue;
                    uint network = ip & mask;
                    for (uint offset = 1; offset < size; offset++)
                        if (network + offset != ip) candidates.Add(FromUInt(network + offset).ToString());
                }
            }

            var open = new List<string>();
            Parallel.ForEach(candidates.Distinct(), new ParallelOptions { MaxDegreeOfParallelism = 64 }, candidate =>
            {
                if (Reachable(candidate, port, ScanConnectMs)) lock (open) open.Add(candidate);
            });
            return open;
        }

        // ssh itself checks the key (Windows' ssh-keyscan cannot talk to
        // OpenSSH 9 and returns nothing). No authentication method is offered,
        // so it never logs in: a matching key ends in "Permission denied", a
        // different one in "Host key verification failed".
        private static bool KeyMatches(string ip, int port, string host)
        {
            try
            {
                string arguments = "-o BatchMode=yes -o StrictHostKeyChecking=yes -o HostKeyAlias=" + host +
                                   " -o PreferredAuthentications=none -o ConnectTimeout=3 -p " + port +
                                   " probe@" + ip + " exit";
                var info = new ProcessStartInfo("ssh.exe", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (Process process = Process.Start(info))
                {
                    process.OutputDataReceived += delegate { };
                    process.BeginOutputReadLine();
                    string error = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(6000)) { try { process.Kill(); } catch (Exception) { } return false; }
                    return error.Contains("Permission denied") && !error.Contains("verification failed");
                }
            }
            catch (Exception ex) { Log.Write("remote: host key check failed: " + ex.Message); }
            return false;
        }

        private static uint ToUInt(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
        }

        private static IPAddress FromUInt(uint value)
        {
            return new IPAddress(new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value });
        }
    }
}
