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

        // Doble rol, y en los dos casos el motivo es el mismo: describir la
        // credencial en el propio dato, nunca depender de los estaticos
        // _clientId/_clientSecret que otro thread puede mover. (1) Poblados
        // por Load() a partir del record en disco, solo como datos de
        // retorno -- Load() en si no los aplica a los estaticos (ver su
        // comentario y el de AccessToken(), fix round final Important 1).
        // Null en un record legado que se guardo antes de que Task 6 agregara
        // estos campos al store. (2) Poblados por TryRefresh (copiados del
        // token que se esta renovando) y por Enroll (copiados de sus
        // parametros locales) ANTES de Save(), para que Save() nunca tenga
        // que leer los estaticos para saber que credencial esta escribiendo
        // (ver el comentario en Save() sobre la interleaving que esto cierra).
        public string ClientId;
        public string ClientSecret;

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

        // Test seam, no feature: sin esto StorePath es el mismo archivo real
        // para tests y para la instalacion en uso, y Save/Load no distinguen
        // quien llama. Paso de verdad en el fix round 1 de Task 5: correr
        // DiscordTests.exe justo despues de un alta real dejo el store en
        // "sin credenciales", porque CorruptStoreIsNullNotCrash pisa a proposito
        // ese mismo archivo con basura para probar que Load() falla sin
        // crashear. El store de produccion es una credencial VIVA, no un
        // archivo de scratch -- este proyecto existe para que las credenciales
        // no mueran solas, asi que un test suite que las mata de verdad no es
        // aceptable. internal, como BackfillRefresh/CanPersist: solo el
        // assembly de test lo toca. Nunca una env var ni configuracion: esto no
        // es una opcion para el usuario, es un desvio que el test runner arma
        // a proposito antes de tocar disco, y lo desarma al terminar.
        internal static string TestStoreOverride;

        public static string StorePath
        {
            get
            {
                if (!string.IsNullOrEmpty(TestStoreOverride)) return TestStoreOverride;

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

            // Preferir el par que trae EL TOKEN sobre los estaticos -- misma
            // muerte permanente que Important 1, por una puerta mas angosta.
            // Sin esto: el thread de Attach esta dentro de AccessToken con
            // _clientId == A; el token necesita renovarse; TryRefresh tarda
            // hasta ~15s en la llamada HTTP a Discord. Mientras tanto el
            // usuario aprieta Autorizar con la app B: Enroll pisa _clientId a
            // B y se queda bloqueado en AUTHORIZE (hasta 60s). El thread de
            // Attach vuelve de TryRefresh y llega aca, a Save(renewed) --si
            // leyera los estaticos ahora, escribiria el token RENOVADO DE A al
            // lado de las credenciales DE B. Si esa reautorizacion despues
            // falla (secret mal tipeado, redirect faltante -- los casos
            // comunes), su `finally` restaura A en el proceso y el dano queda
            // invisible: el store en disco ya quedo con el token de A junto al
            // client id/secret de B. El proximo arranque del proceso encuentra
            // los estaticos vacios, aplica B desde el store (unico par
            // disponible), y todo PostToken devuelve invalid_client para
            // siempre -- exactamente la muerte permanente que este archivo
            // existe para prohibir, ahora por una via que no pasaba por
            // Load(). El token en si mismo (via TryRefresh copiando
            // ClientId/ClientSecret del que esta renovando, y Enroll
            // poblandolos con sus parametros locales, nunca con los
            // estaticos) es lo unico que describe con que credencial se
            // autentico de verdad -- los estaticos son estado ambiente que
            // OTRO thread puede mover mientras este todavia esta trabajando.
            // El fallback a los estaticos solo cubre callers viejos/de test
            // que construyen un DiscordToken sin poblar estos dos campos.
            string clientId = !string.IsNullOrEmpty(token.ClientId) ? token.ClientId : _clientId;
            string clientSecret = !string.IsNullOrEmpty(token.ClientSecret) ? token.ClientSecret : _clientSecret;

            var record = new Dictionary<string, object>
            {
                { "access", token.Access },
                { "refresh", token.Refresh },
                { "scope", token.Scope },
                { "expires", token.ExpiresUtc.ToString("o") },
                { "clientId", clientId },
                { "clientSecret", clientSecret }
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
            catch (Exception ex)
            {
                // Cualquier otra excepcion (permisos, algo especifico del
                // filesystem) deja el .tmp huerfano al lado del store: sellado
                // con DPAPI igual que el store real, asi que no expone nada,
                // pero se acumula para siempre si nadie lo borra. Best-effort:
                // limpiarlo y dejar subir la excepcion original -- el mismo
                // contrato que un caller de Save() ya tiene que poder manejar.
                Log.Write("Discord: File.Replace fallo de forma inesperada: " + ex.Message);
                try { File.Delete(temporary); } catch (Exception) { }
                throw;
            }
        }

        // PURO: nunca toca _clientId/_clientSecret. Round final, Important 1 --
        // antes escribia esos estaticos como efecto secundario cada vez que se
        // leia el store, y Load() se llama desde todos lados: AccessToken()
        // (thread "discord-attach", cada Poke que encuentra la sesion caida) Y
        // PushStatus (thread del host, cada vez que se abre el panel). Enroll()
        // corre en su propio thread ("discord-enroll") y sostiene ese MISMO par
        // de estaticos durante todo un AUTHORIZE que bloquea hasta 60s mientras
        // el usuario esta con el dialogo de Discord. Nada serializaba esas dos
        // escrituras: con la sesion caida (Poke reintentando Attach cada 15s) y
        // el usuario reautorizando con una app nueva, un Load() disparado desde
        // Attach a mitad de ese AUTHORIZE pisaba los estaticos de vuelta a la
        // app vieja -- Exchange terminaba mandando el code de la app nueva con
        // el client_id/secret de la app vieja, Discord lo rechazaba, y en la
        // ventana angosta entre que Exchange volvia y Save() corria, el store
        // podia quedar con el token nuevo al lado de credenciales que no le
        // corresponden: invalid_client para siempre, exactamente la muerte
        // permanente que este proyecto existe para evitar. Ahora Load() solo
        // LEE y devuelve; el unico lugar que aplica el par leido a los
        // estaticos es AccessToken(), y solo cuando no hay nada ya cargado (ver
        // su comentario) -- para que un Enroll en vuelo nunca pueda ser pisado
        // por una lectura concurrente que ni siquiera lo necesitaba.
        public static DiscordToken Load()
        {
            try
            {
                if (!File.Exists(StorePath)) return null;

                byte[] plain = ProtectedData.Unprotect(
                    File.ReadAllBytes(StorePath), Entropy, DataProtectionScope.CurrentUser);
                var record = Serializer.Deserialize<Dictionary<string, object>>(
                    Encoding.UTF8.GetString(plain));

                // clientId/clientSecret son un agregado de Task 6: un record
                // escrito por una version anterior de este archivo no los tiene,
                // y Json.Str devuelve null para una clave ausente -- no hay nada
                // que parsear ni que tire, asi que un store viejo se sigue
                // leyendo igual que antes, solo que con estos dos campos en null
                // en el token devuelto (ver el guard de client id ausente en
                // DiscordService.Attach).
                return new DiscordToken
                {
                    Access = Json.Str(record, "access"),
                    Refresh = Json.Str(record, "refresh"),
                    Scope = Json.Str(record, "scope"),
                    ExpiresUtc = DateTime.Parse(Json.Str(record, "expires"), null,
                                                System.Globalization.DateTimeStyles.RoundtripKind),
                    ClientId = Json.Str(record, "clientId"),
                    ClientSecret = Json.Str(record, "clientSecret")
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

        /// <summary>
        /// Client id de la app propia. DiscordService lo necesita para el
        /// handshake RPC (Connect(clientId)); nunca el client secret, que no
        /// tiene motivo para salir de PostToken.
        /// </summary>
        public static string ClientId { get { return _clientId; } }

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
            bool hadStoredCredential;
            return AccessToken(out hadStoredCredential);
        }

        /// <summary>
        /// Igual que el overload de arriba, pero le informa al llamador si HUBO
        /// una credencial guardada. DiscordService.Attach la necesita para no
        /// confundir "nunca hubo credencial" (hay que autorizar) con "habia una
        /// pero no se pudo renovar ahora" (la red estaba caida, por ejemplo) --
        /// sin esto la tecla le pedia reautorizar a un usuario cuya autorizacion
        /// estaba perfectamente bien (ver la lista MINOR del review final).
        /// </summary>
        public static string AccessToken(out bool hadStoredCredential)
        {
            DiscordToken token = Load();
            hadStoredCredential = token != null;
            if (token == null) return null;

            // Load() es puro (ver su comentario): este es el UNICO lugar que
            // aplica el clientId/clientSecret leido del store a los estaticos
            // que PostToken usa para renovar, y solo cuando _clientId todavia
            // esta vacio. Si ya hay algo cargado -- un Enroll() en curso en el
            // thread "discord-enroll", sosteniendo el par de la app nueva
            // mientras AUTHORIZE bloquea hasta 60s, o una llamada anterior a
            // este mismo metodo -- dejarlo como esta es lo que cierra la
            // carrera de Important 1: un Attach concurrente ya no puede pisar
            // credenciales de una reautorizacion en vuelo solo por haber leido
            // el store un instante antes de que Save() terminara de escribir la
            // credencial nueva.
            if (string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(token.ClientId) &&
                !string.IsNullOrEmpty(token.ClientSecret))
            {
                SetClientCredentials(token.ClientId, token.ClientSecret);
            }

            if (!token.NeedsRenewal) return token.Access;

            DiscordToken renewed = TryRefresh(token);

            // Ninguna credencial sin refresh token puede llegar a Save(): guardar
            // una asi es indistinguible de perder la capacidad de renovar para
            // siempre, que es el bug de HotSpot. TryRefresh ya rellena el refresh
            // token faltante con el viejo (RFC 6749 §6), asi que en el camino
            // normal CanPersist siempre da true; esta linea es la ultima barrera
            // por si ese relleno alguna vez no corre.
            if (!CanPersist(renewed))
            {
                Log.Write(renewed == null
                    ? "Discord: refresh fallido, se conserva el token anterior"
                    : "Discord: renovacion sin refresh_token, se descarta como si hubiera fallado para no perder la capacidad de renovar");
                return token.ExpiresUtc > DateTime.UtcNow ? token.Access : null;
            }

            Save(renewed);
            return renewed.Access;
        }

        /// <summary>
        /// Visible para tests: la regla que decide si una renovacion puede
        /// guardarse. Separada de AccessToken para poder probar la decision
        /// sin red.
        /// </summary>
        internal static bool CanPersist(DiscordToken renewed)
        {
            return renewed != null && !string.IsNullOrEmpty(renewed.Refresh);
        }

        public static DiscordToken Exchange(string code, string redirectUri)
        {
            return PostToken(
                "grant_type=authorization_code" +
                "&code=" + Uri.EscapeDataString(code) +
                "&redirect_uri=" + Uri.EscapeDataString(redirectUri));
        }

        /// <summary>
        /// Alta: AUTHORIZE por RPC, y el code que devuelve se canjea por HTTPS.
        ///
        /// Abre su propia conexion RPC y la cierra al terminar. Discord rechaza un
        /// segundo AUTHORIZE sobre una conexion ya autenticada con "Already
        /// authenticated", que es lo que dejo muerto al plugin de HotSpot.
        /// </summary>
        public static bool Enroll(string clientId, string clientSecret, out string error)
        {
            error = null;

            // _clientId/_clientSecret son los mismos campos estaticos que PostToken
            // usa para renovar la credencial YA guardada en disco. Si los pisamos
            // aca y despues cualquier paso de abajo falla (secret mal tipeado,
            // Discord no contesta, AUTHORIZE sin code, canje sin refresh_token), un
            // reintento de alta que sale mal dejaria la app funcionando hoy sin
            // poder renovar mas: el proximo refresh de la credencial vieja usaria el
            // secret nuevo y roto. Por eso el par anterior se guarda aca y se
            // restaura en cualquier salida que no sea el "ok" final -- `success`
            // marca esa unica salida feliz, y el `finally` es el unico lugar que
            // decide si restaura o no.
            string previousClientId = _clientId;
            string previousClientSecret = _clientSecret;
            SetClientCredentials(clientId, clientSecret);

            bool success = false;
            var rpc = new DiscordRpc();
            try
            {
                // Connect() vive DENTRO del try/finally: si tira (el pipe se cae
                // entre pipe.Connect() y la escritura del handshake, una ventana
                // angosta pero real), rpc.Close() en el finally sigue corriendo y
                // libera el reader thread y el handle. Dejar Connect() afuera del
                // try es la misma clase de leak que ya se saco de DiscordRpc en dos
                // rondas de fix anteriores; no tiene sentido reintroducirla aca.
                if (!rpc.Connect(clientId))
                {
                    error = "Discord no esta corriendo";
                    return false;
                }

                var args = new Dictionary<string, object>
                {
                    { "client_id", clientId },
                    { "scopes", Scopes },
                    { "prompt", "none" }
                };

                Dictionary<string, object> data = rpc.Request("AUTHORIZE", args, 60000);
                string code = Json.Str(data, "code");
                if (string.IsNullOrEmpty(code)) { error = "AUTHORIZE no devolvio code"; return false; }

                DiscordToken token = Exchange(code, RedirectUri);

                // Poblado desde los PARAMETROS locales de este metodo, nunca
                // desde los estaticos: mismo motivo que TryRefresh (ver el
                // comentario en Save()) -- clientId/clientSecret son locales
                // inmutables de esta llamada a Enroll, asi que describen la
                // app que se esta dando de alta sin importar si algo mas
                // llega a tocar _clientId/_clientSecret mientras tanto.
                token.ClientId = clientId;
                token.ClientSecret = clientSecret;

                // Misma regla que CanPersist aplica en AccessToken(): una credencial
                // sin refresh token no se puede renovar nunca, asi que guardarla es
                // indistinguible de perder la capacidad de renovar desde el dia cero
                // -- el mismo bug de HotSpot, pero en la alta en vez de en la
                // renovacion. Alla arriba TryRefresh/BackfillRefresh siempre tienen
                // un refresh token anterior para heredar si Discord omite uno nuevo;
                // aca no hay ningun refresh previo del cual heredar, asi que si
                // Exchange vuelve sin refresh_token no hay nada que hacer salvo
                // fallar la alta.
                if (!CanPersist(token))
                {
                    error = "Discord no devolvio refresh_token en el alta; no se puede persistir una credencial que nunca podria renovarse";
                    return false;
                }

                Save(token);
                Log.Write("Discord: alta ok, scopes concedidos: " + token.Scope);
                success = true;
                return true;
            }
            catch (DiscordRpcException ex)
            {
                error = "RPC " + ex.Code + ": " + ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                rpc.Close();
                if (!success) SetClientCredentials(previousClientId, previousClientSecret);
            }
        }

        /// <summary>
        /// Membresia exacta de un token dentro de un scope string separado por
        /// espacios. Nunca usar string.Contains para esto: "rpc" es substring de
        /// "rpc.voice.read", "rpc.voice.write", "rpc.screenshare.read" y
        /// "rpc.screenshare.write", asi que Contains reportaria "ok" para un "rpc"
        /// suelto que Discord en realidad no otorgo, mientras seguia otorgando los
        /// scopes con punto -- exactamente el otorgamiento parcial que este chequeo
        /// existe para detectar.
        /// </summary>
        internal static bool HasScope(string scopeString, string wanted)
        {
            if (string.IsNullOrEmpty(scopeString)) return false;
            foreach (string token in scopeString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == wanted) return true;
            }
            return false;
        }

        private static DiscordToken TryRefresh(DiscordToken token)
        {
            if (token == null || string.IsNullOrEmpty(token.Refresh)) return null;
            try
            {
                DiscordToken renewed = PostToken("grant_type=refresh_token&refresh_token=" +
                                 Uri.EscapeDataString(token.Refresh));
                renewed = BackfillRefresh(token, renewed);

                // Mismo motivo que BackfillRefresh, aplicado al par de
                // credenciales en vez del refresh token: lo que Save() termina
                // escribiendo tiene que describirse a si mismo, no depender de
                // en que estaban los estaticos _clientId/_clientSecret en el
                // instante en que Save() corre -- ese instante puede llegar
                // hasta ~15s despues de que PostToken arranco, y en el medio
                // un Enroll() concurrente puede haberlos movido a otra app (ver
                // el comentario en Save()). `token` es el que AccessToken()
                // acaba de leer del store para ESTA renovacion puntual, asi
                // que su ClientId/ClientSecret son los correctos sin importar
                // que hagan los estaticos despues.
                if (renewed != null)
                {
                    renewed.ClientId = token.ClientId;
                    renewed.ClientSecret = token.ClientSecret;
                }

                return renewed;
            }
            catch (Exception ex)
            {
                Log.Write("Discord: refresh fallo: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Visible para tests: RFC 6749 §6 permite que el authorization server
        /// omita refresh_token en la respuesta de un refresh -- la omision
        /// significa "segui usando el que tenias", no "ya no tenes ninguno". Si
        /// tomamos ese null tal cual, la proxima renovacion entra a TryRefresh,
        /// ve token.Refresh vacio en su primera linea y se rinde sin ni siquiera
        /// intentar la llamada: muerte silenciosa y permanente, el mismo bug que
        /// este archivo existe para eliminar. Discord hoy siempre manda uno,
        /// pero construir sobre eso es el supuesto que se cae en silencio el
        /// dia que cambian -- por eso el relleno queda aca aunque hoy parezca
        /// no hacer nada.
        /// </summary>
        internal static DiscordToken BackfillRefresh(DiscordToken original, DiscordToken renewed)
        {
            if (renewed != null && string.IsNullOrEmpty(renewed.Refresh))
                renewed.Refresh = original.Refresh;
            return renewed;
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

            HttpWebResponse response;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                // GetResponse() tira WebException para 4xx/5xx antes de que
                // podamos entrar al using de abajo, y ex.Message solo trae algo
                // generico como "(400) Bad Request" -- no dice si Discord
                // rechazo por grant revocado, client secret invalido o rate
                // limit. Esa razon vive en el body de la respuesta de ERROR
                // (ex.Response), asi que lo leemos aca y lo sumamos al mensaje
                // que se loguea mas arriba en TryRefresh. Ojo: esto es
                // unicamente el body de RESPUESTA. El body de REQUEST (arriba,
                // la variable `body`) tiene el client secret y nunca puede
                // terminar en un log.
                string detail = null;
                if (ex.Response != null)
                {
                    using (WebResponse errorResponse = ex.Response)
                    using (var errorReader = new StreamReader(errorResponse.GetResponseStream(), Encoding.UTF8))
                    {
                        detail = errorReader.ReadToEnd();
                    }
                }
                throw new Exception("Discord token endpoint fallo: " + ex.Message +
                                    (string.IsNullOrEmpty(detail) ? "" : " -- " + detail), ex);
            }

            using (response)
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
