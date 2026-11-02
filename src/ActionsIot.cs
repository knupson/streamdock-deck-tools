using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("IotTests")]

namespace StreamDeckAudio
{
    internal abstract class IotAction : ActionBase
    {
        private readonly IotHub _hub;
        protected IotAction(IotHub hub = null) { _hub = hub; }
        protected IotHub Hub { get { return _hub ?? IotHub.Shared; } }
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

        // El estado lo dice el color del ícono; el texto va siempre en la misma tinta.
        protected static readonly Color OnTint = ColorTranslator.FromHtml("#FFB54D");
        protected static readonly Color OffTint = Color.Empty; // cromado original: claro sobre negro
        protected const string NoData = "Sin señal";

        // "pc.norte" -> "PC Norte"; "Todas", "PC" y "Cama" quedan como están.
        protected static string Pretty(string selection)
        {
            int dot = selection.IndexOf('.');
            if (dot < 0) return selection;
            return Capital(selection.Substring(0, dot)) + " " + Capital(selection.Substring(dot + 1));
        }

        private static string Capital(string word)
        {
            if (word == "pc") return "PC";
            return word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word.Substring(1);
        }

        protected WizPilot FirstPilot()
        {
            IList<IotLight> lights = Hub.Resolve(Hub.Selected);
            return lights.Count == 0 ? null : Hub.Pilot(lights[0]);
        }

        protected void ShowAire(string context, bool knob)
        {
            AireState state = Hub.Aire;
            Show(context, KeyImage.Lit("iot-aire", "Aire", state == null ? NoData : state.Summary,
                knob, state != null && state.Power == 1 ? OnTint : OffTint, false));
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

    // La revisión de selección también invalida giros aunque la UI procese Changed más tarde.
    internal sealed class AirePreview
    {
        private int _version = -1;
        private readonly Dictionary<string, int> _values = new Dictionary<string, int>();
        public void Sync(IotHub hub)
        {
            if (_version == hub.SelectionVersion) return;
            _version = hub.SelectionVersion; _values.Clear();
        }
        public void Remove(string context) { _values.Remove(context); }
        private int Current(IotHub hub, string context, bool mode, AireState state)
        {
            Sync(hub);
            int value;
            return _values.TryGetValue(context, out value) ? value : mode ? state.Mode : state.Temp;
        }
        public void Rotate(IotHub hub, string context, int ticks, bool mode)
        {
            AireState state = hub.Aire;
            if (state == null) { Sync(hub); return; }
            long value = Current(hub, context, mode, state) + (long)ticks;
            _values[context] = mode ? (int)((value % 5 + 5) % 5) : (int)Math.Max(16, Math.Min(30, value));
        }
        public string Value(IotHub hub, string context, bool mode)
        {
            AireState state = hub.Aire;
            if (state == null) return "Sin señal";
            int value = Current(hub, context, mode, state);
            return (mode ? AireState.Modes[value] : value + "°") + (value != (mode ? state.Mode : state.Temp) ? " *" : "");
        }
        public void Apply(IotHub hub, string context, bool mode)
        {
            AireState state = hub.Aire;
            if (state == null) return;
            int value = Current(hub, context, mode, state);
            if (mode) hub.ApplyAireMode(value); else hub.ApplyAireTemp(value);
            _values.Remove(context);
        }
    }

    internal sealed class IotAireAction : IotAction
    {
        public IotAireAction(IotHub hub = null) : base(hub) { }
        public override string Uuid { get { return "com.knupwns.audioknob.iot.aire"; } }
        protected override bool DefaultsToKnob { get { return false; } }
        protected override bool HandlesLongPress { get { return true; } }
        public override void Press(string context) { Hub.ToggleAire(); }
        public override void WillAppear(string context, Dictionary<string, object> settings, string controller)
        {
            base.WillAppear(context, settings, controller);
            if (Contexts.Count == 1) Hub.SetAireVisible(this, true);
        }
        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            if (Contexts.Count == 0) Hub.SetAireVisible(this, false);
        }
        protected override void Render(string context) { ShowAire(context, OnKnob(context)); }
    }

    internal sealed class IotSelectorAction : IotAction
    {
        public override string Uuid { get { return "com.knupwns.audioknob.iot.selector"; } }

