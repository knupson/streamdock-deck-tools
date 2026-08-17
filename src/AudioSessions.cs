// Per-application volume, through the audio session API.
//
// Sessions are matched by process *name*, not pid: Chrome, Discord and Edge all
// spread their audio across several processes, and a user who picks "Discord"
// means all of it.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace StreamDeckAudio
{
    #region COM interop

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionId, int flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionId, int flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        [PreserveSig] int GetGroupingParam(out Guid group);
        [PreserveSig] int SetGroupingParam(ref Guid group, ref Guid context);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr notify);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out int pid);
        [PreserveSig] int IsSystemSoundsSession();
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid context);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute(bool mute, ref Guid context);
        [PreserveSig] int GetMute(out bool mute);
    }

    #endregion

    public sealed class AppSession
    {
        public int Pid;
        public string ProcessName;  // matching key, stable across restarts
        public string Display;      // what a person calls the program
        public float Volume;
        public bool Muted;
        internal ISimpleAudioVolume Control;
    }

    public sealed class AppChoice
    {
        public string Id;
        public string Label;
    }

    public static class AudioSessions
    {
        private const int ClsCtxAll = 23;
        private const int StateExpired = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr window, out int pid);

        /// <summary>Live sessions on the default output, system sounds excluded.</summary>
        public static List<AppSession> List()
        {
            var result = new List<AppSession>();

            IAudioSessionEnumerator sessions = OpenEnumerator();
            if (sessions == null) return result;

            int count;
            if (sessions.GetCount(out count) != 0) return result;

            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl2 session;
                if (sessions.GetSession(i, out session) != 0) continue;

                int state;
                if (session.GetState(out state) == 0 && state == StateExpired) continue;
                if (session.IsSystemSoundsSession() == 0) continue;

                int pid;
                if (session.GetProcessId(out pid) != 0 || pid == 0) continue;

                var volume = session as ISimpleAudioVolume;
                if (volume == null) continue;

                float level;
                bool muted;
                volume.GetMasterVolume(out level);
                volume.GetMute(out muted);

                string processName, display;
                Describe(pid, out processName, out display);

                result.Add(new AppSession
                {
                    Pid = pid,
                    ProcessName = processName,
                    Display = display,
                    Volume = level,
                    Muted = muted,
                    Control = volume
                });
            }
            return result;
        }

        /// <summary>
        /// One entry per application holding a session: the process name as the
        /// stored value, the readable name as the label.
        /// </summary>
        public static List<AppChoice> Applications()
        {
            var seen = new Dictionary<string, AppChoice>(StringComparer.OrdinalIgnoreCase);
            foreach (AppSession session in List())
            {
                if (session.ProcessName == null || seen.ContainsKey(session.ProcessName)) continue;
                seen[session.ProcessName] = new AppChoice
                {
                    Id = session.ProcessName,
                    Label = string.IsNullOrEmpty(session.Display) ? session.ProcessName : session.Display
                };
            }

            return seen.Values.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>First session belonging to an application, or null.</summary>
        public static AppSession Find(string processName)
        {
            List<AppSession> matches = Matching(processName);
            return matches.Count == 0 ? null : matches[0];
        }

        public static string ForegroundProcessName()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return null;

            int pid;
            GetWindowThreadProcessId(window, out pid);
            if (pid == 0) return null;

            string processName, display;
            Describe(pid, out processName, out display);
            return processName;
        }

        /// <summary>
        /// Moves every session of <paramref name="processName"/> by
        /// <paramref name="delta"/>, keeping them in sync, and reports the level
        /// they landed on. Returns false when the app has no session.
        /// </summary>
        public static bool Adjust(string processName, float delta, out float level, out bool muted)
        {
            level = 0f;
            muted = false;

            List<AppSession> matches = Matching(processName);
            if (matches.Count == 0) return false;

            level = Clamp(matches[0].Volume + delta);
            Guid noContext = Guid.Empty;
            foreach (AppSession session in matches)
                session.Control.SetMasterVolume(level, ref noContext);

            muted = matches[0].Muted;
            return true;
        }

        public static bool ToggleMute(string processName, out bool muted)
        {
            muted = false;
            List<AppSession> matches = Matching(processName);
            if (matches.Count == 0) return false;

            muted = !matches[0].Muted;
            Guid noContext = Guid.Empty;
            foreach (AppSession session in matches)
                session.Control.SetMute(muted, ref noContext);
            return true;
        }

        public static bool TryRead(string processName, out float level, out bool muted)
        {
            level = 0f;
            muted = false;

            List<AppSession> matches = Matching(processName);
            if (matches.Count == 0) return false;

            level = matches[0].Volume;
            muted = matches[0].Muted;
            return true;
        }

        private static List<AppSession> Matching(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return new List<AppSession>();
            return List()
                .Where(s => string.Equals(s.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static IAudioSessionEnumerator OpenEnumerator()
        {
            try
            {
                var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                IMMDevice device;
                if (enumerator.GetDefaultAudioEndpoint(0, 1, out device) != 0) return null;

                Guid iid = typeof(IAudioSessionManager2).GUID;
                object raw;
                if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out raw) != 0) return null;

                IAudioSessionEnumerator sessions;
                return ((IAudioSessionManager2)raw).GetSessionEnumerator(out sessions) == 0 ? sessions : null;
            }
            catch (Exception ex)
            {
                Log.Write("AudioSessions.OpenEnumerator failed: " + ex.Message);
                return null;
            }
        }

        private static void Describe(int pid, out string processName, out string display)
        {
            try
            {
                Process process = Process.GetProcessById(pid);
                processName = process.ProcessName;
                display = Labels.Friendly(process);
            }
            catch (Exception)
            {
                processName = null; // process already gone
                display = null;
            }
        }

        private static float Clamp(float value)
        {
            return Math.Min(1f, Math.Max(0f, value));
        }
    }
}
