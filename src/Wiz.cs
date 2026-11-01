using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web.Script.Serialization;

namespace StreamDeckAudio
{
    internal sealed class WizPilot
    {
        public bool On;
        public int SceneId;
        public int? Temp, Dimming, R, G, B, SchdPsetId;
        public bool InRhythm { get { return SchdPsetId != null && SceneId == 0; } }
    }

    internal static class WizProtocol
    {
        public static string GetPilot() { return "{\"method\":\"getPilot\",\"params\":{}}"; }
        public static string SetPilot(IDictionary<string, object> p)
        {
            return "{\"method\":\"setPilot\",\"params\":" + new JavaScriptSerializer().Serialize(p) + "}";
        }
        public static string Resume() { return "{\"method\":\"setPilot\",\"params\":{\"state\":true}}"; }

        internal static Dictionary<string, object> Result(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                if (data == null || data.ContainsKey("error")) return null;
                return Json.Dict(data, "result");
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static int? OptionalInt(Dictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null) return null;
            return Json.Int(data, key, 0);
        }

        public static WizPilot ParsePilot(string json)
        {
            Dictionary<string, object> p = Result(json);
            if (p == null) return null;
            return new WizPilot
            {
                On = Json.Bool(p, "state", false), SceneId = Json.Int(p, "sceneId", 0),
                Temp = OptionalInt(p, "temp"), Dimming = OptionalInt(p, "dimming"),
                R = OptionalInt(p, "r"), G = OptionalInt(p, "g"), B = OptionalInt(p, "b"),
                SchdPsetId = OptionalInt(p, "schdPsetId")
            };
        }

        public static string ParseMac(string json) { return Json.Str(Result(json), "mac"); }
    }

    internal interface IWizLink
    {
        WizPilot Get(string ip);
        bool Set(string ip, IDictionary<string, object> p);
    }

    // Operaciones sincrónicas: el hub debe llamarlas desde un hilo de fondo.
    internal sealed class WizClient : IWizLink
    {
        private const int Port = 38899;
        private const int TimeoutMs = 600;
        private const int Attempts = 3;
        private readonly Func<string, WizPilot> _get;
        private readonly Func<string, Dictionary<string, string>> _discover;
        private readonly object _discoveryLock = new object();
        private DateTime? _lastDiscovery;

        public WizClient() { _get = Get; _discover = Discover; }

        internal WizClient(Func<string, WizPilot> get, Func<string, Dictionary<string, string>> discover)
        {
            _get = get;
            _discover = discover;
        }

        private static string Exchange(string ip, string request, Func<string, bool> accept)
        {
            IPAddress address;
            if (!IPAddress.TryParse(ip, out address) || address.AddressFamily != AddressFamily.InterNetwork) return null;
            byte[] bytes = Encoding.UTF8.GetBytes(request);
            for (int attempt = 0; attempt < Attempts; attempt++)
            {
                try
                {
                    using (var udp = new UdpClient(AddressFamily.InterNetwork))
                    {
                        udp.Connect(address, Port);
                        udp.Send(bytes, bytes.Length);
                        var timer = Stopwatch.StartNew();
                        while (timer.ElapsedMilliseconds < TimeoutMs)
                        {
                            udp.Client.ReceiveTimeout = Math.Max(1, TimeoutMs - (int)timer.ElapsedMilliseconds);
                            var remote = new IPEndPoint(IPAddress.Any, 0);
                            string response = Encoding.UTF8.GetString(udp.Receive(ref remote));
                            if (accept(response)) return response;
                        }
                    }
                }
                catch (SocketException) { }
            }
            return null;
        }

        public WizPilot Get(string ip)
        {
            return WizProtocol.ParsePilot(Exchange(ip, WizProtocol.GetPilot(),
                delegate(string json) { return WizProtocol.ParsePilot(json) != null; }));
        }

        public bool Set(string ip, IDictionary<string, object> p)
        {
            return Exchange(ip, WizProtocol.SetPilot(p), delegate(string json)
            {
                return Json.Bool(WizProtocol.Result(json), "success", false);
            }) != null;
        }

        public Dictionary<string, string> Discover(string broadcast)
        {
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            IPAddress address;
            if (!IPAddress.TryParse(broadcast, out address) || address.AddressFamily != AddressFamily.InterNetwork) return found;
            byte[] request = Encoding.UTF8.GetBytes("{\"method\":\"getSystemConfig\",\"params\":{}}");
            try
            {
                using (var udp = new UdpClient(AddressFamily.InterNetwork))
                {
                    udp.EnableBroadcast = true;
                    for (int attempt = 0; attempt < Attempts; attempt++)
                    {
                        udp.Send(request, request.Length, new IPEndPoint(address, Port));
                        var timer = Stopwatch.StartNew();
                        while (timer.ElapsedMilliseconds < TimeoutMs)
                        {
                            udp.Client.ReceiveTimeout = Math.Max(1, TimeoutMs - (int)timer.ElapsedMilliseconds);
                            try
                            {
                                var remote = new IPEndPoint(IPAddress.Any, 0);
                                string mac = WizProtocol.ParseMac(Encoding.UTF8.GetString(udp.Receive(ref remote)));
                                if (!string.IsNullOrEmpty(mac)) found[mac] = remote.Address.ToString();
                            }
                            catch (SocketException) { break; }
                        }
                    }
                }
            }
            catch (SocketException) { }
            return found;
        }

        public string EnsureIp(IotConfig cfg, IotLight light, Func<DateTime> now)
        {
            if (_get(light.Ip) != null) return light.Ip;
            lock (_discoveryLock)
            {
                DateTime time = now();
                if (_lastDiscovery.HasValue && time - _lastDiscovery.Value < TimeSpan.FromSeconds(60)) return null;
                // Registrar también intentos sin resultados para no saturar la red.
                _lastDiscovery = time;
                Dictionary<string, string> found = _discover(cfg.Broadcast);
                foreach (KeyValuePair<string, string> item in found)
                {
                    if (!string.Equals(item.Key, light.Mac, StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.Value != light.Ip) cfg.SaveLightIp(light, item.Value);
                    return item.Value;
                }
                return null;
            }
        }
    }
}
