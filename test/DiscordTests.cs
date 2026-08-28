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

        public static int Main()
        {
            FrameRoundTrip();
            HeaderIsLittleEndian();
            UnicodeSurvives();
            TruncatedStreamIsFalseNotHang();
            return Check.Report();
        }
    }
}
