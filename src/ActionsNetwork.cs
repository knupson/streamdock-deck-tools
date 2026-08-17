// Host watch: a live tile showing whether a machine answers, and a press that
// wakes it over the network.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace StreamDeckAudio
{
    internal sealed class HostStatusAction : ActionBase
    {
        private const int HistoryLength = 22;
        private const int ProbeTimeoutMs = 800;

        // After a wake, the machine is polled hard for a while: watching it come up
        // is the point, and a ten-second cadence would miss it.
        private static readonly TimeSpan WakeWatch = TimeSpan.FromSeconds(75);
        private const int WakeIntervalSeconds = 3;

        private sealed class Watch
        {
            public bool Up;
            public int Milliseconds;
            public bool Probed;
            public DateTime CheckedAt;
            public DateTime WokeAt;
            public bool Busy;
            public readonly Queue<float> History = new Queue<float>();
        }

        private readonly Dictionary<string, Watch> _watches = new Dictionary<string, Watch>(StringComparer.Ordinal);

        public override string Uuid { get { return "com.knupwns.audioknob.hoststatus"; } }

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
            _watches[context] = new Watch();
            base.SettingsChanged(context, settings);
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            if (Json.Str(payload, "command") == "wake") { Wake(context); return; }

            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        public override void Press(string context)
        {
            Watch watch = Of(context);

            // Already up: a press just re-checks rather than shouting at a machine
            // that is plainly awake.
            if (watch.Probed && watch.Up)
            {
                Probe(context, true);
                return;
            }
            Wake(context);
        }

        public override void Tick()
        {
            foreach (string context in Contexts.Keys.ToList())
            {
                Watch watch = Of(context);
                bool waking = DateTime.UtcNow - watch.WokeAt < WakeWatch;
                int interval = waking ? WakeIntervalSeconds : Math.Max(2, Int(context, "interval", 10));

                if ((DateTime.UtcNow - watch.CheckedAt).TotalSeconds < interval) continue;
                Probe(context, false);
            }
        }

        private void Wake(string context)
        {
            string mac = Str(context, "mac", null);
            if (string.IsNullOrEmpty(mac))
            {
                Host.ShowAlert(context);
                return;
            }

            Watch watch = Of(context);
            watch.WokeAt = DateTime.UtcNow;

            int port = Int(context, "wolPort", 9);
            string host = Str(context, "host", null);

            // Off the pump: sending to several broadcast addresses can block.
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool ok = WakeOnLan.Send(mac, port, host);
                Host.Invoke(delegate
                {
                    if (!ok) Host.ShowAlert(context);
                    Render(context);
                });
            });

            Render(context);
        }

        // The probe itself runs on a pool thread - a timing-out ping would freeze
        // every other action for the best part of a second - and comes back through
        // the host to be drawn.
        private void Probe(string context, bool immediate)
        {
            Watch watch = Of(context);
            if (watch.Busy) return;

            string host = Str(context, "host", null);
            if (string.IsNullOrEmpty(host))
            {
                Render(context);
                return;
            }

            int tcpPort = Int(context, "tcpPort", 0);
            watch.Busy = true;
            watch.CheckedAt = DateTime.UtcNow;

            ThreadPool.QueueUserWorkItem(delegate
            {
                HostState state = HostProbe.Check(host, tcpPort, ProbeTimeoutMs);
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

            if (immediate) Render(context);
        }

        protected override void Render(string context)
        {
            try
            {
                Watch watch = Of(context);
                string label = Str(context, "name", null);
                if (string.IsNullOrEmpty(label)) label = Str(context, "host", "Host");

                string value;
                if (!watch.Probed) value = "...";
                else if (watch.Up) value = watch.Milliseconds + "ms";
                else value = DateTime.UtcNow - watch.WokeAt < WakeWatch ? "WAKE" : "DOWN";

                float[] samples = watch.History.Count > 1 ? watch.History.ToArray() : null;
                Host.SetImage(context, KeyImage.Gauge(Labels.Host(label), value, samples, OnKnob(context)));
                Host.SetTitle(context, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("HostStatusAction.Render failed: " + ex.Message);
            }
        }

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
