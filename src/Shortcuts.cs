// Reads a folder of shortcuts and resolves each one's real icon.
//
// A .lnk file carries no icon of its own: it names either an icon location or a
// target, and the icon has to be pulled out of that. A .url file is a plain INI
// that may name an icon file. Anything else is asked for its own icons directly.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamDeckAudio
{
    #region COM interop

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch,
                     IntPtr findData, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int cch,
                             out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relative, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName,
                  [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    #endregion

    public sealed class Shortcut
    {
        public string Path;
        public string Name;
    }

    public static class Shortcuts
    {
        // The shell caches these per path; a knob turn must not pay for a fresh
        // icon extraction every tick.
        private static readonly Dictionary<string, Bitmap> IconCache =
            new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int PrivateExtractIcons(string file, int index, int cx, int cy,
                                                      IntPtr[] icons, int[] ids, int count, int flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        /// <summary>Files in the folder, sorted by name. Directories are skipped.</summary>
        public static List<Shortcut> List(string folder)
        {
            var result = new List<Shortcut>();

            string resolved = Resolve(folder);
            if (resolved == null || !Directory.Exists(resolved)) return result;

            try
            {
                foreach (string path in Directory.GetFiles(resolved))
                {
                    string name = System.IO.Path.GetFileName(path);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                    result.Add(new Shortcut
                    {
                        Path = path,
                        Name = System.IO.Path.GetFileNameWithoutExtension(path)
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Write("Shortcuts.List(" + resolved + ") failed: " + ex.Message);
            }

            return result.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static string Resolve(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return null;

            try
            {
                return Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static bool Launch(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                Log.Write("launched " + path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("launch failed for " + path + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>The item's own icon at the largest size available, or null.</summary>
        public static Bitmap Icon(string path)
        {
            Bitmap cached;
            if (IconCache.TryGetValue(path, out cached)) return cached;

            Bitmap icon = null;
            try
            {
                string source;
                int index;
                if (IconSource(path, out source, out index)) icon = Extract(source, index);
            }
            catch (Exception ex)
            {
                Log.Write("icon lookup failed for " + path + ": " + ex.Message);
            }

            IconCache[path] = icon;
            return icon;
        }

        // Where the artwork actually lives depends on the kind of file.
        private static bool IconSource(string path, out string source, out int index)
        {
            source = null;
            index = 0;

            string extension = System.IO.Path.GetExtension(path);
            if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                return FromShellLink(path, out source, out index);

            if (extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                return FromUrlFile(path, out source, out index);

            source = path;
            return File.Exists(path);
        }

        private static bool FromShellLink(string path, out string source, out int index)
        {
            source = null;
            index = 0;

            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(path, 0);

            var buffer = new StringBuilder(1024);
            link.GetIconLocation(buffer, buffer.Capacity, out index);
            string iconPath = Environment.ExpandEnvironmentVariables(buffer.ToString());

            if (iconPath.Length > 0 && File.Exists(iconPath))
            {
                source = iconPath;
                return true;
            }

            // No icon of its own: fall back to whatever it points at.
            buffer.Length = 0;
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0);
            string target = Environment.ExpandEnvironmentVariables(buffer.ToString());

            index = 0;
            source = target;
            return target.Length > 0 && File.Exists(target);
        }

        private static bool FromUrlFile(string path, out string source, out int index)
        {
            source = null;
            index = 0;

            foreach (string line in File.ReadAllLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    source = Environment.ExpandEnvironmentVariables(trimmed.Substring(9).Trim());
                else if (trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(trimmed.Substring(10).Trim(), out index);
            }

            return source != null && File.Exists(source);
        }

        private static Bitmap Extract(string file, int index)
        {
            var handles = new IntPtr[1];
            var ids = new int[1];

            // Ask for a big one; Windows returns the closest it has.
            if (PrivateExtractIcons(file, index, 256, 256, handles, ids, 1, 0) < 1 ||
                handles[0] == IntPtr.Zero)
                return null;

            try
            {
                using (System.Drawing.Icon icon = System.Drawing.Icon.FromHandle(handles[0]))
                    return icon.ToBitmap();
            }
            finally
            {
                DestroyIcon(handles[0]);
            }
        }
    }
}
