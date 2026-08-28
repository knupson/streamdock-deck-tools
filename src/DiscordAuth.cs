// Credenciales de Discord: store cifrado, renovacion y alta.
//
// Regla que ordena todo este archivo: el token nuevo se escribe antes de
// descartar el viejo. Si la renovacion falla, la credencial vieja sigue en
// disco y se reintenta despues. Nunca hay un estado sin credencial.

using System;
using System.Collections.Generic;
using System.IO;
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

        private static DiscordToken TryRefresh(DiscordToken token)
        {
            return null;   // Task 4
        }
    }
}
