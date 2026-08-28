// Una conexion de Discord para las dos teclas.
//
// Conectar y autenticar es I/O bloqueante; corre en su propio thread y el
// resultado vuelve por el evento Changed, que las acciones traducen a un
// redibujo desde el hilo del host.

using System;
using System.Collections.Generic;
using System.Threading;

namespace StreamDeckAudio
{
    internal enum DiscordLink { NoDiscord, NoAuth, Ready }

    internal static class DiscordService
    {
        private static readonly object Gate = new object();
        private static DiscordRpc _rpc;
        private static bool _connecting;
        private static DateTime _nextAttemptUtc = DateTime.MinValue;

        public static DiscordLink State { get; private set; }
        public static bool Deaf { get; private set; }
        public static bool ScreenSharing { get; private set; }

        public static event Action Changed;

        private static void Announce()
        {
            Action handler = Changed;
            if (handler != null) handler();
        }

        /// <summary>
        /// Intento de conexion. Idempotente y barato: lo llama el tick de cada
        /// segundo, y sale enseguida si ya hay sesion o si toca esperar.
        /// </summary>
        public static void Poke()
        {
            lock (Gate)
            {
                if (_rpc != null || _connecting) return;
                if (DateTime.UtcNow < _nextAttemptUtc) return;
                _connecting = true;
            }

            var worker = new Thread(Attach) { IsBackground = true, Name = "discord-attach" };
            worker.Start();
        }

        private static void Attach()
        {
            try
            {
                string token = DiscordAuth.AccessToken();
                if (string.IsNullOrEmpty(token)) { Fail(DiscordLink.NoAuth); return; }

                var rpc = new DiscordRpc();
                if (!rpc.Connect(DiscordAuth.ClientId)) { Fail(DiscordLink.NoDiscord); return; }

                try
                {
                    rpc.Request("AUTHENTICATE",
                                new Dictionary<string, object> { { "access_token", token } }, 10000);
                }
                catch (Exception ex)
                {
                    Log.Write("Discord: AUTHENTICATE fallo: " + ex.Message);
                    rpc.Close();
                    Fail(DiscordLink.NoAuth);
                    return;
                }

                rpc.Disconnected += Detach;

                // VOICE_SETTINGS_UPDATE es un evento documentado y es lo unico
                // que la tecla de deafen necesita: sin el no hay forma de saber
                // el estado real de mute, asi que un rechazo aca SI aborta
                // Attach (cae al catch de afuera, State pasa a NoDiscord).
                rpc.Subscribe("VOICE_SETTINGS_UPDATE", OnVoiceSettings);

                // SCREENSHARE_STATE_UPDATE no tiene contrato documentado en la
                // API de Discord (a diferencia de VOICE_SETTINGS_UPDATE); si
                // Discord lo renombra o lo saca, Subscribe tira igual que con
                // cualquier evento rechazado. Eso NO puede tirar abajo la sesion
                // entera -- la tecla de deafen no depende de screenshare para
                // nada -- asi que esta suscripcion tiene su propio try/catch:
                // se loguea, ScreenSharing se queda en su default (false), y el
                // resto de Attach sigue como si nada.
                try
                {
                    rpc.Subscribe("SCREENSHARE_STATE_UPDATE", OnScreenShare);
                }
                catch (Exception ex)
                {
                    Log.Write("Discord: SCREENSHARE_STATE_UPDATE no se pudo suscribir " +
                              "(evento no documentado, puede haber cambiado de nombre o de forma): " +
                              ex.Message);
                }

                try { OnVoiceSettings(rpc.Request("GET_VOICE_SETTINGS", null, 10000)); }
                catch (Exception) { }

                lock (Gate) { _rpc = rpc; _connecting = false; }
                State = DiscordLink.Ready;
                Log.Write("Discord: sesion lista");
                Announce();
            }
            catch (Exception ex)
            {
                Log.Write("Discord: conexion fallo: " + ex.Message);
                Fail(DiscordLink.NoDiscord);
            }
        }

        // Backoff para no golpear el pipe una vez por segundo cuando Discord
        // esta cerrado, que es la mayor parte del tiempo.
        private static void Fail(DiscordLink state)
        {
            lock (Gate) { _connecting = false; _nextAttemptUtc = DateTime.UtcNow.AddSeconds(15); }
            if (State == state) return;
            State = state;
            Announce();
        }

        private static void Detach()
        {
            lock (Gate) { _rpc = null; _nextAttemptUtc = DateTime.UtcNow.AddSeconds(5); }
            State = DiscordLink.NoDiscord;
            Log.Write("Discord: sesion caida");
            Announce();
        }

        // Corre en el thread lector de DiscordRpc (ver el comentario sobre
        // Dispatch en DiscordRpc.cs), no en el del host. Por eso este handler
        // solo puede asignar campos y avisar por Changed: si llamara a Request
        // (o a cualquier metodo de DiscordService que termine en Request, como
        // SetDeaf/ToggleScreenShare via Send en OTRO thread esta bien, pero
        // DIRECTO desde aca no) se clavaria esperando su propia respuesta hasta
        // el timeout, y mientras tanto ninguna otra respuesta pendiente en esa
        // conexion se despacha -- incluida la de quien esta esperando adentro
        // de Request ahora mismo.
        private static void OnVoiceSettings(Dictionary<string, object> data)
        {
            if (data == null) return;
            Deaf = Json.Bool(data, "deaf", Deaf);
            Announce();
        }

        // Misma regla que OnVoiceSettings: solo asignar y avisar, nunca llamar
        // ni bloquear. Corre en el mismo thread lector.
        private static void OnScreenShare(Dictionary<string, object> data)
        {
            if (data == null) return;
            ScreenSharing = Json.Bool(data, "active", ScreenSharing);
            Announce();
        }

        public static void SetDeaf(bool on)
        {
            Send(rpc => rpc.Request("SET_VOICE_SETTINGS",
                                    new Dictionary<string, object> { { "deaf", on } }, 5000));
        }

        public static void ToggleScreenShare()
        {
            Send(rpc => rpc.Request("TOGGLE_SCREENSHARE", null, 5000));
        }

        // Los comandos salen en un thread aparte: el hilo del host no puede
        // quedarse esperando a Discord.
        private static void Send(Action<DiscordRpc> work)
        {
            DiscordRpc rpc;
            lock (Gate) { rpc = _rpc; }
            if (rpc == null) { Poke(); return; }

            var worker = new Thread(() =>
            {
                try { work(rpc); }
                catch (Exception ex) { Log.Write("Discord: comando fallo: " + ex.Message); }
            }) { IsBackground = true };
            worker.Start();
        }
    }
}
