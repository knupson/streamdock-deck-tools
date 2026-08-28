// Companion de consola para el backend de Discord. Ejercita transporte, auth y
// comandos sin el deck de por medio, que es como se diagnostica cuando una tecla
// no responde.
//
//   DiscordProbe.exe connect
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

        public static int Main(string[] args)
        {
            Log.Component = "discordprobe";
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "connect";

            try
            {
                switch (command)
                {
                    case "connect": return Connect();
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
