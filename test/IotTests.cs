using System;
using System.Collections.Generic;
using System.IO;
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
                return Check.Report();
            }
            catch (Exception ex) { Console.WriteLine("FALLA: " + ex.Message); return 1; }
            finally { if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }
}
