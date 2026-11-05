// Start of day: one key that opens everything in a folder at once. Adding an app
// to the routine is dropping its shortcut into the folder - no settings to edit.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace StreamDeckAudio
{
    internal sealed class StartDayAction : ActionBase
    {
        private const string DefaultFolder = @"D:\Inicio del día";

        public override string Uuid { get { return "com.knupwns.audioknob.startday"; } }

        protected override bool DefaultsToKnob { get { return false; } }

        // What each key last drew, as the folder's names and write times: the
        // tile is only redrawn when a shortcut is added, removed or changed.
        private readonly Dictionary<string, string> _drawn = new Dictionary<string, string>(StringComparer.Ordinal);
        private int _ticks;

        // A shortcut added to the folder shows up on the tile within a few seconds.
        public override bool WantsTick { get { return true; } }

        public override void Tick()
        {
            if (++_ticks % 5 != 0) return;
            foreach (string context in Contexts.Keys.ToList())
            {
                string drawn;
                if (!_drawn.TryGetValue(context, out drawn) || drawn != Signature(context)) Render(context);
            }
        }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _drawn.Remove(context);
        }

        private string Signature(string context)
        {
            List<Shortcut> items = Shortcuts.List(Folder(context));
            return Str(context, "label", string.Empty) + "\n" + OnKnob(context) + "\n" + string.Join("\n",
                items.Select(i => i.Path + "|" + SafeTicks(i.Path)));
        }

        private static long SafeTicks(string path)
        {
            try { return File.GetLastWriteTimeUtc(path).Ticks; } catch (Exception) { return 0; }
        }

        public override void Press(string context)
        {
            List<Shortcut> items = Shortcuts.List(Folder(context));
            if (items.Count == 0) { Host.ShowAlert(context); return; }

            int failed = 0;
            foreach (Shortcut item in items)
                if (!Shortcuts.Launch(item.Path)) failed++;

            if (failed > 0) Host.ShowAlert(context);
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            if (Json.Str(payload, "command") == "open")
            {
                string folder = Folder(context);
                try
                {
                    Directory.CreateDirectory(folder);
                    Shortcuts.Launch(folder);
                }
                catch (Exception ex) { Log.Write("StartDay: cannot open " + folder + ": " + ex.Message); }
                return;
            }

            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        protected override void Render(string context)
        {
            try
            {
                bool knob = OnKnob(context);
                string label = Str(context, "label", "Inicio del día");
                List<Shortcut> items = Shortcuts.List(Folder(context));

                string tile = items.Count == 0
                    ? KeyImage.Glyph("sun", label, "Vacía", knob)
                    : KeyImage.Mosaic(items.Select(i => Shortcuts.Icon(i.Path)).ToList(), label, knob);

                Host.SetImage(context, tile);
                Host.SetTitle(context, string.Empty);
                _drawn[context] = Signature(context);
            }
            catch (Exception ex)
            {
                Log.Write("StartDayAction.Render failed: " + ex.Message);
            }
        }

        private string Folder(string context)
        {
            string folder = Shortcuts.Resolve(Str(context, "folder", null));
            return string.IsNullOrEmpty(folder) ? DefaultFolder : folder;
        }
    }
}
