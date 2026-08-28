using System;
using System.IO;
using System.Text;

namespace StreamDeckAudio
{
    internal static class DiscordTests
    {
        private static void FrameRoundTrip()
        {
            Console.WriteLine("frames:");
            using (var buffer = new MemoryStream())
            {
                DiscordRpc.WriteFrame(buffer, 1, "{\"cmd\":\"PING\"}");
                buffer.Position = 0;

                int opcode;
                string json;
                Check.True("ReadFrame devuelve true", DiscordRpc.ReadFrame(buffer, out opcode, out json));
                Check.Equal("opcode", 1, opcode);
                Check.Equal("payload", "{\"cmd\":\"PING\"}", json);
            }
        }

        private static void HeaderIsLittleEndian()
        {
            using (var buffer = new MemoryStream())
            {
                DiscordRpc.WriteFrame(buffer, 0, "ab");
                byte[] raw = buffer.ToArray();

                Check.Equal("largo total", 10, raw.Length);
                Check.Equal("opcode byte 0", (byte)0, raw[0]);
                Check.Equal("length byte 4", (byte)2, raw[4]);
                Check.Equal("length byte 5", (byte)0, raw[5]);
            }
        }

        private static void UnicodeSurvives()
        {
            using (var buffer = new MemoryStream())
            {
                DiscordRpc.WriteFrame(buffer, 1, "{\"n\":\"ñé\"}");
                buffer.Position = 0;

                int opcode;
                string json;
                DiscordRpc.ReadFrame(buffer, out opcode, out json);
                Check.Equal("utf-8", "{\"n\":\"ñé\"}", json);
            }
        }

        private static void TruncatedStreamIsFalseNotHang()
        {
            using (var buffer = new MemoryStream(new byte[] { 1, 0, 0, 0, 20, 0, 0, 0, 65 }))
            {
                int opcode;
                string json;
                Check.Equal("stream cortado devuelve false", false,
                            DiscordRpc.ReadFrame(buffer, out opcode, out json));
            }
        }

        // Un largo corrupto no debe dejar el opcode a medio pisar: todos los
        // caminos de falla tienen que dejar el out-param igual, en 0.
        private static void InvalidLengthIsFalseWithZeroOpcode()
        {
            // Largo negativo: -1 como int32 little-endian.
            using (var buffer = new MemoryStream(new byte[] { 1, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF }))
            {
                int opcode;
                string json;
                Check.Equal("largo negativo devuelve false", false,
                            DiscordRpc.ReadFrame(buffer, out opcode, out json));
                Check.Equal("largo negativo deja opcode en 0", 0, opcode);
            }

            // Largo por encima del tope de 8 MiB: 8388609 como int32 little-endian.
            using (var buffer = new MemoryStream(new byte[] { 1, 0, 0, 0, 0x01, 0x00, 0x80, 0x00 }))
            {
                int opcode;
                string json;
                Check.Equal("largo excede el limite devuelve false", false,
                            DiscordRpc.ReadFrame(buffer, out opcode, out json));
                Check.Equal("largo excede el limite deja opcode en 0", 0, opcode);
            }
        }

        private static void TokenRoundTrip()
        {
            Console.WriteLine("store:");
            var token = new DiscordToken
            {
                Access = "at-123",
                Refresh = "rt-456",
                Scope = "identify rpc",
                ExpiresUtc = new DateTime(2030, 1, 2, 3, 4, 5, DateTimeKind.Utc)
            };
            DiscordAuth.Save(token);

            DiscordToken loaded = DiscordAuth.Load();
            Check.Equal("access", "at-123", loaded.Access);
            Check.Equal("refresh", "rt-456", loaded.Refresh);
            Check.Equal("scope", "identify rpc", loaded.Scope);
            Check.Equal("expiracion", token.ExpiresUtc, loaded.ExpiresUtc);
        }

