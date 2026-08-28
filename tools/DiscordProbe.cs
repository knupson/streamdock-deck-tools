// Companion de consola para el backend de Discord. Ejercita transporte, auth y
// comandos sin el deck de por medio, que es como se diagnostica cuando una tecla
// no responde.
//
//   DiscordProbe.exe connect
//   DiscordProbe.exe reconnect
//   DiscordProbe.exe enroll <client-id> <client-secret>
//   DiscordProbe.exe status
//   DiscordProbe.exe refresh [client-id client-secret]
//   DiscordProbe.exe watch
//   DiscordProbe.exe raceattach

using System;
using System.Collections.Generic;
using System.Threading;

namespace StreamDeckAudio
{
    internal static class DiscordProbe
    {
        private static int Connect()
        {
            var rpc = new DiscordRpc();
            if (!rpc.Connect(DiscordAuth.StreamKitClientId))
            {
                Console.WriteLine("no hay pipe de Discord abierto. Discord corriendo?");
                return 1;
            }

            Console.WriteLine("conectado, handshake ok");
            rpc.Close();
            return 0;
        }

        // Smoke test del ciclo Close() -> Connect() sobre la MISMA instancia, mas
        // un observador de Disconnected. Que NO probar con esto: un loop idle de
        // tres ciclos no genera nunca un frame en vuelo justo en el instante de
        // Close(), asi que la rama de "dos readers pisandose el mismo stream" no
        // se puede disparar aca con ningun numero de repeticiones -- eso hay que
        // seguir confiando en la lectura del codigo (el reader captura su propio
        // pipe por parametro, asi que un segundo reader nunca puede empezar a
        // leer del pipe nuevo; ver el comentario en ReadLoop). Lo que este
        // comando SI puede detectar, con el observer de abajo, es la otra rama: un
        // Disconnected espurio disparado por el reader viejo justo despues de un
        // reconnect exitoso. Sin el observer, la corrida "pasaba" tanto con el
        // fix como sin el -- Close() ya cancelaba el read pendiente por el pipe
        // asincronico y el reader viejo salia mucho antes de que Connect()
        // terminara sus ~200ms+ de handshake, asi que el chequeo de _closing
        // nunca alcanzaba a correr tarde. Este comando sigue sin ser prueba de la
        // correccion del fix; es un smoke test mas un observer puntual.
        private static int Reconnect()
        {
            var rpc = new DiscordRpc();
            bool sawSpuriousDisconnect = false;
            rpc.Disconnected += () => { sawSpuriousDisconnect = true; };

            const int cycles = 3;
            for (int i = 0; i < cycles; i++)
            {
                if (!rpc.Connect(DiscordAuth.StreamKitClientId))
                {
                    Console.WriteLine("ciclo " + i + ": no conecto. Discord corriendo?");
                    return 1;
                }
                Console.WriteLine("ciclo " + i + ": conectado, handshake ok");

                rpc.Close();
                Console.WriteLine("ciclo " + i + ": cerrado");

                if (sawSpuriousDisconnect)
                {
                    Console.WriteLine("ciclo " + i + ": Disconnected se disparo durante un Close() pedido por el usuario -- espurio");
                    return 1;
                }
            }

            Console.WriteLine(cycles + " ciclos connect/close ok sobre la misma instancia, sin Disconnected espurio, sin colgarse");
            return 0;
        }

        private static int Enroll(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("uso: DiscordProbe.exe enroll <client-id> <client-secret>");
                return 2;
            }

            string error;
            if (!DiscordAuth.Enroll(args[1], args[2], out error))
            {
                Console.WriteLine("alta fallida: " + error);
                return 1;
            }

            // MINOR: sin este null check, un store ilegible justo despues de
            // una alta que SI salio bien (Enroll ya hizo su propio Save())
            // convertia un exito en un NullReferenceException crudo -- Load()
            // puede devolver null por cualquier motivo de I/O/DPAPI ajeno a si
            // el alta funciono, ver su comentario.
            DiscordToken token = DiscordAuth.Load();
            if (token == null)
            {
                Console.WriteLine("alta ok, pero el store no se pudo releer para mostrar el detalle (ver log)");
                return 0;
            }
            Console.WriteLine("alta ok");
            Console.WriteLine("  vence:  " + token.ExpiresUtc.ToLocalTime());
            Console.WriteLine("  refresh: " + (string.IsNullOrEmpty(token.Refresh) ? "NO" : "si"));
            Console.WriteLine("  scopes: " + token.Scope);

            foreach (string wanted in DiscordAuth.Scopes)
            {
                // DiscordAuth.HasScope, no string.Contains: "rpc" es substring de
                // los cuatro scopes con punto, asi que Contains marcaria "ok" para
                // un "rpc" suelto que Discord no otorgo mientras seguia otorgando
                // los demas -- justo el otorgamiento parcial que este comando
                // existe para detectar.
                bool granted = DiscordAuth.HasScope(token.Scope, wanted);
                Console.WriteLine("    " + (granted ? "ok  " : "NO  ") + wanted);
            }
            return 0;
        }

