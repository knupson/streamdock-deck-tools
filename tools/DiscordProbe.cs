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

using System;
using System.Collections.Generic;

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
