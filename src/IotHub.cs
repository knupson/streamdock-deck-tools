using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
            public DateTime? DeadUntil;
        }

        private sealed class Work
        {
            public IotLight Light;
            public IotTuyaDevice Device;
            public Dictionary<string, object> Values;
            public DateTime Due;
            public int Version;
            public bool Refresh;
            public string TuyaAction;
            public bool Identify;
            public int? RestoreBrightness;
            public string AireAction;
            public int AireValue;
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
        private readonly ITuyaCloudLink _cloud;
        private AireState _aire;
        private DateTime? _lastAirePoll;
        private bool _aireFailed;
        private readonly HashSet<object> _aireVisible = new HashSet<object>();
        private int _selectionVersion;
        public int SelectionVersion { get { lock (_gate) return _selectionVersion; } }
        public AireState Aire { get { lock (_gate) return _aire == null ? null : _aire.Copy(); } }
        private readonly Func<DateTime> _now;
        private readonly object _gate = new object(), _pumpGate = new object();
        private readonly Dictionary<IotLight, LightState> _lights = new Dictionary<IotLight, LightState>();
        private readonly Dictionary<string, Dictionary<string, object>> _tuyaStatus = new Dictionary<string, Dictionary<string, object>>();
        private readonly Dictionary<string, DateTime> _tuyaDeadUntil = new Dictionary<string, DateTime>();
        // Un equipo que no responde frena todo el sondeo (hasta 3 s por intento): se saltea un rato.
        private static readonly TimeSpan DeadBackoff = TimeSpan.FromSeconds(30);
        private readonly HashSet<object> _visible = new HashSet<object>();
        private readonly List<Work> _queue = new List<Work>();
        private readonly IList<string> _targets;
        private readonly Timer _timer;
        // En producción cada equipo es un carril propio y los carriles corren en paralelo;
        // los tests (background=false) ejecutan en serie para ser deterministas.
        private readonly bool _parallel;
        private static readonly object DiscoveryGate = new object();
        private int _selection;
        private DateTime _touched;
        private DateTime? _lastPoll;
        private bool _disposed;

        public IotHub(IotConfig config, IWizLink wiz, ITuyaLink tuya, Func<DateTime> now)
            : this(config, wiz, tuya, now, true) { }

        // background=false deja toda la ejecución en manos de Pump, sin dormir en tests.
        public IotHub(IotConfig config, IWizLink wiz, ITuyaLink tuya, Func<DateTime> now, bool background)
            : this(config, wiz, tuya, now, background, new TuyaCloud(config.ApiKey, config.ApiSecret, config.CloudEndpoint)) { }

        public IotHub(IotConfig config, IWizLink wiz, ITuyaLink tuya, Func<DateTime> now, bool background, ITuyaCloudLink cloud)
        {
            if (config == null || wiz == null || tuya == null || now == null) throw new ArgumentNullException();
            _config = config; _wiz = wiz; _tuya = tuya; _now = now;
            _cloud = cloud;
            _touched = now();
            var targets = new List<string> { "Todas", "PC", "Cama" };
            foreach (IotLight light in config.Lights)
            {
                targets.Add(light.FullName);
                _lights.Add(light, new LightState());
            }
            targets.Add("Aire");
            _targets = targets.AsReadOnly();
            _parallel = background;
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
                int previous = _selection;
                _selection = (int)(((_selection + (long)ticks) % _targets.Count + _targets.Count) % _targets.Count);
                if (previous != _selection)
                {
                    _selectionVersion++;
                    if (_targets[_selection] == "Aire")
                    {
                        _aireFailed = false;
                        if (_aire == null) _lastAirePoll = null;
                    }
                }
                _touched = _now();
                Wake();
            }
            Notify();
        }

        public void TouchSelection() { lock (_gate) _touched = _now(); }
        public void Tick(DateTime now)
        {
            bool changed = false;
            lock (_gate)
            {
                if (_selection != 0 && now - _touched >= TimeSpan.FromSeconds(10)) { _selection = 0; _selectionVersion++; changed = true; }
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
            if (Selected == "Aire") { ToggleAire(); return; }
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
        private void QueueTuya(string alias, string action)
        {
            lock (_gate)
            {
                IotTuyaDevice device;
                if (_disposed || !_config.Tuya.TryGetValue(alias, out device)) return;
                _queue.Add(new Work { Device = device, TuyaAction = action });
                Wake();
            }
        }

        public void TogglePlug(string alias)
        {
            if (alias == "noche" || alias == "vestidor") QueueTuya(alias, "toggle");
        }
        public void TogglePurifier() { QueueTuya("purificador", "toggle"); }
        public void NextPurifierSpeed() { QueueTuya("purificador", "speed"); }
        public void VacuumStep() { QueueTuya("aspiradora", "vacuum"); }

        public void ToggleAire() { QueueAire("toggle", 0); }
        public void ApplyAireMode(int mode) { QueueAire("mode", Clamp(mode, 0, 4)); }
        public void ApplyAireTemp(int temp) { QueueAire("temp", Clamp(temp, 16, 30)); }
        private void QueueAire(string action, int value)
        {
            lock (_gate)
            {
                if (_disposed || (action != "toggle" && _aire == null)) return;
                _touched = _now();
                _queue.Add(new Work { AireAction = action, AireValue = value });
                Wake();
            }
        }

        public void SetAireVisible(object owner, bool visible)
        {
            lock (_gate)
            {
                if (_disposed) return;
                if (visible) { if (_aireVisible.Add(owner)) { _lastAirePoll = null; _aireFailed = false; } }
                else _aireVisible.Remove(owner);
                Wake();
            }
        }
        private AireState ReadAire()
        {
            if (_cloud == null || string.IsNullOrEmpty(_config.InfraredId) || string.IsNullOrEmpty(_config.RemoteId)) return null;
            try { return _cloud.AcStatus(_config.InfraredId, _config.RemoteId); }
            catch (Exception) { return null; }
        }
        private void ExecuteAire(Work work, DateTime now)
        {
            AireState state = Aire;
            if (state == null && work.AireAction == "toggle") state = ReadAire();
            bool ok = false;
            if (state != null)
            {
                state.Power = work.AireAction == "toggle" ? 1 - state.Power : 1;
                if (work.AireAction == "mode") state.Mode = work.AireValue;
                if (work.AireAction == "temp") state.Temp = work.AireValue;
                try { ok = _cloud.AcSend(_config.InfraredId, _config.RemoteId, state.Power, state.Mode, state.Temp, state.Wind); }
                catch (Exception) { }
            }
            lock (_gate) { _aire = ok ? state : null; _aireFailed = !ok; _lastAirePoll = now; }
        }

        private static Dictionary<string, object> TuyaCommand(string action, Dictionary<string, object> status)
        {
            if (status == null) return null;
            if (action == "toggle")
            {
                object on;
                if (!status.TryGetValue("1", out on) || !(on is bool)) return null;
                return new Dictionary<string, object> { { "1", !(bool)on } };
            }
            if (action == "speed")
            {
                string[] speeds = { "sleep", "low", "mid", "high", "auto" };
                int index = Array.IndexOf(speeds, Json.Str(status, "4"));
                return index < 0 ? null : new Dictionary<string, object> { { "4", speeds[(index + 1) % speeds.Length] } };
            }
            string dp;
            switch (Json.Str(status, "5"))
            {
                case "standby": case "charging": case "charge_done": case "sleep": dp = "1"; break;
                case "cleaning": case "zone_clean": case "part_clean": case "goto_pos": dp = "2"; break;
                case "paused": dp = "3"; break;
                default: return null;
            }
            return new Dictionary<string, object> { { dp, true } };
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
            if (p == null && real != null)
            {
                string ip;
                lock (DiscoveryGate) ip = real.EnsureIp(_config, light, _now);
                if (ip != null) p = _wiz.Get(light.Ip);
            }
            return p;
        }
        private void Refresh(IotLight light, int version)
        {
            WizPilot p = null;
            try { p = Read(light); } catch (Exception) { }
            lock (_gate)
            {
                LightState state = _lights[light];
                state.DeadUntil = p == null ? _now().Add(DeadBackoff) : (DateTime?)null;
                if (state.Version == version && !state.Brightness.HasValue) state.Pilot = Copy(p);
            }
        }
        // Sólo con _gate tomado.
        private bool IsDead(Work w, DateTime now)
        {
            DateTime until;
            if (w.Light != null) return _lights[w.Light].DeadUntil.HasValue && _lights[w.Light].DeadUntil.Value > now;
            return w.Device != null && _tuyaDeadUntil.TryGetValue(w.Device.Alias, out until) && until > now;
        }
        private void SetTuyaStatus(string alias, Dictionary<string, object> status)
        {
            lock (_gate)
            {
                _tuyaStatus[alias] = status == null ? null : new Dictionary<string, object>(status);
                if (status == null) _tuyaDeadUntil[alias] = _now().Add(DeadBackoff); else _tuyaDeadUntil.Remove(alias);
            }
        }
        // Sólo con _gate tomado. Mismo criterio que la cola de Pump, para que el sondeo le ceda el paso.
        private bool HasWork(DateTime now)
        {
            if (_queue.Exists(delegate(Work w) { return w.Due <= now; })) return true;
            foreach (LightState state in _lights.Values)
                if (state.Brightness.HasValue && (!state.LastBrightness.HasValue
                    || now - state.LastBrightness.Value >= TimeSpan.FromMilliseconds(100))) return true;
            return false;
        }
        private void Execute(Work work, DateTime now)
        {
            if (work.AireAction != null) { ExecuteAire(work, now); return; }
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
                // Los Tuya aceptan una conexión por vez y a veces rechazan la siguiente
                // si llega muy pegada: cada paso se reintenta una vez.
                Dictionary<string, object> status = null;
                bool reached = false;
                try
                {
                    Dictionary<string, object> values = work.Values;
                    if (work.TuyaAction != null)
                    {
                        status = TuyaRetry(delegate { return _tuya.Status(work.Device); });
                        values = TuyaCommand(work.TuyaAction, status);
                        reached = status != null;
                    }
                    if (values != null)
                    {
                        Dictionary<string, object> before = status;
                        bool sent = TuyaRetry(delegate { return _tuya.Set(work.Device, values) ? new Dictionary<string, object>() : null; }) != null;
                        reached = sent;
                        status = sent ? TuyaRetry(delegate { return _tuya.Status(work.Device); }) : null;
                        // La orden llegó pero la relectura no: se muestra lo que se mandó.
                        if (sent && status == null && before != null)
                        {
                            status = new Dictionary<string, object>(before);
                            foreach (KeyValuePair<string, object> dp in values) status[dp.Key] = dp.Value;
                        }
                    }
                }
                catch (Exception) { status = null; }
                lock (_gate)
                {
                    // Una orden no marca el equipo como caído: eso queda para el sondeo.
                    if (status != null || !reached) _tuyaStatus[work.Device.Alias] = status == null ? null : new Dictionary<string, object>(status);
                    if (status != null) _tuyaDeadUntil.Remove(work.Device.Alias);
                }
            }
        }

        // Ejecuta I/O: lo llama el Timer del ThreadPool o el test explícitamente.
        // Un callback que se superpone se descarta; nunca acumula hilos esperando red.
        // En producción Pump sólo reparte: cada equipo tiene su carril y nadie espera a otro.
        public void Pump(DateTime now)
        {
            if (!Monitor.TryEnter(_pumpGate)) return;
            try
            {
                Tick(now);
                bool changed = false;
              restart:
                while (true)
                {
                    var batch = new List<Work>();
                    lock (_gate)
                    {
                        if (_disposed) return;
                        // Los equipos que responden primero: uno caído tarda segundos en fallar.
                        batch.AddRange(_queue.FindAll(delegate(Work w) { return w.Due <= now && !IsDead(w, now); }));
                        batch.AddRange(_queue.FindAll(delegate(Work w) { return w.Due <= now && IsDead(w, now); }));
                        _queue.RemoveAll(delegate(Work w) { return w.Due <= now; });
                        foreach (IotLight light in _config.Lights)
                        {
                            LightState state = _lights[light];
                            if (!state.Brightness.HasValue || (state.LastBrightness.HasValue
                                && now - state.LastBrightness.Value < TimeSpan.FromMilliseconds(100))) continue;
                            batch.Add(new Work { Light = light, Values = new Dictionary<string, object> { { "dimming", state.Brightness.Value } }, Version = state.Version });
                            state.Brightness = null;
                            DateTime actual = _now();
                            state.LastBrightness = actual > now ? actual : now;
                        }
                    }
                    if (batch.Count == 0) break;
                    // Agrupar por equipo conserva el orden de las órdenes de cada uno.
                    var lanes = new List<List<Work>>();
                    var byKey = new Dictionary<string, List<Work>>();
                    foreach (Work w in batch)
                    {
                        List<Work> lane;
                        string key = LaneKey(w);
                        if (!byKey.TryGetValue(key, out lane)) { lane = new List<Work>(); byKey.Add(key, lane); lanes.Add(lane); }
                        lane.Add(w);
                    }
                    foreach (List<Work> lane in lanes)
                    {
                        List<Work> items = lane;
                        Post(LaneKey(items[0]), delegate
                        {
                            foreach (Work w in items) Timed(w, true, delegate { Execute(w, now); });
                        });
                    }
                    changed = true;
                }
                bool pollAire;
                lock (_gate)
                {
                    pollAire = !_aireFailed && (_aireVisible.Count != 0 || _targets[_selection] == "Aire")
                        && (!_lastAirePoll.HasValue || now - _lastAirePoll.Value >= TimeSpan.FromSeconds(60))
                        && !LaneBusy("aire");
                    if (pollAire) _lastAirePoll = now;
                }
                if (pollAire)
                {
                    Post("aire", delegate
                    {
                        AireState state = ReadAire();
                        lock (_gate) { _aire = state; _aireFailed = state == null; }
                    });
                    changed = true;
                }
                // Mostrar el resultado cloud sin esperar las lecturas de equipos locales.
                if (changed) { Notify(); changed = false; }
                bool poll;
                lock (_gate)
                {
                    poll = _visible.Count != 0 && (!_lastPoll.HasValue || now - _lastPoll.Value >= TimeSpan.FromSeconds(3));
                    if (poll) _lastPoll = now;
                }
                if (poll)
                {
                    var reads = new List<Work>();
                    foreach (IotLight light in _config.Lights) reads.Add(new Work { Light = light });
                    foreach (IotTuyaDevice device in _config.Tuya.Values) reads.Add(new Work { Device = device });
                    foreach (Work read in reads)
                    {
                        Work item = read;
                        int version = 0;
                        lock (_gate)
                        {
                            if (_visible.Count == 0 || _disposed) break;
                            // Equipo caído, o carril ocupado con una orden o una lectura anterior: se saltea esta vuelta.
                            if (IsDead(item, now) || LaneBusy(LaneKey(item))) continue;
                            if (item.Light != null) version = _lights[item.Light].Version;
                        }
                        Post(LaneKey(item), delegate
                        {
                            Timed(item, false, delegate
                            {
                                if (item.Light != null) { Refresh(item.Light, version); return; }
                                Dictionary<string, object> status = null;
                                try { status = _tuya.Status(item.Device); } catch (Exception) { }
                                SetTuyaStatus(item.Device.Alias, status);
                            });
                        });
                    }
                    changed = true;
                    // Una orden que llegó mientras se leía (sólo pasa en serie) se ejecuta en esta misma vuelta.
                    bool more;
                    lock (_gate) more = HasWork(now);
                    if (more) { Notify(); changed = false; goto restart; }
                }
                lock (_gate)
                {
                    bool pending = _queue.Count != 0 || _visible.Count != 0 || _aireVisible.Count != 0 || _selection != 0;
                    foreach (LightState state in _lights.Values) pending |= state.Brightness.HasValue;
                    if (!pending && _timer != null && !_disposed) _timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                if (changed) Notify();
            }
            finally { Monitor.Exit(_pumpGate); }
        }
        public void Flush() { Pump(_now()); }

        private Dictionary<string, object> TuyaRetry(Func<Dictionary<string, object>> op)
        {
            Dictionary<string, object> result = null;
            try { result = op(); } catch (Exception) { }
            if (result != null) return result;
            if (_parallel) Thread.Sleep(150);
            try { return op(); } catch (Exception) { return null; }
        }

        private static string LaneKey(Work w)
        {
            if (w.AireAction != null) return "aire";
            return w.Light != null ? "wiz:" + w.Light.FullName : "tuya:" + w.Device.Alias;
        }

        private sealed class Lane
        {
            public readonly Queue<Action> Items = new Queue<Action>();
            public bool Running;
        }
        private readonly Dictionary<string, Lane> _lanes = new Dictionary<string, Lane>();

        // Sólo con _gate tomado.
        private bool LaneBusy(string key)
        {
            Lane lane;
            return _lanes.TryGetValue(key, out lane) && lane.Running;
        }

        // Cada equipo procesa lo suyo en orden, en un hilo del ThreadPool; los equipos no se esperan
        // entre sí y Pump nunca queda bloqueado por un equipo lento. En tests corre en el acto.
        private void Post(string key, Action action)
        {
            if (!_parallel) { action(); return; }
            Lane lane;
            bool start;
            lock (_gate)
            {
                if (_disposed) return;
                if (!_lanes.TryGetValue(key, out lane)) { lane = new Lane(); _lanes.Add(key, lane); }
                lane.Items.Enqueue(action);
                start = !lane.Running;
                lane.Running = true;
            }
            if (start) ThreadPool.QueueUserWorkItem(delegate { RunLane(key, lane); });
        }
        private void RunLane(string key, Lane lane)
        {
            while (true)
            {
                Action action;
                lock (_gate)
                {
                    if (lane.Items.Count == 0 || _disposed) { lane.Items.Clear(); lane.Running = false; return; }
                    action = lane.Items.Dequeue();
                }
                try { action(); }
                catch (Exception ex) { Log(key + " excepción: " + ex.Message); }
                Notify();
                // Una restauración de Identify u otra orden diferida necesita que el timer siga vivo.
                lock (_gate) if (_queue.Count != 0) Wake();
            }
        }
        private static void Timed(Work w, bool order, Action action)
        {
            var timer = Stopwatch.StartNew();
            action();
            if (timer.ElapsedMilliseconds >= 700)
                Log(LaneKey(w) + (order ? " orden " : " lectura ") + timer.ElapsedMilliseconds + " ms");
        }

        // Diagnóstico liviano: sólo operaciones lentas o con error, en %TEMP%\decktools-iot.log.
        private static readonly object LogGate = new object();
        private static void Log(string line)
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "decktools-iot.log");
                lock (LogGate)
                {
                    if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
                }
            }
            catch (Exception) { }
        }
        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _visible.Clear();
                _aireVisible.Clear();
                _queue.Clear();
                if (_timer != null) _timer.Dispose();
            }
        }
    }
}