        public override void Rotate(string context, int ticks)
        {
            if (ticks == 0) return;
            Hub.MoveSelection(ticks);
        }

        public override void Press(string context) { Hub.Toggle(); }
        public override void Tick() { base.Tick(); RenderAll(); }

        protected override void Render(string context)
        {
            if (Hub.Selected == "Aire") { ShowAire(context, OnKnob(context)); return; }
            IList<IotLight> lights = Hub.Resolve(Hub.Selected);
            WizPilot first = FirstPilot();
            bool manual = false, rhythm = lights.Count > 0;
            foreach (IotLight light in lights)
            {
                WizPilot p = Hub.Pilot(light);
                manual |= Hub.IsManual(light);
                rhythm &= p != null && p.InRhythm;
            }
            string status = first == null ? NoData
                : !first.On ? (lights.Count > 1 ? "Apagadas" : "Apagada")
                : manual ? "Manual" : rhythm ? "Ritmo" : "Escena";
            Show(context, KeyImage.Lit("iot-selector", Pretty(Hub.Selected), status, OnKnob(context),
                                       first != null && first.On ? OnTint : OffTint, false));
        }
    }

    internal sealed class IotPaletteAction : IotAction
    {
        public IotPaletteAction(IotHub hub = null) : base(hub) { }
        private readonly AirePreview _aire = new AirePreview();
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
            if (Hub.Selected == "Aire") { _aire.Rotate(Hub, context, ticks, true); Render(context); return; }
            int count = Colors(context).Count;
            _indices[context] = (int)(((Index(context, count) + (long)ticks) % count + count) % count);
            Render(context);
        }

        public override void Press(string context)
        {
            if (Hub.Selected == "Aire") { _aire.Apply(Hub, context, true); return; }
            List<Color> colors = Colors(context);
            Color color = colors[Index(context, colors.Count)];
            Hub.ApplyColor(color.R, color.G, color.B);
        }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _indices.Remove(context);
            _aire.Remove(context);
            if (Contexts.Count == 0) _previews.Clear();
        }