        private static int Status()
        {
            // Solo lectura para mostrar en pantalla: Load() es puro (Important
            // 1), asi que este comando nunca reapunta las credenciales de
            // renovacion como efecto colateral de haber sido invocado.
            DiscordToken token = DiscordAuth.Load();
            if (token == null) { Console.WriteLine("sin credenciales"); return 1; }

            Console.WriteLine("vence:   " + token.ExpiresUtc.ToLocalTime());
            Console.WriteLine("renueva: " + (token.NeedsRenewal ? "ahora" : "todavia no"));
            Console.WriteLine("scopes:  " + token.Scope);
            return 0;
        }

        // AccessToken() es la misma ruta que usa el plugin en runtime: si esto
        // renueva bien aca, renueva bien alla. client-id/secret son opcionales
        // -- pero, a diferencia de antes del fix de Important 1, YA NO son
        // inertes cuando el store tiene credenciales: AccessToken() ahora solo
        // aplica el clientId/clientSecret del store si _clientId todavia esta
        // vacio (Load() en si es puro, no toca nada). SetClientCredentials de
        // aca abajo corre ANTES de esa lectura, asi que si se pasan por
        // argumento GANAN sobre lo que haya en disco -- exactamente lo que
        // hace falta para forzar una renovacion real (Step 5 del brief) contra
        // un client-id/secret especifico, distinto del guardado. Sin
        // argumentos, y como cada invocacion del probe es un proceso nuevo sin
        // nada cargado todavia, el par del store se aplica solo.
        private static int Refresh(string[] args)
        {
            if (args.Length >= 3) DiscordAuth.SetClientCredentials(args[1], args[2]);

            string token = DiscordAuth.AccessToken();
            Console.WriteLine(token != null ? "token vigente/renovado" : "no se pudo obtener token (ver log)");
            return token != null ? 0 : 1;
        }

        // Task 6: ejercita DiscordService entero -- Poke(), el evento Changed,
        // y ambas suscripciones -- en vez de golpear DiscordRpc a mano como los
        // comandos de arriba. Es la unica forma de confirmar contra Discord de
        // verdad que VOICE_SETTINGS_UPDATE y SCREENSHARE_STATE_UPDATE llegan, y
        // que el campo de screenshare se llama "active" como asume OnScreenShare.
        private static int Watch()
        {
            Console.WriteLine("watch: Poke() cada segundo, Ctrl+C para salir");
            Console.WriteLine("tocar deafen y compartir pantalla desde Discord mismo");

            DiscordService.Changed += () =>
            {
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") +
                                   "  state=" + DiscordService.State +
                                   "  deaf=" + DiscordService.Deaf +
                                   "  screensharing=" + DiscordService.ScreenSharing);
            };

