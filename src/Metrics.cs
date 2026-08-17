// System metrics for the live tiles.
//
// Everything here comes from APIs and performance counters Windows already
// exposes - no driver, no ring-0 helper, no third-party library. That rules out
// per-chip GPU/CPU temperatures; the ACPI thermal zone is what is available, and
// it is labelled honestly as a system temperature rather than a CPU one.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;

namespace StreamDeckAudio
{
    public enum MetricKind
    {
        Cpu,
        Ram,
        Gpu,
        Vram,
        Disk,
        Net,
        Temp
    }

    public sealed class MetricSample
    {
        public string Label;    // short caption for the top of the tile
        public string Text;     // formatted value
        public float Fraction;  // 0..1, drives the sparkline scale
    }

    public static class Metrics
    {
        private static readonly TimeSpan InstanceTtl = TimeSpan.FromSeconds(30);

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        private static long _lastIdle, _lastKernel, _lastUser;

        private static List<PerformanceCounter> _gpuCounters;
        private static DateTime _gpuCountersAt;
        private static PerformanceCounter _vram, _disk, _net;
        private static List<PerformanceCounter> _thermal;

        public static MetricSample Read(MetricKind kind)
        {
            try
            {
                switch (kind)
                {
                    case MetricKind.Cpu: return Cpu();
                    case MetricKind.Ram: return Ram();
                    case MetricKind.Gpu: return Gpu();
                    case MetricKind.Vram: return Vram();
                    case MetricKind.Disk: return Disk();
                    case MetricKind.Net: return Net();
                    case MetricKind.Temp: return Temp();
                }
            }
            catch (Exception ex)
            {
                Log.Write("Metrics.Read(" + kind + ") failed: " + ex.Message);
            }
            return new MetricSample { Label = Caption(kind), Text = "n/a", Fraction = 0f };
        }

        public static string Caption(MetricKind kind)
        {
            switch (kind)
            {
                case MetricKind.Cpu: return "CPU";
                case MetricKind.Ram: return "RAM";
                case MetricKind.Gpu: return "GPU";
                case MetricKind.Vram: return "VRAM";
                case MetricKind.Disk: return "DISK";
                case MetricKind.Net: return "NET";
                case MetricKind.Temp: return "TEMP";
            }
            return "?";
        }

        public static MetricKind Parse(string name)
        {
            foreach (MetricKind kind in Enum.GetValues(typeof(MetricKind)))
                if (string.Equals(kind.ToString(), name, StringComparison.OrdinalIgnoreCase))
                    return kind;
            return MetricKind.Cpu;
        }

        #region individual metrics