        // Cubre la rama File.Replace de Save: la primera Save no encuentra
        // destino y hace un Move directo; esta segunda si. Esto NO prueba que
        // no haya hueco -- una ventana de crash no es testeable desde un test
        // unitario. Lo que si prueba: que pisar un store existente da un
        // resultado legible y correcto, y que los argumentos source/destino de
        // File.Replace estan en el orden correcto.
        private static void SaveOverwritesExistingStore()
        {
            DiscordAuth.Save(new DiscordToken { Access = "primero", ExpiresUtc = DateTime.UtcNow.AddDays(3) });
            DiscordAuth.Save(new DiscordToken { Access = "segundo", ExpiresUtc = DateTime.UtcNow.AddDays(3) });

            Check.Equal("el segundo save gana", "segundo", DiscordAuth.Load().Access);
        }

        // 80 % de 7 dias son 5,6 dias. A los 6 hay que renovar; a los 3 todavia no.
        private static void RenewalWindow()
        {
            var fresh = new DiscordToken { Access = "a", ExpiresUtc = DateTime.UtcNow.AddDays(4) };
            Check.Equal("token de 4 dias no renueva", false, fresh.NeedsRenewal);

            var stale = new DiscordToken { Access = "a", ExpiresUtc = DateTime.UtcNow.AddDays(1) };
            Check.Equal("token de 1 dia renueva", true, stale.NeedsRenewal);

            var dead = new DiscordToken { Access = "a", ExpiresUtc = DateTime.UtcNow.AddDays(-1) };
            Check.Equal("token vencido renueva", true, dead.NeedsRenewal);
        }

        // Fix final, Important 1: la garantia central de este fix, fijada como
        // test. Load() NUNCA debe tocar los estaticos _clientId/_clientSecret
        // -- si lo hiciera, un Load() disparado por Attach (thread
        // "discord-attach") mientras Enroll() sostiene el par de una app nueva
        // en curso (thread "discord-enroll", bloqueado hasta 60s en AUTHORIZE)
        // pisaria esa credencial en vuelo con lo que sea que el store tenia
        // ANTES de la reautorizacion. Guardado y restaurado via
        // SetClientCredentials/ClientId, sin red: exactamente lo que este
        // metodo ya expone para tests (ver CanPersistRejectsMissingRefresh).
        private static void LoadDoesNotTouchStatics()
        {
            Console.WriteLine("purity de Load():");

            DiscordAuth.SetClientCredentials("store-id", "store-secret");
            DiscordAuth.Save(new DiscordToken
            {
                Access = "a", Refresh = "r", ExpiresUtc = DateTime.UtcNow.AddDays(3)
            });

            // Simula un Enroll() en curso sosteniendo el par de una app B --
            // el escenario exacto que Important 1 describe.
            DiscordAuth.SetClientCredentials("inflight-id", "inflight-secret");

            DiscordToken loaded = DiscordAuth.Load();

            Check.Equal("Load() no toca _clientId", "inflight-id", DiscordAuth.ClientId);
            Check.Equal("pero SI devuelve el clientId guardado, como dato",
                        "store-id", loaded.ClientId);
            Check.Equal("y el clientSecret guardado",
                        "store-secret", loaded.ClientSecret);
        }

        // AccessToken() es el UNICO lugar que aplica el par leido del store a
        // los estaticos, y solo cuando no hay nada ya cargado -- ese guard es
        // lo que efectivamente cierra la carrera de Important 1 (Load() puro
        // solo no alcanza si el unico llamador que necesita los estaticos los
        // pisara igual). ExpiresUtc a 4 dias evita la renovacion por red (ver
        // RenewalWindow) para que este test siga sin tocar la red.
        private static void AccessTokenAppliesStoredCredsOnlyWhenNothingInFlight()
        {
            DiscordAuth.SetClientCredentials("store-id", "store-secret");
            DiscordAuth.Save(new DiscordToken
            {
                Access = "at", Refresh = "rt", ExpiresUtc = DateTime.UtcNow.AddDays(4)
            });

            DiscordAuth.SetClientCredentials(null, null);
            DiscordAuth.AccessToken();
            Check.Equal("sin nada en vuelo, aplica el par del store",
                        "store-id", DiscordAuth.ClientId);

            DiscordAuth.SetClientCredentials("inflight-id", "inflight-secret");
            DiscordAuth.AccessToken();
            Check.Equal("con un Enroll en vuelo, no lo pisa aunque el store diga otra cosa",
                        "inflight-id", DiscordAuth.ClientId);

            DiscordAuth.SetClientCredentials(null, null);
        }