        protected override void Render(string context)
        {
            _aire.Sync(Hub);
            if (Hub.Selected == "Aire")
            {
                Show(context, KeyImage.Lit("iot-aire", "Modo", _aire.Value(Hub, context, true), OnKnob(context), OffTint, false));
                return;
            }
            List<Color> colors = Colors(context);
            Color color = colors[Index(context, colors.Count)];
            string key = color.ToArgb() + "\n" + OnKnob(context);
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
                    image = KeyImage.Launcher(swatch, null, OnKnob(context));
                }
                _previews[key] = image;
            }
            Show(context, image);
        }
    }

    internal sealed class IotBrightnessAction : IotAction
    {
        public IotBrightnessAction(IotHub hub = null) : base(hub) { }
        private readonly AirePreview _aire = new AirePreview();
        public override string Uuid { get { return "com.knupwns.audioknob.iot.brightness"; } }
        public override void Rotate(string context, int ticks)
        {
            if (ticks == 0) return;
            if (Hub.Selected == "Aire")
            {
                Hub.TouchSelection(); _aire.Rotate(Hub, context, ticks, false); Render(context); return;
            }
            long delta = (long)ticks * Math.Max(1, Math.Min(25, Int(context, "step", 5)));
            Hub.AddBrightness((int)Math.Max(int.MinValue, Math.Min(int.MaxValue, delta)));
        }
        public override void Press(string context)
        {
            if (Hub.Selected == "Aire") _aire.Apply(Hub, context, false);
            else Hub.Resume();
        }
        public override void WillDisappear(string context) { base.WillDisappear(context); _aire.Remove(context); }
        protected override void Render(string context)
        {
            _aire.Sync(Hub);
            if (Hub.Selected == "Aire")
            {
                Show(context, KeyImage.Lit("iot-aire", "Temp", _aire.Value(Hub, context, false), OnKnob(context), OffTint, false));
                return;
            }
            WizPilot p = FirstPilot();
            string value = p == null || !p.Dimming.HasValue ? NoData : p.Dimming.Value + " %";
            Show(context, KeyImage.Lit("iot-brightness", "Brillo", value, OnKnob(context),
                                       p != null && p.On ? OnTint : OffTint, false));
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
            bool known = status != null && status.ContainsKey("1"), on = known && Json.Bool(status, "1", false);
            string value = !known ? NoData : on ? "Prendido" : "Apagado";
            Show(context, KeyImage.Lit("iot-plug", alias == "noche" ? "Noche" : "Vestidor", value, OnKnob(context),
                                       on ? OnTint : OffTint, false));
        }
    }

    internal sealed class IotPurifierAction : IotAction
    {
        private static readonly Color AirTint = ColorTranslator.FromHtml("#7FD3DC");
        public override string Uuid { get { return "com.knupwns.audioknob.iot.purifier"; } }
        protected override bool DefaultsToKnob { get { return false; } }
        protected override bool HandlesLongPress { get { return true; } }
        public override void Press(string context) { Hub.TogglePurifier(); }
        public override void LongPress(string context) { Hub.NextPurifierSpeed(); }
        protected override void Render(string context)
        {
            Dictionary<string, object> status = Hub.TuyaStatus("purificador");
            bool on = status != null && Json.Bool(status, "1", false);
            string value;
            if (status == null) value = NoData;
            else if (!on) value = "Apagado";
            else switch (Json.Str(status, "4"))
            {
                case "sleep": value = "Nocturno"; break;
                case "low": value = "Baja"; break;
                case "mid": value = "Media"; break;
                case "high": value = "Alta"; break;
                default: value = "Auto"; break;
            }
            // El punto avisa que toca cambiar el filtro (≤ 15 %).
            int filter = Json.Int(status, "5", -1);
            Show(context, KeyImage.Lit("iot-purifier", "Purificador", value, OnKnob(context),
                                       on ? AirTint : OffTint, filter >= 0 && filter <= 15));
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
            Color tint = OffTint;
            switch (Json.Str(status, "5"))
            {
                case "standby": case "charging": case "charge_done": case "sleep": state = "En base"; break;
                case "cleaning": case "zone_clean": case "part_clean": case "goto_pos":
                    state = "Limpiando"; tint = KeyImage.Good; break;
                case "paused": state = "En pausa"; tint = KeyImage.Warn; break;
                case "goto_charge": state = "Volviendo"; tint = KeyImage.Good; break;
                default: state = status == null ? NoData : "Desconocido"; break;
            }
            int fault = Json.Int(status, "28", 0), previous;
            if (fault != 0 && (!_faults.TryGetValue(context, out previous) || previous != fault)) Host.ShowAlert(context);
            _faults[context] = fault;
            int battery = Json.Int(status, "8", -1);
            if (fault != 0) { state = "Falla"; tint = KeyImage.Bad; }
            string title = battery >= 0 ? "Isaura " + battery + " %" : "Isaura";
            Show(context, KeyImage.Lit("iot-vacuum", title, state, OnKnob(context), tint, false));
        }
    }

    internal sealed class IotLightsAction : IotAction
    {
        public IotLightsAction(IotHub hub = null) : base(hub) { }
        private static readonly Color NeutralTint = ColorTranslator.FromHtml("#F4E7D2");
        private static readonly Color ColdTint = ColorTranslator.FromHtml("#9CCBFF");
        private static readonly Color NightTint = ColorTranslator.FromHtml("#A99BFF");
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
            Color tint;
            if (function == "temp")
            {
                WizPilot p = FirstPilot();
                glyph = "iot-temp"; title = "Tono";
                if (p == null || !p.Temp.HasValue) { value = NoData; tint = OffTint; }
                else if (p.Temp.Value <= 3200) { value = "Cálido"; tint = OnTint; }
                else if (p.Temp.Value <= 5000) { value = "Neutro"; tint = NeutralTint; }
                else { value = "Frío"; tint = ColdTint; }
            }
            else if (function == "alloff")
            {
                glyph = "iot-off"; title = "Apagar todo"; value = string.Empty; tint = KeyImage.Bad;
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
                value = active ? "Activo" : string.Empty;
                tint = !active ? OffTint : bedtime ? NightTint : OnTint;
            }
            Show(context, KeyImage.Lit(glyph, title, value, OnKnob(context), tint, false));
        }
    }
}
