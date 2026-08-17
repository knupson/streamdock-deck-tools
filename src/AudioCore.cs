// Shared audio backend: enumerate output devices, read/change the default one,
// and nudge the system volume.
//
// Changing the default endpoint has no public API, so this uses the
// undocumented IPolicyConfig COM interface that the Sound control panel itself
// uses. Two flavours exist and which one answers QueryInterface depends on the
// Windows build, so both are declared.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamDeckAudio
{
    #region COM interop

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey
    {
        public Guid FormatId;
        public int PropertyId;
    }

    // Only the string case is needed (PKEY_Device_FriendlyName is VT_LPWSTR).
    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public short ValueType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    // In both PolicyConfig interfaces the members before SetDefaultEndpoint
    // exist only to keep it at its real vtable slot; they are never called, so
    // their signatures are stubbed. The layouts differ: the Vista one has no
    // ResetDeviceFormat, so SetDefaultEndpoint sits one slot earlier.
    [ComImport, Guid("F8679F50-850A-45DE-BE9B-C4972FBB9B58"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int ResetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility();
    }

    // Windows 11 builds around 26xxx stopped answering QueryInterface for
    // IPolicyConfig; CPolicyConfigVistaClient still exposes this one.
    [ComImport, Guid("568B9108-44BF-40B4-9006-86AFE5B5A620"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfigVista
    {
        [PreserveSig] int GetMixFormat();
        [PreserveSig] int GetDeviceFormat();
        [PreserveSig] int SetDeviceFormat();
        [PreserveSig] int GetProcessingPeriod();
        [PreserveSig] int SetProcessingPeriod();
        [PreserveSig] int GetShareMode();
        [PreserveSig] int SetShareMode();
        [PreserveSig] int GetPropertyValue();
        [PreserveSig] int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility();
    }

    // Only the first slots are needed; the rest of the vtable is omitted since
    // nothing past GetMasterVolumeLevelScalar is ever called.
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out int count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    internal class PolicyConfigClient { }

    [ComImport, Guid("294935CE-F637-4E7C-A41B-AB255460B862")]
    internal class PolicyConfigVistaClient { }

    #endregion

    public sealed class AudioDevice
    {
        public string Id;
        public string Name;

        public override string ToString() { return Name; }
    }

    public static class AudioDevices
    {
        private const int RenderFlow = 0;      // eRender
        private const int StateActive = 0x1;   // DEVICE_STATE_ACTIVE
        private static readonly int[] AllRoles = { 0, 1, 2 }; // console, multimedia, communications

        private static readonly PropertyKey FriendlyName = new PropertyKey
        {
            FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
            PropertyId = 14
        };

        public static List<AudioDevice> ListActiveOutputs()
        {
            var result = new List<AudioDevice>();
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

            IMMDeviceCollection collection;
            Check(enumerator.EnumAudioEndpoints(RenderFlow, StateActive, out collection), "EnumAudioEndpoints");

            int count;
            Check(collection.GetCount(out count), "GetCount");
            for (int i = 0; i < count; i++)
            {
                IMMDevice device;
                Check(collection.Item(i, out device), "Item");
                string id;
                Check(device.GetId(out id), "GetId");
                result.Add(new AudioDevice { Id = id, Name = ReadFriendlyName(device) });
            }
            return result;
        }

        public static string GetDefaultOutputId()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device;
            if (enumerator.GetDefaultAudioEndpoint(RenderFlow, 1 /* eMultimedia */, out device) != 0)
                return null; // nothing active
            string id;
            Check(device.GetId(out id), "GetId");
            return id;
        }

        public static void SetDefaultOutput(string deviceId)
        {
            var modern = TryCreate<IPolicyConfig>(typeof(PolicyConfigClient));
            if (modern != null)
            {
                foreach (int role in AllRoles)
                    Check(modern.SetDefaultEndpoint(deviceId, role), "SetDefaultEndpoint(role " + role + ")");
                return;
            }

            var vista = TryCreate<IPolicyConfigVista>(typeof(PolicyConfigVistaClient));
            if (vista != null)
            {
                foreach (int role in AllRoles)
                    Check(vista.SetDefaultEndpoint(deviceId, role), "SetDefaultEndpointVista(role " + role + ")");
                return;
            }

            throw new InvalidOperationException("No PolicyConfig interface available on this Windows build.");
        }

        public static AudioDevice FindById(List<AudioDevice> devices, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return devices.Find(d => d.Id == id);
        }

        public static AudioDevice FindByName(List<AudioDevice> devices, string substring)
        {
            if (string.IsNullOrEmpty(substring)) return null;
            return devices.Find(d => d.Name != null &&
                d.Name.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string ReadFriendlyName(IMMDevice device)
        {
            IPropertyStore store;
            Check(device.OpenPropertyStore(0 /* STGM_READ */, out store), "OpenPropertyStore");
            PropertyKey key = FriendlyName;
            PropVariant value;
            Check(store.GetValue(ref key, out value), "GetValue");
            return value.Pointer == IntPtr.Zero ? "(unknown)" : Marshal.PtrToStringUni(value.Pointer);
        }

        private static T TryCreate<T>(Type comClass) where T : class
        {
            try
            {
                // A COM object that does not support the interface yields null
                // rather than throwing, which is exactly the probe we want.
                return Activator.CreateInstance(comClass) as T;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void Check(int hr, string what)
        {
            if (hr != 0) throw new COMException(what + " failed", hr);
        }
    }

    // Volume moves through the standard media keys when it can, because that is
    // what makes Windows apply its own step size and show its usual on-screen
    // indicator.
    //
    // UIPI silently discards synthetic input while a process of higher integrity
    // owns the foreground, which is what an elevated game is. keybd_event returns
    // void and reports nothing, so the knob just went dead with no error to log.
    // When the foreground outranks us the level is set through the endpoint API
    // instead: no on-screen indicator, but no focus rule can block it either.
    public static class SystemVolume
    {
        private const byte VkVolumeMute = 0xAD;
        private const byte VkVolumeDown = 0xAE;
        private const byte VkVolumeUp = 0xAF;
        private const uint KeyUp = 0x0002;

        // What Windows itself moves per media-key press. Measured, so that a
        // detent feels the same whichever path carries it.
        private const float StepPerTap = 0.02f;

        private const int TokenQuery = 0x0008;
        private const int QueryLimitedInformation = 0x1000;
        private const int TokenElevation = 20;

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info,
                                                       int length, out int returned);

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentProcessId();

        // Our own token never changes while the process lives. An unreadable
        // answer here means "assume we can be blocked", so the foreground still
        // gets checked.
        private static readonly bool SelfElevated = IsElevated(GetCurrentProcessId(), false);

        // Only so the log records the switch once instead of once per detent.
        private static bool _loggedFallback;

        public static void Up(int steps) { Move(VkVolumeUp, steps, StepPerTap); }
        public static void Down(int steps) { Move(VkVolumeDown, steps, -StepPerTap); }
        public static void ToggleMute() { Tap(VkVolumeMute, 1); }

        private static void Move(byte key, int steps, float stepDelta)
        {
            int times = Math.Max(1, steps);

            if (CanInjectInput())
            {
                Tap(key, times);
                return;
            }

            EndpointVolume.SetScalar(EndpointVolume.GetScalar() + stepDelta * times);
        }

        // Costs ~7 microseconds against an elevated game, so a detent burst can
        // ask every time rather than cache a verdict that may already be stale.
        private static bool CanInjectInput()
        {
            if (SelfElevated) return true;

            int pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            if (pid == 0) return true;

            if (!IsElevated(pid, true)) return true;

            if (!_loggedFallback)
            {
                _loggedFallback = true;
                Log.Write("master volume: foreground pid " + pid + " outranks us, " +
                          "media keys are blocked by UIPI, using the endpoint API");
            }
            return false;
        }

        // onFailure decides what an unreadable token means. For the foreground it
        // is true, so anything we cannot inspect counts as outranking us: losing
        // the on-screen indicator is harmless, losing the volume change is not.
        private static bool IsElevated(int processId, bool onFailure)
        {
            IntPtr process = OpenProcess(QueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero) return onFailure;
            try
            {
                IntPtr token;
                if (!OpenProcessToken(process, TokenQuery, out token)) return onFailure;
                try
                {
                    int elevation, returned;
                    if (!GetTokenInformation(token, TokenElevation, out elevation, 4, out returned))
                        return onFailure;
                    return elevation != 0;
                }
                finally { CloseHandle(token); }
            }
            finally { CloseHandle(process); }
        }

        private static void Tap(byte key, int times)
        {
            for (int i = 0; i < Math.Max(1, times); i++)
            {
                keybd_event(key, 0, 0, IntPtr.Zero);
                keybd_event(key, 0, KeyUp, IntPtr.Zero);
            }
        }
    }

    // Reading the level is only used for diagnostics and tests; the plugin
    // itself never sets it directly, it taps the media keys instead.
    public static class EndpointVolume
    {
        private const int ClsCtxAll = 23;

        public static float GetScalar()
        {
            float level;
            Marshal.ThrowExceptionForHR(Activate().GetMasterVolumeLevelScalar(out level));
            return level;
        }

        public static void SetScalar(float level)
        {
            Guid noContext = Guid.Empty;
            float clamped = Math.Min(1f, Math.Max(0f, level));
            Marshal.ThrowExceptionForHR(Activate().SetMasterVolumeLevelScalar(clamped, ref noContext));
        }

        private static IAudioEndpointVolume Activate()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            IMMDevice device;
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out device));

            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object volume;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out volume));
            return (IAudioEndpointVolume)volume;
        }
    }

    public static class Log
    {
        private static readonly object Gate = new object();
        public static string Component = "audio";

        public static void Write(string message)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StreamDeckAudio");
                Directory.CreateDirectory(dir);
                lock (Gate)
                {
                    File.AppendAllText(Path.Combine(dir, Component + ".log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never break the caller.
            }
        }
    }
}