        // Fix final #2: la misma muerte permanente de Important 1, por una
        // puerta mas angosta -- Save() todavia leia los estaticos en vez del
        // par que trae el token. Escenario real: el thread de Attach esta dentro
        // de AccessToken con _clientId == A, TryRefresh tarda hasta ~15s en la
        // llamada HTTP; mientras tanto el usuario reautoriza con la app B,
        // Enroll pisa _clientId a B y se queda bloqueado en AUTHORIZE; el
        // thread de Attach vuelve y llega a Save(renewed) -- si leyera los
        // estaticos ahi, escribiria el token de A al lado de las credenciales
        // de B. Sin el fix, este test falla: el segundo Save() pisaria
        // "A"/"secret-A" con lo que digan los estaticos en ese momento.
        private static void SavePrefersTokensOwnCredentialsOverStatics()
        {
            var token = new DiscordToken
            {
                Access = "at", Refresh = "rt", ExpiresUtc = DateTime.UtcNow.AddDays(3),
                ClientId = "A", ClientSecret = "secret-A"
            };
            DiscordAuth.Save(token);

            // Simula el Enroll() concurrente de arriba pisando los estaticos
            // con la app B, DESPUES de que el token de A ya se armo.
            DiscordAuth.SetClientCredentials("B", "secret-B");
            DiscordAuth.Save(token);

            DiscordToken loaded = DiscordAuth.Load();
            Check.Equal("Save() prefiere el clientId del token, no el estatico",
                        "A", loaded.ClientId);
            Check.Equal("y el clientSecret del token",
                        "secret-A", loaded.ClientSecret);

            DiscordAuth.SetClientCredentials(null, null);
        }

        // Regla 1. Este es el bug de HotSpot, escrito como test.
        private static void FailedRenewalKeepsOldToken()
        {
            var original = new DiscordToken
            {
                Access = "viejo", Refresh = "rt-viejo",
                ExpiresUtc = DateTime.UtcNow.AddDays(-1)      // vencido a proposito
            };
            DiscordAuth.Save(original);

            // Refresh que falla: sin red, sin credenciales de app cargadas.
            DiscordAuth.SetClientCredentials(null, null);
            string token = DiscordAuth.AccessToken();

            Check.Equal("un refresh fallido devuelve null", null, token);
            Check.True("el token viejo sigue en disco", DiscordAuth.Load() != null);
            Check.Equal("y es el mismo", "viejo", DiscordAuth.Load().Access);
        }

        // RFC 6749 §6: omitir refresh_token en la respuesta de un refresh
        // significa "seguis usando el que tenias". Sin este relleno, la
        // proxima renovacion ve el refresh token vacio y se rinde para
        // siempre -- el bug de HotSpot. No hay red involucrada: BackfillRefresh
        // es una funcion pura sobre dos DiscordToken, asi que se puede probar
        // la decision de merge directamente sin mockear HttpWebRequest.
        private static void BackfillRefreshFillsOmittedToken()
        {
            var original = new DiscordToken { Access = "a", Refresh = "rt-original" };
            var renewedSinRefresh = new DiscordToken { Access = "b", Refresh = null };

            DiscordToken result = DiscordAuth.BackfillRefresh(original, renewedSinRefresh);

            Check.Equal("refresh omitido se rellena con el viejo", "rt-original", result.Refresh);
            Check.Equal("access nuevo se conserva", "b", result.Access);
        }

        private static void BackfillRefreshKeepsFreshToken()
        {
            var original = new DiscordToken { Access = "a", Refresh = "rt-original" };
            var renewedConRefresh = new DiscordToken { Access = "b", Refresh = "rt-nuevo" };

            DiscordToken result = DiscordAuth.BackfillRefresh(original, renewedConRefresh);

            Check.Equal("refresh presente no se pisa", "rt-nuevo", result.Refresh);
        }

