// Una conexion de Discord para las dos teclas.
//
// Conectar y autenticar es I/O bloqueante; corre en su propio thread y el
// resultado vuelve por el evento Changed, que las acciones traducen a un
// redibujo desde el hilo del host.
//
// Garantia sobre Changed (fix round 2, Important 4): un suscriptor nunca ve
// State en un valor viejo junto a un Deaf/ScreenSharing ya actualizado. Los
// handlers de evento (OnVoiceSettings/OnScreenShare) solo avisan si State ya
// es Ready; si Discord empuja un evento entre el SUBSCRIBE y que Attach
// termine de marcar la sesion Ready (algo que si pasa: ver el comentario en
// OnVoiceSettings), el campo se actualiza igual pero el aviso se pospone. El
// Announce() final de Attach, cuando pasa a Ready, ya sale con los campos al
// dia -- nunca hace falta un aviso separado para ese primer valor.

using System;
using System.Collections.Generic;
using System.Threading;

namespace StreamDeckAudio
{
    // RenewalFailed agregado en el fix final, MINOR (~150): AccessToken()
    // devolvia null tanto para "nunca hubo credencial" como para "habia una
    // pero la renovacion no se pudo intentar ahora" (red caida, por ejemplo),
    // y las dos colapsaban en NoAuth -- la tecla le pedia reautorizar a un
    // usuario cuya autorizacion estaba perfectamente bien. Ready y NoDiscord no
    // cambian de significado.
    internal enum DiscordLink { NoDiscord, NoAuth, RenewalFailed, Ready }

    internal static class DiscordService
    {
        private static readonly object Gate = new object();
        private static DiscordRpc _rpc;
        private static bool _connecting;
        private static DateTime _nextAttemptUtc = DateTime.MinValue;

        // Volatile: escritos desde el thread de Attach/Detach/los handlers de
        // evento, leidos desde el thread del host dentro de un handler de
        // Changed. DiscordRpc.cs ya marca _pipe/_reader volatile por esta
        // misma razon (ver su comentario); sin esto no hay garantia de que el
        // hilo del host vea la escritura mas reciente, solo funciona "por las
        // dudas" en x64.
        private static volatile DiscordLink _state;
        private static volatile bool _deaf;
        private static volatile bool _screenSharing;

        public static DiscordLink State { get { return _state; } private set { _state = value; } }
        public static bool Deaf { get { return _deaf; } private set { _deaf = value; } }
        public static bool ScreenSharing { get { return _screenSharing; } private set { _screenSharing = value; } }

        public static event Action Changed;

