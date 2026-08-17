// System actions: monitor brightness over DDC/CI, screen blackout, and live
// metric tiles.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace StreamDeckAudio
{
    /// <summary>
    /// Rotate for hardware backlight brightness, press to toggle a dim preset.
    /// </summary>
    internal sealed class BrightnessAction : ActionBase
    {
        private readonly Dictionary<string, int> _restore = new Dictionary<string, int>(StringComparer.Ordinal);

        public override string Uuid { get { return "com.knupwns.audioknob.brightness"; } }

        public override void Rotate(string context, int ticks)
        {
            if (ticks == 0) return;

            List<MonitorTarget> targets = Targets(context, false);
            if (targets.Count == 0) { Host.ShowAlert(context); return; }

            int step = Math.Max(1, Int(context, "step", 5)) * ticks;
            foreach (MonitorTarget target in targets)
            {
                int percent;
                Monitors.Nudge(target, step, out percent);
            }
            _restore.Remove(context); // manual change cancels the remembered level
            Render(context);
        }

        public override void Press(string context)
        {
            List<MonitorTarget> targets = Targets(context, false);
            if (targets.Count == 0) { Host.ShowAlert(context); return; }

            int dim = Math.Max(0, Math.Min(100, Int(context, "dim", 20)));
            int previous;
            bool restoring = _restore.TryGetValue(context, out previous);

            foreach (MonitorTarget target in targets)
            {
                int percent;
                Monitors.SetPercent(target, restoring ? previous : dim, out percent);
            }

            if (restoring) _restore.Remove(context);
            else _restore[context] = targets[0].Percent >= dim ? targets[0].Percent : 100;

            Render(context);
        }

        public override void PropertyInspectorAppeared(string context)
        {
            PushMonitors(context);
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            if (Json.Str(payload, "command") == "getMonitors")
            {
                PushMonitors(context);
                return;
            }

            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        // "all" covers every panel that answers DDC/CI; a number picks one.
        private List<MonitorTarget> Targets(string context, bool refresh)
        {
            List<MonitorTarget> all = Monitors.List(refresh);
            string wanted = Str(context, "monitor", "all");

            if (wanted == "all") return all.Where(m => m.Supported).ToList();

            int index;
            if (!int.TryParse(wanted, out index)) return all.Where(m => m.Supported).ToList();
            return all.Where(m => m.Index == index && m.Supported).ToList();
        }

        protected override void Render(string context)
        {
            try
            {
                List<MonitorTarget> targets = Targets(context, false);
                string wanted = Str(context, "monitor", "all");
                string label = wanted == "all" ? "All screens" : "Monitor " + wanted;
                string value = targets.Count == 0 ? "no DDC" : targets[0].Percent + "%";

                Host.SetImage(context, KeyImage.Glyph("sun", label, value, OnKnob(context)));
                Host.SetTitle(context, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("BrightnessAction.Render failed: " + ex.Message);
            }
        }

        private void PushMonitors(string context)
        {
            IEnumerable<string> items = Monitors.List(true).Select(m =>
                "{\"id\":\"" + m.Index + "\",\"name\":\"" + Json.Escape(m.Label) +
                "\",\"supported\":" + (m.Supported ? "true" : "false") + "}");

            Host.SendToPropertyInspector(context, Uuid, "{\"monitors\":" + Json.Array(items) + "}");
        }
    }

    /// <summary>
    /// Blanks every screen. Either with black full-screen windows that any key or
    /// click dismisses, or by asking Windows to power the displays down.
    /// </summary>
    internal sealed class BlackoutAction : ActionBase
    {
        private const int HwndBroadcast = 0xFFFF;
        private const int WmSysCommand = 0x0112;
        private const int ScMonitorPower = 0xF170;
        private const int PowerOff = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wparam, IntPtr lparam);

        private Process _overlay;

        public override string Uuid { get { return "com.knupwns.audioknob.blackout"; } }

        // Keys only, per the manifest.
        protected override bool DefaultsToKnob { get { return false; } }

        // Only while an overlay is up, to notice the user dismissing it.
        public override bool WantsTick { get { return _overlay != null; } }

        public override void Press(string context)
        {
            if (Str(context, "mode", "overlay") == "power")
            {
                SendMessage((IntPtr)HwndBroadcast, WmSysCommand, (IntPtr)ScMonitorPower, (IntPtr)PowerOff);
                Log.Write("blackout: displays powered down");
                Render(context);
                return;
            }

            if (Active())
            {
                Stop();
            }
            else
            {
                string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Blackout.exe");
                if (!File.Exists(exe))
                {
                    Log.Write("blackout: missing " + exe);
                    Host.ShowAlert(context);
                    return;
                }

                try
                {
                    _overlay = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
                    Log.Write("blackout: overlay started");
                }
                catch (Exception ex)
                {
                    Log.Write("blackout: start failed: " + ex.Message);
                    Host.ShowAlert(context);
                }
            }
            RenderAll();
        }

        public override void Tick()
        {
            if (_overlay == null || !_overlay.HasExited) return;

            _overlay = null; // dismissed from the keyboard or mouse
            RenderAll();
        }

        protected override void Render(string context)
        {
            try
            {
                bool power = Str(context, "mode", "overlay") == "power";
                bool active = Active();

                Host.SetImage(context, KeyImage.Glyph(active ? "screens-off" : "screens", "Blackout",
                                                      power ? "SLEEP" : (active ? "ON" : "OFF"),
                                                      OnKnob(context)));
                Host.SetTitle(context, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("BlackoutAction.Render failed: " + ex.Message);
            }
        }

        private void RenderAll()
        {
            foreach (string context in Contexts.Keys.ToList()) Render(context);
        }

        private bool Active()
        {
            if (_overlay == null) return false;
            try
            {
                if (!_overlay.HasExited) return true;
            }
            catch (Exception)
            {
                // Handle went away with the process.
            }
            _overlay = null;
            return false;
        }

        private void Stop()
        {
            try { if (!_overlay.HasExited) _overlay.Kill(); }
            catch (Exception ex) { Log.Write("blackout: stop failed: " + ex.Message); }
            _overlay = null;
        }
    }

    /// <summary>
    /// A live metric tile. On a knob, rotating cycles which metric is shown.
    /// </summary>
    internal sealed class SystemMonitorAction : ActionBase
    {
        private const int HistoryLength = 22;

        private static readonly MetricKind[] Order =
        {
            MetricKind.Cpu, MetricKind.Ram, MetricKind.Gpu, MetricKind.Vram,
            MetricKind.Disk, MetricKind.Net, MetricKind.Temp
        };

        private readonly Dictionary<string, Queue<float>> _history =
            new Dictionary<string, Queue<float>>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _sampledAt =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public override string Uuid { get { return "com.knupwns.audioknob.sysmon"; } }

        public override bool WantsTick { get { return Contexts.Count > 0; } }

        public override void WillAppear(string context, Dictionary<string, object> settings, string controller)
        {
            _history[context] = new Queue<float>();
            _sampledAt.Remove(context);
            base.WillAppear(context, settings, controller);
        }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _history.Remove(context);
            _sampledAt.Remove(context);
        }

        public override void Rotate(string context, int ticks)
        {
            if (ticks == 0) return;

            MetricKind current = Metrics.Parse(Str(context, "metric", "cpu"));
            int index = Array.IndexOf(Order, current);
            if (index < 0) index = 0;

            int next = ((index + ticks) % Order.Length + Order.Length) % Order.Length;
            Dictionary<string, object> settings = Settings(context);
            settings["metric"] = Order[next].ToString().ToLowerInvariant();
            Contexts[context] = settings;

            _history[context] = new Queue<float>(); // the scale changed, drop the old curve
            Host.SetSettings(context, settings);
            Sample(context);
        }

        public override void Press(string context)
        {
            try
            {
                Process.Start(new ProcessStartInfo("taskmgr.exe") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Write("sysmon: could not open Task Manager: " + ex.Message);
                Host.ShowAlert(context);
            }
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings == null) return;

            _history[context] = new Queue<float>();
            SettingsChanged(context, settings);
        }

        public override void Tick()
        {
            foreach (string context in Contexts.Keys.ToList())
            {
                int interval = Math.Max(1, Int(context, "interval", 2));
                DateTime last;
                if (_sampledAt.TryGetValue(context, out last) &&
                    (DateTime.UtcNow - last).TotalSeconds < interval) continue;

                Sample(context);
            }
        }

        protected override void Render(string context)
        {
            Sample(context);
        }

        private void Sample(string context)
        {
            try
            {
                MetricKind kind = Metrics.Parse(Str(context, "metric", "cpu"));
                MetricSample sample = Metrics.Read(kind);
                _sampledAt[context] = DateTime.UtcNow;

                Queue<float> history;
                if (!_history.TryGetValue(context, out history))
                {
                    history = new Queue<float>();
                    _history[context] = history;
                }

                history.Enqueue(sample.Fraction);
                while (history.Count > HistoryLength) history.Dequeue();

                Host.SetImage(context, KeyImage.Gauge(sample.Label, sample.Text, history.ToArray(),
                                                     OnKnob(context)));
                Host.SetTitle(context, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("SystemMonitorAction.Sample failed: " + ex.Message);
            }
        }
    }
}
