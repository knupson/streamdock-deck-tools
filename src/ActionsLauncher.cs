// Launcher wheel: a knob that scrolls through the contents of a folder and opens
// the selected item when pressed.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace StreamDeckAudio
{
    internal sealed class LauncherAction : ActionBase
    {
        // Long enough that spinning the knob does not hit the disk on every tick,
        // short enough that a shortcut added to the folder shows up right away.
        private static readonly TimeSpan ListingTtl = TimeSpan.FromSeconds(5);

        private readonly Dictionary<string, int> _selected = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Shortcut>> _listing =
            new Dictionary<string, List<Shortcut>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _listedAt =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Composed tiles, keyed by item and label: rotating back and forth should
        // not redraw anything.
        private readonly Dictionary<string, string> _tiles = new Dictionary<string, string>(StringComparer.Ordinal);

        public override string Uuid { get { return "com.knupwns.audioknob.launcher"; } }

        public override void WillAppear(string context, Dictionary<string, object> settings, string controller)
        {
            base.WillAppear(context, settings, controller);
            _selected[context] = Math.Max(0, Int(context, "index", 0));
            Render(context);
        }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _selected.Remove(context);
        }

        public override void SettingsChanged(string context, Dictionary<string, object> settings)
        {
            if (settings != null) Contexts[context] = settings;
            Forget(context);
            Render(context);
        }

        public override void Rotate(string context, int ticks)
        {
            if (ticks == 0) return;

            List<Shortcut> items = Items(context, false);
            if (items.Count == 0) { Render(context); return; }

            int index = Index(context, items.Count) + ticks;
            _selected[context] = ((index % items.Count) + items.Count) % items.Count;
            Render(context);
        }

        public override void Press(string context)
        {
            List<Shortcut> items = Items(context, false);
            if (items.Count == 0) { Host.ShowAlert(context); return; }

            Shortcut item = items[Index(context, items.Count)];
            if (!Shortcuts.Launch(item.Path))
            {
                Host.ShowAlert(context);
                return;
            }

            // Remember where the wheel was left, so it survives a restart.
            Dictionary<string, object> settings = Settings(context);
            settings["index"] = Index(context, items.Count);
            Contexts[context] = settings;
            Host.SetSettings(context, settings);
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            if (Json.Str(payload, "command") == "refresh")
            {
                Forget(context);
                Render(context);
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
                string folder = Str(context, "folder", null);
                if (string.IsNullOrEmpty(folder))
                {
                    Show(context, KeyImage.Glyph("shortcut", "Set folder", null, knob));
                    return;
                }

                string resolved = Shortcuts.Resolve(folder);
                if (resolved == null || !Directory.Exists(resolved))
                {
                    Show(context, KeyImage.Glyph("shortcut", "No folder", null, knob));
                    return;
                }

                List<Shortcut> items = Items(context, false);
                if (items.Count == 0)
                {
                    Show(context, KeyImage.Glyph("shortcut", "Empty", null, knob));
                    return;
                }

                Shortcut item = items[Index(context, items.Count)];
                string label = Bool(context, "showName", true) ? item.Name : string.Empty;

                string key = item.Path + "\n" + label + "\n" + knob;
                string tile;
                if (!_tiles.TryGetValue(key, out tile))
                {
                    Bitmap icon = Shortcuts.Icon(item.Path);
                    tile = KeyImage.Launcher(icon, label, knob);
                    _tiles[key] = tile;
                }
                Show(context, tile);
            }
            catch (Exception ex)
            {
                Log.Write("LauncherAction.Render failed: " + ex.Message);
            }
        }

        private void Show(string context, string image)
        {
            Host.SetImage(context, image);
            Host.SetTitle(context, string.Empty);
        }

        private int Index(string context, int count)
        {
            int index;
            if (!_selected.TryGetValue(context, out index)) index = Int(context, "index", 0);
            return count == 0 ? 0 : Math.Min(count - 1, Math.Max(0, index));
        }

        private List<Shortcut> Items(string context, bool force)
        {
            string resolved = Shortcuts.Resolve(Str(context, "folder", null));
            if (resolved == null) return new List<Shortcut>();

            DateTime listedAt;
            if (!force && _listing.ContainsKey(resolved) &&
                _listedAt.TryGetValue(resolved, out listedAt) &&
                DateTime.UtcNow - listedAt < ListingTtl)
                return _listing[resolved];

            List<Shortcut> items = Shortcuts.List(resolved);
            _listing[resolved] = items;
            _listedAt[resolved] = DateTime.UtcNow;
            return items;
        }

        private void Forget(string context)
        {
            string resolved = Shortcuts.Resolve(Str(context, "folder", null));
            if (resolved == null) return;

            _listing.Remove(resolved);
            _listedAt.Remove(resolved);
        }
    }
}