        // Fix round 2, Critical 2: Changed es un multicast delegate invocado
        // desde threads de fondo (Attach, Detach, los handlers de evento).
        // Antes de este fix, un suscriptor que tirara (por ejemplo Task 8
        // redibujando una tecla con una imagen que falta) se propagaba fuera
        // de Announce en ese thread de fondo -- y .NET mata el proceso entero
        // ante una excepcion no atrapada en CUALQUIER thread, no solo el
        // principal. Peor: al ser un solo delegate multicast, un suscriptor
        // que tira temprano evita que los suscriptores siguientes (la otra
        // tecla) se enteren de nada. GetInvocationList() + try/catch por
        // suscriptor aisla a cada uno: uno que tira se loguea y no frena a
        // los demas ni al proceso.
        private static void Announce()
        {
            Action handler = Changed;
            if (handler == null) return;

            foreach (Action subscriber in handler.GetInvocationList())
            {
                try
                {
                    subscriber();
                }
                catch (Exception ex)
                {
                    Log.Write("Discord: un suscriptor de Changed tiro: " + ex.Message);
                }
            }
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

        // Fix round 2, Critical 1: seam de test. Forzar la ventana real (el
        // pipe se cae entre que Subscribe confirma y que Attach publica _rpc)
        // requeriria matar a Discord en el instante exacto entre esas dos
        // lineas, algo que no se puede cronometrar de forma confiable desde
        // afuera del proceso. Este hook, seteado por DiscordProbe.exe
        // raceattach, simula el mismo efecto que tendria un Disconnected real
        // llegando justo ahi: dispara la misma logica de "dead" y el mismo
        // Detach que usaria el camino real, asi que ejercita el fix de verdad
        // en vez de solo simularlo por fuera.
        //
        // Fix round 3: esto era un bool que Attach mismo escribia de vuelta a
        // false (one-shot). Eso hacia que el compilador SIEMPRE lo viera
        // asignado dentro de este archivo, asi que CS0649 nunca avisaba --
        // nada probaba que el hook no pudiera dispararse en produccion una vez
        // que Task 8 agregue este archivo a las fuentes de DeckTools.exe. Un
        // Func<bool> que este archivo solo LEE (nunca asigna) restaura esa
        // garantia exactamente como DiscordAuth.TestStoreOverride: el campo
        // queda sin asignar en cualquier build que no incluya
        // tools/DiscordProbe.cs (que es quien lo asigna), y CS0649 vuelve a
        // avisarlo -- el compilador demostrando la ausencia, no el build
        // script. El one-shot ahora vive del lado del probe (ver RaceAttach),
        // no aca: este archivo solo invoca el delegado, nunca lo apaga.
        internal static Func<bool> TestSimulateDisconnectBeforePublish;

        private static void Attach()
        {
            // Declarado ANTES del try (no adentro, como en las rondas de fix
            // anteriores de este mismo codebase): si algo entre Connect() y
            // Ready tira -- Subscribe rechazado, timeout -- el catch de abajo
            // necesita poder cerrar esta conexion. Una var declarada dentro
            // del try no es visible en su catch; dejarla afuera es lo unico
            // que permite cerrar sin duplicar el try por cada paso.
            DiscordRpc rpc = null;

            // Fix round 2, Critical 1: sin bug hace falta, solo timing. Si el
            // pipe se cae DESPUES de que Connect/AUTHENTICATE/Subscribe ya
            // terminaron pero ANTES de que este metodo llegue al lock que
            // publica _rpc, el reader de DiscordRpc ya disparo Disconnected
            // -> Detach() sobre una conexion que _rpc todavia no conoce (sigue
            // null), asi que Detach no tiene nada que limpiar. Este metodo,
            // sin saberlo, publicaria igual una conexion ya muerta, y como
            // Disconnected es un evento de un solo disparo por instancia,
            // jamas volveria a avisar: Poke() vuelve temprano para siempre
            // (_rpc != null), Send() sigue mandando comandos a un cadaver, y
            // solo un reinicio del proceso lo arregla -- exactamente como
            // moria el plugin que este reemplaza. `dead` es local a este
            // intento (no a la clase: dos Attach() nunca corren a la vez,
            // pero cada uno necesita su propia bandera) y solo se toca bajo
            // Gate, tanto desde el closure de Disconnected como aca abajo, en
            // el mismo lock en el que se decide publicar -- eso es lo que
            // hace que la escritura de un thread sea visible para la lectura
            // del otro.
            bool dead = false;

            try
            {
                bool hadStoredCredential;
                string token = DiscordAuth.AccessToken(out hadStoredCredential);
                if (string.IsNullOrEmpty(token))
                {
                    // hadStoredCredential distingue las dos formas de "no hay
                    // token": si nunca hubo credencial guardada, es NoAuth de
                    // verdad (hay que autorizar). Si SI la habia pero no se
                    // pudo renovar ahora (red caida, por ejemplo), reautorizar
                    // no arregla nada -- RenewalFailed evita mandar al usuario
                    // a resolver un problema que no tiene.
                    Fail(hadStoredCredential ? DiscordLink.RenewalFailed : DiscordLink.NoAuth);
                    return;
                }

                // Guard, MINOR (~153): un record del store escrito ANTES de que
                // Task 6 agregara clientId/clientSecret da un token valido pero
                // con ClientId null (Load() no inventa uno), asi que
                // AccessToken() nunca tiene nada que aplicarle a este estatico.
                // Sin este chequeo, Connect(null) manda un client_id vacio,
                // Discord contesta CLOSE, y el catch de mas abajo lo reporta
                // como NoDiscord -- "Discord no esta corriendo" para lo que en
                // realidad es una credencial legada sin client id: el usuario
                // termina buscando el problema en el lugar equivocado.
                string clientId = DiscordAuth.ClientId;
                if (string.IsNullOrEmpty(clientId))
                {
                    Log.Write("Discord: token sin client id asociado (store legado); hace falta reautorizar");
                    Fail(DiscordLink.NoAuth);
                    return;
                }

                rpc = new DiscordRpc();
                if (!rpc.Connect(clientId)) { Fail(DiscordLink.NoDiscord); return; }

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

                // Captura la instancia, no el campo _rpc: si dos ciclos de
                // Connect/Close se pisaran (no deberia pasar, Poke() serializa
                // con _connecting, pero el closure no puede depender de eso),
                // este handler tiene que actuar sobre SU conexion, nunca sobre
                // la que sea que _rpc apunte en el momento en que finalmente
                // dispare.
                rpc.Disconnected += () =>
                {
                    lock (Gate) { dead = true; }
                    Detach(rpc);
                };

                // VOICE_SETTINGS_UPDATE es un evento documentado y es lo unico
                // que la tecla de deafen necesita: sin el no hay forma de saber
                // el estado real de mute, asi que un rechazo aca SI aborta
                // Attach (cae al catch de afuera, State pasa a NoDiscord --
                // y ese catch ahora cierra rpc, ver el comentario de arriba).
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

                // Seam de test para Critical 1 (ver el comentario del campo):
                // solo LEE el hook, nunca lo apaga -- el one-shot es
                // responsabilidad de quien lo asigna (RaceAttach). Copiado a
                // una local antes de invocarlo por el mismo motivo que
                // DiscordRpc copia _pipe a una local: evita una carrera si
                // alguien lo reasignara mientras esto corre.
                Func<bool> simulateDisconnect = TestSimulateDisconnectBeforePublish;
                if (simulateDisconnect != null && simulateDisconnect())
                {
                    lock (Gate) { dead = true; }
                    Detach(rpc);
                }

                // Publicar solo si esta conexion sigue viva. `dead` y esta
                // asignacion se leen/escriben bajo el MISMO lock: si el
                // closure de Disconnected ya corrio (real o simulado) y marco
                // dead=true, no publicamos un cadaver -- lo cerramos (Detach ya
                // lo hizo, pero Close() es idempotente, ver DiscordRpc) y
                // pasamos por Fail como cualquier otro intento fallido.
                //
                // Fix round 3, N1: State = Ready se decide DENTRO de este
                // mismo lock, junto con _rpc = rpc, no despues de soltarlo.
                // Antes vivia afuera: si el pipe moria justo despues de que
                // este lock se soltara pero antes de esa linea, Detach ya
                // corria completo (_rpc = null, State = NoDiscord, avisa) y
                // esta linea lo pisaba con Ready a continuacion -- quedaba
                // State == Ready con _rpc == null: la tecla se dibuja
                // "conectada", un toque cae en Send -> Poke sin hacer nada
                // visible, y sigue asi hasta el proximo intento (>=5s). Se
                // autocuraba solo, pero mostraba mal durante ese tramo.
                // Publicar los dos campos bajo el mismo lock que Detach usa
                // para leerlos hace que quien sea que tome el lock despues
                // vea una version consistente, nunca a mitad de camino.
                bool published;
                lock (Gate)
                {
                    if (dead)
                    {
                        published = false;
                    }
                    else
                    {
                        _rpc = rpc;
                        _connecting = false;
                        State = DiscordLink.Ready;
                        published = true;
                    }
                }

                if (!published)
                {
                    Log.Write("Discord: la conexion murio antes de publicarse, no se activa (ver Critical 1)");
                    rpc.Close();
                    Fail(DiscordLink.NoDiscord);
                    return;
                }

                Log.Write("Discord: sesion lista");
                Announce();
            }
            catch (Exception ex)
            {
                Log.Write("Discord: conexion fallo: " + ex.Message);

                // Cubre dos casos con la misma linea: (a) el leak de la ronda
                // de fix anterior (rpc conectado que nunca llego a _rpc), y
                // (b) la segunda puerta de Critical 1 -- un suscriptor de
                // Changed que tira DESPUES de publicar (_rpc ya es rpc aca).
                // Sin este null-out, (b) deja _rpc apuntando a una conexion
                // que este catch va a cerrar dos lineas mas abajo: el mismo
                // wedge permanente que Critical 1 describe, solo que
                // disparado por un suscriptor en vez de por el pipe.
                lock (Gate) { if (_rpc == rpc) _rpc = null; }
                if (rpc != null) rpc.Close();
                Fail(DiscordLink.NoDiscord);
            }
        }

        // Backoff para no golpear el pipe una vez por segundo cuando Discord
        // esta cerrado, que es la mayor parte del tiempo.
        //
        // MINOR (~293): la escritura de State ahora vive DENTRO del mismo lock
        // que decide si cambio, igual que el round 3 ya hizo con la de Attach.
        // Ningun torn view era alcanzable con la version anterior (State es
        // volatile y el unico lector fuera de lock solo compara, nunca decide
        // sobre un valor a medio escribir), pero mezclar "escribe bajo lock" en
        // un lugar y "escribe afuera" en otro del mismo archivo es la clase de
        // inconsistencia que hace que la proxima ronda de fix tenga que volver
        // a probar lo que ya deberia estar asentado.
        private static void Fail(DiscordLink state)
        {
            bool changed;
            lock (Gate)
            {
                _connecting = false;
                _nextAttemptUtc = DateTime.UtcNow.AddSeconds(15);
                changed = (State != state);
                if (changed) State = state;
            }
            if (changed) Announce();
        }

        // Recibe la instancia por parametro (fix round 2, Critical 1), no lee
        // _rpc: puede dispararse para una conexion que _rpc todavia no conoce
        // (la ventana de la publicacion tardia) o para la que esta viva hoy.
        private static void Detach(DiscordRpc which)
        {
            // Fix round 3, N3: el match (wasLive) se decide UNA vez, bajo
            // lock, y todo lo que sigue depende de ese resultado ya fijo. La
            // version anterior guardaba _rpc==which para decidir si limpiar
            // el campo, pero despues mutaba State y avisaba SIN condicion --
            // la misma clase de bug que Critical 1, aplicada a medias: un
            // Detach(A) tardio (la ventana de publicacion, u otra carrera
            // rara) que ya no coincide con el _rpc actual igual pisaba el
            // State de una sesion B que si esta viva, y avisaba una
            // desconexion que no paso.
            // MINOR (~333): State se escribe DENTRO de este mismo lock ahora,
            // no despues de soltarlo -- misma disciplina que Fail() y que el
            // round 3 ya le aplico a Attach. Ningun torn view era alcanzable
            // antes (mismo razonamiento que en Fail()), pero consistencia
            // entre los tres sitios que escriben State es lo que hace que la
            // regla sea facil de verificar de un vistazo.
            bool wasLive;
            lock (Gate)
            {
                wasLive = (_rpc == which);
                if (wasLive)
                {
                    _rpc = null;
                    _nextAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                    State = DiscordLink.NoDiscord;
                }
            }

            // Cerrar SIEMPRE, coincida o no (fix round 2, Important 3): esta
            // instancia especifica ya esta muerta de todas formas, y dejarla
            // sin cerrar es el mismo leak de handle sin importar si era o no
            // la conexion "oficial" en _rpc.
            which.Close();

            // Si no coincide, ya hicimos lo unico que le correspondia a ESTA
            // instancia (cerrarla) y no hay nada mas que decir sobre el
            // estado de la sesion -- que es de otra conexion, no de esta.
            if (!wasLive) return;

            Log.Write("Discord: sesion caida");
            Announce();
        }

        /// <summary>
        /// Fix final, Important 2: un Enroll() exitoso antes no le decia nada a
        /// la sesion -- solo redibujaba el panel. Con sesion viva, la conexion
        /// seguia usando el token viejo hasta que algo la reiniciara; con la
        /// sesion caida (alta inicial), el ultimo Fail(NoAuth) habia dejado
        /// hasta 15s de backoff, asi que la tecla se quedaba en "Sin permiso"
        /// hasta 15s despues de una autorizacion que ya habia salido bien --
        /// la primera impresion del feature. Llamar esto desde el callback de
        /// alta arregla las dos ramas: limpia el backoff bajo Gate, tira abajo
        /// cualquier _rpc vivo reusando Detach (mismo camino de cierre que ya
        /// evita el leak de handle/thread), y pokea una vez para que Attach
        /// arranque de cero con la credencial recien guardada.
        /// </summary>
        public static void Reauthorized()
        {
            DiscordRpc current;
            lock (Gate) { current = _rpc; }

            // Detach cierra la conexion vieja (Close(), sin leak) y limpia
            // _rpc/State bajo Gate; si la sesion ya estaba caida no hay nada
            // que tirar abajo. OJO con el orden: Detach, cuando SI habia una
            // conexion viva, fija su propio _nextAttemptUtc a +5s (su backoff
            // normal de reconexion) -- si el reset de abajo corriera ANTES de
            // esto, Detach lo pisaria y la reautorizacion con sesion viva
            // terminaria esperando esos 5s de todas formas, exactamente el
            // retraso que este metodo existe para eliminar.
            if (current != null) Detach(current);

            // Limpia el backoff DESPUES de Detach, no antes: cubre tanto el
            // +5s que Detach acaba de fijar (sesion viva) como el +15s que un
            // Fail(NoAuth) anterior pudo haber dejado (alta inicial, sesion ya
            // caida) -- las dos ramas de Important 2 quedan sin espera extra.
            lock (Gate) { _nextAttemptUtc = DateTime.MinValue; }

            Poke();
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
        //
        // El guard de State == Ready es Important 4 (fix round 2): Discord
        // puede empujar el valor actual apenas el SUBSCRIBE se confirma, y el
        // fetch explicito de GET_VOICE_SETTINGS de mas abajo en Attach corre
        // con el mismo riesgo -- ambos pueden llamar a este handler ANTES de
        // que Attach marque State = Ready. El campo se actualiza igual (nunca
        // hay que perder el dato), pero el aviso se pospone: no tiene sentido
        // avisar "cambio algo" con un State todavia viejo, y el Announce()
        // final de Attach al pasar a Ready ya sale con este campo al dia. Una
        // vez que la sesion esta Ready de verdad, este mismo handler sigue
        // avisando normalmente en cada evento real (asi se confirmo en vivo:
        // ver el reporte de Task 6).
        private static void OnVoiceSettings(Dictionary<string, object> data)
        {
            if (data == null) return;
            Deaf = Json.Bool(data, "deaf", Deaf);
            if (State == DiscordLink.Ready) Announce();
        }

        // Misma regla que OnVoiceSettings: solo asignar y avisar, nunca llamar
        // ni bloquear, y el mismo guard de State == Ready (Important 4).
        // Corre en el mismo thread lector.
        //
        // "active" NO esta confirmado contra un SCREENSHARE_STATE_UPDATE real:
        // es un nombre de campo tomado de un bundle de un plugin de terceros,
        // no de documentacion de Discord (el evento en si no tiene contrato
        // documentado). Task 6 lo intento verificar dos formas: (1) watch
        // corriendo con Discord abierto pero sin llamada de voz activa, y (2)
        // un TOGGLE_SCREENSHARE disparado por RPC fuera de una llamada -- ese
        // comando volvio sin error pero con data=null y sin disparar el
        // evento, porque Discord no tiene nada que compartir sin estar en un
        // canal de voz. Ninguno de los dos caminos hizo aparecer el evento de
        // verdad, asi que "active" sigue siendo una suposicion sin confirmar.
        // Verificar esto requiere una llamada de voz real con pantalla
        // compartida; hasta entonces no tratar este nombre como confirmado.
        private static void OnScreenShare(Dictionary<string, object> data)
        {
            if (data == null) return;
            ScreenSharing = Json.Bool(data, "active", ScreenSharing);
            if (State == DiscordLink.Ready) Announce();
        }

        // context/onFailed dejan que el llamador (una accion, que tiene Host)
        // decida como avisar sin que este archivo sepa nada de IHost -- fix
        // final, Important 3. Mantenido deliberadamente al ras: no arma una
        // cola de notificaciones, solo pasa el context de vuelta hasta el
        // unico callback que la accion ya tenia (Host.ShowAlert).
        public static void SetDeaf(bool on, string context, Action<string> onFailed)
        {
            Send(context, onFailed, rpc => rpc.Request("SET_VOICE_SETTINGS",
                                    new Dictionary<string, object> { { "deaf", on } }, 5000));
        }

        public static void ToggleScreenShare(string context, Action<string> onFailed)
        {
            Send(context, onFailed, rpc => rpc.Request("TOGGLE_SCREENSHARE", null, 5000));
        }

        // Los comandos salen en un thread aparte: el hilo del host no puede
        // quedarse esperando a Discord.
        //
        // Fix final, Important 3: antes el catch solo logueaba. Eso alcanzaba
        // cuando el PIPE moria (Disconnected -> Detach ya reconstruye), pero no
        // cuando Discord contesta un frame ERROR sobre un pipe que sigue
        // abierto -- el disparador obvio es el access token venciendo a mitad
        // de sesion, porque Attach solo lo busca una vez, asi que una sesion
        // que sigue Ready mas alla de la vida del token nunca lo renueva.
        // UnwrapReply tira DiscordRpcException en ese caso, el catch de antes
        // se limitaba a loguear, _rpc seguia sin null, Poke() seguia volviendo
        // temprano para siempre, y State se quedaba en Ready: la tecla dibuja
        // "conectada" y no hace nada, exactamente la forma de bug que esta
        // rama existe para eliminar. Detach(rpc) tira la sesion abajo por el
        // mismo camino que ya usa Disconnected (sin leak de handle/thread), asi
        // que el proximo Poke la reconstruye con un token recien pedido.
        private static void Send(string context, Action<string> onFailed, Action<DiscordRpc> work)
        {
            DiscordRpc rpc;
            lock (Gate) { rpc = _rpc; }
            if (rpc == null) { Poke(); return; }

            var worker = new Thread(() =>
            {
                try
                {
                    work(rpc);
                }
                catch (DiscordRpcException ex)
                {
                    Log.Write("Discord: comando fallo, se cae la sesion: " + ex.Message);
                    Detach(rpc);
                    if (onFailed != null) onFailed(context);
                }
                catch (Exception ex)
                {
                    // No es un fallo de protocolo (WriteFrameOrThrow ya
                    // traduce los casos de pipe muerto a DiscordRpcException,
                    // ver su comentario en DiscordRpc.cs) -- se avisa igual,
                    // pero no hay motivo para asumir la sesion muerta, asi que
                    // se deja como esta.
                    Log.Write("Discord: comando fallo: " + ex.Message);
                    if (onFailed != null) onFailed(context);
                }
            }) { IsBackground = true };
            worker.Start();
        }
    }
}
