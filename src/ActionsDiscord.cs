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

        protected abstract string Artwork();
        protected abstract string Caption();

        protected override void Render(string context)
        {
            try
            {
                string art;
                string caption;

                switch (DiscordService.State)
                {
                    case DiscordLink.NoDiscord: art = "headphones-deaf"; caption = "Sin Discord"; break;
                    case DiscordLink.NoAuth:    art = "headphones-deaf"; caption = "Sin permiso"; break;
                    default:                    art = Artwork();         caption = Caption();     break;
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
            DiscordService.SetDeaf(!DiscordService.Deaf);
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
            DiscordService.ToggleScreenShare();
        }

        protected override string Artwork()
        {
            return DiscordService.ScreenSharing ? "share" : "share-off";
        }

        protected override string Caption()
        {
            return DiscordService.ScreenSharing ? "En vivo" : "Pantalla";
        }
    }
}
