// Companion de consola para el backend de Discord. Ejercita transporte, auth y
// comandos sin el deck de por medio, que es como se diagnostica cuando una tecla
// no responde.
//
//   DiscordProbe.exe connect
//   DiscordProbe.exe reconnect
//   DiscordProbe.exe enroll <client-id> <client-secret>
//   DiscordProbe.exe status
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

        // Ejercita el ciclo Close() -> Connect() sobre la MISMA instancia varias
        // veces seguidas: es el camino que el fix de "reader thread resurrection"
        // tiene que dejar sano (reader viejo capturando su propio pipe, Close()
        // esperando a que termine antes de que Connect() pueda reasignar _pipe).
        // Si el fix estuviera mal, esto se cuelga (dos readers peleando el mismo
        // stream) o dispara un Disconnected espurio que un test unitario no
        // puede ver porque hace falta un pipe real de Discord.
        private static int Reconnect()
        {
            var rpc = new DiscordRpc();
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
            }

            Console.WriteLine(cycles + " ciclos connect/close ok sobre la misma instancia, sin colgarse");
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
