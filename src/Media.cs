// What Windows itself thinks is playing.
//
// The transport controls every media app registers with the system - the same
// ones the volume flyout shows - are exposed through WinRT. Reading them takes
// no cooperation from the player: Spotify, a YouTube tab and VLC all land in the
// same place.
//
// Two things about building this without an SDK. The type metadata comes from
// the copy Windows ships in System32\WinMetadata rather than from a Windows Kit,
// and against that metadata the await adapters in System.Runtime.WindowsRuntime
// do not bind - the identity of IAsyncOperation<T> does not match what the
// facade expects. So every asynchronous call below is drained by hand instead.
//
// Everything here blocks. It is called from a background thread and the result
// is handed back to the pump; see MediaPlayerAction.

using System;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

namespace StreamDeckAudio
{
    internal struct MediaState
    {
        public bool HasSession;
        public string Artist;
        public string Title;
        public bool Playing;

        /// <summary>
        /// Whether the player accepts a play/pause toggle at all. A few sources -
        /// a live stream, an ad - report a session but refuse to be paused.
        /// </summary>
        public bool CanToggle;
    }

    internal static class Media
    {
        private const int TimeoutMs = 3000;

        private static readonly object Gate = new object();
        private static GlobalSystemMediaTransportControlsSessionManager _manager;

        /// <summary>Blocks. Background threads only.</summary>
        public static MediaState Read()
        {
            var state = new MediaState { Artist = string.Empty, Title = string.Empty };
            try
            {
                GlobalSystemMediaTransportControlsSession session = Current();
                if (session == null) return state;

                GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
                state.HasSession = true;
                state.Playing = info.PlaybackStatus ==
                                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                state.CanToggle = info.Controls.IsPlayPauseToggleEnabled;

                GlobalSystemMediaTransportControlsSessionMediaProperties properties =
                    Wait(session.TryGetMediaPropertiesAsync());

                if (properties != null)
                {
                    state.Artist = Clean(properties.Artist);
                    state.Title = Clean(properties.Title);

                    // Some players leave the artist field empty and put the whole
                    // "artist - title" string in the title instead.
                    if (state.Artist.Length == 0) Split(ref state);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Media.Read failed: " + ex.Message);
            }
            return state;
        }

        /// <summary>
        /// Plays a paused session or pauses a playing one. False when there was
        /// nothing to toggle. Blocks; background threads only.
        /// </summary>
        public static bool TogglePlayPause()
        {
            try
            {
                GlobalSystemMediaTransportControlsSession session = Current();
                if (session == null) return false;
                if (!session.GetPlaybackInfo().Controls.IsPlayPauseToggleEnabled) return false;

                return Wait(session.TryTogglePlayPauseAsync());
            }
            catch (Exception ex)
            {
                Log.Write("Media.TogglePlayPause failed: " + ex.Message);
                return false;
            }
        }

        private static GlobalSystemMediaTransportControlsSession Current()
        {
            GlobalSystemMediaTransportControlsSessionManager manager = Manager();
            return manager == null ? null : manager.GetCurrentSession();
        }

        // Requested once and kept: the manager tracks sessions coming and going on
        // its own, so re-requesting it per read would only cost time.
        private static GlobalSystemMediaTransportControlsSessionManager Manager()
        {
            lock (Gate)
            {
                if (_manager != null) return _manager;
                try
                {
                    _manager = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                }
                catch (Exception ex)
                {
                    Log.Write("Media: session manager unavailable: " + ex.Message);
                }
                return _manager;
            }
        }

        private static string Clean(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : text.Trim();
        }

        // Only splits on a spaced dash, and only when both halves survive: a title
        // like "Hyphen-Word" has to come through untouched.
        private static void Split(ref MediaState state)
        {
            int dash = state.Title.IndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0) return;

            string artist = state.Title.Substring(0, dash).Trim();
            string title = state.Title.Substring(dash + 3).Trim();
            if (artist.Length == 0 || title.Length == 0) return;

            state.Artist = artist;
            state.Title = title;
        }

        // The WinRT call signals completion on a thread pool thread; this parks the
        // caller until it does. A player that never answers times out rather than
        // holding the reader for good.
        private static T Wait<T>(IAsyncOperation<T> operation)
        {
            using (var done = new ManualResetEventSlim(false))
            {
                operation.Completed = delegate { done.Set(); };
                if (!done.Wait(TimeoutMs)) throw new TimeoutException("media call did not complete");
                return operation.GetResults();
            }
        }
    }
}
