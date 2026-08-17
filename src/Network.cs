// Wake-on-LAN and reachability checks.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

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
}
