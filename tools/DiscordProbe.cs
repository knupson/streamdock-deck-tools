// Companion de consola para el backend de Discord. Ejercita transporte, auth y
// comandos sin el deck de por medio, que es como se diagnostica cuando una tecla
// no responde.
//
//   DiscordProbe.exe connect
//   DiscordProbe.exe reconnect
//   DiscordProbe.exe enroll <client-id> <client-secret>
//   DiscordProbe.exe status
//   DiscordProbe.exe refresh [client-id client-secret]
//   DiscordProbe.exe deafen
//   DiscordProbe.exe screenshare
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

            DiscordToken token = DiscordAuth.Load();
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
            DiscordToken token = DiscordAuth.Load();
            if (token == null) { Console.WriteLine("sin credenciales"); return 1; }

            Console.WriteLine("vence:   " + token.ExpiresUtc.ToLocalTime());
            Console.WriteLine("renueva: " + (token.NeedsRenewal ? "ahora" : "todavia no"));
            Console.WriteLine("scopes:  " + token.Scope);
            return 0;
        }

        // AccessToken() es la misma ruta que usa el plugin en runtime: si esto
        // renueva bien aca, renueva bien alla. client-id/secret son opcionales:
        // cada invocacion del probe es un proceso nuevo, asi que las credenciales
        // que Enroll seteo en memoria no sobreviven -- si el token todavia no
        // necesita renovarse esto alcanza igual (AccessToken devuelve el access
        // guardado sin tocar la red), pero para forzar una renovacion real
        // (Step 5 del brief, con el token vencido a mano) hacen falta de nuevo.
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
        // en vivo. Si el fix esta roto, este comando se cuelga hasta el
        // timeout de abajo sin nunca llegar a Ready -- Poke() devolveria
        // temprano para siempre porque _rpc quedaria apuntando a la conexion
        // muerta que se publico por error.
        private static int RaceAttach()
        {
            Console.WriteLine("raceattach: fuerza Disconnected justo antes de publicar _rpc " +
                               "(Critical 1) y confirma que un intento posterior igual llega a Ready");

            DiscordService.TestSimulateDisconnectBeforePublish = true;

            bool sawForcedDeath = false;
            bool reachedReady = false;

            DiscordService.Changed += () =>
            {
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") +
                                   "  state=" + DiscordService.State +
                                   "  deaf=" + DiscordService.Deaf +
                                   "  screensharing=" + DiscordService.ScreenSharing);
                if (DiscordService.State == DiscordLink.NoDiscord) sawForcedDeath = true;
                if (DiscordService.State == DiscordLink.Ready) reachedReady = true;
            };

            // El backoff de Fail() tras la muerte forzada es de 15s; darle
            // margen de sobra para el reintento real (conectar + autenticar
            // contra Discord de verdad) antes de declarar wedge.
            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
            while (DateTime.UtcNow < deadline && !reachedReady)
            {
                DiscordService.Poke();
                Thread.Sleep(1000);
            }

            if (!sawForcedDeath)
            {
                Console.WriteLine("FALLO: nunca se vio la muerte forzada -- el seam no disparo " +
                                   "(revisar que Discord este corriendo y la credencial enrolada)");
                return 1;
            }

            if (!reachedReady)
            {
                Console.WriteLine("FALLO: nunca volvio a Ready despues de la muerte forzada -- " +
                                   "wedge permanente, Critical 1 sigue roto");
                return 1;
            }

            Console.WriteLine("OK: la conexion muerta antes de publicarse no dejo a Poke() colgado; " +
                               "un intento posterior llego a Ready solo");
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
