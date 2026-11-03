using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace StreamDeckAudio
{
    internal sealed class IotLight
    {
        public string Group, Name, Mac, Ip;
        public string FullName { get { return Group + "." + Name; } }
    }

    internal sealed class IotTuyaDevice
    {
        public string Alias, Nombre, Ip, Tipo, Id, Key;
    }

    internal sealed class IotConfig
    {
        public const string DefaultFolder = @"E:\Repositorios\Personal\_datos\iot";
        public string Broadcast;
        public string ApiKey, ApiSecret, ApiRegion, InfraredId, RemoteId;
        public string CloudEndpoint
        {
            get
            {
                switch (ApiRegion)
                {
                    case "us": return "https://openapi.tuyaus.com";
                    case "eu": return "https://openapi.tuyaeu.com";
                    case "cn": return "https://openapi.tuyacn.com";
                    case "in": return "https://openapi.tuyain.com";
                    default: return null;
                }
            }
        }
        public readonly List<IotLight> Lights = new List<IotLight>();
        public readonly Dictionary<string, IotTuyaDevice> Tuya = new Dictionary<string, IotTuyaDevice>();
        private string _lightsPath;
        private readonly object _saveLock = new object();

        public static IotConfig Load(string folder)
        {
            var cfg = new IotConfig();
            cfg._lightsPath = Path.GetFullPath(Path.Combine(folder, "luces.json"));
            var serializer = new JavaScriptSerializer();
            var root = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(cfg._lightsPath));
            cfg.Broadcast = Json.Str(root, "broadcast");
            Dictionary<string, object> groups = Json.Dict(root, "grupos");
            if (groups != null)
            {
                foreach (KeyValuePair<string, object> group in groups)
                {
                    var lights = group.Value as Dictionary<string, object>;
                    if (lights == null) continue;
                    foreach (KeyValuePair<string, object> light in lights)
                    {
                        var data = light.Value as Dictionary<string, object>;
                        if (data == null) continue;
                        cfg.Lights.Add(new IotLight { Group = group.Key, Name = light.Key,
                            Mac = Json.Str(data, "mac"), Ip = Json.Str(data, "ip") });
                    }
                }
            }
            List<Dictionary<string, object>> devices = null;
            try
            {
                devices = serializer.Deserialize<List<Dictionary<string, object>>>(
                    File.ReadAllText(Path.Combine(folder, "tuya-devices.json")));
            }
            catch (Exception) { /* Sin archivo válido, no se resuelven los equipos locales Tuya. */ }
            try
            {
                var aire = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(folder, "aire.json")));
                cfg.InfraredId = Json.Str(aire, "infraredId"); cfg.RemoteId = Json.Str(aire, "remoteId");
            }
            catch (Exception) { /* Sin archivo válido, el aire queda sin señal. */ }
            try
            {
                var cloud = serializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(folder, "tuya.json")));
                cfg.ApiKey = Json.Str(cloud, "apiKey"); cfg.ApiSecret = Json.Str(cloud, "apiSecret");
                cfg.ApiRegion = Json.Str(cloud, "apiRegion");
            }
            catch (Exception) { /* Las luces locales no necesitan credenciales cloud. */ }
            Dictionary<string, object> aliases = Json.Dict(root, "tuya");
            if (aliases != null)
            {
                foreach (KeyValuePair<string, object> alias in aliases)
                {
                    var data = alias.Value as Dictionary<string, object>;
                    if (data == null) continue;
                    var device = new IotTuyaDevice { Alias = alias.Key, Nombre = Json.Str(data, "nombre"),
                        Ip = Json.Str(data, "ip"), Tipo = Json.Str(data, "tipo") };
                    if (devices != null)
                    {
                        foreach (Dictionary<string, object> entry in devices)
                        {
                            if (device.Nombre == null || Json.Str(entry, "name") != device.Nombre) continue;
                            device.Id = Json.Str(entry, "id");
                            device.Key = Json.Str(entry, "key");
                            break;
                        }
                    }
                    cfg.Tuya.Add(alias.Key, device);
                }
            }
            return cfg;
        }

        public void SaveLightIp(IotLight light, string newIp)
        {
            lock (_saveLock)
            {
                // Releer para conservar claves ajenas y cambios posteriores a Load.
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(_lightsPath));
                Dictionary<string, object> data = Json.Dict(Json.Dict(Json.Dict(root, "grupos"), light.Group), light.Name);
                if (data == null) throw new InvalidDataException("La luz no existe en luces.json: " + light.FullName);
                data["ip"] = newIp;
                var text = new StringBuilder();
                WriteJson(text, root, 0);
                text.Append('\n');
                string temporary = _lightsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
                    File.Replace(temporary, _lightsPath, null);
                    light.Ip = newIp;
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }

        // JavaScriptSerializer conserva los valores y escapa escalares; acá sólo
        // agregamos estructura e indentación compatible con json.dump(indent=2).
        private static void WriteJson(StringBuilder text, object value, int depth)
        {
            var map = value as IDictionary<string, object>;
            var list = value as IList;
            if (map != null)
            {
                text.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> pair in map)
                {
                    if (!first) text.Append(',');
                    text.Append('\n').Append(' ', (depth + 1) * 2);
                    text.Append(new JavaScriptSerializer().Serialize(pair.Key)).Append(": ");
                    WriteJson(text, pair.Value, depth + 1);
                    first = false;
                }
                if (!first) text.Append('\n').Append(' ', depth * 2);
                text.Append('}');
            }
            else if (list != null)
            {
                text.Append('[');
                for (int i = 0; i < list.Count; i++)
                {
                    if (i != 0) text.Append(',');
                    text.Append('\n').Append(' ', (depth + 1) * 2);
                    WriteJson(text, list[i], depth + 1);
                }
                if (list.Count != 0) text.Append('\n').Append(' ', depth * 2);
                text.Append(']');
            }
            else text.Append(new JavaScriptSerializer().Serialize(value));
        }
    }
}
