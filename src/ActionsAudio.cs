// Audio actions: the output switcher and the per-application volume knob.

using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDeckAudio
{
    /// <summary>
    /// Rotate for system volume, press to swap the default output between two
    /// chosen devices.
    /// </summary>
    internal sealed class AudioOutputAction : ActionBase
    {
        public override string Uuid { get { return "com.knupwns.audioknob.action1"; } }

        public override void Rotate(string context, int ticks)
        {
            if (ticks == 0) return;
            int taps = Math.Abs(ticks) * Math.Max(1, Int(context, "step", 1));

            if (ticks > 0) SystemVolume.Up(taps);
            else SystemVolume.Down(taps);
        }

        public override void Press(string context)
        {
            List<AudioDevice> active = AudioDevices.ListActiveOutputs();
            if (active.Count == 0) { Host.ShowAlert(context); return; }

            string currentId = AudioDevices.GetDefaultOutputId();
            AudioDevice target = PickTarget(context, active, currentId);
            if (target == null) { Host.ShowAlert(context); return; }

            if (target.Id != currentId)
            {
                AudioDevices.SetDefaultOutput(target.Id);
                Log.Write("switched output to " + target.Name);
            }
            Render(context);
        }

        public override void PropertyInspectorAppeared(string context)
        {
            PushDevices(context);
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            if (Json.Str(payload, "command") == "getDevices") { PushDevices(context); return; }

            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        // With both devices configured this is a straight A/B swap. Otherwise it
        // walks every active output, which keeps the knob useful before the
        // property inspector has been touched.
        private AudioDevice PickTarget(string context, List<AudioDevice> active, string currentId)
        {
            AudioDevice a = AudioDevices.FindById(active, Str(context, "deviceA", null));
            AudioDevice b = AudioDevices.FindById(active, Str(context, "deviceB", null));

            if (a != null && b != null) return currentId == a.Id ? b : a;
            if (a != null) return currentId == a.Id ? null : a;
            if (b != null) return currentId == b.Id ? null : b;

            int index = active.FindIndex(d => d.Id == currentId);
            return index < 0 ? active[0] : active[(index + 1) % active.Count];
        }

        protected override void Render(string context)
        {
            try
            {
                List<AudioDevice> active = AudioDevices.ListActiveOutputs();
                AudioDevice current = AudioDevices.FindById(active, AudioDevices.GetDefaultOutputId());

                // State 1 is the speakers artwork, used whenever device B is live.
                int state = current != null && current.Id == Str(context, "deviceB", null) ? 1 : 0;
                Host.SetState(context, state);

                string label = Bool(context, "showTitle", true) && current != null
                    ? Labels.Device(current.Name)
                    : string.Empty;

                Host.SetImage(context, KeyImage.Glyph(state == 1 ? "speakers" : "headphones", label, null,
                                                     OnKnob(context)));
                Host.SetTitle(context, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("AudioOutputAction.Render failed: " + ex.Message);
            }
        }

        private void PushDevices(string context)
        {
            IEnumerable<string> items = AudioDevices.ListActiveOutputs().Select(d =>
                "{\"id\":\"" + Json.Escape(d.Id) + "\",\"name\":\"" + Json.Escape(d.Name) + "\"}");

            Host.SendToPropertyInspector(context, Uuid, "{\"devices\":" + Json.Array(items) + "}");
        }
    }

    /// <summary>
    /// Rotate for the volume of one application, press to mute it. The target is
    /// either a fixed app or whatever is in the foreground.
    /// </summary>
    internal sealed class AppVolumeAction : ActionBase
    {
        private readonly Dictionary<string, string> _rendered = new Dictionary<string, string>(StringComparer.Ordinal);

        public override string Uuid { get { return "com.knupwns.audioknob.appvolume"; } }

        // Foreground tracking needs polling, and an app's volume can also be
        // changed elsewhere; the render key check keeps the traffic down.
        public override bool WantsTick { get { return Contexts.Count > 0; } }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _rendered.Remove(context);
        }

        public override void Rotate(string context, int ticks)
        {
            if (ticks == 0) return;

            // Resolved once and handed to the render: working out which app is in
            // the foreground means asking the OS about a process, and a detent
            // arrives as part of a burst.
            string target = Target(context);
            if (target == null) { Render(context, null); return; }

            float delta = ticks * Math.Max(1, Int(context, "step", 4)) / 100f;
            float level;
            bool muted;
            AudioSessions.Adjust(target, delta, out level, out muted);
            Render(context, target);
        }

        public override void Press(string context)
        {
            string target = Target(context);
            bool muted;
            if (target == null || !AudioSessions.ToggleMute(target, out muted))
            {
                Host.ShowAlert(context);
                return;
            }
            Render(context, target);
        }

        public override void Tick()
        {
            foreach (string context in Contexts.Keys.ToList()) Render(context);
        }

        public override void PropertyInspectorAppeared(string context)
        {
            PushApps(context);
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            if (Json.Str(payload, "command") == "getApps") { PushApps(context); return; }

            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        private string Target(string context)
        {
            return Str(context, "mode", "foreground") == "foreground"
                ? AudioSessions.ForegroundProcessName()
                : Str(context, "app", null);
        }

        protected override void Render(string context)
        {
            Render(context, Target(context));
        }

        private void Render(string context, string target)
        {
            try
            {
                AppSession session = target == null ? null : AudioSessions.Find(target);
                bool found = session != null;

                // A focused app with no audio session still gets its real name.
                string label = target == null
                    ? "No app"
                    : (found && !string.IsNullOrEmpty(session.Display)
                        ? session.Display
                        : Labels.FriendlyByName(target));

                string value = found ? Math.Round(session.Volume * 100f) + "%" : "--";
                string art = found && session.Muted ? "mixer-muted" : "mixer";

                // Redrawing an unchanged tile would flood the socket every second.
                string key = art + "|" + label + "|" + value;
                string previous;
                if (_rendered.TryGetValue(context, out previous) && previous == key) return;
                _rendered[context] = key;

                Host.SetImage(context, KeyImage.Glyph(art, label, value, OnKnob(context)));
                Host.SetTitle(context, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("AppVolumeAction.Render failed: " + ex.Message);
            }
        }

        private void PushApps(string context)
        {
            IEnumerable<string> items = AudioSessions.Applications().Select(choice =>
                "{\"id\":\"" + Json.Escape(choice.Id) + "\",\"name\":\"" + Json.Escape(choice.Label) + "\"}");

            Host.SendToPropertyInspector(context, Uuid, "{\"apps\":" + Json.Array(items) + "}");
        }
    }
}
