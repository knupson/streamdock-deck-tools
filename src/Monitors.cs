// Hardware monitor brightness over DDC/CI.
//
// This is the real backlight control the monitor's own buttons drive, not a
// software gamma trick. Support is per-monitor and often disabled in the OSD:
// on this machine two of three panels answer and the third returns
// ERROR_GEN_FAILURE, so every call has to tolerate refusal.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace StreamDeckAudio
{
    public sealed class MonitorTarget
    {
        public int Index;
        public string Label;
        public bool Supported;
        public uint Min;
        public uint Max;
        public uint Current;
        internal IntPtr Handle;

        public int Percent
        {
            get
            {
                uint span = Max > Min ? Max - Min : 100;
                return (int)Math.Round((Current - Min) * 100.0 / span);
            }
        }
    }

    public static class Monitors
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PhysicalMonitor
        {
            public IntPtr Handle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfoEx
        {
            public int Size;
            public Rect Monitor;
            public Rect Work;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count,
                                                                   [Out] PhysicalMonitor[] monitors);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool GetMonitorBrightness(IntPtr physical, out uint min, out uint current, out uint max);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool SetMonitorBrightness(IntPtr physical, uint value);

        [DllImport("dxva2.dll", SetLastError = true)]
        private static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] monitors);

        private static List<MonitorTarget> _cache;
        private static readonly List<PhysicalMonitor[]> Opened = new List<PhysicalMonitor[]>();

        /// <summary>
        /// Monitors in EnumDisplayMonitors order. Handles stay open because each
        /// DDC/CI round trip costs tens of milliseconds and a knob turn cannot
        /// afford re-opening them per tick.
        /// </summary>
        public static List<MonitorTarget> List(bool refresh)
        {
            if (_cache != null && !refresh) return _cache;
            Release();

            var targets = new List<MonitorTarget>();
            var handles = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                delegate(IntPtr m, IntPtr dc, IntPtr r, IntPtr d) { handles.Add(m); return true; },
                IntPtr.Zero);

            for (int i = 0; i < handles.Count; i++)
            {
                uint count;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(handles[i], out count) || count == 0) continue;

                var physical = new PhysicalMonitor[count];
                if (!GetPhysicalMonitorsFromHMONITOR(handles[i], count, physical)) continue;
                Opened.Add(physical);

                string size = Geometry(handles[i]);
                foreach (PhysicalMonitor monitor in physical)
                {
                    uint min, current, max;
                    bool supported = GetMonitorBrightness(monitor.Handle, out min, out current, out max);

                    targets.Add(new MonitorTarget
                    {
                        Index = targets.Count + 1,
                        Label = "Monitor " + (targets.Count + 1) + (size == null ? string.Empty : " - " + size) +
                                (supported ? string.Empty : " (no DDC)"),
                        Supported = supported,
                        Min = supported ? min : 0,
                        Max = supported ? max : 100,
                        Current = supported ? current : 0,
                        Handle = monitor.Handle
                    });
                }
            }

            _cache = targets;
            return targets;
        }

        /// <summary>Nudges brightness and returns the value actually applied.</summary>
        public static bool Nudge(MonitorTarget target, int deltaPercent, out int percent)
        {
            percent = target.Percent;
            if (!target.Supported) return false;

            uint span = target.Max > target.Min ? target.Max - target.Min : 100;
            double step = span * deltaPercent / 100.0;
            long wanted = (long)Math.Round(target.Current + step);

            // A step smaller than one unit would otherwise round to a no-op.
            if (deltaPercent > 0 && wanted <= target.Current) wanted = target.Current + 1;
            if (deltaPercent < 0 && wanted >= target.Current) wanted = target.Current - 1;

            uint clamped = (uint)Math.Min(target.Max, Math.Max(target.Min, wanted));
            return Set(target, clamped, out percent);
        }

        public static bool SetPercent(MonitorTarget target, int wantedPercent, out int percent)
        {
            percent = target.Percent;
            if (!target.Supported) return false;

            uint span = target.Max > target.Min ? target.Max - target.Min : 100;
            uint value = (uint)Math.Min(target.Max,
                Math.Max(target.Min, target.Min + Math.Round(span * wantedPercent / 100.0)));
            return Set(target, value, out percent);
        }

        private static bool Set(MonitorTarget target, uint value, out int percent)
        {
            if (!SetMonitorBrightness(target.Handle, value))
            {
                Log.Write("SetMonitorBrightness failed on " + target.Label + ": " + Marshal.GetLastWin32Error());
                percent = target.Percent;
                _cache = null; // handles may be stale after a display change
                return false;
            }

            target.Current = value;
            percent = target.Percent;
            return true;
        }

        public static void Release()
        {
            foreach (PhysicalMonitor[] set in Opened)
            {
                try { DestroyPhysicalMonitors((uint)set.Length, set); }
                catch (Exception) { /* nothing useful to do while shutting down */ }
            }
            Opened.Clear();
            _cache = null;
        }

        private static string Geometry(IntPtr monitor)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf(typeof(MonitorInfoEx)) };
            if (!GetMonitorInfo(monitor, ref info)) return null;

            return (info.Monitor.Right - info.Monitor.Left) + "x" + (info.Monitor.Bottom - info.Monitor.Top);
        }
    }
}
