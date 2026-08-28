// Credenciales de Discord: store cifrado, renovacion y alta.
//
// Regla que ordena todo este archivo: el token nuevo se escribe antes de
// descartar el viejo. Si la renovacion falla, la credencial vieja sigue en
// disco y se reintenta despues. Nunca hay un estado sin credencial.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace StreamDeckAudio
{
    internal sealed class DiscordToken
    {
        public string Access;
        public string Refresh;
        public string Scope;
        public DateTime ExpiresUtc;

        // 7 dias es un supuesto sobre el lifetime real de los tokens de Discord,
        // no algo que leamos del token: no hay issued-at en el store, asi que
        // no hay forma de calcular el 80% transcurrido de verdad. Estas dos
        // constantes solo sirven para derivar el umbral fijo de abajo.
        private const double LifetimeDays = 7.0;
        private const double RenewAfter = 0.8;

        /// <summary>
        /// Cierto cuando faltan 1.4 dias o menos. Ese numero es 20 % de los 7
        /// dias asumidos arriba -- equivale a "80 % de la vida consumida" solo
        /// mientras Discord siga emitiendo tokens de 7 dias. Renovar temprano
        /// deja margen para reintentar; renovar al vencer no deja ninguno.
        /// </summary>
        public bool NeedsRenewal
        {
            get
            {
                double left = (ExpiresUtc - DateTime.UtcNow).TotalDays;
                return left <= LifetimeDays * (1.0 - RenewAfter);
            }
        }
    }

    internal static class DiscordAuth
    {
        internal const string StreamKitClientId = "207646673902501888";
        internal const string TokenEndpoint = "https://discord.com/api/v10/oauth2/token";
        internal const string RedirectUri = "http://localhost";

        internal static readonly string[] Scopes =
        {
            "identify", "rpc", "rpc.voice.read", "rpc.voice.write",
            "rpc.screenshare.read", "rpc.screenshare.write"
        };

        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeckTools.Discord.v1");

        public static string StorePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StreamDeckAudio");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "discord.dat");
            }
        }

        public static void Save(DiscordToken token)
        {
            if (token == null) return;
            var record = new Dictionary<string, object>
            {
                { "access", token.Access },
                { "refresh", token.Refresh },
                { "scope", token.Scope },
                { "expires", token.ExpiresUtc.ToString("o") }
            };

            byte[] plain = Encoding.UTF8.GetBytes(Serializer.Serialize(record));
            byte[] sealed_ = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            // Escritura atomica: un corte de luz a mitad no puede dejar el store
            // a medio escribir, que seria justamente quedarse sin credencial.
            string temporary = StorePath + ".tmp";
            File.WriteAllBytes(temporary, sealed_);

            // File.Move no puede pisar un destino existente en .NET Framework
            // 4.x (tira IOException; el overload que sobreescribe recien llega
            // en .NET Core 3.0), asi que un Move directo obligaria a un
            // delete-y-despues-move -- y en el hueco entre esas dos llamadas
            // StorePath no existe: si el proceso muere ahi, el .tmp queda
            // completo y cifrado al lado pero Load() nunca lo busca, y
            // devuelve null como si no hubiera credencial. Eso es exactamente
            // el estado que este archivo existe para prohibir. File.Replace es
            // un swap transaccional del filesystem (NTFS): no hay hueco donde
            // el store pueda faltar. Sin destino todavia (primer alta) no hay
            // nada que reemplazar, asi que un Move alcanza. Si el filesystem no
            // soporta Replace, el fallback es el delete-then-move viejo, con
            // el hueco angosto que eso implica, como ultimo recurso.
            if (!File.Exists(StorePath))
            {
                File.Move(temporary, StorePath);
                return;
            }

            try
            {
                File.Replace(temporary, StorePath, null);
            }
            catch (IOException ex)
            {
                Log.Write("Discord: File.Replace fallo, fallback a delete-then-move: " + ex.Message);
                File.Delete(StorePath);
                File.Move(temporary, StorePath);
            }
        }

        public static DiscordToken Load()
        {
            try
            {
                if (!File.Exists(StorePath)) return null;

                byte[] plain = ProtectedData.Unprotect(
                    File.ReadAllBytes(StorePath), Entropy, DataProtectionScope.CurrentUser);
                var record = Serializer.Deserialize<Dictionary<string, object>>(
                    Encoding.UTF8.GetString(plain));

                return new DiscordToken
                {
                    Access = Json.Str(record, "access"),
                    Refresh = Json.Str(record, "refresh"),
                    Scope = Json.Str(record, "scope"),
                    ExpiresUtc = DateTime.Parse(Json.Str(record, "expires"), null,
                                                System.Globalization.DateTimeStyles.RoundtripKind)
                };
            }
            catch (Exception ex)
            {
                Log.Write("Discord: store ilegible: " + ex.Message);
                return null;
            }
        }

        private static string _clientId;
        private static string _clientSecret;

        public static void SetClientCredentials(string clientId, string clientSecret)
        {
            _clientId = clientId;
            _clientSecret = clientSecret;
        }

        /// <summary>
        /// Token vivo, renovando si hace falta. Null cuando no se puede: el llamador
        /// dibuja NoAuth y reintenta despues. El token viejo nunca se borra acá.
        /// </summary>
        public static string AccessToken()
        {
            DiscordToken token = Load();
            if (token == null) return null;
            if (!token.NeedsRenewal) return token.Access;

            DiscordToken renewed = TryRefresh(token);
            if (renewed == null)
            {
                Log.Write("Discord: refresh fallido, se conserva el token anterior");
                return token.ExpiresUtc > DateTime.UtcNow ? token.Access : null;
            }

            Save(renewed);
            return renewed.Access;
        }

        public static DiscordToken Exchange(string code, string redirectUri)
        {
            return PostToken(
                "grant_type=authorization_code" +
                "&code=" + Uri.EscapeDataString(code) +
                "&redirect_uri=" + Uri.EscapeDataString(redirectUri));
        }

        private static DiscordToken TryRefresh(DiscordToken token)
        {
            if (token == null || string.IsNullOrEmpty(token.Refresh)) return null;
            try
            {
                return PostToken("grant_type=refresh_token&refresh_token=" +
                                 Uri.EscapeDataString(token.Refresh));
            }
            catch (Exception ex)
            {
                Log.Write("Discord: refresh fallo: " + ex.Message);
                return null;
            }
        }

        private static DiscordToken PostToken(string grant)
        {
            if (string.IsNullOrEmpty(_clientId) || string.IsNullOrEmpty(_clientSecret))
                throw new InvalidOperationException("faltan CLIENT ID y CLIENT SECRET");

            // El default de .NET Framework 4 es TLS 1.0 y discord.com corta la conexion.
            // El enum puede no exponer Tls12 segun el framework contra el que compile csc.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;

            string body = grant +
                          "&client_id=" + Uri.EscapeDataString(_clientId) +
                          "&client_secret=" + Uri.EscapeDataString(_clientSecret);
            byte[] payload = Encoding.UTF8.GetBytes(body);

            var request = (HttpWebRequest)WebRequest.Create(TokenEndpoint);
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.ContentLength = payload.Length;
            request.Timeout = 15000;
            using (Stream stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                var body_ = Serializer.Deserialize<Dictionary<string, object>>(reader.ReadToEnd());
                string access = Json.Str(body_, "access_token");
                if (string.IsNullOrEmpty(access)) throw new Exception("respuesta sin access_token");

                return new DiscordToken
                {
                    Access = access,
                    Refresh = Json.Str(body_, "refresh_token"),
                    Scope = Json.Str(body_, "scope"),
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(Json.Int(body_, "expires_in", 604800))
                };
            }
        }
    }
}
