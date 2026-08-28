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

        public static int Main()
        {
            FrameRoundTrip();
            HeaderIsLittleEndian();
            UnicodeSurvives();
            TruncatedStreamIsFalseNotHang();
            InvalidLengthIsFalseWithZeroOpcode();
            return Check.Report();
        }
    }
}
