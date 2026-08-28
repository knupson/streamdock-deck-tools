// Teclas de Discord: enmudecer los auriculares y compartir pantalla.
//
// Las dos comparten una sola sesion; ver DiscordService. Lo que cada una
// aporta es el comando que manda y el tile que dibuja.
//
// Task 8 (desvio autorizado sobre el borrador original): en vez de esperar al
// tick de un segundo para enterarse de un cambio, las acciones se suscriben a
// DiscordService.Changed. Eso es lo que hace que soltar el mute DESDE Discord
// se refleje en la tecla sin que el usuario tenga que esperar ni tocar nada -
// justo lo que el plugin que esto reemplaza nunca hizo.
//
// Changed dispara desde threads de fondo (el reader del RPC, el thread de
// Attach). Dibujar toca GDI+ y el socket del host, asi que el handler nunca
// dibuja directo: encola el redibujo con Host.Invoke, que lo pasa al thread
// que ya es dueno de ambas cosas. DiscordService.Announce ya aisla una
// excepcion por suscriptor (no tira abajo al proceso ni a la otra tecla), pero
// eso es un backstop, no una excusa para no cuidar este handler.

using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamDeckAudio
{
    internal abstract class DiscordActionBase : ActionBase
    {
        private readonly Dictionary<string, string> _rendered =
            new Dictionary<string, string>(StringComparer.Ordinal);

        // Un solo suscriptor por instancia de accion, sin importar cuantas
        // teclas tenga activas: se prende con la primera que aparece y se
        // apaga con la ultima que desaparece, igual que Contexts se vacia.
        private bool _subscribed;

        // Fix final, Important 4: el mensaje de una alta fallida (o de una
        // REalta fallida, mientras la credencial vieja sigue "autorizada")
        // solo vivia en un Log.Write -- el panel se quedaba mudo. Guardado por
        // instancia de accion, no por context: son las dos unicas que llaman a
        // Enroll con el mismo par de campos del panel, asi que un intento
        // vale para toda la accion. Solo se toca desde el thread del host (el
        // callback de "authorize" via Host.Invoke, y PushStatus), nunca desde
        // el thread de fondo que corre Enroll.
        private string _lastError;

        protected override bool DefaultsToKnob { get { return false; } }
        public override bool WantsTick { get { return Contexts.Count > 0; } }

        public override void WillAppear(string context, Dictionary<string, object> settings, string controller)
        {
            base.WillAppear(context, settings, controller);

            if (!_subscribed)
            {
                DiscordService.Changed += OnChanged;
                _subscribed = true;
            }
        }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _rendered.Remove(context);

            // Sin la ultima tecla no queda nada que redibujar: desuscribirse
            // evita un handler colgado que redibuja teclas que ya no existen
            // (y que, en Render, ya no encontraria contexto que actualizar,
            // pero seguiria corriendo por cada evento de Discord para siempre).
            if (_subscribed && Contexts.Count == 0)
            {
                DiscordService.Changed -= OnChanged;
                _subscribed = false;
            }
        }

        // Corre en el thread que haya disparado Changed (Attach, Detach, o el
        // reader del RPC via los handlers de evento) - nunca en el del host.
        // Por eso esto no toca Contexts ni dibuja nada directamente: solo
        // encola el trabajo real para que corra donde SetImage es seguro.
        private void OnChanged()
        {
            Host.Invoke(delegate
            {
                foreach (string context in Contexts.Keys.ToList()) Render(context);
            });
        }

        // El tick sirve para dos cosas: empujar la reconexion y ser red de
        // seguridad barata si algun evento de Discord se perdiera. El dedupe
        // de Render hace que un tick redundante (lo normal, si Changed ya
        // redibujo) no cueste nada.
        public override void Tick()
        {
            DiscordService.Poke();
            foreach (string context in Contexts.Keys.ToList()) Render(context);
        }

        public override void PropertyInspectorAppeared(string context)
        {
            PushStatus(context);
        }

        // "authorize" corre Enroll en un thread de fondo porque Enroll BLOQUEA
        // mientras Discord muestra el dialogo de consentimiento - potencialmente
        // varios segundos. Correrlo aca (en el thread que bombea mensajes del
        // host) congelaria el plugin entero: ninguna otra tecla respondería
        // hasta que el usuario terminara con el dialogo. Host.Invoke trae el
        // resultado de vuelta al thread dueno del socket antes de tocarlo o de
        // redibujar.
        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            switch (Json.Str(payload, "command"))
            {
                case "getStatus":
                    PushStatus(context);
                    return;

                case "authorize":
                    string clientId = Json.Str(payload, "clientId");
                    string secret = Json.Str(payload, "clientSecret");

                    var worker = new System.Threading.Thread(() =>
                    {
                        string error;
                        bool ok = DiscordAuth.Enroll(clientId, secret, out error);

                        // Fix final, Important 2: corre ANTES de Host.Invoke,
                        // en este mismo thread de fondo ("discord-enroll"), a
                        // proposito -- Reauthorized() puede llamar a Detach(),
                        // que hace Close() con join acotado (hasta 2s) sobre
                        // el reader viejo. Ese bloqueo tiene que pasar lejos
                        // del thread dueno del socket del host, exactamente
                        // la misma razon por la que Enroll ya corre aca y no
                        // en el thread de mensajes.
                        if (ok) DiscordService.Reauthorized();

                        Host.Invoke(() =>
                        {
                            if (!ok) Log.Write("Discord: alta fallida: " + error);
                            _lastError = ok ? null : error;
                            PushStatus(context);
                            Render(context);
                        });
                    }) { IsBackground = true, Name = "discord-enroll" };
                    worker.Start();
                    return;
            }

            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        // El client secret nunca vuelve al panel: PushStatus solo manda si hay
        // credencial y cuando vence, nada que identifique la credencial misma.
        // DiscordAuth.Load() es puro (fix final, Important 1): llamarlo aca --
        // cada vez que se abre el panel, o despues de cada intento de alta --
        // ya no reapunta las credenciales de renovacion solo por leer el store.
        private void PushStatus(string context)
        {
            DiscordToken token = DiscordAuth.Load();
            string status = token == null ? "sin autorizar" : "autorizado";
            string expires = token == null ? string.Empty : token.ExpiresUtc.ToLocalTime().ToString("g");

            // Fix final, Important 4: _lastError viene de DiscordAuth.Enroll,
            // que a su vez lo arma solo a partir del cuerpo de RESPUESTA de
            // Discord (rpc.Request/Exchange) -- nunca del cuerpo de REQUEST,
            // que es el unico lugar donde viaja el client secret (ver el
            // comentario sobre esto en DiscordAuth.PostToken). Ningun secreto
            // puede llegar a este JSON por esta via.
            string error = _lastError ?? string.Empty;

            Host.SendToPropertyInspector(context, Uuid,
                "{\"status\":\"" + Json.Escape(status) + "\",\"expires\":\"" + Json.Escape(expires) +
                "\",\"error\":\"" + Json.Escape(error) + "\"}");
        }

        protected abstract string Artwork();
        protected abstract string Caption();

        // Fix final, MINOR: default compartido por las dos teclas mientras
        // Discord esta cerrado o sin permiso. ScreenShareAction lo overridea
        // con "share-off" -- sin esto, esa tecla dibujaba auriculares tachados
        // (el arte de la OTRA accion) cada vez que Discord estaba cerrado,
        // porque Render() tenia "headphones-deaf" a fuego para las dos.
        protected virtual string OfflineArtwork() { return "headphones-deaf"; }

        protected override void Render(string context)
        {
            try
            {
                string art;
                string caption;

                switch (DiscordService.State)
                {
                    case DiscordLink.NoDiscord:     art = OfflineArtwork(); caption = "Sin Discord";  break;
                    case DiscordLink.NoAuth:        art = OfflineArtwork(); caption = "Sin permiso";  break;
                    case DiscordLink.RenewalFailed: art = OfflineArtwork(); caption = "Sin red";  break;
                    default:                        art = Artwork();         caption = Caption();     break;
                }

                // Redibujar un tile que no cambio inundaria el socket una vez
                // por segundo, por tecla.
                string key = art + "|" + caption;
                string previous;
                if (_rendered.TryGetValue(context, out previous) && previous == key) return;
                _rendered[context] = key;

                // Una sola tinta para toda la fila de texto: siempre el overload
                // de 4 argumentos, nunca el de 5 con bottomColor.
                Host.SetImage(context, KeyImage.Glyph(art, caption, null, OnKnob(context)));
                Host.SetTitle(context, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("DiscordAction.Render fallo: " + ex.Message);
            }
        }
    }

    internal sealed class DiscordDeafenAction : DiscordActionBase
    {
        public override string Uuid { get { return "com.knupwns.audioknob.discorddeafen"; } }

        public override void Press(string context)
        {
            if (DiscordService.State != DiscordLink.Ready) { Host.ShowAlert(context); return; }
            DiscordService.SetDeaf(!DiscordService.Deaf, context, Host.ShowAlert);
        }

        protected override string Artwork()
        {
            return DiscordService.Deaf ? "headphones-deaf" : "headphones";
        }

        protected override string Caption()
        {
            return DiscordService.Deaf ? "Mudo" : "Audio";
        }
    }

    internal sealed class DiscordScreenShareAction : DiscordActionBase
    {
        public override string Uuid { get { return "com.knupwns.audioknob.discordscreenshare"; } }

        public override void Press(string context)
        {
            if (DiscordService.State != DiscordLink.Ready) { Host.ShowAlert(context); return; }
            DiscordService.ToggleScreenShare(context, Host.ShowAlert);
        }

        protected override string Artwork()
        {
            return DiscordService.ScreenSharing ? "share" : "share-off";
        }

        protected override string Caption()
        {
            return DiscordService.ScreenSharing ? "En vivo" : "Pantalla";
        }

        // share-off.png ya existe y es el arte "inactivo" normal de esta
        // tecla; reusarlo tambien para offline evita el auricular tachado
        // ajeno que Render() dibujaba antes para las dos acciones por igual.
        protected override string OfflineArtwork() { return "share-off"; }
    }
}