        private static void BackfillRefreshPassesThroughNull()
        {
            var original = new DiscordToken { Access = "a", Refresh = "rt-original" };

            Check.Equal("renewal null sigue siendo null", null, DiscordAuth.BackfillRefresh(original, null));
        }

        // Ultima barrera de AccessToken: ninguna credencial sin refresh token
        // puede persistirse, sin importar de donde haya salido.
        private static void CanPersistRejectsMissingRefresh()
        {
            Check.True("null nunca se puede persistir", !DiscordAuth.CanPersist(null));
            Check.True("refresh vacio no se puede persistir",
                !DiscordAuth.CanPersist(new DiscordToken { Access = "a", Refresh = "" }));
            Check.True("refresh presente si se puede persistir",
                DiscordAuth.CanPersist(new DiscordToken { Access = "a", Refresh = "rt" }));
        }

        // Fija el falso positivo que string.Contains dejaba pasar: "rpc" es
        // substring de "rpc.voice.read", asi que un scope string que otorga el
        // dotted scope pero retiene el "rpc" suelto tiene que reportar "rpc" como
        // NO concedido, no como "ok" por casualidad de substring.
        private static void HasScopeRejectsSubstringFalsePositive()
        {
            string granted = "identify rpc.voice.read rpc.voice.write";

            Check.True("rpc.voice.read concedido de verdad", DiscordAuth.HasScope(granted, "rpc.voice.read"));
            Check.True("rpc suelto NO concedido pese a ser substring", !DiscordAuth.HasScope(granted, "rpc"));
        }

        private static void CorruptStoreIsNullNotCrash()
        {
            System.IO.File.WriteAllText(DiscordAuth.StorePath, "esto no es DPAPI");
            Check.Equal("store corrupto devuelve null", null, DiscordAuth.Load());
        }

        public static int Main()
        {
            // Incidente real, fix round 1 de Task 5: sin este desvio, StorePath
            // es el mismo archivo que usa la instalacion real, y
            // CorruptStoreIsNullNotCrash (mas abajo) pisa ese archivo con basura
            // a proposito. Correr este suite justo despues de un alta real dejo
            // el store de produccion en "sin credenciales" -- una credencial
            // viva, destruida por un test. DiscordAuth.TestStoreOverride redirige
            // StorePath a un temp file solo mientras este proceso corre; el
            // store real nunca se toca desde aca.
            string tempStore = Path.Combine(Path.GetTempPath(),
                "discordtests-store-" + Guid.NewGuid().ToString("N") + ".dat");
            DiscordAuth.TestStoreOverride = tempStore;
            try
            {
                FrameRoundTrip();
                HeaderIsLittleEndian();
                UnicodeSurvives();
                TruncatedStreamIsFalseNotHang();
                InvalidLengthIsFalseWithZeroOpcode();
                TokenRoundTrip();
                SaveOverwritesExistingStore();
                RenewalWindow();
                LoadDoesNotTouchStatics();
                AccessTokenAppliesStoredCredsOnlyWhenNothingInFlight();
                SavePrefersTokensOwnCredentialsOverStatics();
                FailedRenewalKeepsOldToken();
                BackfillRefreshFillsOmittedToken();
                BackfillRefreshKeepsFreshToken();
                BackfillRefreshPassesThroughNull();
                CanPersistRejectsMissingRefresh();
                HasScopeRejectsSubstringFalsePositive();
                CorruptStoreIsNullNotCrash();   // último: deja el store ilegible a proposito
                return Check.Report();
            }
            finally
            {
                // Desarmar el desvio y borrar el temp file: no dejar rastro, y
                // no dejar TestStoreOverride puesto para ningun caller posterior
                // dentro del mismo proceso.
                DiscordAuth.TestStoreOverride = null;
                try { if (File.Exists(tempStore)) File.Delete(tempStore); }
                catch (Exception) { /* best-effort cleanup, no afecta el resultado */ }
            }
        }
    }
}
