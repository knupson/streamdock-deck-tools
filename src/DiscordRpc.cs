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

        // Compartido con PluginHost.cs: un JavaScriptSerializer por proceso alcanza,
        // no hay estado que lo haga inseguro entre llamadas concurrentes.
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        // Volatile: el reader thread la lee en su loop principal mientras Close()
        // la pone en null desde otro thread; sin esto no hay garantia de que el
        // reader vea el null (solo funciona por casualidad en x64).
        private volatile NamedPipeClientStream _pipe;

        // Volatile por la misma razon que _pipe: Close() la lee (para el Join
        // acotado) desde un thread que no es el que la asigno.
        private volatile Thread _reader;
        private volatile bool _closing;

        private readonly Dictionary<string, Dictionary<string, object>> _replies =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Action<Dictionary<string, object>>> _handlers =
            new Dictionary<string, Action<Dictionary<string, object>>>(StringComparer.Ordinal);

        // Nonces que todavia le importan a alguien. Sin este set, una respuesta
        // que llega despues de que WaitFor ya tiro por timeout se queda para
        // siempre en _replies: nadie la va a pedir de nuevo ni a sacarla.
        private readonly HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal);

        private readonly object _gate = new object();

        // Candado aparte para escribir el pipe. El reader thread manda PONG desde
        // ReadLoop mientras un caller manda Request/Subscribe/Close al mismo tiempo;
        // sin serializar los dos WriteFrame (header y payload van en escrituras
        // separadas) se intercalan y el stream queda corrupto para siempre. No
        // puede ser _gate: ese lock lo tiene tomado Dispatch mientras espera
        // Monitor.Wait, y una escritura bloqueante de pipe ahi adentro frena todo
        // el reader.
        private readonly object _writeGate = new object();

        // Handshake: hasta que llegue READY (o Discord mande CLOSE, o se agote el
        // timeout) Connect no puede devolver true. Se resuelven bajo _gate igual
        // que las respuestas por nonce, pero READY no trae nonce.
        private volatile bool _handshakeReady;
        private volatile bool _handshakeClosed;

        public event Action Disconnected;

        /// <summary>
        /// Abre el primer pipe que responda y completa el handshake.
        /// Bloquea hasta ~2 s cuando Discord no esta corriendo (10 pipes x 200 ms
        /// de connect timeout cada uno), mas hasta 5 s esperando READY, mas hasta
        /// 2 s adicionales de Close() si el handshake termina rechazado o en
        /// timeout (Close() ahora hace join del reader viejo): ~9 s en el peor
        /// caso. Nunca llamar desde el thread de mensajes del host, tiene que ir
        /// en su propio thread como hace DiscordProbe.
        /// </summary>
        public bool Connect(string clientId)
        {
            // Guard defensivo, no un patron de uso soportado: si ya habia una
            // conexion viva (dos Connect() seguidos sin Close() en el medio),
            // cerrarla antes de abrir la nueva. Sin esto, el reader y el pipe
            // viejos quedan huerfanos para siempre -- nadie los va a Join ni a
            // Dispose -- exactamente la misma familia de leak que Close() ya
            // tiene que evitar en su propio ciclo.
            if (_pipe != null) Close();

            // Sin este reset, una segunda llamada a Connect despues de Close()
            // arranca un reader cuyo "while (!_closing ...)" ya es false: el
            // objeto queda "conectado" pero sordo.
            _closing = false;

            for (int i = 0; i < 10; i++)
            {
                // Asynchronous (overlapped) es obligatorio acá: con el modo
                // sincrónico por defecto, Windows serializa el handle entero, y un
                // Write desde este thread se cuelga para siempre mientras el reader
                // thread tiene un Read pendiente sobre el mismo pipe (confirmado
                // colgando el Close() de este mismo task antes de este fix).
                var pipe = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut,
                                                      PipeOptions.Asynchronous);
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
                _handshakeReady = false;
                _handshakeClosed = false;

                lock (_writeGate)
                {
                    WriteFrame(pipe, OpHandshake,
                               "{\"v\":1,\"client_id\":\"" + Json.Escape(clientId) + "\"}");
                }

                // El reader recibe SU pipe por closure, nunca lee el campo _pipe:
                // si un Close() seguido de un Connect() reemplaza _pipe mientras
                // este thread todavia esta desenrollandose de su ultimo read, no
                // tiene que empezar a leer del pipe nuevo (dos readers sobre un
                // mismo stream corrompen el framing para siempre).
                _reader = new Thread(() => ReadLoop(pipe)) { IsBackground = true, Name = "discord-rpc" };
                _reader.Start();

                if (!WaitForHandshake(5000))
                {
                    // Un pipe que acepta la conexion TCP-like pero rechaza el
                    // client_id (CLOSE) o no contesta nada (timeout) no sirve:
                    // "conectado" tiene que significar que Discord confirmo, no
                    // solo que el named pipe existia.
                    Log.Write("Discord RPC: discord-ipc-" + i + " no confirmo el handshake (READY)");
                    Close();
                    return false;
                }

                Log.Write("Discord RPC: conectado a discord-ipc-" + i);
                return true;
            }
            return false;
        }

        private bool WaitForHandshake(int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            lock (_gate)
            {
                while (!_handshakeReady && !_handshakeClosed)
                {
                    var left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                    if (left <= 0) return false;
                    Monitor.Wait(_gate, left);
                }
            }
            return _handshakeReady;
        }

        // Recibe el pipe por parametro (capturado por closure en Connect), no lo
        // lee de _pipe: ese campo puede apuntar a la conexion SIGUIENTE si este
        // reader es el viejo, todavia desenrollandose de Close().
        private void ReadLoop(NamedPipeClientStream pipe)
        {
            try
            {
                int opcode;
                string json;
                while (!_closing && ReadFrame(pipe, out opcode, out json))
                {
                    if (opcode == OpPing)
                    {
                        lock (_writeGate) { WriteFrame(pipe, OpPong, json); }
                        continue;
                    }

                    if (opcode == OpClose)
                    {
                        // El payload trae el motivo (client id invalido, version
                        // vieja, etc.) y es el unico diagnostico que Discord da
                        // antes de tirar la conexion: vale la pena verlo en el log
                        // cuando una tecla deja de responder.
                        Log.Write("Discord RPC: CLOSE recibido: " + json);
                        lock (_gate)
                        {
                            _handshakeClosed = true;
                            Monitor.PulseAll(_gate);
                        }
                        continue;
                    }

                    if (opcode != OpFrame) continue;

                    var message = Serializer.Deserialize<Dictionary<string, object>>(json);
                    Dispatch(message);
                }
            }
            catch (Exception ex)
            {
                if (!_closing) Log.Write("Discord RPC: lectura cortada: " + ex.Message);
            }

            // Re-chequear bajo _gate: si Close() corrio justo entre que el while
            // de arriba salio y esta linea, un chequeo sin lock puede leer
            // _closing == false y disparar Disconnected por una desconexion que
            // el usuario pidio el mismo, haciendo que Task 6 reconecte a algo que
            // se acaba de cerrar a proposito.
            //
            // El segundo chequeo (_pipe != pipe) cubre la ventana que _closing
            // solo no cierra: si para cuando este reader viejo llega aca ya corrio
            // un Close() Y un Connect() completo (Connect resetea _closing a
            // false y _pipe ya es el pipe nuevo), _closing vuelve a leer false
            // pero esta salida sigue siendo la del pipe viejo, no una
            // desconexion real de la conexion actual.
            lock (_gate)
            {
                if (_closing) return;
                if (_pipe != pipe) return;
            }
            Action handler = Disconnected;
            if (handler != null) handler();
        }

        // Los handlers de Subscribe corren en este mismo thread, el del reader.
        // Eso preserva el orden de los eventos y alcanza para lo que hace Task 6
        // (asignar campos), pero significa que un handler que llama Request se
        // clava esperando su propia respuesta hasta el timeout, y mientras tanto
        // ninguna otra respuesta pendiente se despacha. Los handlers no pueden
        // mandar comandos ni bloquear.
        private void Dispatch(Dictionary<string, object> message)
        {
            string nonce = Json.Str(message, "nonce");
            if (!string.IsNullOrEmpty(nonce))
            {
                lock (_gate)
                {
                    // Si WaitFor ya se rindio por timeout, el nonce salio de
                    // _pending: una respuesta tardia se descarta en vez de
                    // quedarse en _replies para siempre.
                    if (_pending.Contains(nonce))
                    {
                        _replies[nonce] = message;
                        Monitor.PulseAll(_gate);
                    }
                }
                return;
            }

            string evt = Json.Str(message, "evt");
            if (string.IsNullOrEmpty(evt)) return;

            if (evt == "READY")
            {
                lock (_gate)
                {
                    _handshakeReady = true;
                    Monitor.PulseAll(_gate);
                }
                return;
            }

            Action<Dictionary<string, object>> subscriber;
            lock (_gate)
            {
                if (!_handlers.TryGetValue(evt, out subscriber)) return;
            }
            subscriber(Json.Dict(message, "data"));
        }

        public Dictionary<string, object> Request(string cmd, Dictionary<string, object> args, int timeoutMs)
        {
            NamedPipeClientStream pipe = _pipe;
            if (pipe == null) throw new DiscordRpcException(0, "no conectado a Discord");

            string nonce = Guid.NewGuid().ToString("N");
            lock (_gate) { _pending.Add(nonce); }
            try
            {
                var frame = new Dictionary<string, object>
                {
                    { "cmd", cmd },
                    { "args", args ?? new Dictionary<string, object>() },
                    { "nonce", nonce }
                };
                WriteFrameOrThrow(cmd, pipe, frame);

                Dictionary<string, object> reply = WaitFor(nonce, timeoutMs);
                return UnwrapReply(cmd, reply, timeoutMs);
            }
            finally
            {
                // Cubre el timeout normal de WaitFor Y una excepcion de
                // WriteFrame (WaitFor ni siquiera llega a correr en ese caso):
                // en cualquiera de los dos, el nonce deja de estar pendiente, asi
                // que una respuesta tardia se descarta en Dispatch en vez de
                // quedarse acumulada en _replies para siempre.
                lock (_gate) { _pending.Remove(nonce); }
            }
        }

        // _pipe puede pasar a null (Close corriendo en otro thread) entre el
        // chequeo de arriba y esta escritura; trabajar sobre el pipe local que ya
        // capturamos evita la NullReferenceException por esa carrera puntual,
        // pero el pipe en si puede seguir siendo el mismo objeto ya disposeado, o
        // seguir vivo del lado nuestro pero muerto del otro lado (Discord cerro,
        // crasheo). .NET Framework tira ObjectDisposedException para lo primero
        // casi siempre (a veces NullReferenceException: PipeStream tiene campos
        // internos que Dispose deja en null antes de que el objeto se considere
        // completamente disposed para todos los chequeos), e IOException
        // ("Pipe is broken", "El proceso no tiene acceso...") para lo segundo --
        // el caso mas comun de los tres en la practica. Task 5 y 6 solo saben
        // atrapar DiscordRpcException, asi que traducimos las tres.
        private void WriteFrameOrThrow(string context, NamedPipeClientStream pipe, Dictionary<string, object> frame)
        {
            try
            {
                lock (_writeGate) { WriteFrame(pipe, OpFrame, Serializer.Serialize(frame)); }
            }
            catch (ObjectDisposedException ex)
            {
                throw new DiscordRpcException(0, context + ": el pipe se cerro mientras se escribia (" + ex.Message + ")");
            }
            catch (NullReferenceException ex)
            {
                throw new DiscordRpcException(0, context + ": el pipe se cerro mientras se escribia (" + ex.Message + ")");
            }
            catch (IOException ex)
            {
                throw new DiscordRpcException(0, context + ": el pipe se cerro mientras se escribia (" + ex.Message + ")");
            }
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

        // Comun a Request y Subscribe: ambos mandan un frame con nonce y esperan
        // exactamente la misma forma de respuesta (ok, ERROR, o nada).
        private static Dictionary<string, object> UnwrapReply(string context, Dictionary<string, object> reply, int timeoutMs)
        {
            if (reply == null) throw new DiscordRpcException(0, context + ": sin respuesta en " + timeoutMs + " ms");

            if (Json.Str(reply, "evt") == "ERROR")
            {
                Dictionary<string, object> error = Json.Dict(reply, "data");
                throw new DiscordRpcException(Json.Int(error, "code", 0), Json.Str(error, "message"));
            }
            return Json.Dict(reply, "data");
        }

        public void Subscribe(string evt, Action<Dictionary<string, object>> handler)
        {
            NamedPipeClientStream pipe = _pipe;
            if (pipe == null) throw new DiscordRpcException(0, "no conectado a Discord");

            // Registrado ANTES de mandar el SUBSCRIBE: si el evento llegara entre
            // el ack de Discord y el registro del handler, se perderia. Save-and-
            // restore, no un Remove incondicional: si ya habia un handler previo
            // andando para este mismo evt (re-subscribe) y el intento nuevo es
            // rechazado o vence, el catch de abajo tiene que devolver el handler
            // VIEJO, no borrar la entrada entera -- la suscripcion vieja del lado
            // de Discord sigue viva, y un Remove sin condicion tira ese handler
            // que seguia funcionando y deja los eventos cayendo al piso en
            // silencio. Cuando no habia nada antes, restaurar "nada" es el mismo
            // comportamiento que el Remove que finding 4 (ronda 1) pedia.
            Action<Dictionary<string, object>> previous;
            bool hadPrevious;
            lock (_gate)
            {
                hadPrevious = _handlers.TryGetValue(evt, out previous);
                _handlers[evt] = handler;
            }

            string nonce = Guid.NewGuid().ToString("N");
            lock (_gate) { _pending.Add(nonce); }
            try
            {
                var frame = new Dictionary<string, object>
                {
                    { "cmd", "SUBSCRIBE" }, { "evt", evt },
                    { "args", new Dictionary<string, object>() }, { "nonce", nonce }
                };
                WriteFrameOrThrow("SUBSCRIBE " + evt, pipe, frame);

                Dictionary<string, object> reply = WaitFor(nonce, 5000);
                // Un SUBSCRIBE puede volver ERROR (evento desconocido, falta scope
                // OAuth) igual que cualquier Request; sin este chequeo, un rechazo
                // reporta exito y despues nunca llega nada.
                UnwrapReply("SUBSCRIBE " + evt, reply, 5000);
            }
            catch
            {
                lock (_gate)
                {
                    if (hadPrevious) _handlers[evt] = previous;
                    else _handlers.Remove(evt);
                }
                throw;
            }
            finally
            {
                lock (_gate) { _pending.Remove(nonce); }
            }
        }

        public void Close()
        {
            lock (_gate) { _closing = true; }

            // Trabajar sobre locales, no sobre los campos: un Request/Subscribe
            // concurrente que ya paso el chequeo de null puede estar a mitad de
            // escribir: capturar y nulear _pipe aca (en vez de dejarlo vivo hasta
            // el final del metodo) acota esa ventana a lo mas chico posible.
            NamedPipeClientStream pipe = _pipe;
            Thread reader = _reader;
            _pipe = null;

            try
            {
                if (pipe != null)
                {
                    // El pipe es asincronico (ver Connect), asi que Dispose() ya
                    // cancela el ReadFile pendiente del reader por si solo -- no
                    // dependemos de este frame para desbloquearlo. Lo mandamos
                    // igual porque es el protocolo correcto (Discord se entera de
                    // por que nos vamos, no solo que el pipe murio) y como capa
                    // extra de correctitud, no porque sea el mecanismo que evita
                    // el leak de thread/handle.
                    lock (_writeGate) { WriteFrame(pipe, OpClose, "{}"); }
                }
            }
            catch (Exception) { /* el pipe ya puede estar muerto del otro lado */ }

            try { if (pipe != null) pipe.Dispose(); }
            catch (Exception) { }

            // Bounded join: sin esperar al reader viejo, un Connect() llamado
            // justo despues de Close() puede reasignar _pipe y resetear _closing
            // mientras ese reader todavia esta desenrollandose de su ultimo
            // ReadFrame, lo que corre el riesgo de dos threads leyendo el mismo
            // stream (mitigado ademas por el pipe-por-parametro de ReadLoop y el
            // chequeo _pipe != pipe de arriba, pero el join lo cierra en el caso
            // comun sin depender solo de esas dos redes de seguridad). Nunca nos
            // esperamos a nosotros mismos: si Close() corre desde dentro del
            // handler de Disconnected (que se dispara en este mismo reader
            // thread), reader == Thread.CurrentThread y Join colgaria para
            // siempre.
            if (reader != null && reader != Thread.CurrentThread)
            {
                reader.Join(2000);
            }
        }
    }
}
