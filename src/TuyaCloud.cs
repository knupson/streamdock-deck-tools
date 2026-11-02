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
        public int Power, Mode, Temp, Wind;
        public AireState Copy() { return new AireState { Power = Power, Mode = Mode, Temp = Temp, Wind = Wind }; }
        public static readonly string[] Modes = { "Frío", "Calor", "Auto", "Vent", "Seco" };
        public string Summary { get { return Power == 0 ? "Apagado" : Modes[Mode] + " " + Temp + "°"; } }
    }

    internal interface ITuyaCloudLink
    {
        AireState AcStatus(string irId, string remoteId);
        bool AcSend(string irId, string remoteId, int power, int mode, int temp, int wind);
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

        public AireState AcStatus(string irId, string remoteId)
        {
            lock (_gate) try
            {
                if (!Token()) return null;
                var data = Json.Dict(Request("GET", "/v2.0/infrareds/" + Uri.EscapeDataString(irId)
                    + "/remotes/" + Uri.EscapeDataString(remoteId) + "/ac/status", null, _token), "result");
                int power = Json.Int(data, "power", -1), mode = Json.Int(data, "mode", -1),
                    temp = Json.Int(data, "temp", -1), wind = Json.Int(data, "wind", -1);
                if (power < 0 || power > 1 || mode < 0 || mode > 4 || temp < 16 || temp > 30 || wind < 0 || wind > 3) return null;
                return new AireState { Power = power, Mode = mode, Temp = temp, Wind = wind };
            }
            catch (Exception) { return null; }
        }

        public bool AcSend(string irId, string remoteId, int power, int mode, int temp, int wind)
        {
            lock (_gate) try
            {
                if (!Token()) return false;
                string body = new JavaScriptSerializer().Serialize(new Dictionary<string, object> {
                    { "power", power }, { "mode", mode }, { "temp", temp }, { "wind", wind } });
                return Request("POST", "/v2.0/infrareds/" + Uri.EscapeDataString(irId)
                    + "/air-conditioners/" + Uri.EscapeDataString(remoteId) + "/scenes/command", body, _token) != null;
            }
            catch (Exception) { return false; }
        }
    }
}