        // Deltas of the kernel's own accounting: cheaper and steadier than the
        // processor performance counters, which need one instance per core here.
        private static MetricSample Cpu()
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user))
                return new MetricSample { Label = "CPU", Text = "n/a", Fraction = 0f };

            long idleDelta = idle - _lastIdle;
            long busyDelta = (kernel - _lastKernel) + (user - _lastUser) - idleDelta;
            long totalDelta = (kernel - _lastKernel) + (user - _lastUser);

            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;

            float fraction = totalDelta > 0 ? (float)busyDelta / totalDelta : 0f;
            return Percentage("CPU", fraction);
        }

        private static MetricSample Ram()
        {
            var status = new MemoryStatusEx();
            status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
            if (!GlobalMemoryStatusEx(ref status))
                return new MetricSample { Label = "RAM", Text = "n/a", Fraction = 0f };

            ulong used = status.TotalPhys - status.AvailPhys;
            return new MetricSample
            {
                Label = "RAM",
                Text = Gigabytes(used),
                Fraction = status.TotalPhys == 0 ? 0f : (float)used / status.TotalPhys
            };
        }

        // One counter instance per process per engine, so the 3D engines are
        // summed. The instance list is refreshed on a timer because processes
        // come and go constantly.
        private static MetricSample Gpu()
        {
            if (_gpuCounters == null || DateTime.UtcNow - _gpuCountersAt > InstanceTtl)
            {
                DisposeAll(_gpuCounters);
                _gpuCounters = new List<PerformanceCounter>();
                _gpuCountersAt = DateTime.UtcNow;

                foreach (string instance in new PerformanceCounterCategory("GPU Engine").GetInstanceNames())
                {
                    if (!instance.EndsWith("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        counter.NextValue(); // prime
                        _gpuCounters.Add(counter);
                    }
                    catch (Exception) { /* instance vanished between listing and use */ }
                }
            }

            float total = 0f;
            foreach (PerformanceCounter counter in _gpuCounters)
            {
                try { total += counter.NextValue(); }
                catch (Exception) { /* process exited; the refresh will drop it */ }
            }
            return Percentage("GPU", total / 100f);
        }

        private static MetricSample Vram()
        {
            if (_vram == null)
            {
                // Two adapters are present (integrated and discrete); the one
                // holding the most dedicated memory is the interesting one.
                string best = null;
                float most = -1f;
                foreach (string instance in new PerformanceCounterCategory("GPU Adapter Memory").GetInstanceNames())
                {
                    try
                    {
                        using (var probe = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", instance, true))
                        {
                            float value = probe.NextValue();
                            if (value > most) { most = value; best = instance; }
                        }
                    }
                    catch (Exception) { /* skip */ }
                }
                if (best == null) return new MetricSample { Label = "VRAM", Text = "n/a", Fraction = 0f };
                _vram = new PerformanceCounter("GPU Adapter Memory", "Dedicated Usage", best, true);
            }

            float bytes = _vram.NextValue();
            return new MetricSample
            {
                Label = "VRAM",
                Text = Gigabytes((ulong)Math.Max(0f, bytes)),
                Fraction = Math.Min(1f, bytes / (16f * 1024f * 1024f * 1024f))
            };
        }

        private static MetricSample Disk()
        {
            if (_disk == null)
            {
                _disk = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
                _disk.NextValue();
            }
            return Percentage("DISK", _disk.NextValue() / 100f);
        }

        private static MetricSample Net()
        {
            if (_net == null)
            {
                string[] instances = new PerformanceCounterCategory("Network Interface").GetInstanceNames();
                if (instances.Length == 0) return new MetricSample { Label = "NET", Text = "n/a", Fraction = 0f };

                _net = new PerformanceCounter("Network Interface", "Bytes Total/sec", instances[0], true);
                _net.NextValue();
            }

            float bitsPerSecond = _net.NextValue() * 8f;
            string text = bitsPerSecond >= 1000000f
                ? (bitsPerSecond / 1000000f).ToString(bitsPerSecond >= 10000000f ? "0" : "0.0",
                                                      CultureInfo.InvariantCulture) + " Mb"
                : Math.Round(bitsPerSecond / 1000f) + " kb";

            return new MetricSample
            {
                Label = "NET",
                Text = text,
                Fraction = Math.Min(1f, bitsPerSecond / 1000000000f) // against a gigabit link
            };
        }

        private static MetricSample Temp()
        {
            if (_thermal == null)
            {
                _thermal = new List<PerformanceCounter>();
                foreach (string instance in new PerformanceCounterCategory("Thermal Zone Information").GetInstanceNames())
                {
                    try
                    {
                        _thermal.Add(new PerformanceCounter("Thermal Zone Information", "Temperature", instance, true));
                    }
                    catch (Exception) { /* skip */ }
                }
            }

            float hottestKelvin = 0f;
            foreach (PerformanceCounter counter in _thermal)
            {
                try { hottestKelvin = Math.Max(hottestKelvin, counter.NextValue()); }
                catch (Exception) { /* skip */ }
            }
            if (hottestKelvin <= 0f) return new MetricSample { Label = "TEMP", Text = "n/a", Fraction = 0f };

            float celsius = hottestKelvin - 273.15f;
            return new MetricSample
            {
                Label = "TEMP",
                Text = Math.Round(celsius) + "°",
                Fraction = Math.Min(1f, Math.Max(0f, celsius / 100f))
            };
        }

        #endregion

        #region formatting

        private static MetricSample Percentage(string label, float fraction)
        {
            float clamped = Math.Min(1f, Math.Max(0f, fraction));
            return new MetricSample
            {
                Label = label,
                Text = Math.Round(clamped * 100f) + "%",
                Fraction = clamped
            };
        }

        private static string Gigabytes(ulong bytes)
        {
            double gb = bytes / (1024.0 * 1024.0 * 1024.0);
            return gb.ToString(gb >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture) + " GB";
        }

        private static void DisposeAll(IEnumerable<PerformanceCounter> counters)
        {
            if (counters == null) return;
            foreach (PerformanceCounter counter in counters)
            {
                try { counter.Dispose(); }
                catch (Exception) { /* nothing to salvage */ }
            }
        }

        #endregion
    }
}
