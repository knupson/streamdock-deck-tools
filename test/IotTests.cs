using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace StreamDeckAudio
{
    internal static class IotTests
    {
        private static void Pilot_ParsesRhythm()
        {
            WizPilot p = WizProtocol.ParsePilot("{\"method\":\"getPilot\",\"result\":{\"state\":true,\"sceneId\":0,\"temp\":4200,\"dimming\":79,\"schdPsetId\":5}}");
            Check.True("Pilot_ParsesRhythm: encendida", p.On);
            Check.Equal<int?>("temperatura", 4200, p.Temp);
            Check.Equal<int?>("brillo", 79, p.Dimming);
            Check.True("en ritmo", p.InRhythm);
        }

        private static void Pilot_SceneIsNotRhythm()
        {
            WizPilot p = WizProtocol.ParsePilot("{\"result\":{\"state\":true,\"sceneId\":10,\"dimming\":100}}");
            Check.Equal("Pilot_SceneIsNotRhythm", false, p.InRhythm);
            Check.Equal("escena", 10, p.SceneId);
            Check.Equal<int?>("temperatura ausente", null, p.Temp);
        }

        private static void Pilot_ErrorIsNull()
        {
            Check.Equal<WizPilot>("Pilot_ErrorIsNull", null, WizProtocol.ParsePilot("{\"error\":{\"code\":-32602}}"));
            Check.Equal<WizPilot>("sin result", null, WizProtocol.ParsePilot("{}"));
            Check.Equal<WizPilot>("JSON roto", null, WizProtocol.ParsePilot("{"));
            Check.Equal<WizPilot>("result inválido", null, WizProtocol.ParsePilot("{\"result\":[]}"));
        }

        private static void Resume_IsBareState()
        {
            Check.Equal("Resume_IsBareState", "{\"method\":\"setPilot\",\"params\":{\"state\":true}}", WizProtocol.Resume());
            Check.Equal("GetPilot", "{\"method\":\"getPilot\",\"params\":{}}", WizProtocol.GetPilot());
            var json = new JavaScriptSerializer();
            var set = json.Deserialize<Dictionary<string, object>>(WizProtocol.SetPilot(new Dictionary<string, object> { { "dimming", 30 } }));
            Check.Equal("SetPilot método", "setPilot", Json.Str(set, "method"));
            Check.Equal("SetPilot brillo", 30, Json.Int(Json.Dict(set, "params"), "dimming", 0));
            Check.Equal("ParseMac", "001122aabbcc", WizProtocol.ParseMac("{\"result\":{\"mac\":\"001122aabbcc\"}}"));
            Check.Equal<string>("MAC ausente", null, WizProtocol.ParseMac("{}"));
        }

        private static void WriteConfig(string folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "luces.json"),
                "{\"broadcast\":\"192.0.2.255\",\"grupos\":{\"pc\":{\"norte\":{\"mac\":\"001122aabbcc\",\"ip\":\"192.0.2.1\",\"extra\":true},\"sur\":{\"mac\":\"001122aabbdd\",\"ip\":\"192.0.2.2\"}},\"cama\":{\"este\":{\"mac\":\"001122aabbee\",\"ip\":\"192.0.2.3\"}}},\"tuya\":{\"noche\":{\"nombre\":\"Enchufe inventado\",\"ip\":\"192.0.2.4\",\"tipo\":\"cz\"}},\"x\":1,\"lista\":[null,\"línea\\n\\\"texto\\\"\",2.5]}");
            File.WriteAllText(Path.Combine(folder, "tuya-devices.json"),
                "[{\"name\":\"Otro inventado\",\"id\":\"otro\",\"key\":\"otra\"},{\"name\":\"Enchufe inventado\",\"id\":\"testdev0001\",\"key\":\"0123456789abcdef\"}]");
        }

        private static void Config_LoadsGroupsAndTuya(string folder)
        {
            IotConfig cfg = IotConfig.Load(folder);
            Check.Equal("Config_LoadsGroupsAndTuya: cantidad", 3, cfg.Lights.Count);
            Check.Equal("primera luz", "pc.norte", cfg.Lights[0].FullName);
            Check.Equal("segunda luz", "pc.sur", cfg.Lights[1].FullName);
            Check.Equal("tercera luz", "cama.este", cfg.Lights[2].FullName);
            Check.Equal("clave cruzada por nombre", "0123456789abcdef", cfg.Tuya["noche"].Key);
            Check.Equal("ID cruzado por nombre", "testdev0001", cfg.Tuya["noche"].Id);
            Check.Equal("alias", "noche", cfg.Tuya["noche"].Alias);
        }

        private static void Config_SaveKeepsUnknownKeys(string folder)
        {
            IotConfig cfg = IotConfig.Load(folder);
            string path = Path.Combine(folder, "luces.json");
            var json = new JavaScriptSerializer();
            var before = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            cfg.SaveLightIp(cfg.Lights[0], "192.0.2.20");
            var after = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            Check.Equal("Config_SaveKeepsUnknownKeys: IP en memoria", "192.0.2.20", cfg.Lights[0].Ip);
            Check.Equal("IP persistida", "192.0.2.20", IotConfig.Load(folder).Lights[0].Ip);
            Json.Dict(Json.Dict(Json.Dict(before, "grupos"), "pc"), "norte")["ip"] = "192.0.2.20";
            Check.Equal("sólo cambia la IP", json.Serialize(before), json.Serialize(after));
            Check.True("indentación de dos espacios", File.ReadAllText(path).Contains("\n  \"broadcast\": "));
            cfg.SaveLightIp(cfg.Lights[1], "192.0.2.21");
            Check.Equal("segundo guardado conserva el primero", "192.0.2.20", IotConfig.Load(folder).Lights[0].Ip);
        }

        private static void Discovery_NotRepeatedWithinCooldown(string folder)
        {
            IotConfig cfg = IotConfig.Load(folder);
            DateTime time = new DateTime(2030, 1, 1);
            int calls = 0;
            var client = new WizClient(delegate(string ip) { return null; },
                delegate(string broadcast) { calls++; return new Dictionary<string, string>(); });
            client.EnsureIp(cfg, cfg.Lights[0], delegate { return time; });
            time = time.AddSeconds(59);
            client.EnsureIp(cfg, cfg.Lights[1], delegate { return time; });
            Check.Equal("Discovery_NotRepeatedWithinCooldown", 1, calls);
            time = time.AddSeconds(1);
            client.EnsureIp(cfg, cfg.Lights[0], delegate { return time; });
            Check.Equal("redescubre a los 60 segundos", 2, calls);

            client = new WizClient(delegate(string ip) { return null; },
                delegate(string broadcast) { return new Dictionary<string, string> { { "001122AABBCC", "192.0.2.30" } }; });
            Check.Equal("redescubre por MAC", "192.0.2.30", client.EnsureIp(cfg, cfg.Lights[0], delegate { return time; }));
            Check.Equal("redescubrimiento persiste IP", "192.0.2.30", IotConfig.Load(folder).Lights[0].Ip);
            client = new WizClient(delegate(string ip) { return new WizPilot(); },
                delegate(string broadcast) { throw new Exception("No debería descubrir"); });
            Check.Equal("luz accesible conserva IP", cfg.Lights[0].Ip, client.EnsureIp(cfg, cfg.Lights[0], delegate { return time; }));
        }

        private const string TuyaKey = "0123456789abcdef";
        private const string TuyaId = "testdev0001";
        private const long TuyaTime = 1700000000;

        private static string Hex(byte[] bytes)
        {
            return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        }

        private static void Frame_MatchesTinytuyaQuery()
        {
            // tinytuya: gwId, devId, uid, t, sin espacios; sin encabezado 3.3.
            const string expected = "000055aa000000010000000a000000687b615f96a69a699ee142581f0a3270551ea4cf801d00a865c6c28f7502ef0f24a07f7b749454059ae41950f691f80c65fbc3243210ea82b4130215a124df1a397a8db6f7195e48867341e7cb291a942203000aeb4ff51d29188f82e4a162a1f6c7bc47460000aa55";
            Check.Equal("Frame_MatchesTinytuyaQuery", expected,
                Hex(TuyaProtocol.BuildQuery(TuyaId, TuyaKey, 1, TuyaTime)));
        }

        private static void Frame_MatchesTinytuyaControl()
        {
            // tinytuya: devId, uid, t, dps, sin espacios; encabezado 3.3 + 12 ceros.
            const string expected = "000055aa000000020000000700000067332e330000000000000000000000000d6545c9afde6f8747f491b90fc111ffd21df37d6fd70b00c1e77a5377e3b2dbe6fdc57f49d1a4329ce695e80f17f52cf9825e8a7c974132c83dccc1368b0f44b98d177f076695bd7ecd6cfb887fe9ad569fafe80000aa55";
            Check.Equal("Frame_MatchesTinytuyaControl", expected,
                Hex(TuyaProtocol.BuildControl(TuyaId, TuyaKey, 2,
                    new Dictionary<string, object> { { "1", true } }, TuyaTime)));
        }

        private static void Roundtrip_Decrypt()
        {
            // Incluye bloques exactos, padding completo y UTF-8.
            foreach (string text in new string[] { "", "1234567890123456", "{\"dps\":{\"1\":true,\"4\":\"sueño\"}}" })
            {
                byte[] plain = Encoding.UTF8.GetBytes(text);
                Check.Equal("Roundtrip_Decrypt: " + text.Length, Hex(plain),
                    Hex(TuyaProtocol.Decrypt(TuyaProtocol.Encrypt(plain, TuyaKey), TuyaKey)));
            }
        }

        private static void Decrypt_WrongKeyReturnsNull()
        {
            byte[] cipher = TuyaProtocol.Encrypt(Encoding.UTF8.GetBytes("{\"dps\":{\"1\":true}}"), TuyaKey);
            Check.Equal<byte[]>("Decrypt_WrongKeyReturnsNull", null,
                TuyaProtocol.Decrypt(cipher, "fedcba9876543210"));
            Check.Equal<byte[]>("clave de largo inválido", null, TuyaProtocol.Decrypt(cipher, "corta"));
            Check.Equal<byte[]>("clave no ASCII", null, TuyaProtocol.Decrypt(cipher, "0123456789abcdeñ"));
            Check.Equal<byte[]>("cifrado truncado", null, TuyaProtocol.Decrypt(new byte[15], TuyaKey));
            Check.Equal<byte[]>("cifrado ausente", null, TuyaProtocol.Decrypt(null, TuyaKey));
        }

        private static byte[] TuyaResponse(string json, bool versionHeader, int returnCode)
        {
            byte[] cipher = TuyaProtocol.Encrypt(Encoding.UTF8.GetBytes(json), TuyaKey);
            var payload = new byte[4 + (versionHeader ? 15 : 0) + cipher.Length];
            payload[3] = (byte)returnCode;
            if (versionHeader) Buffer.BlockCopy(Encoding.ASCII.GetBytes("3.3"), 0, payload, 4, 3);
            Buffer.BlockCopy(cipher, 0, payload, versionHeader ? 19 : 4, cipher.Length);
            return TuyaProtocol.Frame(3, 8, payload);
        }

        private static void Parse_EmptyAndVersionHeader()
        {
            Check.Equal<Dictionary<string, object>>("Parse_EmptyAndVersionHeader: ACK vacío", null,
                TuyaProtocol.ParseResponse(TuyaProtocol.Frame(1, TuyaProtocol.CmdDpQuery, new byte[4]), TuyaKey));
            Check.Equal<Dictionary<string, object>>("frame sin payload", null,
                TuyaProtocol.ParseResponse(TuyaProtocol.Frame(1, TuyaProtocol.CmdControl, new byte[0]), TuyaKey));
            foreach (bool version in new bool[] { false, true })
            {
                Dictionary<string, object> dps = TuyaProtocol.ParseResponse(
                    TuyaResponse("{\"devId\":\"testdev0001\",\"dps\":{\"1\":true,\"4\":\"sleep\",\"8\":75}}", version, 0), TuyaKey);
                Check.True("DP1, encabezado " + version, Json.Bool(dps, "1", false));
                Check.Equal("DP4, encabezado " + version, "sleep", Json.Str(dps, "4"));
                Check.Equal("DP8, encabezado " + version, 75, Json.Int(dps, "8", 0));
            }
        }

        private static void Parse_RejectsInvalidFrames()
        {
            byte[] valid = TuyaResponse("{\"dps\":{\"1\":true}}", true, 0);
            Check.Equal<Dictionary<string, object>>("respuesta con otra clave", null,
                TuyaProtocol.ParseResponse(valid, "fedcba9876543210"));
            // Cada campo estructural y el cifrado quedan cubiertos por la validación.
            foreach (int offset in new int[] { 0, 12, 20, valid.Length - 8, valid.Length - 1 })
            {
                byte[] broken = (byte[])valid.Clone();
                broken[offset] ^= 1;
                Check.Equal<Dictionary<string, object>>("frame corrupto en " + offset, null,
                    TuyaProtocol.ParseResponse(broken, TuyaKey));
            }
            for (int length = 0; length < valid.Length; length++)
            {
                var truncated = new byte[length];
                Buffer.BlockCopy(valid, 0, truncated, 0, length);
                if (TuyaProtocol.ParseResponse(truncated, TuyaKey) != null)
                    throw new Exception("Se aceptó un frame Tuya truncado.");
            }
            Check.True("todos los truncamientos devuelven null", true);
            Check.Equal<Dictionary<string, object>>("respuesta nula", null, TuyaProtocol.ParseResponse(null, TuyaKey));
            Check.Equal<Dictionary<string, object>>("return code de error", null,
                TuyaProtocol.ParseResponse(TuyaResponse("{\"dps\":{\"1\":true}}", false, 1), TuyaKey));
            foreach (string json in new string[] { "{", "[]", "{}", "{\"dps\":[]}", "{\"error\":\"falló\",\"dps\":{\"1\":true}}" })
                Check.Equal<Dictionary<string, object>>("JSON sin dps válidos: " + json, null,
                    TuyaProtocol.ParseResponse(TuyaResponse(json, false, 0), TuyaKey));
            var payload = new byte[19];
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("3.3"), 0, payload, 4, 3);
            Check.Equal<Dictionary<string, object>>("encabezado sin cifrado", null,
                TuyaProtocol.ParseResponse(TuyaProtocol.Frame(1, 8, payload), TuyaKey));
            payload = new byte[35];
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("3.4"), 0, payload, 4, 3);
            Check.Equal<Dictionary<string, object>>("versión no soportada", null,
                TuyaProtocol.ParseResponse(TuyaProtocol.Frame(1, 8, payload), TuyaKey));
        }

        private sealed class Sent
        {
            public string Target;
            public Dictionary<string, object> Values;
            public DateTime Time;
        }

        private sealed class FakeWiz : IWizLink
        {
            public readonly Dictionary<string, WizPilot> Pilots = new Dictionary<string, WizPilot>();
            public readonly List<Sent> Sent = new List<Sent>();
            public Func<DateTime> Now;
            public int Gets;
            public string Broken;
            public WizPilot Get(string ip)
            {
                Gets++;
                if (ip == Broken) throw new IOException("Equipo inventado desconectado");
                WizPilot p = Pilots[ip];
                return new WizPilot { On = p.On, SceneId = p.SceneId, Temp = p.Temp, Dimming = p.Dimming, SchdPsetId = p.SchdPsetId };
            }
            public bool Set(string ip, IDictionary<string, object> values)
            {
                Sent.Add(new Sent { Target = ip, Values = new Dictionary<string, object>(values), Time = Now() });
                if (ip == Broken) throw new IOException("Equipo inventado desconectado");
                WizPilot p = Pilots[ip];
                object v;
                if (values.TryGetValue("state", out v)) { p.On = (bool)v; if (p.On) { p.SceneId = 0; p.SchdPsetId = 5; } }
                if (values.TryGetValue("sceneId", out v)) { p.SceneId = (int)v; p.SchdPsetId = null; }
                if (values.TryGetValue("temp", out v)) p.Temp = (int)v;
                if (values.TryGetValue("dimming", out v)) p.Dimming = (int)v;
                return true;
            }
        }
        private sealed class FakeTuya : ITuyaLink
        {
            public readonly Dictionary<string, Dictionary<string, object>> States = new Dictionary<string, Dictionary<string, object>>();
            public readonly List<Sent> Sent = new List<Sent>();
            public int Gets;
            public string Broken;
            public Dictionary<string, object> Status(IotTuyaDevice d)
            {
                Gets++;
                if (d.Alias == Broken) throw new IOException("Equipo inventado sin respuesta");
                Dictionary<string, object> state;
                return States.TryGetValue(d.Alias, out state) ? new Dictionary<string, object>(state)
                    : new Dictionary<string, object> { { "1", false } };
            }
            public bool Set(IotTuyaDevice d, IDictionary<string, object> dps)
            {
                Sent.Add(new Sent { Target = d.Alias, Values = new Dictionary<string, object>(dps) });
                if (d.Alias == Broken) throw new IOException("Equipo inventado sin respuesta");
                Dictionary<string, object> state;
                if (States.TryGetValue(d.Alias, out state))
                    foreach (KeyValuePair<string, object> dp in dps) state[dp.Key] = dp.Value;
                return true;
            }
        }
        private sealed class HubFixture : IDisposable
        {
            public DateTime Now = new DateTime(2030, 1, 1);
            public readonly IotConfig Config = new IotConfig();
            public readonly FakeWiz Wiz = new FakeWiz();
            public readonly FakeTuya Tuya = new FakeTuya();
            public readonly IotHub Hub;
            public HubFixture()
            {
                Config.Lights.Add(new IotLight { Group = "pc", Name = "a", Ip = "192.0.2.1" });
                Config.Lights.Add(new IotLight { Group = "cama", Name = "b", Ip = "192.0.2.2" });
                foreach (IotLight l in Config.Lights)
                    Wiz.Pilots[l.Ip] = new WizPilot { On = true, Temp = 4200, Dimming = 50, SchdPsetId = 5 };
                foreach (string alias in new string[] { "noche", "vestidor", "purificador", "aspiradora" })
                    Config.Tuya[alias] = new IotTuyaDevice { Alias = alias };
                Wiz.Now = delegate { return Now; };
                Hub = new IotHub(Config, Wiz, Tuya, delegate { return Now; }, false);
            }
            public void Seed()
            {
                Hub.SetVisible(this, true);
                Hub.Flush();
                Hub.SetVisible(this, false);
            }
            public void Dispose() { Hub.Dispose(); }
        }

        private static void Selection_CyclesAndWraps()
        {
            using (var f = new HubFixture())
            {
                Check.Equal("orden de objetivos", "Todas,PC,Cama,pc.a,cama.b", string.Join(",", f.Hub.Targets));
                foreach (string selected in new string[] { "PC", "Cama", "pc.a", "cama.b", "Todas" })
                {
                    f.Hub.MoveSelection(1);
                    Check.Equal("Selection_CyclesAndWraps", selected, f.Hub.Selected);
                }
                f.Hub.MoveSelection(-1);
                Check.Equal("giro negativo", "cama.b", f.Hub.Selected);
                f.Hub.MoveSelection(int.MinValue);
                Check.True("giro grande válido", f.Hub.Targets.Contains(f.Hub.Selected));
                Check.Equal("resolver PC", f.Config.Lights[0], f.Hub.Resolve("PC")[0]);
                Check.Equal("resolver Cama", f.Config.Lights[1], f.Hub.Resolve("Cama")[0]);
                Check.Equal("resolver individual", 1, f.Hub.Resolve("pc.a").Count);
                Check.Equal("objetivo desconocido", 0, f.Hub.Resolve("inexistente").Count);
            }
        }
        private static void Selection_ResetsAfter10s()
        {
            using (var f = new HubFixture())
            {
                f.Hub.MoveSelection(1);
                f.Hub.Tick(f.Now.AddSeconds(9));
                Check.Equal("Selection_ResetsAfter10s: antes", "PC", f.Hub.Selected);
                f.Hub.Tick(f.Now.AddSeconds(10));
                Check.Equal("Selection_ResetsAfter10s: límite", "Todas", f.Hub.Selected);
                f.Hub.MoveSelection(1);
                f.Now = f.Now.AddSeconds(8);
                f.Hub.TouchSelection();
                f.Hub.Tick(f.Now.AddSeconds(9));
                Check.Equal("TouchSelection renueva el plazo", "PC", f.Hub.Selected);
            }
        }
        private static void Resume_SendsBareStateAndClearsManual()
        {
            using (var f = new HubFixture())
            {
                f.Seed();
                f.Hub.MoveSelection(1);
                f.Hub.ApplyColor(1, 2, 3);
                Check.True("color marca manual", f.Hub.IsManual(f.Config.Lights[0]));
                f.Hub.AddBrightness(5);
                f.Hub.Resume();
                Check.Equal("acciones sin I/O", 0, f.Wiz.Sent.Count);
                f.Hub.Flush();
                Dictionary<string, object> last = f.Wiz.Sent[f.Wiz.Sent.Count - 1].Values;
                Check.Equal("Resume_SendsBareStateAndClearsManual", "{\"state\":true}", new JavaScriptSerializer().Serialize(last));
                Check.Equal("Resume limpia manual", false, f.Hub.IsManual(f.Config.Lights[0]));
                Check.Equal("Resume cancela brillo pendiente", 2, f.Wiz.Sent.Count);
                f.Hub.AddBrightness(-5);
                f.Hub.ResumeAll();
                f.Hub.Flush();
                foreach (IotLight l in f.Config.Lights) Check.Equal("ResumeAll limpia manual", false, f.Hub.IsManual(l));
            }
        }
        private static void Bedtime_AlwaysAllLights()
        {
            using (var f = new HubFixture())
            {
                f.Hub.MoveSelection(1);
                f.Hub.Bedtime();
                Check.Equal("Bedtime sin I/O", 0, f.Wiz.Gets + f.Wiz.Sent.Count);
                f.Hub.Flush();
                Check.Equal("Bedtime_AlwaysAllLights", 2, f.Wiz.Sent.Count);
                foreach (Sent s in f.Wiz.Sent) Check.Equal("escena dormir", 10, (int)s.Values["sceneId"]);
            }
        }
        private static void AllOff_SkipsVacuum()
        {
            using (var f = new HubFixture())
            {
                f.Hub.MoveSelection(1);
                f.Hub.AllOff();
                Check.Equal("AllOff sin I/O", 0, f.Tuya.Gets + f.Tuya.Sent.Count);
                f.Hub.Flush();
                Check.Equal("AllOff luces", 2, f.Wiz.Sent.Count);
                Check.Equal("AllOff_SkipsVacuum", 3, f.Tuya.Sent.Count);
                for (int i = 0; i < 3; i++)
                {
                    Check.Equal("alias apagado", new string[] { "noche", "vestidor", "purificador" }[i], f.Tuya.Sent[i].Target);
                    Check.Equal("DP1 apagado", false, (bool)f.Tuya.Sent[i].Values["1"]);
                }
            }
        }
        private static void CycleTemperature_FromCurrent()
        {
            using (var f = new HubFixture())
            {
                f.Seed();
                f.Hub.MoveSelection(1);
                foreach (int temp in new int[] { 6500, 2700, 4000, 6500 })
                {
                    f.Hub.CycleTemperature();
                    f.Hub.Flush();
                    Check.Equal("CycleTemperature_FromCurrent", temp, (int)f.Wiz.Sent[f.Wiz.Sent.Count - 1].Values["temp"]);
                }
                Check.True("temperatura marca manual", f.Hub.IsManual(f.Config.Lights[0]));
            }
        }
        private static void Brightness_CoalescesBurst()
        {
            using (var f = new HubFixture())
            {
                f.Seed();
                for (int i = 0; i < 12; i++)
                {
                    f.Hub.AddBrightness(5);
                    f.Hub.Flush();
                    f.Now = f.Now.AddMilliseconds(5);
                }
                Check.Equal("un paquete por lámpara antes de 100 ms", 2, f.Wiz.Sent.Count);
                f.Now = f.Now.AddMilliseconds(40);
                f.Hub.Pump(f.Now);
                Check.Equal("Brightness_CoalescesBurst: dos por lámpara", 4, f.Wiz.Sent.Count);
                for (int i = 2; i < 4; i++)
                {
                    Check.Equal("último brillo", 100, (int)f.Wiz.Sent[i].Values["dimming"]);
                    Check.True("intervalo mínimo", f.Wiz.Sent[i].Time - f.Wiz.Sent[i - 2].Time >= TimeSpan.FromMilliseconds(100));
                }
                f.Hub.AddBrightness(int.MinValue);
                f.Now = f.Now.AddMilliseconds(100);
                f.Hub.Flush();
                Check.Equal("mínimo de brillo", 10, (int)f.Wiz.Sent[4].Values["dimming"]);
                f.Hub.AddBrightness(10);
                f.Hub.Resume();
                f.Hub.Flush();
                f.Now = f.Now.AddSeconds(1);
                f.Hub.Flush();
                Check.Equal("no reaparece brillo después de Resume", "state", new List<string>(f.Wiz.Sent[f.Wiz.Sent.Count - 1].Values.Keys)[0]);
            }
        }
        private static void Polling_VisibilityCacheAndFailures()
        {
            using (var f = new HubFixture())
            {
                int changes = 0;
                f.Hub.Changed += delegate { changes++; };
                f.Hub.Flush();
                Check.Equal("oculto no sondea", 0, f.Wiz.Gets);
                object second = new object();
                f.Hub.SetVisible(f, true);
                Check.Equal("SetVisible sin I/O", 0, f.Wiz.Gets);
                f.Hub.SetVisible(second, true);
                f.Hub.Flush();
                f.Hub.Pilot(f.Config.Lights[0]).Dimming = 1;
                Check.Equal<int?>("caché WiZ defensiva", 50, f.Hub.Pilot(f.Config.Lights[0]).Dimming);
                f.Hub.TuyaStatus("noche")["1"] = true;
                Check.Equal("caché Tuya defensiva", false, (bool)f.Hub.TuyaStatus("noche")["1"]);
                f.Hub.SetVisible(f, false);
                f.Now = f.Now.AddMilliseconds(2999);
                f.Hub.Flush();
                Check.Equal("no sondea antes de 3 s", 2, f.Wiz.Gets);
                f.Wiz.Broken = f.Config.Lights[0].Ip;
                f.Tuya.Broken = "noche";
                f.Now = f.Now.AddMilliseconds(1);
                f.Hub.Flush();
                Check.Equal("otro dueño mantiene sondeo", 4, f.Wiz.Gets);
                Check.Equal<WizPilot>("luz sin respuesta", null, f.Hub.Pilot(f.Config.Lights[0]));
                Check.True("otra luz sigue disponible", f.Hub.Pilot(f.Config.Lights[1]) != null);
                Check.Equal<Dictionary<string, object>>("Tuya sin respuesta", null, f.Hub.TuyaStatus("noche"));
                Check.True("otro Tuya sigue disponible", f.Hub.TuyaStatus("vestidor") != null);
                f.Hub.AllOff();
                f.Hub.Flush();
                Check.Equal("fallo de envío no corta otras luces", 2, f.Wiz.Sent.Count);
                Check.Equal("fallo Tuya no corta otros envíos", 3, f.Tuya.Sent.Count);
                f.Hub.SetVisible(second, false);
                f.Now = f.Now.AddSeconds(3);
                int gets = f.Wiz.Gets;
                f.Hub.Flush();
                Check.Equal("último dueño detiene sondeo", gets, f.Wiz.Gets);
                Check.True("Changed avisa cambios", changes > 0);
            }
        }
        private static void Identify_RestoresWithoutManual()
        {
            using (var f = new HubFixture())
            {
                f.Seed();
                f.Hub.Identify(f.Config.Lights[0]);
                Check.Equal("Identify sin I/O", 0, f.Wiz.Sent.Count);
                f.Now = f.Now.AddSeconds(1);
                f.Hub.Flush();
                Check.Equal("Identify baja brillo", 10, (int)f.Wiz.Sent[0].Values["dimming"]);
                Check.Equal("Identify demora la restauración desde el envío real", 1, f.Wiz.Sent.Count);
                f.Now = f.Now.AddMilliseconds(199);
                f.Hub.Flush();
                Check.Equal("Identify espera sin dormir", 1, f.Wiz.Sent.Count);
                f.Now = f.Now.AddMilliseconds(1);
                f.Hub.Flush();
                Check.Equal("Identify restaura", 50, (int)f.Wiz.Sent[1].Values["dimming"]);
                Check.Equal("Identify no marca manual", false, f.Hub.IsManual(f.Config.Lights[0]));
                f.Hub.Identify(f.Config.Lights[0]);
                f.Hub.Flush();
                f.Hub.ResumeAll();
                f.Hub.Flush();
                int count = f.Wiz.Sent.Count;
                f.Now = f.Now.AddMilliseconds(200);
                f.Hub.Flush();
                Check.Equal("Resume cancela restauración de Identify", count, f.Wiz.Sent.Count);
                f.Wiz.Sent.Clear();
                f.Hub.Toggle();
                f.Hub.Toggle();
                f.Hub.Flush();
                Check.Equal("Toggle conserva ambos toques", 4, f.Wiz.Sent.Count);
                Check.Equal("Toggle primero apaga", false, (bool)f.Wiz.Sent[0].Values["state"]);
                Check.Equal("Toggle luego prende", true, (bool)f.Wiz.Sent[2].Values["state"]);
            }
        }

        private static void Vacuum_StepByStatus()
        {
            string[] states = { "standby", "charging", "charge_done", "sleep", "cleaning", "zone_clean", "part_clean", "goto_pos", "paused", "goto_charge", "unknown" };
            string[] dps = { "1", "1", "1", "1", "2", "2", "2", "2", "3", null, null };
            using (var f = new HubFixture())
            {
                for (int i = 0; i < states.Length; i++)
                {
                    f.Tuya.Sent.Clear();
                    f.Tuya.States["aspiradora"] = new Dictionary<string, object> { { "5", states[i] } };
                    int gets = f.Tuya.Gets;
                    f.Hub.VacuumStep();
                    Check.Equal("Isaura sin I/O al encolar", gets, f.Tuya.Gets);
                    Check.Equal("Isaura sin envío al encolar", 0, f.Tuya.Sent.Count);
                    f.Hub.Flush();
                    Check.Equal("Vacuum_StepByStatus: " + states[i], dps[i] == null ? 0 : 1, f.Tuya.Sent.Count);
                    if (dps[i] == null) continue;
                    Check.Equal("destino Isaura", "aspiradora", f.Tuya.Sent[0].Target);
                    Check.Equal("un solo DP", 1, f.Tuya.Sent[0].Values.Count);
                    Check.Equal("DP de Isaura", true, (bool)f.Tuya.Sent[0].Values[dps[i]]);
                }
                f.Tuya.Broken = "aspiradora";
                f.Tuya.Sent.Clear();
                f.Hub.VacuumStep();
                f.Hub.Flush();
                Check.Equal("Isaura sin respuesta no recibe comandos", 0, f.Tuya.Sent.Count);
                Check.Equal<Dictionary<string, object>>("Isaura muestra error", null, f.Hub.TuyaStatus("aspiradora"));
            }
        }

        private static void Purifier_SpeedCycle()
        {
            using (var f = new HubFixture())
            {
                f.Tuya.States["purificador"] = new Dictionary<string, object> { { "1", false }, { "4", "auto" }, { "5", 12 } };
                foreach (string speed in new string[] { "sleep", "low", "mid", "high", "auto", "sleep" })
                {
                    int gets = f.Tuya.Gets, sent = f.Tuya.Sent.Count;
                    f.Hub.NextPurifierSpeed();
                    Check.Equal("velocidad sin I/O al encolar", gets, f.Tuya.Gets);
                    Check.Equal("velocidad sin envío al encolar", sent, f.Tuya.Sent.Count);
                    f.Hub.Flush();
                    Sent last = f.Tuya.Sent[f.Tuya.Sent.Count - 1];
                    Check.Equal("destino purificador", "purificador", last.Target);
                    Check.Equal("sólo DP4", 1, last.Values.Count);
                    Check.Equal("Purifier_SpeedCycle", speed, (string)last.Values["4"]);
                    Check.Equal("velocidad no prende el equipo", false, (bool)f.Hub.TuyaStatus("purificador")["1"]);
                    Check.Equal("filtro conservado", 12, (int)f.Hub.TuyaStatus("purificador")["5"]);
                }
            }
        }

        private static void Tuya_TogglesAndFailures()
        {
            using (var f = new HubFixture())
            {
                foreach (string alias in new string[] { "noche", "vestidor", "purificador" })
                {
                    f.Tuya.States[alias] = new Dictionary<string, object> { { "1", false } };
                    int sent = f.Tuya.Sent.Count, gets = f.Tuya.Gets;
                    for (int i = 0; i < 2; i++)
                        if (alias == "purificador") f.Hub.TogglePurifier(); else f.Hub.TogglePlug(alias);
                    Check.Equal("toggle sin I/O", gets, f.Tuya.Gets);
                    Check.Equal("toggle sin envío", sent, f.Tuya.Sent.Count);
                    f.Hub.Flush();
                    Check.Equal("dos toques conservados", sent + 2, f.Tuya.Sent.Count);
                    Check.Equal("toggle prende", true, (bool)f.Tuya.Sent[sent].Values["1"]);
                    Check.Equal("toggle apaga", false, (bool)f.Tuya.Sent[sent + 1].Values["1"]);
                }
                f.Tuya.Sent.Clear();
                f.Hub.TogglePlug("aspiradora");
                f.Hub.TogglePlug("inexistente");
                f.Tuya.States["purificador"] = new Dictionary<string, object>();
                f.Hub.TogglePurifier();
                f.Hub.NextPurifierSpeed();
                f.Tuya.Broken = "noche";
                f.Hub.TogglePlug("noche");
                f.Hub.Flush();
                Check.Equal("datos inválidos no envían comandos", 0, f.Tuya.Sent.Count);
                Check.Equal<Dictionary<string, object>>("enchufe sin respuesta", null, f.Hub.TuyaStatus("noche"));
            }
        }

        private static int Live()
        {
            IotConfig cfg = IotConfig.Load(IotConfig.DefaultFolder);
            var client = new WizClient();
            bool failed = false;
            foreach (IotLight light in cfg.Lights)
            {
                WizPilot p = client.Get(light.Ip);
                if (p == null) { Console.WriteLine(light.FullName + " sin respuesta"); failed = true; }
                else Console.WriteLine(light.FullName + " " + p.On + " " + p.Dimming + " " + p.InRhythm);
            }
            var tuya = new TuyaClient();
            foreach (IotTuyaDevice device in cfg.Tuya.Values)
            {
                Dictionary<string, object> dps = tuya.Status(device);
                if (dps == null) { Console.WriteLine(device.Alias + " sin respuesta"); failed = true; }
                else Console.WriteLine(device.Alias + " " + new JavaScriptSerializer().Serialize(dps));
            }
            return failed ? 1 : 0;
        }

        public static int Main(string[] args)
        {
            string folder = null;
            try
            {
                if (args.Length == 1 && args[0] == "--live") return Live();
                if (args.Length != 0) throw new ArgumentException("Uso: IotTests.exe [--live]");
                folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "iot-tests-" + Guid.NewGuid().ToString("N"));
                WriteConfig(folder);
                Pilot_ParsesRhythm();
                Pilot_SceneIsNotRhythm();
                Pilot_ErrorIsNull();
                Resume_IsBareState();
                Config_LoadsGroupsAndTuya(folder);
                Config_SaveKeepsUnknownKeys(folder);
                Discovery_NotRepeatedWithinCooldown(folder);
                Frame_MatchesTinytuyaQuery();
                Frame_MatchesTinytuyaControl();
                Roundtrip_Decrypt();
                Decrypt_WrongKeyReturnsNull();
                Parse_EmptyAndVersionHeader();
                Parse_RejectsInvalidFrames();
                Selection_CyclesAndWraps();
                Selection_ResetsAfter10s();
                Resume_SendsBareStateAndClearsManual();
                Bedtime_AlwaysAllLights();
                AllOff_SkipsVacuum();
                CycleTemperature_FromCurrent();
                Brightness_CoalescesBurst();
                Polling_VisibilityCacheAndFailures();
                Identify_RestoresWithoutManual();
                Vacuum_StepByStatus();
                Purifier_SpeedCycle();
                Tuya_TogglesAndFailures();
                return Check.Report();
            }
            catch (Exception ex) { Console.WriteLine("FALLA: " + ex.Message); return 1; }
            finally { if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }
}
