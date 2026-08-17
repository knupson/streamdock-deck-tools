// Turns long system names into something readable on a 72px key.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace StreamDeckAudio
{
    internal static class Labels
    {
        // Past this many characters the label has to shrink to fit, so trimming
        // beats keeping the full name.
        private const int Budget = 12;

        private static readonly string[] Noise =
        {
            "pro", "plus", "max", "wireless", "wired", "bluetooth", "bt", "usb",
            "stereo", "audio", "headset", "headphones", "speakers", "device"
        };

        private static readonly Dictionary<string, string> KnownProcesses =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "chrome", "Chrome" },
                { "msedge", "Edge" },
                { "msedgewebview2", "WebView2" },
                { "firefox", "Firefox" },
                { "discord", "Discord" },
                { "spotify", "Spotify" },
                { "vlc", "VLC" },
                { "obs64", "OBS" },
                { "escapefromtarkov", "Tarkov" },
                { "eft", "Tarkov" },
                { "steam", "Steam" },
                { "steamwebhelper", "Steam" },
                { "windowsterminal", "Terminal" },
                { "explorer", "Explorer" },
                { "teams", "Teams" },
                { "ms-teams", "Teams" },
                { "audiodg", "System" }
            };

        /// <summary>
        /// Device names arrive as "Speakers (2- Razer BlackShark V2 Pro)". Only the
        /// parenthesised half identifies the hardware, and the shorter the result
        /// the larger it can be drawn:
        ///
        ///   Speakers (2- Razer BlackShark V2 Pro)  ->  BlackShark
        ///   Speakers (JBL Charge 6)                ->  JBL Charge
        /// </summary>
        public static string Device(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            int open = name.IndexOf('(');
            int close = name.LastIndexOf(')');
            string core = open >= 0 && close > open ? name.Substring(open + 1, close - open - 1) : name;

            core = core.Trim();
            if (core.Length > 2 && char.IsDigit(core[0]) && core[1] == '-')
                core = core.Substring(2).Trim();

            List<string> words = core
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !IsNoise(w))
                .ToList();
            if (words.Count == 0) return core;

            // Drop leading words while the rest still says something: the brand
            // goes before the model name does.
            while (words.Count > 1 && string.Join(" ", words).Length > Budget)
                words.RemoveAt(0);

            return string.Join(" ", words);
        }

        /// <summary>
        /// A machine's name cut down to what a caption band can hold. The domain
        /// goes first, because the leftmost label is the part that identifies the
        /// machine, but an address keeps all four of its parts:
        ///
        ///   pi4.lan  ->  pi4
        ///   192.168.1.50      ->  192.168.1.50
        /// </summary>
        public static string Host(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;

            string trimmed = name.Trim();
            int dot = trimmed.IndexOf('.');

            bool address = trimmed.Length > 0 && char.IsDigit(trimmed[trimmed.Length - 1]) &&
                           trimmed.IndexOf(':') < 0 && dot > 0 &&
                           trimmed.Split('.').Length == 4;
            if (!address && dot > 0) trimmed = trimmed.Substring(0, dot);

            return trimmed.Length > Budget ? trimmed.Substring(0, Budget) : trimmed;
        }

        private sealed class Named
        {
            public string ProcessName;  // guards against a recycled pid
            public string Display;
        }

        // Reading MainModule costs a fraction of a millisecond for an ordinary
        // program and about 760ms for one guarded by anti-cheat, which refuses the
        // handle slowly rather than quickly. That runs on the pump thread, once per
        // audio session, several times per knob detent - measured at 3.1 seconds
        // per detent with a game open. What a live process is called cannot change,
        // so it is asked once per pid and remembered, the refusals included: those
        // are the ones worth not repeating.
        private static readonly Dictionary<int, Named> Cache = new Dictionary<int, Named>();

        // A deck runs for weeks. Without a ceiling this would keep an entry for
        // every process that ever held a session.
        private const int CacheLimit = 256;

        /// <summary>
        /// The shortest sensible name for a running program. Process names are
        /// internal ("WhatsApp.Root", "msedgewebview2"), and the executable's own
        /// description is often longer than needed ("Google Chrome"), so every
        /// candidate is cleaned up and the shortest one wins.
        /// </summary>
        public static string Friendly(System.Diagnostics.Process process)
        {
            int pid;
            string processName;
            try
            {
                pid = process.Id;
                processName = process.ProcessName;
            }
            catch (Exception)
            {
                return string.Empty; // the process went away mid-call
            }

            lock (Cache)
            {
                Named hit;
                if (Cache.TryGetValue(pid, out hit) &&
                    string.Equals(hit.ProcessName, processName, StringComparison.Ordinal))
                    return hit.Display;
            }

            var candidates = new List<string>();
            Push(candidates, processName);

            try
            {
                FileVersionInfo info = process.MainModule.FileVersionInfo;
                Push(candidates, info.FileDescription);
                Push(candidates, info.ProductName);
            }
            catch (Exception)
            {
                // Elevated, another session, or guarded by anti-cheat: the process
                // name is all we get, and finding that out is what costs the time.
            }

            string display = Pick(candidates);

            lock (Cache)
            {
                if (Cache.Count >= CacheLimit) Cache.Clear();
                Cache[pid] = new Named { ProcessName = processName, Display = display };
            }
            return display;
        }

        /// <summary>Same, for a process name with no handle at hand.</summary>
        public static string FriendlyByName(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return string.Empty;

            try
            {
                System.Diagnostics.Process[] running =
                    System.Diagnostics.Process.GetProcessesByName(processName);
                if (running.Length > 0) return Friendly(running[0]);
            }
            catch (Exception)
            {
                // Fall through to the name itself.
            }

            var candidates = new List<string>();
            Push(candidates, processName);
            return Pick(candidates);
        }

        private static void Push(List<string> candidates, string raw)
        {
            string cleaned = Clean(raw);
            if (cleaned.Length > 0 && !candidates.Contains(cleaned)) candidates.Add(cleaned);
        }

        private static string Clean(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;

            string name = raw.Trim();
            foreach (string suffix in new[] { ".exe", " (64 bit)", " (32 bit)", " (x64)", " (x86)" })
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    name = name.Substring(0, name.Length - suffix.Length).Trim();

            // "WhatsApp.Root", "Foo.Server": the part before the first dot is the
            // product, the rest is plumbing.
            int dot = name.IndexOf('.');
            if (dot >= 3 && name.Substring(0, dot).IndexOf(' ') < 0) name = name.Substring(0, dot);

            return name.Trim();
        }

        private static string Pick(List<string> candidates)
        {
            if (candidates.Count == 0) return string.Empty;

            foreach (string candidate in candidates)
            {
                string known;
                if (KnownProcesses.TryGetValue(candidate, out known)) return known;
            }

            string best = null;
            foreach (string candidate in candidates)
            {
                if (candidate.Length < 3 || !candidate.Any(char.IsLetter)) continue;
                if (best == null || candidate.Length < best.Length) best = candidate;
            }
            best = best ?? candidates[0];

            if (best.Length > Budget + 4) best = best.Substring(0, Budget + 4);
            return char.ToUpperInvariant(best[0]) + best.Substring(1);
        }

        // Model numbers and marketing suffixes carry no meaning on a small key.
        private static bool IsNoise(string word)
        {
            string bare = word.Trim().TrimEnd('.', ',');
            if (bare.Length == 0) return true;

            if (System.Array.Exists(Noise, n => string.Equals(n, bare, StringComparison.OrdinalIgnoreCase)))
                return true;

            // "6", "2.0" - nothing but digits and punctuation.
            if (!bare.Any(char.IsLetter)) return true;

            // "V2", "v3" - a version tag rather than a name.
            return (bare[0] == 'v' || bare[0] == 'V') && bare.Length > 1 && bare.Skip(1).All(char.IsDigit);
        }
    }
}
