// RustDesk key: one saved peer per key, opened with a press.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace StreamDeckAudio
{
    internal sealed class RustDeskAction : ActionBase
    {
        private const string DefaultPath = @"C:\Program Files\RustDesk\rustdesk.exe";

        // The ID goes on a command line, so it is held to what an ID can contain
        // rather than escaped afterwards. RustDesk IDs are digits; an alias may
        // carry letters, dashes or underscores.
        private static readonly Regex Safe = new Regex(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

        public override string Uuid { get { return "com.knupwns.audioknob.rustdesk"; } }

        // Keys, per the manifest.
        protected override bool DefaultsToKnob { get { return false; } }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            if (Json.Str(payload, "command") == "connect") { Press(context); return; }

            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        public override void Press(string context)
        {
            string id = Id(context);
            if (id == null)
            {
                Log.Write("RustDesk: no usable peer ID on this key");
                Host.ShowAlert(context);
                return;
            }

            string path = Executable(context);
            try
            {
                Process.Start(new ProcessStartInfo(path, "--connect " + id) { UseShellExecute = true });
                Log.Write("RustDesk: connecting to " + id);
            }
            catch (Exception ex)
            {
                Log.Write("RustDesk: launching " + path + " failed: " + ex.Message);
                Host.ShowAlert(context);
            }
        }

        protected override void Render(string context)
        {
            try
            {
                bool knob = OnKnob(context);
                string id = Id(context);
                if (id == null)
                {
                    Show(context, KeyImage.Glyph("rustdesk", "Set ID", null, knob));
                    return;
                }

                string name = Str(context, "name", null);
                string label = string.IsNullOrEmpty(name) ? Grouped(id) : Labels.Host(name);

                // Worth saying on the tile rather than only when you press it: the
                // key looks perfectly fine right up until it does nothing. Shown
                // even on a key that wanted no writing on it, because a broken key
                // that says nothing is the failure this is meant to prevent.
                if (!File.Exists(Executable(context)))
                {
                    Show(context, KeyImage.Glyph("rustdesk", label, "NO APP", knob, KeyImage.Bad));
                    return;
                }

                // With nothing to write, the mark gets the whole tile: the same box
                // the icon pack centres its glyphs in, rather than the smaller one
                // that leaves room for a caption.
                if (!Bool(context, "showLabel", true))
                {
                    Show(context, KeyImage.Glyph("rustdesk-big", null, null, knob));
                    return;
                }

                Show(context, KeyImage.Glyph("rustdesk", label, null, knob));
            }
            catch (Exception ex)
            {
                Log.Write("RustDeskAction.Render failed: " + ex.Message);
            }
        }

        private void Show(string context, string image)
        {
            Host.SetImage(context, image);
            Host.SetTitle(context, string.Empty);
        }

        // Spaces are how RustDesk itself prints an ID, and they are what make a
        // ten digit number readable at a glance.
        private static string Grouped(string id)
        {
            if (id.Length != 9 && id.Length != 10) return id;

            foreach (char c in id)
                if (!char.IsDigit(c)) return id;

            int first = id.Length - 7;
            return id.Substring(0, first) + " " + id.Substring(first, 3) + " " + id.Substring(first + 3);
        }

        /// <summary>The peer to dial, with the spaces people paste taken out.</summary>
        private string Id(string context)
        {
            string id = Str(context, "id", null);
            if (string.IsNullOrEmpty(id)) return null;

            id = id.Replace(" ", string.Empty).Replace("\t", string.Empty);
            return Safe.IsMatch(id) ? id : null;
        }

        // Not called Path: System.IO.Path is in scope, and one of the two having to
        // win a name lookup is not worth the saving.
        private string Executable(string context)
        {
            return Environment.ExpandEnvironmentVariables(Str(context, "path", DefaultPath));
        }
    }
}
