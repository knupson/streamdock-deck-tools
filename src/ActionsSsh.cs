// SSH key: a live tile showing whether a machine is accepting SSH, and a press
// that opens a session against it in a terminal.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace StreamDeckAudio
{
    internal sealed class SshAction : ActionBase
    {
        private const int HistoryLength = 22;
        private const int ProbeTimeoutMs = 800;
        private const int DefaultPort = 22;

        // Hostnames, usernames and ports end up on a command line, so they are held
        // to what those can legitimately contain rather than escaped afterwards.
        private static readonly Regex Safe = new Regex(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

        private sealed class Watch
        {
            public bool Up;
            public int Milliseconds;
            public bool Probed;
            public DateTime CheckedAt;
            public bool Busy;
            public readonly Queue<float> History = new Queue<float>();
        }

        private sealed class Command
        {
            public string File;
            public string Arguments;
        }

        private readonly Dictionary<string, Watch> _watches = new Dictionary<string, Watch>(StringComparer.Ordinal);

        public override string Uuid { get { return "com.knupwns.audioknob.ssh"; } }

        // Keys, per the manifest.
        protected override bool DefaultsToKnob { get { return false; } }

        public override bool WantsTick { get { return Contexts.Count > 0; } }

        public override void WillAppear(string context, Dictionary<string, object> settings, string controller)
        {
            _watches[context] = new Watch();
            base.WillAppear(context, settings, controller);
        }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _watches.Remove(context);
        }

        public override void SettingsChanged(string context, Dictionary<string, object> settings)
        {
            // A new host invalidates the history: keeping it would draw one
            // machine's latency under another machine's name.
            _watches[context] = new Watch();
            base.SettingsChanged(context, settings);
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            if (Json.Str(payload, "command") == "connect") { Press(context); return; }

            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        /// <summary>
        /// Opens the session. A host that is down is still dialled: the connection
        /// error belongs in the terminal, where it can be read, rather than being
        /// swallowed here.
        /// </summary>
        public override void Press(string context)
        {
            Command command = Build(context);
            if (command == null)
            {
                Log.Write("SSH: nothing to launch - check the host, user and port");
                Host.ShowAlert(context);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(command.File, command.Arguments) { UseShellExecute = true });
                Log.Write("SSH: launched " + command.File + " " + command.Arguments);
            }
            catch (Exception ex)
            {
                Log.Write("SSH: launching " + command.File + " failed: " + ex.Message);
                Host.ShowAlert(context);
                return;
            }

            // The machine was just dialled, so what the tile says about it should
            // be current rather than up to ten seconds old.
            if (Watching(context)) Probe(context);
        }

        public override void Tick()
        {
            foreach (string context in Contexts.Keys.ToList())
            {
                if (!Watching(context)) continue;

                Watch watch = Of(context);
                int interval = Math.Max(2, Int(context, "interval", 10));
                if ((DateTime.UtcNow - watch.CheckedAt).TotalSeconds < interval) continue;

                Probe(context);
            }
        }

        // Off the pump: a connection attempt against a host that is not there takes
        // the full timeout, and every other action runs on that same thread.
        private void Probe(string context)
        {
            Watch watch = Of(context);
            if (watch.Busy) return;

            string host = Str(context, "host", null);
            if (string.IsNullOrEmpty(host))
            {
                Render(context);
                return;
            }

            int port = Port(context);
            watch.Busy = true;
            watch.CheckedAt = DateTime.UtcNow;

            ThreadPool.QueueUserWorkItem(delegate
            {
                // The SSH port itself, not ICMP: a machine that pings but has no
                // sshd is not a machine this key can do anything with.
                HostState state = HostProbe.Check(host, port, ProbeTimeoutMs);
                Host.Invoke(delegate
                {
                    watch.Busy = false;
                    watch.Probed = true;
                    watch.Up = state.Up;
                    watch.Milliseconds = state.Milliseconds;

                    // Latency against a 200ms ceiling, so a LAN host sits low and
                    // something over a VPN visibly rides higher.
                    watch.History.Enqueue(state.Up ? Math.Min(1f, state.Milliseconds / 200f) : 0f);
                    while (watch.History.Count > HistoryLength) watch.History.Dequeue();

                    Render(context);
                });
            });
        }

        protected override void Render(string context)
        {
            try
            {
                bool knob = OnKnob(context);
                string host = Str(context, "host", null);
                if (string.IsNullOrEmpty(host))
                {
                    Show(context, KeyImage.Glyph("ssh", "Set host", null, knob));
                    return;
                }

                string label = Labels.Host(Str(context, "name", null) ?? host);
                if (!Watching(context))
                {
                    Show(context, KeyImage.Glyph("ssh", label, null, knob));
                    return;
                }

                Watch watch = Of(context);
                string value;
                Color colour;
                if (!watch.Probed) { value = "..."; colour = KeyImage.Idle; }
                else if (watch.Up) { value = watch.Milliseconds + "ms"; colour = KeyImage.Good; }
                else { value = "DOWN"; colour = KeyImage.Bad; }

                Show(context, KeyImage.Glyph("ssh", label, value, knob, colour));
            }
            catch (Exception ex)
            {
                Log.Write("SshAction.Render failed: " + ex.Message);
            }
        }

        private void Show(string context, string image)
        {
            Host.SetImage(context, image);
            Host.SetTitle(context, string.Empty);
        }

        private bool Watching(string context)
        {
            return Bool(context, "watch", true);
        }

        private int Port(string context)
        {
            int port = Int(context, "port", DefaultPort);
            return port < 1 || port > 65535 ? DefaultPort : port;
        }

        #region launching

        /// <summary>
        /// What to run for this key, or null when the settings cannot produce a
        /// command - an empty host, or one carrying characters a hostname has no
        /// business carrying.
        /// </summary>
        private Command Build(string context)
        {
            string host = Str(context, "host", null);
            if (string.IsNullOrEmpty(host) || !Safe.IsMatch(host)) return null;

            string user = Str(context, "user", null);
            if (!string.IsNullOrEmpty(user) && !Safe.IsMatch(user)) return null;

            int port = Port(context);
            string target = string.IsNullOrEmpty(user) ? host : user + "@" + host;

            // The default port is left off: "ssh pi4" is what a person would type,
            // and it is what shows up in the log.
            string arguments = (port == DefaultPort ? string.Empty : "-p " + port + " ") + target;

            switch (Str(context, "terminal", "wt"))
            {
                case "console":
                    return new Command { File = "ssh.exe", Arguments = arguments };

                case "powershell":
                    return new Command
                    {
                        File = "powershell.exe",
                        Arguments = "-NoExit -Command ssh " + arguments
                    };

                case "cmd":
                    return new Command { File = "cmd.exe", Arguments = "/k ssh " + arguments };

                case "custom":
                    return Custom(context, host, user, port, target);

                default:
                    return new Command { File = "wt.exe", Arguments = "ssh " + arguments };
            }
        }

        // A whole command line typed by hand, with the connection details
        // substituted in. This is how PuTTY, an identity file or a jump host get
        // in without every one of them needing a field of its own.
        private Command Custom(string context, string host, string user, int port, string target)
        {
            string template = Str(context, "command", null);
            if (string.IsNullOrEmpty(template)) return null;

            return Split(template
                .Replace("{host}", host)
                .Replace("{user}", user ?? string.Empty)
                .Replace("{port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("{target}", target));
        }

        // Splits a command line into program and arguments without handing it to a
        // shell, so nothing in the template is re-interpreted on the way.
        private static Command Split(string command)
        {
            string trimmed = command.Trim();
            if (trimmed.Length == 0) return null;

            if (trimmed[0] == '"')
            {
                int close = trimmed.IndexOf('"', 1);
                if (close < 0) return null;

                return new Command
                {
                    File = trimmed.Substring(1, close - 1),
                    Arguments = trimmed.Substring(close + 1).Trim()
                };
            }

            int space = trimmed.IndexOf(' ');
            if (space < 0) return new Command { File = trimmed, Arguments = string.Empty };

            return new Command
            {
                File = trimmed.Substring(0, space),
                Arguments = trimmed.Substring(space + 1).Trim()
            };
        }

        #endregion

        private Watch Of(string context)
        {
            Watch watch;
            if (!_watches.TryGetValue(context, out watch))
            {
                watch = new Watch();
                _watches[context] = watch;
            }
            return watch;
        }
    }
}
