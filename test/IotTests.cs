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
                return Check.Report();
            }
            catch (Exception ex) { Console.WriteLine("FALLA: " + ex.Message); return 1; }
            finally { if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }
}
