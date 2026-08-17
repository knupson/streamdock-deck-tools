// The now-playing tile: play or pause whatever Windows says is sounding, with
// the artist and the track name arranged around the glyph.
//
// Two clocks drive it. The ordinary one-second tick asks the system what is
// playing; the fast tick only runs while some row is too long for its band, and
// moves that row a few pixels. Neither one sends an image unless the tile would
// actually come out different, so a track whose text fits costs nothing between
// songs.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace StreamDeckAudio
{
    internal sealed class MediaPlayerAction : ActionBase
    {
        // Long enough to read the end of a title before it swings back, measured in
        // fast ticks rather than seconds so the two stay in step.
        private const int HoldFrames = 10;

        // How long a player is given to report its new state after a toggle. Below
        // about this, some of them still answer with the state they just left, and
        // the glyph would flip back for a second.
        private const int SettleMs = 250;

        private const int Artist = 0;
        private const int Title = 1;

        // Something a track name cannot contain, so two different layouts can never
        // sign the same way and leave one of them undrawn.
        private const string Unit = "\u0001";

        private readonly Dictionary<string, Scroll[]> _rows =
            new Dictionary<string, Scroll[]>(StringComparer.Ordinal);

        // What was last drawn for a context. Rendering is skipped outright when the
        // new tile would come out identical.
        private readonly Dictionary<string, string> _drawn =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private MediaState _state;
        private bool _reading;
        private bool _animating;

        public override string Uuid { get { return "com.knupwns.audioknob.media"; } }

        // Keys only, per the manifest.
        protected override bool DefaultsToKnob { get { return false; } }

        public override bool WantsTick { get { return Contexts.Count > 0; } }

        public override bool WantsFastTick { get { return _animating; } }

        public override void WillAppear(string context, Dictionary<string, object> settings, string controller)
        {
            _rows[context] = new[] { new Scroll(), new Scroll() };
            _drawn.Remove(context);
            base.WillAppear(context, settings, controller);
            Refresh();
        }

        public override void WillDisappear(string context)
        {
            base.WillDisappear(context);
            _rows.Remove(context);
            _drawn.Remove(context);
        }

        public override void FromPropertyInspector(string context, Dictionary<string, object> payload)
        {
            Dictionary<string, object> settings = Json.Dict(payload, "settings");
            if (settings != null) SettingsChanged(context, settings);
        }

        public override void Press(string context)
        {
            if (!_state.HasSession || !_state.CanToggle)
            {
                Host.ShowAlert(context);
                return;
            }

            // Flipped here rather than waiting for the answer: a key that takes a
            // quarter of a second to admit it was pressed feels broken, and the
            // read underneath corrects it if the player disagreed.
            _state.Playing = !_state.Playing;
            RenderAll();

            Background(delegate
            {
                Media.TogglePlayPause();
                Thread.Sleep(SettleMs);
                MediaState fresh = Media.Read();
                Host.Invoke(delegate { Apply(fresh); });
            });
        }

        public override void Tick()
        {
            Refresh();
        }

        public override void FastTick()
        {
            foreach (string context in Contexts.Keys.ToList())
            {
                Scroll[] rows;
                if (!_rows.TryGetValue(context, out rows)) continue;

                float speed = Math.Max(1, Int(context, "speed", 3));

                bool moved = false;
                foreach (Scroll row in rows)
                    if (row.Advance(speed)) moved = true;

                if (moved) Render(context);
            }
        }

        protected override void Render(string context)
        {
            try
            {
                Scroll[] rows;
                if (!_rows.TryGetValue(context, out rows))
                {
                    rows = new[] { new Scroll(), new Scroll() };
                    _rows[context] = rows;
                }

                bool showArtist = Bool(context, "artistShow", true);
                bool showTitle = Bool(context, "titleShow", true);
                bool artistOnTop = Str(context, "artistPos", "top") != "bottom";
                bool titleOnTop = Str(context, "titlePos", "bottom") == "top";

                rows[Artist].Track(showArtist ? _state.Artist : null);
                rows[Title].Track(showTitle ? _state.Title : null);
                _animating = Scrolling();

                // A row only takes a slot when it has something to say. An enabled
                // row with no text would otherwise hold a 30px band of nothing and
                // shrink the glyph for it, which is what an idle player looks like:
                // the session is still there, so the tile cannot go by that, but
                // both fields are empty and the glyph should have the whole tile.
                //
                // Artist is added first, so when both land in the same band the
                // artist is the upper of the two - a fixed rule rather than one
                // that depends on the order the settings happened to be touched.
                var top = new List<KeyImage.Line>();
                var bottom = new List<KeyImage.Line>();

                if (showArtist && rows[Artist].HasText) (artistOnTop ? top : bottom).Add(rows[Artist].Line());
                if (showTitle && rows[Title].HasText) (titleOnTop ? top : bottom).Add(rows[Title].Line());

                string signature = Signature(top, bottom);
                string previous;
                if (_drawn.TryGetValue(context, out previous) && previous == signature) return;
                _drawn[context] = signature;

                Host.SetImage(context, KeyImage.NowPlaying(top.ToArray(), bottom.ToArray(),
                                                           _state.Playing, _state.HasSession));
                Host.SetTitle(context, string.Empty);
            }
            catch (Exception ex)
            {
                Log.Write("MediaPlayerAction.Render failed: " + ex.Message);
            }
        }

        // Everything that can change what the tile looks like. Comparing this beats
        // comparing the rendered image: it costs nothing, where composing a tile
        // only to throw it away costs a bitmap and a PNG encode.
        private string Signature(List<KeyImage.Line> top, List<KeyImage.Line> bottom)
        {
            var builder = new StringBuilder();
            builder.Append(_state.HasSession ? '1' : '0').Append(_state.Playing ? '1' : '0');
            Append(builder, 'T', top);
            Append(builder, 'B', bottom);
            return builder.ToString();
        }

        private static void Append(StringBuilder builder, char band, List<KeyImage.Line> lines)
        {
            foreach (KeyImage.Line line in lines)
                builder.Append(Unit).Append(band).Append((int)line.Offset)
                       .Append(Unit).Append(line.Text);
        }

        private bool Scrolling()
        {
            foreach (Scroll[] rows in _rows.Values)
                foreach (Scroll row in rows)
                    if (row.Overflow > 0f) return true;
            return false;
        }

        // One read in flight at a time. It takes a few milliseconds in practice;
        // the guard is for the case where a wedged player never answers and the
        // call sits on its timeout.
        private void Refresh()
        {
            if (_reading || Contexts.Count == 0) return;
            _reading = true;

            Background(delegate
            {
                MediaState fresh = Media.Read();
                Host.Invoke(delegate
                {
                    _reading = false;
                    Apply(fresh);
                });
            });
        }

        private void Apply(MediaState fresh)
        {
            _state = fresh;
            RenderAll();
        }

        private void RenderAll()
        {
            foreach (string context in Contexts.Keys.ToList()) Render(context);
        }

        // The media API blocks, and the pump thread is the one holding GDI+, COM
        // and the socket, so none of it is allowed to run there.
        private static void Background(Action work)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { work(); }
                catch (Exception ex) { Log.Write("media background work failed: " + ex.Message); }
            });
        }

        /// <summary>
        /// One row's marquee. A row that fits never moves; one that does not swings
        /// left until its end shows, waits, and swings back.
        /// </summary>
        private sealed class Scroll
        {
            private string _text = string.Empty;
            private float _overflow;
            private float _offset;
            private int _hold = HoldFrames;
            private bool _leftward = true;

            public float Overflow { get { return _overflow; } }

            /// <summary>False when there is nothing to draw and the row can be dropped.</summary>
            public bool HasText { get { return _text.Length > 0; } }

            public void Track(string text)
            {
                text = text ?? string.Empty;
                if (text == _text) return;

                _text = text;
                _overflow = KeyImage.Overflow(text);
                _offset = 0f;
                _hold = HoldFrames;
                _leftward = true;
            }

            public KeyImage.Line Line()
            {
                return new KeyImage.Line { Text = _text, Offset = _offset };
            }

            /// <summary>True when the row moved and the tile needs redrawing.</summary>
            public bool Advance(float speed)
            {
                if (_overflow <= 0f) return false;
                if (_hold > 0) { _hold--; return false; }

                float before = _offset;
                if (_leftward)
                {
                    _offset -= speed;
                    if (_offset <= -_overflow)
                    {
                        _offset = -_overflow;
                        _leftward = false;
                        _hold = HoldFrames;
                    }
                }
                else
                {
                    _offset += speed;
                    if (_offset >= 0f)
                    {
                        _offset = 0f;
                        _leftward = true;
                        _hold = HoldFrames;
                    }
                }
                return _offset != before;
            }
        }
    }
}
