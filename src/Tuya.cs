using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace StreamDeckAudio
{
    internal static class TuyaProtocol
    {
        public const int CmdControl = 7;
        public const int CmdDpQuery = 10;
        internal const int MaxBodyLength = 1024 * 1024;

        internal static uint ReadUInt32(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
                | ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        private static void WriteUInt32(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }

        private static uint Crc32(byte[] data, int count)
        {
            uint crc = 0xffffffff;
            for (int i = 0; i < count; i++)
            {
                crc ^= data[i];
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0U);
            }
            return ~crc;
        }

        public static byte[] Frame(int seq, int cmd, byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException("payload");
            var frame = new byte[16 + payload.Length + 8];
            WriteUInt32(frame, 0, 0x55aa);
            WriteUInt32(frame, 4, unchecked((uint)seq));
            WriteUInt32(frame, 8, unchecked((uint)cmd));
            WriteUInt32(frame, 12, (uint)(payload.Length + 8));
            Buffer.BlockCopy(payload, 0, frame, 16, payload.Length);
            WriteUInt32(frame, frame.Length - 8, Crc32(frame, frame.Length - 8));
            WriteUInt32(frame, frame.Length - 4, 0xaa55);
            return frame;
        }

        private static byte[] KeyBytes(string key)
        {
            if (key == null || key.Length != 16) throw new ArgumentException("La clave Tuya debe tener 16 caracteres ASCII.");
            foreach (char c in key)
                if (c > 127) throw new ArgumentException("La clave Tuya debe ser ASCII.");
            return Encoding.ASCII.GetBytes(key);
        }

        private static byte[] Transform(byte[] data, string key, bool encrypt)
        {
            using (var aes = Aes.Create())
            {
                aes.Key = KeyBytes(key);
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;
                using (ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor())
                    return transform.TransformFinalBlock(data, 0, data.Length);
            }
        }

        public static byte[] Encrypt(byte[] plain, string key) { return Transform(plain, key, true); }

        public static byte[] Decrypt(byte[] cipher, string key)
        {
            if (cipher == null || cipher.Length == 0 || cipher.Length % 16 != 0) return null;
            try { return Transform(cipher, key, false); }
            catch (CryptographicException) { return null; }
            catch (ArgumentException) { return null; }
        }

        public static byte[] BuildQuery(string devId, string key, int seq, long unixTime)
        {
            var json = new JavaScriptSerializer();
            string id = json.Serialize(devId);
            string payload = "{\"gwId\":" + id + ",\"devId\":" + id + ",\"uid\":" + id
                + ",\"t\":\"" + unixTime.ToString(CultureInfo.InvariantCulture) + "\"}";
            return Frame(seq, CmdDpQuery, Encrypt(Encoding.UTF8.GetBytes(payload), key));
        }

        public static byte[] BuildControl(string devId, string key, int seq, IDictionary<string, object> dps, long unixTime)
        {
            var json = new JavaScriptSerializer();
            string id = json.Serialize(devId);
            string payload = "{\"devId\":" + id + ",\"uid\":" + id + ",\"t\":\""
                + unixTime.ToString(CultureInfo.InvariantCulture) + "\",\"dps\":" + json.Serialize(dps) + "}";
            byte[] encrypted = Encrypt(Encoding.UTF8.GetBytes(payload), key);
            var versioned = new byte[15 + encrypted.Length];
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("3.3"), 0, versioned, 0, 3);
            Buffer.BlockCopy(encrypted, 0, versioned, 15, encrypted.Length);
            return Frame(seq, CmdControl, versioned);
        }

        // Las respuestas agregan un return code de cuatro bytes antes del payload.
        internal static bool ValidResponse(byte[] frame)
        {
            return frame != null && frame.Length >= 28 && frame.Length <= 16 + MaxBodyLength
                && ReadUInt32(frame, 0) == 0x55aa
                && ReadUInt32(frame, 12) == (uint)(frame.Length - 16)
                && ReadUInt32(frame, frame.Length - 4) == 0xaa55
                && ReadUInt32(frame, frame.Length - 8) == Crc32(frame, frame.Length - 8)
                && ReadUInt32(frame, 16) == 0;
        }

        public static Dictionary<string, object> ParseResponse(byte[] frame, string key)
        {
            if (!ValidResponse(frame)) return null;
            int offset = 20;
            int count = frame.Length - offset - 8;
            if (count >= 15 && frame[offset] == '3' && frame[offset + 1] == '.' && frame[offset + 2] == '3')
            {
                offset += 15;
                count -= 15;
            }
            var cipher = new byte[count];
            Buffer.BlockCopy(frame, offset, cipher, 0, count);
            byte[] plain = Decrypt(cipher, key);
            if (plain == null) return null;
            try
            {
                string text = new UTF8Encoding(false, true).GetString(plain);
                var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                if (data == null || data.ContainsKey("error")) return null;
                return Json.Dict(data, "dps");
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    // Operaciones sincrónicas: el hub debe llamarlas desde un hilo de fondo.
    internal sealed class TuyaClient
    {
        private const int Port = 6668;
        private const int TimeoutMs = 3000;
        private int _sequence;

        private static long UnixTime()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }

        private static int Remaining(Stopwatch timer)
        {
            int remaining = TimeoutMs - (int)timer.ElapsedMilliseconds;
            if (remaining <= 0) throw new IOException("Se agotó el tiempo de respuesta Tuya.");
            return remaining;
        }

        private static bool ReadExactly(NetworkStream stream, byte[] data, int offset, int count, Stopwatch timer)
        {
            while (count > 0)
            {
                stream.ReadTimeout = Remaining(timer);
                int read = stream.Read(data, offset, count);
                if (read == 0) return false;
                offset += read;
                count -= read;
            }
            return true;
        }

        private static byte[] Exchange(IotTuyaDevice device, byte[] request, bool control)
        {
            IPAddress address;
            if (!IPAddress.TryParse(device.Ip, out address)) return null;
            var timer = Stopwatch.StartNew();
            using (var client = new TcpClient(address.AddressFamily))
            {
                IAsyncResult pending = client.BeginConnect(address, Port, null, null);
                using (WaitHandle wait = pending.AsyncWaitHandle)
                {
                    if (!wait.WaitOne(Remaining(timer))) return null;
                    client.EndConnect(pending);
                }
                using (NetworkStream stream = client.GetStream())
                {
                    stream.WriteTimeout = Remaining(timer);
                    stream.Write(request, 0, request.Length);
                    while (true)
                    {
                        var header = new byte[16];
                        if (!ReadExactly(stream, header, 0, header.Length, timer)) return null;
                        uint length = TuyaProtocol.ReadUInt32(header, 12);
                        if (TuyaProtocol.ReadUInt32(header, 0) != 0x55aa || length < 12
                            || length > TuyaProtocol.MaxBodyLength) return null;
                        var frame = new byte[16 + (int)length];
                        Buffer.BlockCopy(header, 0, frame, 0, 16);
                        if (!ReadExactly(stream, frame, 16, (int)length, timer)) return null;
                        if (!TuyaProtocol.ValidResponse(frame)) return null;
                        uint cmd = TuyaProtocol.ReadUInt32(frame, 8);
                        if (cmd != TuyaProtocol.ReadUInt32(request, 8) && cmd != 8) continue;
                        // DP_QUERY puede recibir un ACK vacío antes del estado.
                        if (frame.Length == 28 && !control) continue;
                        return frame;
                    }
                }
            }
        }

        public Dictionary<string, object> Status(IotTuyaDevice d)
        {
            if (d == null || string.IsNullOrEmpty(d.Id)) return null;
            try
            {
                byte[] request = TuyaProtocol.BuildQuery(d.Id, d.Key, Interlocked.Increment(ref _sequence), UnixTime());
                return TuyaProtocol.ParseResponse(Exchange(d, request, false), d.Key);
            }
            catch (SocketException) { return null; }
            catch (IOException) { return null; }
            catch (CryptographicException) { return null; }
            catch (ArgumentException) { return null; }
        }

        public bool Set(IotTuyaDevice d, IDictionary<string, object> dps)
        {
            if (d == null || string.IsNullOrEmpty(d.Id) || dps == null) return false;
            try
            {
                byte[] request = TuyaProtocol.BuildControl(d.Id, d.Key, Interlocked.Increment(ref _sequence), dps, UnixTime());
                byte[] response = Exchange(d, request, true);
                return response != null && (response.Length == 28 || TuyaProtocol.ParseResponse(response, d.Key) != null);
            }
            catch (SocketException) { return false; }
            catch (IOException) { return false; }
            catch (CryptographicException) { return false; }
            catch (ArgumentException) { return false; }
        }
    }
}
