// Remote monitor: the same live metric tile as the local System Monitor, but for
// a machine across the network. One reading per host serves every tile pointed at
// it - four keys watching pi4 are one SSH connection, not four.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace StreamDeckAudio
{
    internal sealed class RemoteMonitorAction : ActionBase
    {
        private const int HistoryLength = 22;

        // Generous: this is a whole SSH handshake, not a counter read. The Windows
        // OpenSSH client has no ControlMaster, so every poll pays for one.
        private const int PollTimeoutMs = 12000;

        // Where the installer puts it. Sent explicitly even though an authorised
        // key runs it as a forced command regardless: without a command of its own
        // an SSH session asks for a shell, and a host whose key is *not* restricted
        // would hand one over and never return.
        private const string RemoteScript = ".local/bin/deckstats";

        private static readonly string[] Order = { "cpu", "mem", "temp", "disk", "up" };

        private sealed class Reading
        {
            public DateTime At;
            public DateTime StartedAt;
            public bool Busy;
            public bool Ok;
            public readonly Dictionary<string, float> Values =
                new Dictionary<string, float>(StringComparer.Ordinal);
        }

        // Keyed by machine rather than by key, which is what makes several tiles
        // share one poll.
        private readonly Dictionary<string, Reading> _readings =
            new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, Queue<float>> _history =
            new Dictionary<string, Queue<float>>(StringComparer.Ordinal);

        // What each tile last drew, so a tile is only recomposed when its host has
        // actually reported something new.
        private readonly Dictionary<string, DateTime> _drawn =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public override string Uuid { get { return "com.knupwns.audioknob.remotestats"; } }

        protected override bool DefaultsToKnob { get { return false; } }

        public override bool WantsTick { get { return Contexts.Count > 0; } }

        public override void WillAppear(string context, Dictionary<string, object> settings, string controller)
        {
            _history[context] = new Queue<float>();
            _drawn.Remove(context);
            base.WillAppear(context, settings, controller);
        }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _history.Remove(context);
            _drawn.Remove(context);

            // A machine nothing is watching any more should not keep being polled.
            Forget();
        }

        public override void SettingsChanged(string context, Dictionary<string, object> settings)
        {
            if (settings != null) Contexts[context] = settings;

            _history[context] = new Queue<float>(); // the scale may have changed
            _drawn.Remove(context);

            Forget();
            Render(context);
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            if (Json.Str(payload, "command") == "refresh")
            {
                Reading reading = Of(context);
                if (reading != null) reading.At = DateTime.MinValue;
                return;
            }

            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        /// <summary>
        /// Reads the machine now rather than at the next interval. There is
        /// nothing to cycle through any more: a tile shows every metric it was
        /// given at once.
        /// </summary>
        public override void Press(string context)
        {
            Reading reading = Of(context);
            reading.At = DateTime.MinValue;
            _drawn.Remove(context);
        }

        public override void Tick()
        {
            foreach (string context in Contexts.Keys.ToList())
            {
                string machine = Machine(context);
                if (machine == null) continue;

                Reading reading = Of(context);
                int interval = Math.Max(5, Int(context, "interval", 20));

                if (!reading.Busy && (DateTime.UtcNow - reading.At).TotalSeconds >= interval)
                    Poll(context, machine, reading);

                // Drawn from whatever the shared reading holds, which may have been
                // fetched for a different tile a moment ago.
                DateTime drawn;
                if (!_drawn.TryGetValue(context, out drawn) || drawn != reading.At)
                    Render(context);
            }
        }

        // Off the pump: an unreachable host costs the connect timeout, and every
        // other action on the deck runs on that same thread.
        private void Poll(string context, string machine, Reading reading)
        {
            string identity = Identity(context);
            string user = Str(context, "user", null);
            string host = Str(context, "host", null);
            int port = Int(context, "port", 22);

            reading.Busy = true;
            reading.StartedAt = DateTime.UtcNow;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string output = Run(identity, user, host, port);
                Host.Invoke(delegate
                {
                    reading.Busy = false;
                    reading.At = DateTime.UtcNow;
                    reading.Values.Clear();
                    reading.Ok = Parse(output, reading.Values);

                    if (!reading.Ok) Log.Write("remote: no reading from " + machine + ": " + Trim(output));
                });
            });
        }

        private static string Run(string identity, string user, string host, int port)
        {
            var arguments = new StringBuilder();
            if (!string.IsNullOrEmpty(identity))
                arguments.Append("-i \"").Append(identity).Append("\" -o IdentitiesOnly=yes ");

            // BatchMode so a host that wants a password fails instead of waiting for
            // one, and -T so a key that turns out not to be restricted cannot open
            // an interactive shell this would then sit inside until the timeout.
            arguments.Append("-o BatchMode=yes -o ConnectTimeout=5 -o StrictHostKeyChecking=accept-new ");
            arguments.Append("-p ").Append(port.ToString(CultureInfo.InvariantCulture)).Append(" -T ");
            arguments.Append(string.IsNullOrEmpty(user) ? host : user + "@" + host);
            arguments.Append(' ').Append(RemoteScript);

            var info = new ProcessStartInfo("ssh.exe", arguments.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var captured = new StringBuilder();
            try
            {
                using (Process process = Process.Start(info))
                {
                    if (process == null) return string.Empty;

                    // Read as it arrives: filling either pipe's buffer with nobody
                    // draining it is the classic way to deadlock a child process.
                    DataReceivedEventHandler collect = delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) lock (captured) captured.AppendLine(e.Data);
                    };

                    process.OutputDataReceived += collect;
                    process.ErrorDataReceived += collect;
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (!process.WaitForExit(PollTimeoutMs))
                    {
                        try { process.Kill(); } catch (Exception) { /* already gone */ }
                        lock (captured) captured.AppendLine("timed out");
                    }
                }
            }
            catch (Exception ex)
            {
                return "could not run ssh.exe: " + ex.Message;
            }

            lock (captured) return captured.ToString();
        }

        /// <summary>
        /// Reads the one line deckstats prints. Anything else - an SSH error, a
        /// login banner, an empty reply - simply does not parse, which is the same
        /// answer as the host being down.
        /// </summary>
        private static bool Parse(string output, Dictionary<string, float> into)
        {
            if (string.IsNullOrEmpty(output)) return false;

            foreach (string line in output.Split('\n'))
            {
                if (line.IndexOf("cpu=", StringComparison.Ordinal) < 0) continue;

                foreach (string pair in line.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int equals = pair.IndexOf('=');
                    if (equals <= 0) continue;

                    float value;
                    if (!float.TryParse(pair.Substring(equals + 1), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out value)) continue;

                    into[pair.Substring(0, equals)] = value;
                }
                return into.Count > 0;
            }
            return false;
        }

        protected override void Render(string context)
        {
            try
            {
                bool knob = OnKnob(context);
                string host = Str(context, "host", null);
                if (string.IsNullOrEmpty(host))
                {
                    Host.SetImage(context, KeyImage.Glyph("remotestats", "Set host", null, knob));
                    Host.SetTitle(context, string.Empty);
                    return;
                }

                // Left off when the key sits directly above or below an SSH key for
                // the same machine: the name is already on the deck, and the room
                // it costs is a whole row of figures.
                string caption = Bool(context, "showName", true)
                    ? Labels.Host(Str(context, "name", null) ?? host)
                    : null;

                Reading reading = Of(context);
                _drawn[context] = reading.At;

                string[] selected = Selected(context);
                if (selected.Length > 1)
                {
                    Host.SetImage(context, KeyImage.Stats(caption, Rows(reading, selected), knob));
                    Host.SetTitle(context, string.Empty);
                    return;
                }

                // A single figure gets the tile to itself: the big value and the
                // curve say more than one row would.
                string metric = selected[0];
                caption = string.IsNullOrEmpty(caption) ? Caption(metric) : caption + " " + Caption(metric);

                string text;
                float fraction;
                Color accent;
                bool plotted = Value(reading, metric, out text, out fraction, out accent);

                Queue<float> history;
                if (!_history.TryGetValue(context, out history))
                {
                    history = new Queue<float>();
                    _history[context] = history;
                }

                // Uptime has no ceiling to draw against, so it gets a number and no
                // curve rather than a curve that means nothing.
                if (plotted && reading.At != DateTime.MinValue)
                {
                    history.Enqueue(fraction);
                    while (history.Count > HistoryLength) history.Dequeue();
                }

                float[] samples = plotted && history.Count > 1 ? history.ToArray() : null;

                // No accent means no threshold worth colouring - uptime - and that
                // figure belongs in the ordinary ink rather than in a state colour.
                Host.SetImage(context, accent == Color.Empty
                    ? KeyImage.Gauge(caption, text, samples, knob)
                    : KeyImage.Gauge(caption, text, samples, knob, accent));
                Host.SetTitle(context, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("RemoteMonitorAction.Render failed: " + ex.Message);
            }
        }

        // Which figures this tile carries. A checkbox each rather than a list, so
        // the order on the tile is always the order in Order and never whatever
        // order they happened to be ticked in.
        private string[] Selected(string context)
        {
            var chosen = new List<string>();
            foreach (string metric in Order)
            {
                // CPU, RAM and temperature on by default: the three that say
                // whether a machine is in trouble.
                bool on = metric == "cpu" || metric == "mem" || metric == "temp";
                if (Bool(context, "m" + char.ToUpperInvariant(metric[0]) + metric.Substring(1), on))
                    chosen.Add(metric);
            }

            return chosen.Count == 0 ? new[] { "cpu" } : chosen.ToArray();
        }

        private static KeyImage.Stat[] Rows(Reading reading, string[] metrics)
        {
            var rows = new KeyImage.Stat[metrics.Length];
            for (int i = 0; i < metrics.Length; i++)
            {
                string text;
                float fraction;
                Color accent;
                bool plotted = Value(reading, metrics[i], out text, out fraction, out accent);

                rows[i] = new KeyImage.Stat
                {
                    Label = Caption(metrics[i]),
                    Text = text,
                    Fraction = fraction,
                    Bar = plotted,
                    Accent = accent
                };
            }
            return rows;
        }

        // Text, sparkline height and what the figure is saying. Returns false when
        // the metric is not one to plot.
        private static bool Value(Reading reading, string metric, out string text, out float fraction,
                                  out Color accent)
        {
            fraction = 0f;
            accent = KeyImage.Idle;

            if (reading.At == DateTime.MinValue) { text = "..."; return false; }
            if (!reading.Ok) { text = "DOWN"; accent = KeyImage.Bad; return false; }

            float raw;
            if (!reading.Values.TryGetValue(metric, out raw) || raw < 0f) { text = "n/a"; return false; }

            // Thresholds are compared against the figure the tile prints, not the
            // one behind it: 64.6 reads as 65, and a tile showing 65 in green when
            // amber starts at 65 looks like a bug rather than a rounding.
            float shown = (float)Math.Round(raw);
            accent = Accent(metric, shown);

            switch (metric)
            {
                case "temp":
                    text = shown + "°";
                    fraction = Math.Min(1f, raw / 100f);
                    return true;

                case "up":
                    text = Uptime(raw);
                    return false;

                default:
                    text = shown + "%";
                    fraction = Math.Min(1f, Math.Max(0f, raw / 100f));
                    return true;
            }
        }

        /// <summary>
        /// Where a figure stops being fine. CPU is allowed to run higher than the
        /// rest before it counts as anything: a machine at 80% CPU is working,
        /// while a disk at 80% is a problem you have not had yet. Temperature is
        /// pitched at a Raspberry Pi, which starts throttling itself at 80.
        /// </summary>
        private static Color Accent(string metric, float value)
        {
            float warn, bad;
            switch (metric)
            {
                case "temp": warn = 65f; bad = 80f; break;
                case "disk": warn = 80f; bad = 92f; break;
                case "mem": warn = 80f; bad = 92f; break;
                case "cpu": warn = 75f; bad = 90f; break;

                // Uptime has no bad value, and colouring it would only suggest it
                // does. It stays in the ordinary ink.
                default: return Color.Empty;
            }

            if (value >= bad) return KeyImage.Bad;
            return value >= warn ? KeyImage.Warn : KeyImage.Good;
        }

        // Two units, largest first: "6d 4h" says more at a glance than a count of
        // seconds ever does, and the band only holds so much.
        private static string Uptime(float seconds)
        {
            var span = TimeSpan.FromSeconds(seconds);
            if (span.TotalDays >= 1) return (int)span.TotalDays + "d " + span.Hours + "h";
            if (span.TotalHours >= 1) return span.Hours + "h " + span.Minutes + "m";
            return span.Minutes + "m";
        }

        private static string Caption(string metric)
        {
            switch (metric)
            {
                case "mem": return "RAM";
                case "temp": return "TEMP";
                case "disk": return "DISK";
                case "up": return "UP";
                default: return "CPU";
            }
        }

        private string Identity(string context)
        {
            // What the installer generates, so a key left at its defaults is
            // already pointed at the restricted identity rather than at whatever
            // ssh would have offered on its own.
            string path = Str(context, "identity", @"%USERPROFILE%\.ssh\id_decktools");
            return string.IsNullOrEmpty(path) ? null : Environment.ExpandEnvironmentVariables(path);
        }

        /// <summary>The machine a tile watches, or null when it is not set up yet.</summary>
        private string Machine(string context)
        {
            string host = Str(context, "host", null);
            if (string.IsNullOrEmpty(host)) return null;

            return Str(context, "user", string.Empty) + "@" + host + ":" +
                   Int(context, "port", 22).ToString(CultureInfo.InvariantCulture) + "|" +
                   (Identity(context) ?? string.Empty);
        }

        private Reading Of(string context)
        {
            string machine = Machine(context);
            if (machine == null) return new Reading();

            Reading reading;
            if (!_readings.TryGetValue(machine, out reading))
            {
                reading = new Reading();
                _readings[machine] = reading;
            }
            return reading;
        }

        // Drops readings for machines no tile points at any more, which happens
        // whenever a key is removed or repointed.
        private void Forget()
        {
            var live = new HashSet<string>(
                Contexts.Keys.Select(Machine).Where(m => m != null), StringComparer.OrdinalIgnoreCase);

            foreach (string machine in _readings.Keys.Where(m => !live.Contains(m)).ToList())
            {
                // A poll already in flight owns its reading; dropping it here would
                // leave the callback writing into an entry nothing reads.
                if (_readings[machine].Busy) continue;
                _readings.Remove(machine);
            }
        }

        private static string Trim(string text)
        {
            if (string.IsNullOrEmpty(text)) return "(nothing)";

            string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length > 160 ? flat.Substring(0, 160) : flat;
        }
    }
}
