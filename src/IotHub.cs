using System;
using System.Collections.Generic;
using System.Threading;

namespace StreamDeckAudio
{
    internal sealed class IotHub : IDisposable
    {
        private sealed class LightState
        {
            public WizPilot Pilot;
            public bool Manual;
            public int Version;
            public int? Brightness;
            public DateTime? LastBrightness;
        }

        private sealed class Work
        {
            public IotLight Light;
            public IotTuyaDevice Device;
            public Dictionary<string, object> Values;
            public DateTime Due;
            public int Version;
            public bool Refresh;
            public bool Identify;
            public int? RestoreBrightness;
        }

        private static readonly Lazy<IotHub> Instance = new Lazy<IotHub>(delegate
        {
            return new IotHub(IotConfig.Load(IotConfig.DefaultFolder), new WizClient(),
                new TuyaClient(), delegate { return DateTime.UtcNow; });
        });
        public static IotHub Shared { get { return Instance.Value; } }

        private readonly IotConfig _config;
        private readonly IWizLink _wiz;
        private readonly ITuyaLink _tuya;
        private readonly Func<DateTime> _now;
        private readonly object _gate = new object(), _pumpGate = new object();
        private readonly Dictionary<IotLight, LightState> _lights = new Dictionary<IotLight, LightState>();
        private readonly Dictionary<string, Dictionary<string, object>> _tuyaStatus = new Dictionary<string, Dictionary<string, object>>();
        private readonly HashSet<object> _visible = new HashSet<object>();
        private readonly List<Work> _queue = new List<Work>();
        private readonly IList<string> _targets;
        private readonly Timer _timer;
        private int _selection;
        private DateTime _touched;
        private DateTime? _lastPoll;
        private bool _disposed;

        public IotHub(IotConfig config, IWizLink wiz, ITuyaLink tuya, Func<DateTime> now)
            : this(config, wiz, tuya, now, true) { }

        // background=false deja toda la ejecución en manos de Pump, sin dormir en tests.
        public IotHub(IotConfig config, IWizLink wiz, ITuyaLink tuya, Func<DateTime> now, bool background)
        {
            if (config == null || wiz == null || tuya == null || now == null) throw new ArgumentNullException();
            _config = config; _wiz = wiz; _tuya = tuya; _now = now;
            _touched = now();
            var targets = new List<string> { "Todas", "PC", "Cama" };
            foreach (IotLight light in config.Lights)
            {
                targets.Add(light.FullName);
                _lights.Add(light, new LightState());
            }
            _targets = targets.AsReadOnly();
            if (background) _timer = new Timer(delegate(object ignored) { Pump(_now()); }, null, Timeout.Infinite, Timeout.Infinite);
        }

        public IList<string> Targets { get { return _targets; } }
        public string Selected { get { lock (_gate) return _targets[_selection]; } }
        // Se dispara en el hilo que procesa el cambio. La UI debe usar Host.Invoke.
        public event Action Changed;
        private void Notify()
        {
            Action changed = Changed;
            if (changed == null) return;
            foreach (Action handler in changed.GetInvocationList())
            {
                try { handler(); }
                catch (Exception) { /* Un consumidor no debe detener el sondeo. */ }
            }
        }

        public void MoveSelection(int ticks)
        {
            lock (_gate)
            {
                _selection = (int)(((_selection + (long)ticks) % _targets.Count + _targets.Count) % _targets.Count);
                _touched = _now();
            }
            Notify();
        }

        public void TouchSelection() { lock (_gate) _touched = _now(); }
        public void Tick(DateTime now)
        {
            bool changed = false;
            lock (_gate)
            {
                if (_selection != 0 && now - _touched >= TimeSpan.FromSeconds(10)) { _selection = 0; changed = true; }
            }
            if (changed) Notify();
        }

        public IList<IotLight> Resolve(string target)
        {
            var result = new List<IotLight>();
            foreach (IotLight light in _config.Lights)
                if (target == "Todas" || target == light.FullName
                    || (target == "PC" && light.Group == "pc") || (target == "Cama" && light.Group == "cama")) result.Add(light);
            return result.AsReadOnly();
        }

        private static WizPilot Copy(WizPilot p)
        {
            if (p == null) return null;
            return new WizPilot { On = p.On, SceneId = p.SceneId, Temp = p.Temp, Dimming = p.Dimming,
                R = p.R, G = p.G, B = p.B, SchdPsetId = p.SchdPsetId };
        }
        public WizPilot Pilot(IotLight light) { lock (_gate) return Copy(_lights[light].Pilot); }
        public bool IsManual(IotLight light) { lock (_gate) return _lights[light].Manual; }
        public Dictionary<string, object> TuyaStatus(string alias)
        {
            lock (_gate)
            {
                Dictionary<string, object> status;
                return _tuyaStatus.TryGetValue(alias, out status) && status != null ? new Dictionary<string, object>(status) : null;
            }
        }

