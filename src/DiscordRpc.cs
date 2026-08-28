// Cliente del RPC local de Discord.
//
// Discord expone un named pipe por cada instancia: \\.\pipe\discord-ipc-0 a -9.
// El encuadre es opcode y largo en int32 little-endian, y despues el JSON en
// UTF-8. Es formato binario propio, no HTTP ni WebSocket.

using System;
using System.IO;
using System.Text;

namespace StreamDeckAudio
{
    internal sealed partial class DiscordRpc
    {
        internal const int OpHandshake = 0;
        internal const int OpFrame = 1;
        internal const int OpClose = 2;
        internal const int OpPing = 3;
        internal const int OpPong = 4;

        internal static void WriteFrame(Stream stream, int opcode, string json)
        {
            byte[] payload = Encoding.UTF8.GetBytes(json);
            var header = new byte[8];
            Buffer.BlockCopy(BitConverter.GetBytes(opcode), 0, header, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(payload.Length), 0, header, 4, 4);

            stream.Write(header, 0, header.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
        }

        internal static bool ReadFrame(Stream stream, out int opcode, out string json)
        {
            opcode = 0;
            json = null;

            var header = new byte[8];
            if (!ReadExactly(stream, header, 8)) return false;

            opcode = BitConverter.ToInt32(header, 0);
            int length = BitConverter.ToInt32(header, 4);
            if (length < 0 || length > 8 * 1024 * 1024) return false;

            var payload = new byte[length];
            if (!ReadExactly(stream, payload, length)) return false;

            json = Encoding.UTF8.GetString(payload);
            return true;
        }

        // Un read parcial es normal en un pipe; un stream que se termina no lo es.
        private static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int got = stream.Read(buffer, read, count - read);
                if (got <= 0) return false;
                read += got;
            }
            return true;
        }
    }
}
