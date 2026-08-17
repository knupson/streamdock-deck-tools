// Separator: a key that divides the deck into groups. Pressing it does nothing,
// which is the whole feature - a gap in a profile is still a key, and a key that
// launches something when your thumb lands on it by mistake is worse than a bar.

using System.Collections.Generic;

namespace StreamDeckAudio
{
    internal sealed class SeparatorAction : ActionBase
    {
        public override string Uuid { get { return "com.knupwns.audioknob.separator"; } }

        // Keys, per the manifest. A knob hides the bottom of its screen, which
        // would cut the bar short.
        protected override bool DefaultsToKnob { get { return false; } }

        // Press is deliberately not overridden: the base does nothing, and that is
        // exactly what this key is for.

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        protected override void Render(string context)
        {
            bool flat = Str(context, "orientation", "vertical") == "horizontal";

            Host.SetImage(context, KeyImage.Glyph(flat ? "separator-h" : "separator", null, null,
                                                  OnKnob(context)));
            Host.SetTitle(context, string.Empty);
        }
    }
}