            // Idempotente: llamarlo una vez por segundo es exactamente lo que
            // hace el tick real del host (ver el comentario de Poke en
            // DiscordService), asi que este loop es el mismo patron de uso, no
            // uno especial para el probe.
            while (true)
            {
                DiscordService.Poke();
                Thread.Sleep(1000);
            }
        }

        // Fix round 2, Critical 1: fuerza la ventana exacta que el bug
        // describe -- Disconnected llegando justo antes de que Attach
        // publique _rpc -- via el seam DiscordService.
        // TestSimulateDisconnectBeforePublish, en vez de intentar cronometrar
        // un corte real de Discord (no se puede hacer confiable desde
        // afuera). Necesita Discord corriendo y una credencial enrolada,
        // igual que watch/connect: esto NO es un test offline, es un probe
        // en vivo.
        //
        // Fix round 3, N2: la version anterior solo pedia "vi NoDiscord" y
        // "eventualmente vi Ready" -- y el codigo CON el bug tambien cumple
        // las dos, en unos 300ms: Detach anuncia NoDiscord al publicarse el
        // cadaver, y el mismo Attach que acaba de publicarlo anuncia Ready
        // dos lineas despues. El unico testigo real de que algo andaba mal
        // era la brecha de 16s y una linea de log, y las dos las leia yo a
        // mano despues de correr el comando -- el aserto en si no probaba
        // nada. Ahora se mide la brecha: Ready tiene que tardar al menos 5s
        // desde la muerte forzada. Bajo el bug esa brecha es ~0, porque no
        // hay ningun reintento real de por medio -- es literalmente el mismo
        // Attach publicando el cadaver y anunciando Ready acto seguido. Con
        // el fix, la unica forma de llegar a Ready es un Poke() posterior que
        // pase por Fail (15s de backoff) y reconecte de cero, asi que la
        // brecha observada ronda los 15-17s. 5s es un piso comodo entre
        // ambos: muy por encima de lo que el bug podria producir, muy por
        // debajo del backoff real.
        private static int RaceAttach()
        {
            Console.WriteLine("raceattach: fuerza Disconnected justo antes de publicar _rpc " +
                               "(Critical 1) y exige que Ready tarde al menos 5s desde la muerte forzada");

            // Fix round 3, N4: escritos desde el thread de Attach (el hook de
            // abajo, y el handler de Changed, ambos en threads de fondo) y
            // leidos desde el loop de este metodo -- la misma discriminacion
            // de visibilidad que M5 le aplico a DiscordService.State/Deaf/
            // ScreenSharing. DateTime?/bool no pueden ser volatile aca (el
            // primero no es de los tipos permitidos, y ambos comparten
            // invariantes que necesitan actualizarse juntos), asi que un lock
            // cumple el mismo rol.
            object sync = new object();
            bool consumed = false;
            DateTime? deathObservedUtc = null;
            DateTime? readyObservedUtc = null;

            // One-shot del lado del probe (fix round 3): el hook en
            // DiscordService ahora solo LEE, nunca apaga el flag -- si lo
            // hiciera, CS0649 no avisaria en un build sin este archivo (ver
            // el comentario del campo).
            //
            // El timestamp de "muerte forzada" se toma ACA, en el momento
            // exacto en que el hook dispara -- no infiriendolo de un Announce
            // con State == NoDiscord. Motivo: N3 corrigio a Detach para que
            // NO anuncie cuando la conexion todavia no era la oficial (que es
            // exactamente este caso, la ventana de Critical 1); y el catch de
            // Attach que sigue llama a Fail(NoDiscord), cuyo dedup
            // "if (State == state) return" no anuncia nada si State ya
            // arranco en su default (NoDiscord, un proceso de probe recien
            // arrancado). Con las dos correcciones N1/N3 en su lugar, un
            // Changed con State==NoDiscord ya NO esta garantizado para este
            // evento puntual -- medir en la fuente (este hook) es lo unico
            // que no depende de esa cadena de anuncios.
            DiscordService.TestSimulateDisconnectBeforePublish = () =>
            {
                lock (sync)
                {
                    if (consumed) return false;
                    consumed = true;
                    deathObservedUtc = DateTime.UtcNow;
                }
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") +
                                   "  *** muerte forzada disparada (seam Critical 1) ***");
                return true;
            };

            DiscordService.Changed += () =>
            {
                DiscordLink state = DiscordService.State;
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") +
                                   "  state=" + state +
                                   "  deaf=" + DiscordService.Deaf +
                                   "  screensharing=" + DiscordService.ScreenSharing);
                if (state == DiscordLink.Ready)
                {
                    lock (sync) { if (readyObservedUtc == null) readyObservedUtc = DateTime.UtcNow; }
                }
            };

            // El backoff de Fail() tras la muerte forzada es de 15s; darle
            // margen de sobra para el reintento real (conectar + autenticar
            // contra Discord de verdad) antes de declarar wedge.
            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline)
            {
                DiscordService.Poke();
                Thread.Sleep(1000);
                lock (sync) { if (readyObservedUtc.HasValue) break; }
            }

            DateTime? death, ready;
            lock (sync) { death = deathObservedUtc; ready = readyObservedUtc; }

            if (!death.HasValue)
            {
                Console.WriteLine("FALLO: nunca se vio la muerte forzada -- el seam no disparo " +
                                   "(revisar que Discord este corriendo y la credencial enrolada)");
                return 1;
            }

            if (!ready.HasValue)
            {
                Console.WriteLine("FALLO: nunca volvio a Ready despues de la muerte forzada -- " +
                                   "wedge permanente, Critical 1 sigue roto");
                return 1;
            }

            double gapSeconds = (ready.Value - death.Value).TotalSeconds;
            Console.WriteLine("brecha muerte forzada -> Ready: " + gapSeconds.ToString("0.0") + "s");

            const double minGapSeconds = 5.0;
            if (gapSeconds < minGapSeconds)
            {
                Console.WriteLine("FALLO: Ready llego a los " + gapSeconds.ToString("0.0") + "s de la muerte " +
                                   "forzada -- eso es consistente con publicar el cadaver de inmediato (el bug " +
                                   "de Critical 1), no con un reintento real despues del backoff de 15s. " +
                                   "Critical 1 NO queda probado como arreglado por esta corrida.");
                return 1;
            }

            Console.WriteLine("OK: Ready llego " + gapSeconds.ToString("0.0") + "s despues de la muerte " +
                               "forzada -- consistente con un reintento real tras el backoff de Fail(), no " +
                               "con publicar el cadaver de inmediato");
            return 0;
        }

        public static int Main(string[] args)
        {
            Log.Component = "discordprobe";
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "connect";

            try
            {
                switch (command)
                {
                    case "connect": return Connect();
                    case "reconnect": return Reconnect();
                    case "enroll": return Enroll(args);
                    case "status": return Status();
                    case "refresh": return Refresh(args);
                    case "watch": return Watch();
                    case "raceattach": return RaceAttach();
                    default:
                        Console.WriteLine("comando desconocido: " + command);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("fallo: " + ex.Message);
                return 1;
            }
        }
    }
}
