using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace StreamDeckAudio
{
    internal sealed class AireState
    {
        public int Power, Mode, Temp;
        public AireState Copy() { return new AireState { Power = Power, Mode = Mode, Temp = Temp }; }
        public static readonly string[] Modes = { "Auto", "Frío", "Seco", "Ventilador" };
        public string Summary { get { return Power == 0 ? "Apagado" : Modes[Mode] + " " + Temp + "°"; } }

        public static AireState Parse(string text)
        {
            string[] parts = (text ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            int power, mode, temp;
            if (parts.Length == 3 && int.TryParse(parts[0], out power) && power >= 0 && power <= 1
                && int.TryParse(parts[1], out mode) && mode >= 0 && mode <= 3
                && int.TryParse(parts[2], out temp) && temp >= 16 && temp <= 30)
                return new AireState { Power = power, Mode = mode, Temp = temp };
            return new AireState { Power = 0, Mode = 1, Temp = 24 };
        }
        public string Serialize() { return Power + " " + Mode + " " + Temp; }
    }

    internal static class AireIr
    {
        public static byte[] Frame(bool toggle, int mode, int temp)
        {
            if (mode < 0 || mode > 3) throw new ArgumentOutOfRangeException("mode", "Modo de aire inválido.");
            if (temp < 16 || temp > 30) throw new ArgumentOutOfRangeException("temp", "Temperatura de aire inválida.");
            int b1 = (mode >= 2 ? 0x10 : 0) | mode | (toggle ? 0x80 : 0);
            return new byte[] { 0xC0, (byte)b1, (byte)temp, (byte)(0xFF - ((0xC0 + b1 + temp) & 0xFF)) };
        }

        public static string Code(byte[] frame)
        {
            if (frame == null || frame.Length != 4) throw new ArgumentException("La trama debe tener cuatro bytes.", "frame");
            var code = new StringBuilder("f6226211");
            foreach (byte value in frame)
                for (int bit = 0; bit < 8; bit++)
                    code.Append("2602").Append((value & (1 << bit)) != 0 ? "7206" : "f401");
            return code.Append("260252bf").ToString();
        }
    }

    internal interface ITuyaCloudLink
    {
        bool SendCode(string irId, string remoteId, string code);
    }

    // Sincrónico y sin logs de credenciales; sólo se usa desde la cola del hub.
    internal sealed class TuyaCloud : ITuyaCloudLink
    {
        private readonly string _key, _secret, _endpoint;
        private string _token;
        private DateTime _expires;
        private readonly object _gate = new object();
        public TuyaCloud(string key, string secret, string endpoint)
        { _key = key; _secret = secret; _endpoint = endpoint; }

        public static string Sign(string secret, string clientId, string token, string timestamp,
                                  string method, string url, string body)
        {
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(body ?? ""))).Replace("-", "").ToLowerInvariant();
            string value = clientId + (token ?? "") + timestamp + method + "\n" + hash + "\n\n" + url;
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret)))
                return BitConverter.ToString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "");
        }

        private Dictionary<string, object> Request(string method, string path, string body, string token)
        {
            // csc compila contra un framework cuyo enum puede no incluir Tls12.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            string timestamp = ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            var request = (HttpWebRequest)WebRequest.Create(_endpoint + path);
            request.Method = method;
            request.Timeout = 5000; request.ReadWriteTimeout = 5000;
            request.Headers["client_id"] = _key;
            request.Headers["t"] = timestamp;
            request.Headers["sign_method"] = "HMAC-SHA256";
            request.Headers["sign"] = Sign(_secret, _key, token, timestamp, method, path, body);
            if (token != null) request.Headers["access_token"] = token;
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentType = "application/json"; request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream()))
            {
                var result = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                return Json.Bool(result, "success", false) ? result : null;
            }
        }

        private bool Token()
        {
            if (string.IsNullOrEmpty(_key) || string.IsNullOrEmpty(_secret) || string.IsNullOrEmpty(_endpoint)) return false;
            if (_token != null && DateTime.UtcNow < _expires) return true;
            DateTime started = DateTime.UtcNow;
            var result = Json.Dict(Request("GET", "/v1.0/token?grant_type=1", null, null), "result");
            _token = Json.Str(result, "access_token");
            _expires = started.AddSeconds(Json.Int(result, "expire_time", 0));
            return !string.IsNullOrEmpty(_token) && DateTime.UtcNow < _expires;
        }

        public bool SendCode(string irId, string remoteId, string code)
        {
            lock (_gate) try
            {
                if (!Token()) return false;
                string body = new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "code", code } });
                return Json.Bool(Request("POST", "/v2.0/infrareds/" + Uri.EscapeDataString(irId)
                    + "/remotes/" + Uri.EscapeDataString(remoteId) + "/learning-codes", body, _token), "result", false);
            }
            catch (Exception) { return false; }
        }
    }
}
