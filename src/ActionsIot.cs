using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

namespace StreamDeckAudio
{
    internal abstract class IotAction : ActionBase
    {
        protected IotHub Hub { get { return IotHub.Shared; } }
        private readonly Dictionary<string, string> _rendered = new Dictionary<string, string>();

        public override bool WantsTick { get { return Contexts.Count > 0; } }

        public override void WillAppear(string context, Dictionary<string, object> settings, string controller)
        {
            if (Contexts.Count == 0) Hub.Changed += OnChanged;
            base.WillAppear(context, settings, controller);
            Hub.SetVisible(this, true);
        }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _rendered.Remove(context);
            if (Contexts.Count == 0)
            {
                Hub.Changed -= OnChanged;
                Hub.SetVisible(this, false);
            }
        }

        private void OnChanged()
        {
            // Changed puede llegar desde el ThreadPool; ni Contexts ni GDI son thread-safe.
            Host.Invoke(delegate { RenderAll(); });
        }

        protected void RenderAll()
        {
            foreach (string context in new List<string>(Contexts.Keys)) Render(context);
        }

        public override void Tick()
        {
            // El hub usa UTC para medir los diez segundos desde el último toque.
            Hub.Tick(DateTime.UtcNow);
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        protected WizPilot FirstPilot()
        {
            IList<IotLight> lights = Hub.Resolve(Hub.Selected);
            return lights.Count == 0 ? null : Hub.Pilot(lights[0]);
        }

        protected void Show(string context, string image)
        {
            string previous;
            if (image == null || (_rendered.TryGetValue(context, out previous) && previous == image)) return;
            Host.SetImage(context, image);
            Host.SetTitle(context, string.Empty);
            _rendered[context] = image;
        }
    }

    internal sealed class IotSelectorAction : IotAction
    {
        public override string Uuid { get { return "com.knupwns.audioknob.iot.selector"; } }

        public override void Rotate(string context, int ticks)
        {
            if (ticks == 0) return;
            Hub.MoveSelection(ticks);
            string selected = Hub.Selected;
            IList<IotLight> lights = Hub.Resolve(selected);
            if (lights.Count == 1 && selected == lights[0].FullName) Hub.Identify(lights[0]);
        }

        public override void Press(string context) { Hub.Toggle(); }
        public override void Tick() { base.Tick(); RenderAll(); }

        protected override void Render(string context)
        {
            IList<IotLight> lights = Hub.Resolve(Hub.Selected);
            WizPilot first = FirstPilot();
            bool manual = false, rhythm = lights.Count > 0;
            foreach (IotLight light in lights)
            {
                WizPilot p = Hub.Pilot(light);
                manual |= Hub.IsManual(light);
                rhythm &= p != null && p.InRhythm;
            }
            string status = first == null ? "sin datos" : (first.On ? "ON" : "OFF")
                + " " + (manual ? "manual" : rhythm ? "ritmo" : "escena");
            Show(context, KeyImage.Glyph("iot-selector", Hub.Selected, status, OnKnob(context)));
        }
    }

    internal sealed class IotPaletteAction : IotAction
    {
        private const string DefaultColors = "#ff0000 #ff6400 #ffc800 #00ff00 #00c8ff #0000ff #9600ff #ff0078";
        private readonly Dictionary<string, int> _indices = new Dictionary<string, int>();
        private readonly Dictionary<string, string> _previews = new Dictionary<string, string>();
        public override string Uuid { get { return "com.knupwns.audioknob.iot.palette"; } }

        private static List<Color> ParseColors(string value)
        {
            var colors = new List<Color>();
            foreach (string token in value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
            {
                int rgb;
                if (token.Length == 7 && token[0] == '#'
                    && int.TryParse(token.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb))
                    colors.Add(Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255));
            }
            return colors;
        }

        private List<Color> Colors(string context)
        {
            List<Color> colors = ParseColors(Str(context, "colors", DefaultColors));
            return colors.Count == 0 ? ParseColors(DefaultColors) : colors;
        }

        private int Index(string context, int count)
        {
            int index;
            return _indices.TryGetValue(context, out index) ? index % count : 0;
        }

        public override void Rotate(string context, int ticks)
        {
            if (ticks == 0) return;
            Hub.TouchSelection();
            int count = Colors(context).Count;
            _indices[context] = (int)(((Index(context, count) + (long)ticks) % count + count) % count);
            Render(context);
        }

        public override void Press(string context)
        {
            List<Color> colors = Colors(context);
            Color color = colors[Index(context, colors.Count)];
            Hub.ApplyColor(color.R, color.G, color.B);
        }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _indices.Remove(context);
            if (Contexts.Count == 0) _previews.Clear();
        }

