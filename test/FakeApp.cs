// Test harness: pretends to be the StreamDock software so the plugin can be
// exercised without touching the hardware.
//
// It opens a WebSocket server, launches the plugin with the same command line
// the host uses, then feeds it willAppear / dialRotate / dialPress and checks
// what comes back and whether the default output actually moved.
//
//   FakeApp.exe <path to AudioKnob.exe>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace StreamDeckAudio
{
    internal static class FakeApp
    {
        private const string Context = "ctx-test-1";
        private const string ActionUuid = "com.knupwns.audioknob.action1";
        private const string AppVolumeUuid = "com.knupwns.audioknob.appvolume";
        private const string BrightnessUuid = "com.knupwns.audioknob.brightness";
        private const string SysMonUuid = "com.knupwns.audioknob.sysmon";
        private const string LauncherUuid = "com.knupwns.audioknob.launcher";
        private const string HostStatusUuid = "com.knupwns.audioknob.hoststatus";

        private static readonly List<string> Received = new List<string>();
        private static NetworkStream _stream;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("usage: FakeApp.exe <path to AudioKnob.exe>");
                return 1;
            }

            List<AudioDevice> devices = AudioDevices.ListActiveOutputs();
            if (devices.Count < 2)
            {
                Console.WriteLine("SKIP: need at least two active output devices, found " + devices.Count);
                return 3;
            }

            string startId = AudioDevices.GetDefaultOutputId();
            Console.WriteLine("devices:");
            foreach (AudioDevice d in devices)
                Console.WriteLine("  " + (d.Id == startId ? "* " : "  ") + d.Name);

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;

            Process plugin = StartPlugin(args[0], port);
            try
            {
                using (TcpClient client = listener.AcceptTcpClient())
                {
                    _stream = client.GetStream();
                    Handshake();

                    string register = ReadMessage(5000);
                    Console.WriteLine("register: " + register);
                    bool registered = register != null && register.Contains("registerPlugin");

                    var reader = new Thread(ReadLoop) { IsBackground = true };
                    reader.Start();

                    // The knob starts on device A, so a press must land on B.
                    SendWillAppear(devices[0].Id, devices[1].Id);
                    Thread.Sleep(400);

                    bool volumeMoved = CheckRotation();

                    if (startId != devices[0].Id)
                    {
                        AudioDevices.SetDefaultOutput(devices[0].Id);
                        Thread.Sleep(200);
                    }

                    Console.WriteLine("-> dialPress (pressed)");
                    Send(Event("dialPress", "\"pressed\":true"));
                    Thread.Sleep(800);
                    string afterFirst = AudioDevices.GetDefaultOutputId();

                    Console.WriteLine("-> dialPress (pressed) again");
                    Send(Event("dialPress", "\"pressed\":true"));
                    Thread.Sleep(800);
                    string afterSecond = AudioDevices.GetDefaultOutputId();

                    bool appVolume = CheckAppVolume();
                    bool brightness = CheckBrightness();
                    bool sysmon = CheckSystemMonitor();
                    bool launcher = CheckLauncher();
                    bool hostWatch = CheckHostStatus();

                    Console.WriteLine();
                    Console.WriteLine("messages from plugin: " + Received.Count +
                                      " (images stripped for readability)");
                    lock (Received)
                        foreach (string message in Received) Console.WriteLine("  " + Summarize(message));

                    bool switchedTo = afterFirst == devices[1].Id;
                    bool switchedBack = afterSecond == devices[0].Id;
                    bool sawState;
                    lock (Received) sawState = Received.Any(m => m.Contains("\"setState\""));
                    int previews = SaveImagePreviews(AppDomain.CurrentDomain.BaseDirectory);

                    Console.WriteLine();
                    Report("registered", registered);
                    Report("output: rotate changed the volume", volumeMoved);
                    Report("output: press switched A -> B", switchedTo);
                    Report("output: press switched B -> A", switchedBack);
                    Report("output: plugin reported setState", sawState);
                    Report("output: key images sent (" + previews + " saved)", previews > 0);
                    Report("app volume: rotate moved one app's level", appVolume);
                    Report("brightness: rotate reached the panel over DDC/CI", brightness);
                    Report("system monitor: tile refreshed on its own", sysmon);
                    Report("launcher: a tile per shortcut, with its icon", launcher);
                    Report("host watch: wake packet on the wire, both states drawn", hostWatch);

                    if (startId != null) AudioDevices.SetDefaultOutput(startId);

                    bool ok = registered && volumeMoved && switchedTo && switchedBack &&
                              sawState && previews > 0 && appVolume && brightness && sysmon &&
                              launcher && hostWatch;
                    Console.WriteLine(ok ? "RESULT: PASS" : "RESULT: FAIL");
                    return ok ? 0 : 1;
                }
            }
            finally
            {
                listener.Stop();
                try { if (!plugin.HasExited) plugin.Kill(); }
                catch (Exception) { /* already gone */ }
            }
        }

        // Drives the knob in the direction that has room to move, then puts the
        // volume back where it was.
        private static bool CheckRotation()
        {
            float start = EndpointVolume.GetScalar();
            int ticks = start > 0.1f ? -3 : 3;

            Console.WriteLine("-> dialRotate " + Signed(ticks) + "  (volume " + Percent(start) + ")");
            Send(Event("dialRotate", "\"ticks\":" + ticks + ",\"pressed\":false"));
            Thread.Sleep(600);

            float moved = EndpointVolume.GetScalar();
            Console.WriteLine("   volume now " + Percent(moved));

            Console.WriteLine("-> dialRotate " + Signed(-ticks));
            Send(Event("dialRotate", "\"ticks\":" + (-ticks) + ",\"pressed\":false"));
            Thread.Sleep(600);
            Console.WriteLine("   volume back to " + Percent(EndpointVolume.GetScalar()));

            EndpointVolume.SetScalar(start);
            return Math.Abs(moved - start) > 0.005f;
        }

        #region other actions

        // Moves a real application's volume, then puts it back.
        private static bool CheckAppVolume()
        {
            Console.WriteLine();
            List<AppSession> sessions = AudioSessions.List();
            if (sessions.Count == 0)
            {
                Console.WriteLine("app volume: SKIPPED - no application is holding an audio session");
                return true;
            }

            string app = sessions[0].ProcessName;
            float before;
            bool muted;
            AudioSessions.TryRead(app, out before, out muted);
            Console.WriteLine("app volume: target " + app + " at " + Percent(before));

            SendWillAppear(AppVolumeUuid, "ctx-appvol",
                "\"mode\":\"app\",\"app\":\"" + app + "\",\"step\":10", "Knob");
            Thread.Sleep(400);

            int direction = before > 0.15f ? -1 : 1;
            Send(Event(AppVolumeUuid, "ctx-appvol", "dialRotate", "\"ticks\":" + direction));
            Thread.Sleep(700);

            float after;
            AudioSessions.TryRead(app, out after, out muted);
            Console.WriteLine("app volume: now " + Percent(after));

            float restore;
            AudioSessions.Adjust(app, before - after, out restore, out muted);
            return Math.Abs(after - before) > 0.01f && ImagesFor("ctx-appvol") > 0;
        }

        // Drives the real backlight one step and restores it.
        private static bool CheckBrightness()
        {
            Console.WriteLine();
            List<MonitorTarget> monitors = Monitors.List(true);
            MonitorTarget panel = monitors.Find(m => m.Supported);
            if (panel == null)
            {
                Console.WriteLine("brightness: SKIPPED - no monitor accepts DDC/CI");
                return true;
            }

            int before = panel.Percent;
            Console.WriteLine("brightness: " + panel.Label + " at " + before + "%");

            SendWillAppear(BrightnessUuid, "ctx-bright",
                "\"monitor\":\"" + panel.Index + "\",\"step\":10", "Knob");
            Thread.Sleep(400);

            int direction = before > 20 ? -1 : 1;
            Send(Event(BrightnessUuid, "ctx-bright", "dialRotate", "\"ticks\":" + direction));
            Thread.Sleep(1200);

            List<MonitorTarget> after = Monitors.List(true);
            MonitorTarget again = after.Find(m => m.Index == panel.Index);
            int now = again == null ? before : again.Percent;
            Console.WriteLine("brightness: now " + now + "%");

            if (again != null)
            {
                int restored;
                Monitors.SetPercent(again, before, out restored);
            }
            return now != before && ImagesFor("ctx-bright") > 0;
        }

        // Real shortcuts copied out of the Start Menu, so the icons being resolved
        // are the ones a user would actually put in the folder. Nothing is
        // launched: that would open applications on the machine running the test.
        private static bool CheckLauncher()
        {
            Console.WriteLine();
            string folder = Path.Combine(Path.GetTempPath(), "deck-launcher-test");
            var copied = new List<string>();

            try
            {
                Directory.CreateDirectory(folder);
                foreach (string link in StartMenuShortcuts().Take(3))
                {
                    string destination = Path.Combine(folder, Path.GetFileName(link));
                    File.Copy(link, destination, true);
                    copied.Add(Path.GetFileNameWithoutExtension(link));
                }

                if (copied.Count == 0)
                {
                    Console.WriteLine("launcher: SKIPPED - no shortcuts found in the Start Menu");
                    return true;
                }
                Console.WriteLine("launcher: " + string.Join(", ", copied.ToArray()));

                SendWillAppear(LauncherUuid, "ctx-launch",
                    "\"folder\":\"" + folder.Replace("\\", "\\\\") + "\",\"showName\":true", "Knob");
                Thread.Sleep(700);

                for (int step = 1; step < copied.Count; step++)
                {
                    Send(Event(LauncherUuid, "ctx-launch", "dialRotate", "\"ticks\":1"));
                    Thread.Sleep(500);
                }

                int images = ImagesFor("ctx-launch");
                Console.WriteLine("launcher: " + images + " tiles for " + copied.Count + " items");

                // The same folder with the name hidden: that layout centres the
                // icon on the whole tile instead of below a caption.
                SendWillAppear(LauncherUuid, "ctx-launch-bare",
                    "\"folder\":\"" + folder.Replace("\\", "\\\\") + "\",\"showName\":false", "Knob");
                Thread.Sleep(700);

                int bare = ImagesFor("ctx-launch-bare");
                Console.WriteLine("launcher: " + bare + " tile with the name hidden");
                return images >= copied.Count && bare > 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("launcher: FAILED - " + ex.Message);
                return false;
            }
            finally
            {
                try { Directory.Delete(folder, true); }
                catch (Exception) { /* leave it for the temp cleaner */ }
            }
        }

        // A host that cannot answer reads as down, and pressing it then has to put a
        // real magic packet on the wire. The packet is caught on a spare port
        // rather than 9, and checked byte for byte.
        private static bool CheckHostStatus()
        {
            Console.WriteLine();
            const int wolPort = 9998;
            const string mac = "AA:BB:CC:DD:EE:FF";

            // TEST-NET-3: reserved for documentation, so nothing will reply.
            const string unreachable = "203.0.113.1";

            using (var listener = new UdpClient(new IPEndPoint(IPAddress.Any, wolPort)))
            {
                listener.Client.ReceiveTimeout = 4000;

                SendWillAppear(HostStatusUuid, "ctx-host",
                    "\"name\":\"Test\",\"host\":\"" + unreachable + "\",\"mac\":\"" + mac +
                    "\",\"wolPort\":" + wolPort + ",\"interval\":3", "Keypad");
                Thread.Sleep(2000);

                Console.WriteLine("host watch: pressing while the host is unreachable");
                Send(Event(HostStatusUuid, "ctx-host", "keyDown", "\"state\":0"));

                byte[] packet = null;
                try
                {
                    IPEndPoint from = null;
                    packet = listener.Receive(ref from);
                    Console.WriteLine("host watch: caught " + packet.Length + " bytes from " + from);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("host watch: no packet - " + ex.Message);
                }

                bool wellFormed = packet != null && packet.Length == 102;
                if (wellFormed)
                {
                    for (int i = 0; i < 6; i++) wellFormed &= packet[i] == 0xFF;

                    byte[] expected = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
                    for (int repeat = 0; repeat < 16 && wellFormed; repeat++)
                        for (int i = 0; i < 6; i++)
                            wellFormed &= packet[6 + repeat * 6 + i] == expected[i];
                }
                Console.WriteLine("host watch: magic packet " + (wellFormed ? "well formed" : "WRONG"));

                // And a host that does answer, so the reachable path renders too.
                SendWillAppear(HostStatusUuid, "ctx-host-up",
                    "\"name\":\"loopback\",\"host\":\"127.0.0.1\",\"interval\":3", "Keypad");
                Thread.Sleep(2500);

                int tiles = ImagesFor("ctx-host") + ImagesFor("ctx-host-up");
                Console.WriteLine("host watch: " + tiles + " tiles across both hosts");
                return wellFormed && ImagesFor("ctx-host-up") > 0 && ImagesFor("ctx-host") > 0;
            }
        }

        private static IEnumerable<string> StartMenuShortcuts()
        {
            var roots = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
            };

            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;

                string[] found;
                try { found = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories); }
                catch (Exception) { continue; }

                foreach (string link in found) yield return link;
            }
        }

        // The tile has to redraw itself on its own timer.
        private static bool CheckSystemMonitor()
        {
            Console.WriteLine();
            SendWillAppear(SysMonUuid, "ctx-sysmon", "\"metric\":\"cpu\",\"interval\":1", "Keypad");
            Thread.Sleep(2600);

            int images = ImagesFor("ctx-sysmon");
            Console.WriteLine("system monitor: " + images + " tiles received in 2.6s");
            return images >= 2;
        }

        private static int ImagesFor(string context)
        {
            lock (Received)
                return Received.Count(m => m.Contains("\"setImage\"") &&
                                           m.Contains("\"context\":\"" + context + "\""));
        }

        private static string Summarize(string message)
        {
            return Regex.Replace(message, @"""image"":""data:image/png;base64,[^""]+""",
                                 @"""image"":""<png>""");
        }

        #endregion

        // Writes every distinct setImage payload to disk so the composed key can
        // be eyeballed without the hardware.
        private static int SaveImagePreviews(string dir)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string[] messages;
            lock (Received) messages = Received.ToArray();

            int saved = 0;
            foreach (string message in messages)
            {
                Match match = Regex.Match(message,
                    @"""setImage"".*?""image"":""data:image/png;base64,([A-Za-z0-9+/=]+)""");
                if (!match.Success || !seen.Add(match.Groups[1].Value)) continue;

                string path = Path.Combine(dir, "key-preview-" + saved + ".png");
                File.WriteAllBytes(path, Convert.FromBase64String(match.Groups[1].Value));
                Console.WriteLine("  saved " + path);
                saved++;
            }
            return saved;
        }

        private static string Signed(int value)
        {
            return value > 0 ? "+" + value : value.ToString();
        }

        private static string Percent(float scalar)
        {
            return Math.Round(scalar * 100f) + "%";
        }

        private static void Report(string what, bool ok)
        {
            Console.WriteLine((ok ? "  [ok]   " : "  [FAIL] ") + what);
        }

        private static Process StartPlugin(string exe, int port)
        {
            string info = "{\"application\":{\"language\":\"en\",\"platform\":\"windows\",\"version\":\"3.10\"}}";
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.GetFullPath(exe),
                Arguments = "-port " + port + " -pluginUUID fake-plugin-uuid" +
                            " -registerEvent registerPlugin -info " + Quote(info),
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))
            };
            Console.WriteLine("launching: " + startInfo.FileName + " " + startInfo.Arguments);
            return Process.Start(startInfo);
        }

        private static string Quote(string text)
        {
            return "\"" + text.Replace("\"", "\\\"") + "\"";
        }

        #region minimal WebSocket server

        private static void Handshake()
        {
            var request = new StringBuilder();
            var one = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (_stream.Read(one, 0, 1) != 1) throw new IOException("connection closed during handshake");
                request.Append((char)one[0]);
            }

            Match key = Regex.Match(request.ToString(), @"Sec-WebSocket-Key:\s*(\S+)", RegexOptions.IgnoreCase);
            if (!key.Success) throw new IOException("no Sec-WebSocket-Key in handshake");

            string accept;
            using (SHA1 sha = SHA1.Create())
            {
                accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(
                    key.Groups[1].Value + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            }

            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
            _stream.Write(response, 0, response.Length);
        }

        private static void Send(string text)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            var frame = new List<byte> { 0x81 }; // FIN + text

            if (payload.Length < 126)
            {
                frame.Add((byte)payload.Length);
            }
            else
            {
                frame.Add(126);
                frame.Add((byte)(payload.Length >> 8));
                frame.Add((byte)(payload.Length & 0xFF));
            }
            frame.AddRange(payload);

            byte[] bytes = frame.ToArray();
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
        }

        private static string ReadMessage(int timeoutMs)
        {
            _stream.ReadTimeout = timeoutMs;
            try
            {
                byte[] header = ReadExactly(2);
                bool masked = (header[1] & 0x80) != 0;
                long length = header[1] & 0x7F;

                if (length == 126)
                {
                    byte[] extended = ReadExactly(2);
                    length = (extended[0] << 8) | extended[1];
                }
                else if (length == 127)
                {
                    byte[] extended = ReadExactly(8);
                    length = 0;
                    for (int i = 0; i < 8; i++) length = (length << 8) | extended[i];
                }

                byte[] mask = masked ? ReadExactly(4) : null;
                byte[] payload = ReadExactly((int)length);
                if (masked)
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];

                return Encoding.UTF8.GetString(payload);
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static byte[] ReadExactly(int count)
        {
            var buffer = new byte[count];
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, read, count - read);
                if (n <= 0) throw new IOException("connection closed");
                read += n;
            }
            return buffer;
        }

        private static void ReadLoop()
        {
            while (true)
            {
                string message = ReadMessage(60000);
                if (message == null) return;
                lock (Received) Received.Add(message);
            }
        }

        #endregion

        #region event payloads

        private static void SendWillAppear(string deviceA, string deviceB)
        {
            SendWillAppear(ActionUuid, Context,
                "\"deviceA\":\"" + deviceA + "\",\"deviceB\":\"" + deviceB + "\",\"step\":1,\"showTitle\":true",
                "Knob");
        }

        private static void SendWillAppear(string action, string context, string settings, string controller)
        {
            Send("{\"action\":\"" + action + "\",\"event\":\"willAppear\",\"context\":\"" + context +
                 "\",\"device\":\"fake-device\",\"payload\":{\"settings\":{" + settings +
                 "},\"coordinates\":{\"column\":0,\"row\":0},\"controller\":\"" + controller + "\",\"state\":0," +
                 "\"isInMultiAction\":false}}");
        }

        private static string Event(string name, string extraPayload)
        {
            return Event(ActionUuid, Context, name, extraPayload);
        }

        private static string Event(string action, string context, string name, string extraPayload)
        {
            return "{\"action\":\"" + action + "\",\"event\":\"" + name + "\",\"context\":\"" + context +
                   "\",\"device\":\"fake-device\",\"payload\":{" + extraPayload +
                   ",\"controller\":\"Knob\",\"coordinates\":{\"column\":0,\"row\":0}}}";
        }

        #endregion
    }
}
