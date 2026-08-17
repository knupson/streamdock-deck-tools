// StreamDock plugin backend.
//
// The host launches this executable with
//     -port <n> -pluginUUID <uuid> -registerEvent registerPlugin -info <json>
// and expects a WebSocket client on ws://127.0.0.1:<port> that registers with
//     {"event": "<registerEvent>", "uuid": "<pluginUUID>"}
// Everything after that is the Elgato-compatible event protocol the Redragon /
// MiraBox StreamDock software implements.
//
// One process serves every action in the manifest; messages carry the action's
// UUID and are routed to the matching handler.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StreamDeckAudio
{
    internal interface IHost
    {
        void SetImage(string context, string dataUrl);
        void SetTitle(string context, string title);
        void SetState(string context, int state);
        void ShowAlert(string context);
        void SendToPropertyInspector(string context, string action, string payloadJson);

        /// <summary>Persists an action instance's settings back into the profile.</summary>
        void SetSettings(string context, Dictionary<string, object> settings);

        /// <summary>
        /// Queues work onto the thread every handler runs on. Anything slow - a
        /// ping, a shell-out - belongs on a background thread, and the result has
        /// to come back through here before it touches COM, GDI+ or the socket.
        /// </summary>
        void Invoke(Action work);
    }

    internal abstract class ActionBase
    {
        protected IHost Host;

        protected readonly Dictionary<string, Dictionary<string, object>> Contexts =
            new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);

        // Knob screens hide their bottom strip at the deck's viewing angle, so the
        // tiles are composed differently there. Which one an instance sits on comes
        // from the host, not from the manifest: an action offered for both can be
        // on either.
        private readonly Dictionary<string, bool> _onKnob = new Dictionary<string, bool>(StringComparer.Ordinal);

        public abstract string Uuid { get; }

        /// <summary>Whether this action needs the once-a-second tick.</summary>
        public virtual bool WantsTick { get { return false; } }

        /// <summary>
        /// Whether this action needs the ten-times-a-second tick as well. Only
        /// worth asking for while something is genuinely moving on screen: it is
        /// the same pump every other action shares.
        /// </summary>
        public virtual bool WantsFastTick { get { return false; } }

        public void Attach(IHost host) { Host = host; }

        public virtual void WillAppear(string context, Dictionary<string, object> settings, string controller)
        {
            Contexts[context] = settings ?? new Dictionary<string, object>();
            _onKnob[context] = string.IsNullOrEmpty(controller)
                ? DefaultsToKnob
                : !controller.Equals("Keypad", StringComparison.OrdinalIgnoreCase);
            Render(context);
        }

        public virtual void WillDisappear(string context)
        {
            Contexts.Remove(context);
            _onKnob.Remove(context);
        }

        /// <summary>Assumed placement when the host does not say which it is.</summary>
        protected virtual bool DefaultsToKnob { get { return true; } }

        protected bool OnKnob(string context)
        {
            bool knob;
            return _onKnob.TryGetValue(context, out knob) ? knob : DefaultsToKnob;
        }

        public virtual void SettingsChanged(string context, Dictionary<string, object> settings)
        {
            if (settings != null) Contexts[context] = settings;
            Render(context);
        }

        public virtual void Rotate(string context, int ticks) { }
        public virtual void Press(string context) { }
        public virtual void PropertyInspectorAppeared(string context) { }
        public virtual void FromPropertyInspector(string context, Dictionary<string, object> payload) { }
        public virtual void Tick() { }
        public virtual void FastTick() { }

        protected abstract void Render(string context);

        protected Dictionary<string, object> Settings(string context)
        {
            Dictionary<string, object> settings;
            if (context != null && Contexts.TryGetValue(context, out settings)) return settings;
            return new Dictionary<string, object>();
        }

        protected string Str(string context, string key, string fallback)
        {
            string value = Json.Str(Settings(context), key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }

        protected int Int(string context, string key, int fallback)
        {
            return Json.Int(Settings(context), key, fallback);
        }

        protected bool Bool(string context, string key, bool fallback)
        {
            return Json.Bool(Settings(context), key, fallback);
        }
    }

    internal static class PluginHost
    {
        private static readonly BlockingCollection<object> Inbox = new BlockingCollection<object>();
        private static readonly object SendGate = new object();
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        private static readonly Dictionary<string, ActionBase> Router =
            new Dictionary<string, ActionBase>(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> ActionScoped = new HashSet<string>(StringComparer.Ordinal)
        {
            "willAppear", "willDisappear", "didReceiveSettings", "dialRotate", "dialPress",
            "dialDown", "keyDown", "touchTap", "propertyInspectorDidAppear", "sendToPlugin"
        };

        private static ClientWebSocket _socket;
        private static Messenger _messenger;

        // COM, GDI+ and every handler run on this one STA thread: the pump below
        // owns it, and the receive loop only enqueues work.
        [STAThread]
        private static int Main(string[] args)
        {
            Log.Component = "audioknob";
            try
            {
                string port = ArgValue(args, "-port");
                string uuid = ArgValue(args, "-pluginUUID");
                string registerEvent = ArgValue(args, "-registerEvent");
                if (port == null || uuid == null || registerEvent == null)
                {
                    Log.Write("missing launch arguments: " + string.Join(" ", args));
                    return 1;
                }

                Log.Write("starting, port=" + port + " uuid=" + uuid);
                Register(new AudioOutputAction());
                Register(new AppVolumeAction());
                Register(new BrightnessAction());
                Register(new BlackoutAction());
                Register(new SystemMonitorAction());
                Register(new LauncherAction());
                Register(new HostStatusAction());
                Register(new SshAction());
                Register(new RemoteMonitorAction());
                Register(new RustDeskAction());
                Register(new SeparatorAction());
                Register(new MediaPlayerAction());

                Connect(port, uuid, registerEvent);
                StartTicker();
                StartFastTicker();
                Pump();

                Log.Write("socket closed, exiting");
                Monitors.Release();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("FATAL " + ex);
                return 2;
            }
        }

        private static void Register(ActionBase action)
        {
            _messenger = _messenger ?? new Messenger();
            action.Attach(_messenger);
            Router[action.Uuid] = action;
        }

        #region transport

        private static void Connect(string port, string uuid, string registerEvent)
        {
            _socket = new ClientWebSocket();
            _socket.ConnectAsync(new Uri("ws://127.0.0.1:" + port), CancellationToken.None)
                   .GetAwaiter().GetResult();

            Send("{\"event\":\"" + registerEvent + "\",\"uuid\":\"" + Json.Escape(uuid) + "\"}");
            Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
        }

        private static void ReceiveLoop()
        {
            var buffer = new byte[32 * 1024];
            var message = new StringBuilder();
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult result = _socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None)
                        .GetAwaiter().GetResult();

                    if (result.MessageType == WebSocketMessageType.Close) break;

                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    if (!result.EndOfMessage) continue;

                    Inbox.Add(message.ToString());
                    message.Length = 0;
                }
            }
            catch (Exception ex)
            {
                Log.Write("receive loop ended: " + ex.Message);
            }
            Inbox.CompleteAdding();
        }

        // Live tiles refresh from here rather than from their own threads, so
        // every handler keeps running on the STA thread.
        private static void StartTicker()
        {
            var ticker = new Thread(delegate()
            {
                while (!Inbox.IsAddingCompleted)
                {
                    Thread.Sleep(1000);
                    try { Inbox.Add(TickJob); }
                    catch (Exception) { return; } // inbox closed
                }
            });
            ticker.IsBackground = true;
            ticker.Start();
        }

        // A second, faster ticker for anything animating - today only the marquee
        // on the now-playing tile. It is enqueued unconditionally and the pump
        // decides whether anyone wants it, so that the decision is made on the
        // thread that owns the actions' state rather than from here.
        private const int FastTickMs = 100;

        private static void StartFastTicker()
        {
            var ticker = new Thread(delegate()
            {
                while (!Inbox.IsAddingCompleted)
                {
                    Thread.Sleep(FastTickMs);
                    try { Inbox.Add(FastTickJob); }
                    catch (Exception) { return; } // inbox closed
                }
            });
            ticker.IsBackground = true;
            ticker.Start();
        }

        private static readonly object TickJob = new object();
        private static readonly object FastTickJob = new object();

        private static void Pump()
        {
            foreach (object item in Inbox.GetConsumingEnumerable())
            {
                try
                {
                    var work = item as Action;

                    if (ReferenceEquals(item, TickJob)) TickAll();
                    else if (ReferenceEquals(item, FastTickJob)) FastTickAll();
                    else if (work != null) work();
                    else Dispatch((string)item);
                }
                catch (Exception ex)
                {
                    Log.Write("handler error: " + ex);
                }
            }
        }

        private static void TickAll()
        {
            foreach (ActionBase action in Router.Values)
            {
                if (!action.WantsTick) continue;
                try { action.Tick(); }
                catch (Exception ex) { Log.Write(action.Uuid + " tick failed: " + ex.Message); }
            }
        }

        private static void FastTickAll()
        {
            foreach (ActionBase action in Router.Values)
            {
                if (!action.WantsFastTick) continue;
                try { action.FastTick(); }
                catch (Exception ex) { Log.Write(action.Uuid + " fast tick failed: " + ex.Message); }
            }
        }

        private static void Send(string payload)
        {
            lock (SendGate)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text,
                                  true, CancellationToken.None).GetAwaiter().GetResult();
            }
        }

        private sealed class Messenger : IHost
        {
            public void SetImage(string context, string dataUrl)
            {
                if (dataUrl == null) return;
                Send("{\"event\":\"setImage\",\"context\":\"" + Json.Escape(context) +
                     "\",\"payload\":{\"image\":\"" + dataUrl + "\",\"target\":0}}");
            }

            public void SetTitle(string context, string title)
            {
                Send("{\"event\":\"setTitle\",\"context\":\"" + Json.Escape(context) +
                     "\",\"payload\":{\"title\":\"" + Json.Escape(title) + "\",\"target\":0}}");
            }

            public void SetState(string context, int state)
            {
                Send("{\"event\":\"setState\",\"context\":\"" + Json.Escape(context) +
                     "\",\"payload\":{\"state\":" + state.ToString(CultureInfo.InvariantCulture) + "}}");
            }

            public void ShowAlert(string context)
            {
                Send("{\"event\":\"showAlert\",\"context\":\"" + Json.Escape(context) + "\"}");
            }

            public void SendToPropertyInspector(string context, string action, string payloadJson)
            {
                Send("{\"event\":\"sendToPropertyInspector\",\"action\":\"" + Json.Escape(action) +
                     "\",\"context\":\"" + Json.Escape(context) + "\",\"payload\":" + payloadJson + "}");
            }

            public void SetSettings(string context, Dictionary<string, object> settings)
            {
                Send("{\"event\":\"setSettings\",\"context\":\"" + Json.Escape(context) +
                     "\",\"payload\":" + Serializer.Serialize(settings) + "}");
            }

            public void Invoke(Action work)
            {
                try { Inbox.Add(work); }
                catch (Exception) { /* shutting down */ }
            }
        }

        #endregion

        #region event dispatch

        private static void Dispatch(string raw)
        {
            var message = Serializer.DeserializeObject(raw) as Dictionary<string, object>;
            if (message == null) return;

            string eventName = Json.Str(message, "event");
            string context = Json.Str(message, "context");
            string actionUuid = Json.Str(message, "action");
            var payload = Json.Dict(message, "payload");

            // Device, application and global events carry no action, so they are
            // simply not ours to route.
            if (!ActionScoped.Contains(eventName)) return;

            ActionBase action;
            if (actionUuid == null || !Router.TryGetValue(actionUuid, out action))
            {
                Log.Write("no handler for action '" + actionUuid + "' (" + eventName + ")");
                return;
            }

            switch (eventName)
            {
                case "willAppear":
                    action.WillAppear(context, Json.Dict(payload, "settings"),
                                      Json.Str(payload, "controller"));
                    break;

                case "didReceiveSettings":
                    action.SettingsChanged(context, Json.Dict(payload, "settings"));
                    break;

                case "willDisappear":
                    action.WillDisappear(context);
                    break;

                case "dialRotate":
                    action.Rotate(context, Json.Int(payload, "ticks", 0));
                    break;

                // Which of these the host sends depends on its SDK version, so
                // all of them are accepted; releases are ignored.
                case "dialPress":
                    if (Json.Bool(payload, "pressed", true)) action.Press(context);
                    break;
                case "dialDown":
                case "keyDown":
                case "touchTap":
                    action.Press(context);
                    break;

                case "propertyInspectorDidAppear":
                    action.PropertyInspectorAppeared(context);
                    break;

                case "sendToPlugin":
                    action.FromPropertyInspector(context, payload);
                    break;
            }
        }

        private static string ArgValue(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }

        #endregion
    }

    internal static class Json
    {
        public static Dictionary<string, object> Dict(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value)) return null;
            return value as Dictionary<string, object>;
        }

        public static string Str(Dictionary<string, object> source, string key)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return null;
            return value as string ?? value.ToString();
        }

        public static int Int(Dictionary<string, object> source, string key, int fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        public static bool Bool(Dictionary<string, object> source, string key, bool fallback)
        {
            object value;
            if (source == null || !source.TryGetValue(key, out value) || value == null) return fallback;
            if (value is bool) return (bool)value;

            bool parsed;
            return bool.TryParse(value.ToString(), out parsed) ? parsed : fallback;
        }

        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var builder = new StringBuilder(text.Length + 8);
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (c < 0x20) builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else builder.Append(c);
                        break;
                }
            }
            return builder.ToString();
        }

        /// <summary>Builds a JSON array of objects from already-escaped pairs.</summary>
        public static string Array(IEnumerable<string> objects)
        {
            var builder = new StringBuilder("[");
            bool first = true;
            foreach (string item in objects)
            {
                if (!first) builder.Append(',');
                builder.Append(item);
                first = false;
            }
            return builder.Append(']').ToString();
        }
    }
}