        protected override void Render(string context)
        {
            List<Color> colors = Colors(context);
            Color color = colors[Index(context, colors.Count)];
            string hex = "#" + color.R.ToString("x2") + color.G.ToString("x2") + color.B.ToString("x2");
            string key = hex + OnKnob(context);
            string image;
            if (!_previews.TryGetValue(key, out image))
            {
                // Launcher conserva el color del bitmap y la tipografía del plugin.
                using (var swatch = new Bitmap(64, 64))
                {
                    using (Graphics g = Graphics.FromImage(swatch))
                    using (var brush = new SolidBrush(color))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.FillEllipse(brush, 2, 2, 60, 60);
                    }
                    image = KeyImage.Launcher(swatch, hex, OnKnob(context));
                }
                _previews[key] = image;
            }
            Show(context, image);
        }
    }

    internal sealed class IotBrightnessAction : IotAction
    {
        public override string Uuid { get { return "com.knupwns.audioknob.iot.brightness"; } }
        public override void Rotate(string context, int ticks)
        {
            if (ticks == 0) return;
            long delta = (long)ticks * Math.Max(1, Math.Min(25, Int(context, "step", 5)));
            Hub.AddBrightness((int)Math.Max(int.MinValue, Math.Min(int.MaxValue, delta)));
        }
        public override void Press(string context) { Hub.Resume(); }
        protected override void Render(string context)
        {
            WizPilot p = FirstPilot();
            string value = p == null || !p.Dimming.HasValue ? "sin datos" : p.Dimming.Value + "%";
            Show(context, KeyImage.Glyph("iot-brightness", "Brillo", value, OnKnob(context)));
        }
    }

    internal sealed class IotPlugAction : IotAction
    {
        public override string Uuid { get { return "com.knupwns.audioknob.iot.plug"; } }
        protected override bool DefaultsToKnob { get { return false; } }
        private string Alias(string context) { return Str(context, "plug", "noche") == "vestidor" ? "vestidor" : "noche"; }
        public override void Press(string context) { Hub.TogglePlug(Alias(context)); }
        protected override void Render(string context)
        {
            string alias = Alias(context);
            Dictionary<string, object> status = Hub.TuyaStatus(alias);
            string value = status == null || !status.ContainsKey("1") ? "sin datos" : Json.Bool(status, "1", false) ? "ON" : "OFF";
            Show(context, KeyImage.Glyph("iot-plug", alias == "noche" ? "Noche" : "Vestidor", value, OnKnob(context)));
        }
    }

    internal sealed class IotPurifierAction : IotAction
    {
        public override string Uuid { get { return "com.knupwns.audioknob.iot.purifier"; } }
        protected override bool DefaultsToKnob { get { return false; } }
        protected override bool HandlesLongPress { get { return true; } }
        public override void Press(string context) { Hub.TogglePurifier(); }
        public override void LongPress(string context) { Hub.NextPurifierSpeed(); }
        protected override void Render(string context)
        {
            Dictionary<string, object> status = Hub.TuyaStatus("purificador");
            string speed = Json.Str(status, "4") ?? "sin datos";
            string title = status == null ? "Purificador" : (Json.Bool(status, "1", false) ? "ON " : "OFF ") + speed;
            int filter = Json.Int(status, "5", -1);
            string value = filter < 0 ? "sin datos" : "filtro " + filter + "%";
            Show(context, filter >= 0 && filter <= 15
                ? KeyImage.Glyph("iot-purifier", title, value, OnKnob(context), KeyImage.Warn)
                : KeyImage.Glyph("iot-purifier", title, value, OnKnob(context)));
        }
    }

    internal sealed class IotVacuumAction : IotAction
    {
        private readonly Dictionary<string, int> _faults = new Dictionary<string, int>();
        public override string Uuid { get { return "com.knupwns.audioknob.iot.vacuum"; } }
        protected override bool DefaultsToKnob { get { return false; } }
        public override void Press(string context) { Hub.VacuumStep(); }
        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _faults.Remove(context);
        }
        protected override void Render(string context)
        {
            Dictionary<string, object> status = Hub.TuyaStatus("aspiradora");
            string state;
            switch (Json.Str(status, "5"))
            {
                case "standby": case "charging": case "charge_done": case "sleep": state = "en base"; break;
                case "cleaning": case "zone_clean": case "part_clean": case "goto_pos": state = "limpiando"; break;
                case "paused": state = "en pausa"; break;
                case "goto_charge": state = "volviendo"; break;
                default: state = status == null ? "sin datos" : "desconocido"; break;
            }
            int fault = Json.Int(status, "28", 0), previous;
            if (fault != 0 && (!_faults.TryGetValue(context, out previous) || previous != fault)) Host.ShowAlert(context);
            _faults[context] = fault;
            int battery = Json.Int(status, "8", -1);
            string value = fault != 0 ? "falla" : battery < 0 ? "sin datos" : "batería " + battery + "%";
            Show(context, KeyImage.Glyph("iot-vacuum", "Isaura: " + state, value, OnKnob(context)));
        }
    }

    internal sealed class IotLightsAction : IotAction
    {
        public override string Uuid { get { return "com.knupwns.audioknob.iot.lights"; } }
        protected override bool DefaultsToKnob { get { return false; } }
        public override void Press(string context)
        {
            switch (Str(context, "function", "rhythm"))
            {
                case "bedtime": Hub.Bedtime(); break;
                case "temp": Hub.CycleTemperature(); break;
                case "alloff": Hub.AllOff(); break;
                default: Hub.Resume(); break;
            }
        }

        protected override void Render(string context)
        {
            string function = Str(context, "function", "rhythm");
            string glyph, title, value;
            if (function == "temp")
            {
                WizPilot p = FirstPilot();
                glyph = "iot-temp"; title = "Cálido/Frío";
                value = p == null || !p.Temp.HasValue ? "sin datos" : p.Temp.Value + " K";
            }
            else if (function == "alloff")
            {
                glyph = "iot-off"; title = "Todo off"; value = string.Empty;
            }
            else
            {
                bool bedtime = function == "bedtime";
                IList<IotLight> lights = Hub.Resolve("Todas");
                bool active = lights.Count > 0;
                foreach (IotLight light in lights)
                {
                    WizPilot p = Hub.Pilot(light);
                    active &= p != null && (bedtime ? p.SceneId == 10 : p.InRhythm && !Hub.IsManual(light));
                }
                glyph = bedtime ? "iot-bedtime" : "iot-rhythm";
                title = bedtime ? "A dormir" : "Ritmo";
                value = active ? "ACTIVO" : string.Empty;
            }
            Show(context, KeyImage.Glyph(glyph, title, value, OnKnob(context)));
        }
    }
}
