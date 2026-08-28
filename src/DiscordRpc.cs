// Cliente del RPC local de Discord.
//
// Discord expone un named pipe por cada instancia: \\.\pipe\discord-ipc-0 a -9.
// El encuadre es opcode y largo en int32 little-endian, y despues el JSON en
// UTF-8. Es formato binario propio, no HTTP ni WebSocket.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace StreamDeckAudio
{
    internal sealed class DiscordRpcException : Exception
    {
        public readonly int Code;

        public DiscordRpcException(int code, string message) : base(message)
        {
            Code = code;
        }
    }

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
            if (length < 0 || length > 8 * 1024 * 1024) { opcode = 0; return false; }

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

        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        private NamedPipeClientStream _pipe;
        private Thread _reader;
        private volatile bool _closing;

        private readonly Dictionary<string, Dictionary<string, object>> _replies =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action<Dictionary<string, object>>> _handlers =
            new Dictionary<string, Action<Dictionary<string, object>>>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public event Action Disconnected;

        /// <summary>Abre el primer pipe que responda y completa el handshake.</summary>
        public bool Connect(string clientId)
        {
            for (int i = 0; i < 10; i++)
            {
                var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut);
                try
                {
                    pipe.Connect(200);
                }
                catch (Exception)
                {
                    pipe.Dispose();
                    continue;
                }

                _pipe = pipe;
                WriteFrame(_pipe, OpHandshake,
                           "{\"v\":1,\"client_id\":\"" + Json.Escape(clientId) + "\"}");

                _reader = new Thread(ReadLoop) { IsBackground = true, Name = "discord-rpc" };
                _reader.Start();
                Log.Write("Discord RPC: conectado a discord-ipc-" + i);
                return true;
            }
            return false;
        }

        private void ReadLoop()
        {
            try
            {
                int opcode;
                string json;
                while (!_closing && ReadFrame(_pipe, out opcode, out json))
                {
                    if (opcode == OpPing) { WriteFrame(_pipe, OpPong, json); continue; }
                    if (opcode != OpFrame) continue;

                    var message = Serializer.Deserialize<Dictionary<string, object>>(json);
                    Dispatch(message);
                }
            }
            catch (Exception ex)
            {
                if (!_closing) Log.Write("Discord RPC: lectura cortada: " + ex.Message);
            }

            if (_closing) return;
            Action handler = Disconnected;
            if (handler != null) handler();
        }

        // Una respuesta trae el nonce del pedido; un evento suscrito no trae ninguno.
        private void Dispatch(Dictionary<string, object> message)
        {
            string nonce = Json.Str(message, "nonce");
            if (!string.IsNullOrEmpty(nonce))
            {
                lock (_gate)
                {
                    _replies[nonce] = message;
                    Monitor.PulseAll(_gate);
                }
                return;
            }

            string evt = Json.Str(message, "evt");
            if (string.IsNullOrEmpty(evt)) return;

            Action<Dictionary<string, object>> subscriber;
            lock (_gate)
            {
                if (!_handlers.TryGetValue(evt, out subscriber)) return;
            }
            subscriber(Json.Dict(message, "data"));
        }

        public Dictionary<string, object> Request(string cmd, Dictionary<string, object> args, int timeoutMs)
        {
            string nonce = Guid.NewGuid().ToString("N");
            var frame = new Dictionary<string, object>
            {
                { "cmd", cmd },
                { "args", args ?? new Dictionary<string, object>() },
                { "nonce", nonce }
            };
            WriteFrame(_pipe, OpFrame, Serializer.Serialize(frame));

            Dictionary<string, object> reply = WaitFor(nonce, timeoutMs);
            if (reply == null) throw new DiscordRpcException(0, cmd + ": sin respuesta en " + timeoutMs + " ms");

            if (Json.Str(reply, "evt") == "ERROR")
            {
                Dictionary<string, object> error = Json.Dict(reply, "data");
                throw new DiscordRpcException(Json.Int(error, "code", 0), Json.Str(error, "message"));
            }
            return Json.Dict(reply, "data");
        }

        private Dictionary<string, object> WaitFor(string nonce, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (_gate)
            {
                while (true)
                {
                    Dictionary<string, object> reply;
                    if (_replies.TryGetValue(nonce, out reply)) { _replies.Remove(nonce); return reply; }

                    var left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return null;
                    Monitor.Wait(_gate, left);
                }
            }
        }

        public void Subscribe(string evt, Action<Dictionary<string, object>> handler)
        {
            lock (_gate) { _handlers[evt] = handler; }

            string nonce = Guid.NewGuid().ToString("N");
            var frame = new Dictionary<string, object>
            {
                { "cmd", "SUBSCRIBE" }, { "evt", evt },
                { "args", new Dictionary<string, object>() }, { "nonce", nonce }
            };
            WriteFrame(_pipe, OpFrame, Serializer.Serialize(frame));
            WaitFor(nonce, 5000);
        }

        public void Close()
        {
            _closing = true;
            try { if (_pipe != null) _pipe.Dispose(); }
            catch (Exception) { }
            _pipe = null;
        }
    }
}