        private void Wake()
        {
            if (!_disposed && _timer != null) _timer.Change(0, 50);
        }

        // Sólo se llama con _gate tomado; nunca hace I/O.
        private void Enqueue(IotLight light, Dictionary<string, object> values, bool? manual)
        {
            LightState state = _lights[light];
            state.Version++;
            state.Brightness = null;
            // Cancela también restauraciones pendientes de Identify.
            _queue.RemoveAll(delegate(Work w) { return w.Light == light && w.Identify; });
            if (manual.HasValue) state.Manual = manual.Value;
            // La intención más reciente también guía giros/toques antes de ejecutar la cola.
            if (state.Pilot != null)
            {
                object value;
                if (values.TryGetValue("state", out value)) state.Pilot.On = (bool)value;
                if (values.TryGetValue("temp", out value)) state.Pilot.Temp = (int)value;
            }
            _queue.Add(new Work { Light = light, Values = values, Version = state.Version, Refresh = true });
        }

        private void QueueSelected(Dictionary<string, object> values, bool? manual, bool all)
        {
            _touched = _now();
            foreach (IotLight light in Resolve(all ? "Todas" : _targets[_selection])) Enqueue(light, values, manual);
            Wake();
        }

        private void SendSelected(Dictionary<string, object> values, bool? manual, bool all)
        {
            lock (_gate)
            {
                if (_disposed) return;
                QueueSelected(values, manual, all);
            }
            Notify();
        }
        public void Toggle()
        {
            lock (_gate)
            {
                if (_disposed) return;
                IList<IotLight> lights = Resolve(_targets[_selection]);
                bool on = lights.Count != 0 && _lights[lights[0]].Pilot != null && _lights[lights[0]].Pilot.On;
                QueueSelected(new Dictionary<string, object> { { "state", !on } }, on ? (bool?)null : false, false);
            }
            Notify();
        }
        public void Resume() { SendSelected(new Dictionary<string, object> { { "state", true } }, false, false); }
        public void ResumeAll() { SendSelected(new Dictionary<string, object> { { "state", true } }, false, true); }
        public void Bedtime() { SendSelected(new Dictionary<string, object> { { "sceneId", 10 } }, null, true); }
        public void ApplyColor(int r, int g, int b)
        {
            SendSelected(new Dictionary<string, object> { { "r", Clamp(r, 0, 255) }, { "g", Clamp(g, 0, 255) }, { "b", Clamp(b, 0, 255) } }, true, false);
        }
        private static int Clamp(long value, int min, int max) { return (int)Math.Max(min, Math.Min(max, value)); }
        public void CycleTemperature()
        {
            lock (_gate)
            {
                if (_disposed) return;
                IList<IotLight> lights = Resolve(_targets[_selection]);
                WizPilot p = lights.Count == 0 ? null : _lights[lights[0]].Pilot;
                int temp = p == null ? 0 : p.Temp.GetValueOrDefault();
                int next = temp < 2700 || temp >= 6500 ? 2700 : temp < 4000 ? 4000 : 6500;
                QueueSelected(new Dictionary<string, object> { { "temp", next } }, true, false);
            }
            Notify();
        }
        public void AddBrightness(int delta)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _touched = _now();
                foreach (IotLight light in Resolve(_targets[_selection]))
                {
                    LightState state = _lights[light];
                    int current = state.Brightness ?? (state.Pilot == null ? 100 : state.Pilot.Dimming ?? 100);
                    state.Brightness = Clamp((long)current + delta, 10, 100);
                    state.Manual = true;
                    state.Version++;
                    _queue.RemoveAll(delegate(Work w) { return w.Light == light && w.Identify; });
                    if (state.Pilot != null) state.Pilot.Dimming = state.Brightness;
                }
                Wake();
            }
            Notify();
        }
        public void AllOff()
        {
            lock (_gate)
            {
                if (_disposed) return;
                QueueSelected(new Dictionary<string, object> { { "state", false } }, null, true);
                foreach (string alias in new string[] { "noche", "vestidor", "purificador" })
                {
                    IotTuyaDevice device;
                    if (_config.Tuya.TryGetValue(alias, out device))
                        _queue.Add(new Work { Device = device, Values = new Dictionary<string, object> { { "1", false } } });
                }
                Wake();
            }
            Notify();
        }
        public void Identify(IotLight light)
        {
            lock (_gate)
            {
                if (_disposed) return;
                LightState state = _lights[light];
                if (state.Pilot == null || !state.Pilot.On || state.Brightness.HasValue) return;
                // Repetir la selección no apila parpadeos ni altera la marca manual.
                _queue.RemoveAll(delegate(Work w) { return w.Light == light && w.Identify; });
                int previous = state.Pilot.Dimming ?? 100;
                _queue.Add(new Work { Light = light, Values = new Dictionary<string, object> { { "dimming", 10 } },
                    Version = state.Version, Identify = true, RestoreBrightness = previous });
                Wake();
            }
        }

        public void SetVisible(object owner, bool visible)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            lock (_gate)
            {
                if (_disposed) return;
                if (visible)
                {
                    if (_visible.Count == 0) _lastPoll = null;
                    _visible.Add(owner);
                }
                else _visible.Remove(owner);
                Wake();
            }
        }

        private WizPilot Read(IotLight light)
        {
            WizPilot p = _wiz.Get(light.Ip);
            var real = _wiz as WizClient;
            if (p == null && real != null && real.EnsureIp(_config, light, _now) != null) p = _wiz.Get(light.Ip);
            return p;
        }
        private void Refresh(IotLight light, int version)
        {
            WizPilot p = null;
            try { p = Read(light); } catch (Exception) { }
            lock (_gate)
                if (_lights[light].Version == version && !_lights[light].Brightness.HasValue) _lights[light].Pilot = Copy(p);
        }
        private void Execute(Work work, DateTime now)
        {
            if (work.Light != null)
            {
                bool ok = false;
                try { ok = _wiz.Set(work.Light.Ip, work.Values); } catch (Exception) { }
                if (ok && work.RestoreBrightness.HasValue)
                {
                    lock (_gate)
                    {
                        if (!_disposed && _lights[work.Light].Version == work.Version)
                        {
                            DateTime actual = _now();
                            _queue.Add(new Work { Light = work.Light, Version = work.Version, Identify = true,
                                Values = new Dictionary<string, object> { { "dimming", work.RestoreBrightness.Value } },
                                Due = (actual > now ? actual : now).AddMilliseconds(200) });
                        }
                    }
                }
                if (ok && work.Refresh) Refresh(work.Light, work.Version);
                if (!ok) lock (_gate) { if (_lights[work.Light].Version == work.Version) _lights[work.Light].Pilot = null; }
            }
            else
            {
                Dictionary<string, object> status = null;
                try { if (_tuya.Set(work.Device, work.Values)) status = _tuya.Status(work.Device); } catch (Exception) { }
                lock (_gate) _tuyaStatus[work.Device.Alias] = status == null ? null : new Dictionary<string, object>(status);
            }
        }

        // Ejecuta I/O: lo llama el Timer del ThreadPool o el test explícitamente.
        // Un callback que se superpone se descarta; nunca acumula hilos esperando red.
        public void Pump(DateTime now)
        {
            if (!Monitor.TryEnter(_pumpGate)) return;
            try
            {
                Tick(now);
                bool changed = false;
                while (true)
                {
                    Work work = null;
                    lock (_gate)
                    {
                        if (_disposed) return;
                        int index = _queue.FindIndex(delegate(Work w) { return w.Due <= now; });
                        if (index >= 0) { work = _queue[index]; _queue.RemoveAt(index); }
                        else foreach (IotLight light in _config.Lights)
                        {
                            LightState state = _lights[light];
                            if (!state.Brightness.HasValue || (state.LastBrightness.HasValue
                                && now - state.LastBrightness.Value < TimeSpan.FromMilliseconds(100))) continue;
                            work = new Work { Light = light, Values = new Dictionary<string, object> { { "dimming", state.Brightness.Value } }, Version = state.Version };
                            state.Brightness = null;
                            DateTime actual = _now();
                            state.LastBrightness = actual > now ? actual : now;
                            break;
                        }
                    }
                    if (work == null) break;
                    Execute(work, now);
                    changed = true;
                }
                bool poll;
                lock (_gate)
                {
                    poll = _visible.Count != 0 && (!_lastPoll.HasValue || now - _lastPoll.Value >= TimeSpan.FromSeconds(3));
                    if (poll) _lastPoll = now;
                }
                if (poll)
                {
                    foreach (IotLight light in _config.Lights)
                    {
                        int version;
                        lock (_gate) { if (_visible.Count == 0 || _disposed) break; version = _lights[light].Version; }
                        Refresh(light, version);
                    }
                    foreach (IotTuyaDevice device in _config.Tuya.Values)
                    {
                        lock (_gate) { if (_visible.Count == 0 || _disposed) break; }
                        Dictionary<string, object> status = null;
                        try { status = _tuya.Status(device); } catch (Exception) { }
                        lock (_gate) _tuyaStatus[device.Alias] = status == null ? null : new Dictionary<string, object>(status);
                    }
                    changed = true;
                }
                lock (_gate)
                {
                    bool pending = _queue.Count != 0 || _visible.Count != 0;
                    foreach (LightState state in _lights.Values) pending |= state.Brightness.HasValue;
                    if (!pending && _timer != null && !_disposed) _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                if (changed) Notify();
            }
            finally { Monitor.Exit(_pumpGate); }
        }
        public void Flush() { Pump(_now()); }
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _visible.Clear();
                _queue.Clear();
                if (_timer != null) _timer.Dispose();
            }
        }
    }
}
