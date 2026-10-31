// Playnite GenericPlugin extension that keeps D:\Juegos and Playnite's installed
// library synchronized in both directions:
//
//   1. Unmanaged shortcuts (.lnk / .url) found in D:\Juegos are matched to
//      existing Playnite games (making sure they are marked Installed) or
//      imported as new installed games in Playnite, preserving their artwork.
//   2. Every installed, non-hidden game in Playnite gets a managed .url shortcut
//      in D:\Juegos (URL=playnite://playnite/start/<Guid>) with a 256x256 .ico.
//   3. Managed shortcuts in D:\Juegos whose game was uninstalled, hidden, or
//      removed in Playnite are automatically deleted.

using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DeckToolsPlaynite
{
    #region Shell COM & Win32 Interop

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

    [ComImport, Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(ShellSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellSize
    {
        public int Width;
        public int Height;
    }

    #endregion

    public class DeckToolsSyncPlugin : GenericPlugin
    {
        private const string PlayniteUrlPrefix = "playnite://playnite/start/";
        private const string DefaultTargetFolder = @"D:\Juegos";
        private const string ExcludedTagName = "Oculto del Dock";

        private readonly object _syncLock = new object();
        private Timer _debounceTimer;
        private FileSystemWatcher _folderWatcher;
        private volatile bool _isSyncing;
        private string _pendingReason = "startup";

        private readonly string _dataDir;
        private readonly string _iconCacheDir;
        private readonly string _logPath;
        private readonly string _configPath;
        private readonly string _excludedPath;

        public override Guid Id
        {
            get { return new Guid("8d4b7c21-5f3a-4e92-b1a0-7c6d8e9f0a12"); }
        }

        public DeckToolsSyncPlugin(IPlayniteAPI api) : base(api)
        {
            _dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StreamDeckAudio");
            _iconCacheDir = Path.Combine(_dataDir, "playnite-icons");
            _logPath = Path.Combine(_dataDir, "playnite-sync.log");
            _configPath = Path.Combine(_dataDir, "playnite-sync.ini");
            _excludedPath = Path.Combine(_dataDir, "playnite-excluded.txt");

            try
            {
                Directory.CreateDirectory(_dataDir);
                Directory.CreateDirectory(_iconCacheDir);
            }
            catch (Exception) { }

            ClearSafeStartupFlag();
        }

        private void ClearSafeStartupFlag()
        {
            try
            {
                string configDir = PlayniteApi != null && PlayniteApi.Paths != null
                    ? PlayniteApi.Paths.ConfigurationPath
                    : null;
                if (!string.IsNullOrEmpty(configDir))
                {
                    string flag = Path.Combine(configDir, "safestart.flag");
                    if (File.Exists(flag))
                        File.Delete(flag);
                }

                string appDataFlag = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Playnite",
                    "safestart.flag");
                if (File.Exists(appDataFlag))
                    File.Delete(appDataFlag);
            }
            catch (Exception) { }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            ClearSafeStartupFlag();

            string desktopName = GetCurrentDesktopName();
            WriteLog("DeckToolsSyncPlugin started on desktop '" + desktopName + "'. Target folder: " + GetTargetFolder());

            PlayniteApi.Database.Games.ItemCollectionChanged += OnGamesCollectionChanged;
            PlayniteApi.Database.Games.ItemUpdated += OnGamesUpdated;

            SetupFolderWatcher();
            ScheduleSync("OnApplicationStarted", 800);
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            ClearSafeStartupFlag();

            PlayniteApi.Database.Games.ItemCollectionChanged -= OnGamesCollectionChanged;
            PlayniteApi.Database.Games.ItemUpdated -= OnGamesUpdated;

            if (_debounceTimer != null)
            {
                _debounceTimer.Dispose();
                _debounceTimer = null;
            }
            if (_folderWatcher != null)
            {
                _folderWatcher.EnableRaisingEvents = false;
                _folderWatcher.Dispose();
                _folderWatcher = null;
            }
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            ScheduleSync("OnLibraryUpdated", 600);
        }

        public override void OnGameInstalled(OnGameInstalledEventArgs args)
        {
            ScheduleSync("OnGameInstalled: " + (args != null && args.Game != null ? args.Game.Name : ""), 400);
        }

        public override void OnGameUninstalled(OnGameUninstalledEventArgs args)
        {
            ScheduleSync("OnGameUninstalled: " + (args != null && args.Game != null ? args.Game.Name : ""), 400);
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                MenuSection = "@DeckTools",
                Description = "Sincronizar carpeta de juegos (" + GetTargetFolder() + ") ahora",
                Action = _ =>
                {
                    ThreadPool.QueueUserWorkItem(__ => SyncNow("manual menu"));
                }
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args == null || args.Games == null || args.Games.Count == 0)
                yield break;

            HashSet<Guid> excludedIds = LoadExcludedIds();
            Guid excludedTagId = FindExcludedTagId();

            bool anyVisible = args.Games.Any(g => g != null && !IsExcludedFromDock(g, excludedIds, excludedTagId));
            bool anyExcluded = args.Games.Any(g => g != null && IsExcludedFromDock(g, excludedIds, excludedTagId));

            if (anyVisible)
            {
                yield return new GameMenuItem
                {
                    Description = "Ocultar del Dock",
                    Action = a => SetGamesExcludedFromDock(a.Games, true)
                };
            }

            if (anyExcluded)
            {
                yield return new GameMenuItem
                {
                    Description = "Mostrar en el Dock",
                    Action = a => SetGamesExcludedFromDock(a.Games, false)
                };
            }
        }

        private void OnGamesCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            if (_isSyncing) return;
            ScheduleSync("Games.ItemCollectionChanged", 500);
        }

        private void OnGamesUpdated(object sender, ItemUpdatedEventArgs<Game> e)
        {
            if (_isSyncing || e == null || e.UpdatedItems == null) return;

            bool relevant = false;
            foreach (var item in e.UpdatedItems)
            {
                if (item.OldData == null || item.NewData == null) { relevant = true; break; }
                if (item.OldData.Name != item.NewData.Name ||
                    item.OldData.IsInstalled != item.NewData.IsInstalled ||
                    item.OldData.Hidden != item.NewData.Hidden ||
                    item.OldData.Icon != item.NewData.Icon ||
                    item.OldData.CoverImage != item.NewData.CoverImage ||
                    GetDirectWebUrl(item.OldData) != GetDirectWebUrl(item.NewData) ||
                    !SameGuidList(item.OldData.TagIds, item.NewData.TagIds))
                {
                    relevant = true;
                    break;
                }
            }

            if (relevant) ScheduleSync("Games.ItemUpdated", 400);
        }

        private static bool SameGuidList(List<Guid> a, List<Guid> b)
        {
            if (ReferenceEquals(a, b)) return true;
            int countA = a != null ? a.Count : 0;
            int countB = b != null ? b.Count : 0;
            if (countA != countB) return false;
            if (countA == 0) return true;
            for (int i = 0; i < countA; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private HashSet<Guid> LoadExcludedIds()
        {
            var set = new HashSet<Guid>();
            try
            {
                if (File.Exists(_excludedPath))
                {
                    foreach (string line in File.ReadAllLines(_excludedPath))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith("#")) continue;
                        Guid id;
                        if (Guid.TryParse(trimmed, out id)) set.Add(id);
                    }
                }
            }
            catch (Exception) { }
            return set;
        }

        private void SaveExcludedIds(HashSet<Guid> ids)
        {
            try
            {
                string[] lines = ids.Select(id => id.ToString()).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
                File.WriteAllLines(_excludedPath, lines, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                WriteLog("SaveExcludedIds failed: " + ex.Message);
            }
        }

        private Guid FindExcludedTagId()
        {
            try
            {
                Tag tag = PlayniteApi.Database.Tags.FirstOrDefault(t =>
                    t != null && string.Equals(t.Name, ExcludedTagName, StringComparison.OrdinalIgnoreCase));
                return tag != null ? tag.Id : Guid.Empty;
            }
            catch (Exception)
            {
                return Guid.Empty;
            }
        }

        private Guid GetOrCreateExcludedTagId()
        {
            Guid existing = FindExcludedTagId();
            if (existing != Guid.Empty) return existing;

            try
            {
                var tag = new Tag(ExcludedTagName);
                PlayniteApi.Database.Tags.Add(tag);
                return tag.Id;
            }
            catch (Exception ex)
            {
                WriteLog("Failed creating tag '" + ExcludedTagName + "': " + ex.Message);
                return Guid.Empty;
            }
        }

        private static bool IsExcludedFromDock(Game game, HashSet<Guid> excludedIds, Guid excludedTagId)
        {
            if (game == null) return true;
            if (excludedIds != null && excludedIds.Contains(game.Id)) return true;
            if (excludedTagId != Guid.Empty && game.TagIds != null && game.TagIds.Contains(excludedTagId)) return true;
            if (game.Tags != null && game.Tags.Any(t =>
                t != null && string.Equals(t.Name, ExcludedTagName, StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }

        private void SetGamesExcludedFromDock(List<Game> games, bool exclude)
        {
            if (games == null || games.Count == 0) return;

            HashSet<Guid> excludedIds = LoadExcludedIds();
            Guid tagId = exclude ? GetOrCreateExcludedTagId() : FindExcludedTagId();

            foreach (Game game in games)
            {
                if (game == null) continue;

                if (exclude)
                {
                    excludedIds.Add(game.Id);
                    if (tagId != Guid.Empty)
                    {
                        if (game.TagIds == null) game.TagIds = new List<Guid>();
                        if (!game.TagIds.Contains(tagId))
                        {
                            game.TagIds.Add(tagId);
                            PlayniteApi.Database.Games.Update(game);
                        }
                    }
                    WriteLog("Excluded from Dock: '" + game.Name + "' (" + game.Id + ")");
                }
                else
                {
                    excludedIds.Remove(game.Id);
                    if (tagId != Guid.Empty && game.TagIds != null && game.TagIds.Contains(tagId))
                    {
                        game.TagIds.Remove(tagId);
                        PlayniteApi.Database.Games.Update(game);
                    }
                    WriteLog("Restored to Dock: '" + game.Name + "' (" + game.Id + ")");
                }
            }

            SaveExcludedIds(excludedIds);
            ThreadPool.QueueUserWorkItem(_ => SyncNow(exclude ? "Ocultar del Dock" : "Mostrar en el Dock"));
        }

        private void SetupFolderWatcher()
        {
            try
            {
                string folder = GetTargetFolder();
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

                _folderWatcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false
                };
                FileSystemEventHandler handler = (s, e) =>
                {
                    if (_isSyncing || e == null || string.IsNullOrEmpty(e.FullPath)) return;
                    string ext = Path.GetExtension(e.FullPath);
                    if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                        return;

                    // Ignore our own managed Playnite .url files so we never loop.
                    if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    {
                        Guid managedId;
                        if (TryReadManagedGuid(e.FullPath, out managedId)) return;
                    }

                    ScheduleSync("FolderWatcher: " + e.Name, 1000);
                };
                _folderWatcher.Created += handler;
                _folderWatcher.Changed += handler;
                _folderWatcher.Renamed += (s, e) => handler(s, e);
                _folderWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                WriteLog("SetupFolderWatcher failed: " + ex.Message);
            }
        }

        private void ScheduleSync(string reason, int delayMs)
        {
            _pendingReason = reason;
            if (_debounceTimer == null)
            {
                _debounceTimer = new Timer(_ => SyncNow(_pendingReason), null, delayMs, Timeout.Infinite);
            }
            else
            {
                try { _debounceTimer.Change(delayMs, Timeout.Infinite); }
                catch (Exception) { }
            }
        }

        private string GetTargetFolder()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    foreach (string line in File.ReadAllLines(_configPath))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.Length == 0 || trimmed.StartsWith("#") || trimmed.StartsWith(";"))
                            continue;
                        if (trimmed.StartsWith("Folder=", StringComparison.OrdinalIgnoreCase))
                            trimmed = trimmed.Substring(7).Trim();
                        string expanded = Environment.ExpandEnvironmentVariables(trimmed.Trim('"'));
                        if (!string.IsNullOrEmpty(expanded)) return expanded;
                    }
                }
            }
            catch (Exception) { }
            return DefaultTargetFolder;
        }

        private void SyncNow(string reason)
        {
            if (!Monitor.TryEnter(_syncLock))
            {
                ScheduleSync(reason, 800);
                return;
            }

            _isSyncing = true;
            try
            {
                ClearSafeStartupFlag();

                string folder = GetTargetFolder();
                if (string.IsNullOrEmpty(folder)) return;
                if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
                if (!Directory.Exists(_iconCacheDir)) Directory.CreateDirectory(_iconCacheDir);

                WriteLog("--- Sync started (" + reason + ") ---");

                RepairBrokenInstalledGames();

                var unmanagedToRemove = new List<string>();
                ImportOrMatchFolderShortcuts(folder, unmanagedToRemove);
                SyncInstalledGamesToFolder(folder, unmanagedToRemove);

                WriteLog("--- Sync completed ---");
            }
            catch (Exception ex)
            {
                WriteLog("SyncNow error: " + ex);
            }
            finally
            {
                _isSyncing = false;
                Monitor.Exit(_syncLock);
            }
        }

        #region Step A: D:\Juegos -> Playnite

        private sealed class UnmanagedShortcutInfo
        {
            public string FilePath;
            public string Name;
            public bool IsUrl;
            public string Url;
            public string TargetPath;
            public string Arguments;
            public string WorkingDir;
            public string IconFile;
            public int IconIndex;
            public string ParsingName;
        }

        private void RepairBrokenInstalledGames()
        {
            try
            {
                const string MansionTaleUrl = "https://www.facebook.com/gaming/play/1473244006907246/?source=www_homepage_shortcut";
                foreach (Game game in PlayniteApi.Database.Games.Where(g => g != null && g.PluginId == Guid.Empty).ToList())
                {
                    if (string.Equals(game.Name, "Mansion Tale", StringComparison.OrdinalIgnoreCase))
                    {
                        bool hasValidAction = game.GameActions != null &&
                                              game.GameActions.Any(a => a != null && !string.IsNullOrEmpty(a.Path));
                        if (!hasValidAction)
                        {
                            game.GameId = MansionTaleUrl;
                            game.GameActions = new ObservableCollection<GameAction>
                            {
                                new GameAction
                                {
                                    Name = "Play",
                                    Type = GameActionType.URL,
                                    Path = MansionTaleUrl,
                                    IsPlayAction = true
                                }
                            };
                            PlayniteApi.Database.Games.Update(game);
                            WriteLog("Repaired missing URL action on Playnite game '" + game.Name + "' (" + game.Id + ")");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog("RepairBrokenInstalledGames error: " + ex.Message);
            }
        }

        private void ImportOrMatchFolderShortcuts(string folder, List<string> unmanagedToRemove)
        {
            string[] files;
            try { files = Directory.GetFiles(folder); }
            catch (Exception ex)
            {
                WriteLog("Failed to list " + folder + ": " + ex.Message);
                return;
            }

            foreach (string filePath in files)
            {
                string ext = Path.GetExtension(filePath);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    continue;

                Guid existingGuid;
                if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase) &&
                    TryReadManagedGuid(filePath, out existingGuid))
                {
                    // Already managed by DeckToolsSync.
                    continue;
                }

                try
                {
                    UnmanagedShortcutInfo info = ParseUnmanagedShortcut(filePath);
                    if (info == null) continue;

                    Game matched = FindMatchingGame(info);
                    if (matched != null)
                    {
                        WriteLog("Matched unmanaged shortcut '" + info.Name + "' -> Playnite game '" +
                                 matched.Name + "' (" + matched.Id + ")");

                        bool changed = false;
                        if (!matched.IsInstalled) { matched.IsInstalled = true; changed = true; }
                        if (matched.Hidden) { matched.Hidden = false; changed = true; }

                        if (matched.PluginId == Guid.Empty &&
                            (matched.GameActions == null || matched.GameActions.Count == 0 ||
                             matched.GameActions.All(a => a == null || string.IsNullOrEmpty(a.Path))))
                        {
                            Game template = CreateGameFromShortcut(info);
                            if (template.GameActions != null && template.GameActions.Count > 0)
                            {
                                matched.GameActions = template.GameActions;
                                if (string.IsNullOrEmpty(matched.InstallDirectory) && !string.IsNullOrEmpty(template.InstallDirectory))
                                    matched.InstallDirectory = template.InstallDirectory;
                                if (string.IsNullOrEmpty(matched.GameId) && !string.IsNullOrEmpty(template.GameId))
                                    matched.GameId = template.GameId;
                                changed = true;
                                WriteLog("Repaired empty GameActions on matched game '" + matched.Name + "'");
                            }
                        }

                        if (string.IsNullOrEmpty(matched.Icon))
                        {
                            using (Bitmap extractedBmp = ExtractShortcutBitmap(info))
                            {
                                string cachedIco = SaveShortcutIconForGame(matched.Id, info.IconFile, extractedBmp);
                                if (!string.IsNullOrEmpty(cachedIco) && File.Exists(cachedIco))
                                {
                                    try
                                    {
                                        matched.Icon = PlayniteApi.Database.AddFile(cachedIco, matched.Id);
                                        changed = true;
                                    }
                                    catch (Exception ex)
                                    {
                                        WriteLog("AddFile icon for " + matched.Name + " failed: " + ex.Message);
                                    }
                                }
                            }
                        }

                        if (changed)
                        {
                            PlayniteApi.Database.Games.Update(matched);
                        }
                    }
                    else
                    {
                        WriteLog("Importing new game into Playnite from shortcut: '" + info.Name + "' (" + filePath + ")");
                        Game created = CreateGameFromShortcut(info);
                        PlayniteApi.Database.Games.Add(created);

                        using (Bitmap extractedBmp = ExtractShortcutBitmap(info))
                        {
                            string cachedIco = SaveShortcutIconForGame(created.Id, info.IconFile, extractedBmp);
                            if (!string.IsNullOrEmpty(cachedIco) && File.Exists(cachedIco))
                            {
                                try
                                {
                                    created.Icon = PlayniteApi.Database.AddFile(cachedIco, created.Id);
                                    PlayniteApi.Database.Games.Update(created);
                                }
                                catch (Exception ex)
                                {
                                    WriteLog("AddFile icon for new game " + created.Name + " failed: " + ex.Message);
                                }
                            }
                        }
                    }

                    unmanagedToRemove.Add(filePath);
                }
                catch (Exception ex)
                {
                    WriteLog("Error processing unmanaged shortcut " + filePath + ": " + ex.Message);
                }
            }
        }

        private static UnmanagedShortcutInfo ParseUnmanagedShortcut(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            var info = new UnmanagedShortcutInfo
            {
                FilePath = filePath,
                Name = Path.GetFileNameWithoutExtension(filePath),
                IsUrl = ext.Equals(".url", StringComparison.OrdinalIgnoreCase)
            };

            if (info.IsUrl)
            {
                foreach (string line in File.ReadAllLines(filePath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        info.Url = trimmed.Substring(4).Trim();
                    else if (trimmed.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                        info.IconFile = Environment.ExpandEnvironmentVariables(trimmed.Substring(9).Trim());
                    else if (trimmed.StartsWith("IconIndex=", StringComparison.OrdinalIgnoreCase))
                        int.TryParse(trimmed.Substring(10).Trim(), out info.IconIndex);
                }
                return info;
            }

            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(filePath, 0);

            var sb = new StringBuilder(1024);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            info.TargetPath = Environment.ExpandEnvironmentVariables(sb.ToString());

            sb.Length = 0;
            link.GetArguments(sb, sb.Capacity);
            info.Arguments = sb.ToString();

            sb.Length = 0;
            link.GetWorkingDirectory(sb, sb.Capacity);
            info.WorkingDir = Environment.ExpandEnvironmentVariables(sb.ToString());

            sb.Length = 0;
            link.GetIconLocation(sb, sb.Capacity, out info.IconIndex);
            info.IconFile = Environment.ExpandEnvironmentVariables(sb.ToString());

            IntPtr pidl = IntPtr.Zero;
            try
            {
                link.GetIDList(out pidl);
                if (pidl != IntPtr.Zero)
                {
                    IntPtr pszName;
                    // SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000
                    if (SHGetNameFromIDList(pidl, 0x80028000, out pszName) == 0 && pszName != IntPtr.Zero)
                    {
                        info.ParsingName = Marshal.PtrToStringUni(pszName);
                        Marshal.FreeCoTaskMem(pszName);
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                if (pidl != IntPtr.Zero) Marshal.FreeCoTaskMem(pidl);
            }

            return info;
        }

        private Game FindMatchingGame(UnmanagedShortcutInfo info)
        {
            List<Game> allGames = PlayniteApi.Database.Games.ToList();

            // 1. Steam URL match (steam://rungameid/<AppId>)
            if (!string.IsNullOrEmpty(info.Url) &&
                info.Url.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase))
            {
                string appId = info.Url.Substring("steam://rungameid/".Length).Trim().Trim('/');
                Game bySteamId = allGames.FirstOrDefault(g =>
                    string.Equals(g.GameId, appId, StringComparison.OrdinalIgnoreCase));
                if (bySteamId != null) return bySteamId;
            }

            // 2. Direct URL match in GameActions or Links or GameId
            if (!string.IsNullOrEmpty(info.Url))
            {
                Game byUrl = allGames.FirstOrDefault(g =>
                    string.Equals(g.GameId, info.Url, StringComparison.OrdinalIgnoreCase) ||
                    (g.GameActions != null && g.GameActions.Any(a =>
                        a.Type == GameActionType.URL &&
                        string.Equals(a.Path, info.Url, StringComparison.OrdinalIgnoreCase))) ||
                    (g.Links != null && g.Links.Any(l =>
                        string.Equals(l.Url, info.Url, StringComparison.OrdinalIgnoreCase))));
                if (byUrl != null) return byUrl;
            }

            // 3. Target executable / InstallDirectory match
            if (!string.IsNullOrEmpty(info.TargetPath) && File.Exists(info.TargetPath))
            {
                string targetDir = Path.GetDirectoryName(info.TargetPath);
                if (!string.IsNullOrEmpty(targetDir))
                {
                    Game byDir = allGames.FirstOrDefault(g =>
                        !string.IsNullOrEmpty(g.InstallDirectory) &&
                        PathsOverlap(g.InstallDirectory, targetDir));
                    if (byDir != null) return byDir;
                }

                Game byActionPath = allGames.FirstOrDefault(g =>
                    g.GameActions != null && g.GameActions.Any(a =>
                        !string.IsNullOrEmpty(a.Path) &&
                        string.Equals(ExpandGamePath(g, a.Path), info.TargetPath, StringComparison.OrdinalIgnoreCase)));
                if (byActionPath != null) return byActionPath;
            }

            // 4. UWP / Store AUMID or PackageFamilyName match
            if (!string.IsNullOrEmpty(info.ParsingName) && info.ParsingName.Contains("!"))
            {
                string pfn = info.ParsingName.Split('!')[0];
                Game byPfn = allGames.FirstOrDefault(g =>
                    (!string.IsNullOrEmpty(g.GameId) &&
                     (g.GameId.IndexOf(pfn, StringComparison.OrdinalIgnoreCase) >= 0 ||
                      info.ParsingName.IndexOf(g.GameId, StringComparison.OrdinalIgnoreCase) >= 0)) ||
                    (g.GameActions != null && g.GameActions.Any(a =>
                        (!string.IsNullOrEmpty(a.Arguments) && a.Arguments.IndexOf(pfn, StringComparison.OrdinalIgnoreCase) >= 0) ||
                        (!string.IsNullOrEmpty(a.Path) && a.Path.IndexOf(pfn, StringComparison.OrdinalIgnoreCase) >= 0))));
                if (byPfn != null) return byPfn;
            }

            // 5. Exact Name match (case-insensitive), preferring installed games
            Game byExactName = allGames
                .Where(g => !string.IsNullOrEmpty(g.Name) &&
                            string.Equals(g.Name.Trim(), info.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(g => g.IsInstalled)
                .FirstOrDefault();
            if (byExactName != null) return byExactName;

            // 6. Alphanumeric normalized Name match
            string normShortcut = NormalizeName(info.Name);
            if (normShortcut.Length > 0)
            {
                Game byNormName = allGames
                    .Where(g => !string.IsNullOrEmpty(g.Name) && NormalizeName(g.Name) == normShortcut)
                    .OrderByDescending(g => g.IsInstalled)
                    .FirstOrDefault();
                if (byNormName != null) return byNormName;
            }

            return null;
        }

        private static Game CreateGameFromShortcut(UnmanagedShortcutInfo info)
        {
            var game = new Game(info.Name)
            {
                IsInstalled = true,
                Hidden = false
            };

            if (info.IsUrl && !string.IsNullOrEmpty(info.Url))
            {
                game.GameId = info.Url;
                game.GameActions = new ObservableCollection<GameAction>
                {
                    new GameAction
                    {
                        Name = "Play",
                        Type = GameActionType.URL,
                        Path = info.Url,
                        IsPlayAction = true
                    }
                };
            }
            else if (!string.IsNullOrEmpty(info.TargetPath))
            {
                string dir = Path.GetDirectoryName(info.TargetPath);
                game.InstallDirectory = dir;
                game.GameActions = new ObservableCollection<GameAction>
                {
                    new GameAction
                    {
                        Name = "Play",
                        Type = GameActionType.File,
                        Path = info.TargetPath,
                        Arguments = string.IsNullOrEmpty(info.Arguments) ? null : info.Arguments,
                        WorkingDir = string.IsNullOrEmpty(info.WorkingDir) ? dir : info.WorkingDir,
                        IsPlayAction = true
                    }
                };
            }
            else if (!string.IsNullOrEmpty(info.ParsingName))
            {
                game.GameId = info.ParsingName;
                game.GameActions = new ObservableCollection<GameAction>
                {
                    new GameAction
                    {
                        Name = "Play",
                        Type = GameActionType.File,
                        Path = "explorer.exe",
                        Arguments = "shell:AppsFolder\\" + info.ParsingName,
                        IsPlayAction = true
                    }
                };
            }

            return game;
        }

        #endregion

        #region Step B: Playnite -> D:\Juegos

        private void SyncInstalledGamesToFolder(string folder, List<string> unmanagedToRemove)
        {
            HashSet<Guid> excludedIds = LoadExcludedIds();
            Guid excludedTagId = FindExcludedTagId();

            List<Game> activeGames = PlayniteApi.Database.Games
                .Where(g => g != null && g.IsInstalled && !g.Hidden && !IsExcludedFromDock(g, excludedIds, excludedTagId))
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var expectedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Game game in activeGames)
            {
                try
                {
                    string safeName = SanitizeFileName(game.Name);
                    if (string.IsNullOrEmpty(safeName)) safeName = game.Id.ToString();

                    string urlFilePath = Path.Combine(folder, safeName + ".url");
                    // Handle rare case where two installed games sanitize to the same filename
                    if (expectedFiles.Contains(urlFilePath))
                    {
                        urlFilePath = Path.Combine(folder, safeName + " (" + game.Id.ToString().Substring(0, 6) + ").url");
                    }

                    string iconPath = ResolveBestIconForGame(game);
                    string content = BuildUrlFileContent(game, iconPath);

                    WriteUrlFileIfChanged(urlFilePath, content);
                    expectedFiles.Add(urlFilePath);
                }
                catch (Exception ex)
                {
                    WriteLog("Error syncing game '" + game.Name + "' to folder: " + ex.Message);
                }
            }

            // Delete unmanaged shortcuts that were converted/imported in Step A
            foreach (string oldPath in unmanagedToRemove)
            {
                if (expectedFiles.Contains(oldPath)) continue;
                TryDeleteFile(oldPath, "replaced unmanaged shortcut");
            }

            // Delete any managed Playnite .url in the folder that is no longer active
            string[] currentFiles;
            try { currentFiles = Directory.GetFiles(folder, "*.url"); }
            catch (Exception) { return; }

            foreach (string file in currentFiles)
            {
                if (expectedFiles.Contains(file)) continue;

                Guid managedId;
                if (TryReadManagedGuid(file, out managedId))
                {
                    TryDeleteFile(file, "game uninstalled, hidden, or renamed (" + managedId + ")");
                }
            }
        }

        private static string GetDirectWebUrl(Game game)
        {
            if (game == null || game.GameActions == null) return null;
            foreach (GameAction action in game.GameActions)
            {
                if (action != null && action.IsPlayAction &&
                    action.Type == GameActionType.URL &&
                    !string.IsNullOrEmpty(action.Path))
                {
                    string path = action.Path.Trim();
                    if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
            }
            return null;
        }

        private static string BuildUrlFileContent(Game game, string iconPath)
        {
            string directWebUrl = GetDirectWebUrl(game);
            string targetUrl = !string.IsNullOrEmpty(directWebUrl)
                ? directWebUrl
                : (PlayniteUrlPrefix + game.Id);

            var sb = new StringBuilder();
            sb.AppendLine("[InternetShortcut]");
            sb.AppendLine("URL=" + targetUrl);
            sb.AppendLine("PlayniteId=" + game.Id);
            if (!string.IsNullOrEmpty(iconPath))
            {
                sb.AppendLine("IconFile=" + iconPath);
                sb.AppendLine("IconIndex=0");
            }
            return sb.ToString();
        }

        private void WriteUrlFileIfChanged(string path, string content)
        {
            try
            {
                if (File.Exists(path))
                {
                    string existing = File.ReadAllText(path);
                    if (string.Equals(existing.Trim(), content.Trim(), StringComparison.Ordinal))
                        return;
                }

                File.WriteAllText(path, content, Encoding.UTF8);
                WriteLog("Wrote shortcut: " + path);
            }
            catch (Exception ex)
            {
                WriteLog("Failed writing " + path + ": " + ex.Message);
            }
        }

        private void TryDeleteFile(string path, string reason)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    WriteLog("Deleted shortcut: " + path + " (" + reason + ")");
                }
            }
            catch (Exception ex)
            {
                WriteLog("Failed deleting " + path + ": " + ex.Message);
            }
        }

        #endregion

        #region Icon Resolution & 256x256 .ico Generation

        private string GetCachedIcoPath(Guid gameId)
        {
            return Path.Combine(_iconCacheDir, gameId + ".ico");
        }

        private string SaveShortcutIconForGame(Guid gameId, string originalIconFile, Bitmap extractedBmp)
        {
            string cachedIco = GetCachedIcoPath(gameId);
            try
            {
                if (!string.IsNullOrEmpty(originalIconFile) &&
                    File.Exists(originalIconFile) &&
                    originalIconFile.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(originalIconFile, cachedIco, true);
                    return cachedIco;
                }

                if (extractedBmp != null)
                {
                    SaveBitmapAsIco(extractedBmp, cachedIco);
                    return cachedIco;
                }
            }
            catch (Exception ex)
            {
                WriteLog("SaveShortcutIconForGame failed for " + gameId + ": " + ex.Message);
            }
            return File.Exists(cachedIco) ? cachedIco : null;
        }

        private string ResolveBestIconForGame(Game game)
        {
            string cachedIco = GetCachedIcoPath(game.Id);

            // Check if Playnite has a database icon file
            string dbIconPath = null;
            if (!string.IsNullOrEmpty(game.Icon))
            {
                try { dbIconPath = PlayniteApi.Database.GetFullFilePath(game.Icon); }
                catch (Exception) { dbIconPath = null; }
            }

            // If we already have a valid cached .ico and our cache is up-to-date, return it.
            if (File.Exists(cachedIco))
            {
                long existingLen = 0;
                try { existingLen = new FileInfo(cachedIco).Length; } catch (Exception) { }
                if (existingLen > 1500)
                {
                    bool dbIconIsNewer = !string.IsNullOrEmpty(dbIconPath) &&
                                         File.Exists(dbIconPath) &&
                                         File.GetLastWriteTimeUtc(dbIconPath) > File.GetLastWriteTimeUtc(cachedIco).AddSeconds(2);
                    if (!dbIconIsNewer)
                    {
                        return cachedIco;
                    }
                }
            }

            // Otherwise, find the highest resolution artwork available for this game:
            // use Playnite's dbIconPath directly if it is already >= 64x64; only probe executable/UWP
            // when Playnite has no icon or has a tiny low-res icon (such as Steam's 32x32 client .ico).
            Bitmap bestBmp = null;
            try
            {
                Bitmap dbBmp = LoadBitmapFromAnyFile(dbIconPath, 0);
                if (dbBmp != null && dbBmp.Width >= 64 && dbBmp.Height >= 64)
                {
                    bestBmp = dbBmp;
                }
                else
                {
                    Bitmap exeBmp = ExtractFromGameExecutableOrUwp(game);
                    if (exeBmp != null && dbBmp != null)
                    {
                        if (exeBmp.Width * exeBmp.Height > dbBmp.Width * dbBmp.Height)
                        {
                            bestBmp = exeBmp;
                            dbBmp.Dispose();
                        }
                        else
                        {
                            bestBmp = dbBmp;
                            exeBmp.Dispose();
                        }
                    }
                    else
                    {
                        bestBmp = exeBmp ?? dbBmp;
                    }
                }

                if (bestBmp == null && !string.IsNullOrEmpty(game.CoverImage))
                {
                    try
                    {
                        string coverPath = PlayniteApi.Database.GetFullFilePath(game.CoverImage);
                        bestBmp = LoadBitmapFromAnyFile(coverPath, 0);
                    }
                    catch (Exception) { }
                }

                if (bestBmp != null)
                {
                    SaveBitmapAsIco(bestBmp, cachedIco);

                    if (string.IsNullOrEmpty(game.Icon) && File.Exists(cachedIco))
                    {
                        try
                        {
                            game.Icon = PlayniteApi.Database.AddFile(cachedIco, game.Id);
                            PlayniteApi.Database.Games.Update(game);
                        }
                        catch (Exception) { }
                    }

                    return cachedIco;
                }
            }
            finally
            {
                if (bestBmp != null) bestBmp.Dispose();
            }

            if (File.Exists(cachedIco)) return cachedIco;
            if (!string.IsNullOrEmpty(dbIconPath) && File.Exists(dbIconPath)) return dbIconPath;

            string playniteExe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Playnite\Playnite.DesktopApp.exe");
            return File.Exists(playniteExe) ? playniteExe : null;
        }

        private Bitmap ExtractFromGameExecutableOrUwp(Game game)
        {
            // 1. Check GameActions for a file or UWP explorer shell:AppsFolder argument
            if (game.GameActions != null)
            {
                foreach (GameAction action in game.GameActions)
                {
                    if (action == null) continue;
                    if (!string.IsNullOrEmpty(action.Arguments) &&
                        action.Arguments.IndexOf("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int idx = action.Arguments.IndexOf("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);
                        string aumid = action.Arguments.Substring(idx + "shell:AppsFolder\\".Length).Trim().Trim('"');
                        Bitmap uwpBmp = ExtractUwpPackageLogo(aumid, game.InstallDirectory);
                        if (uwpBmp != null) return uwpBmp;
                    }

                    if (action.Type == GameActionType.File && !string.IsNullOrEmpty(action.Path))
                    {
                        string fullPath = ExpandGamePath(game, action.Path);
                        if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                        {
                            Bitmap bmp = LoadBitmapFromAnyFile(fullPath, 0);
                            if (bmp != null) return bmp;
                        }
                    }
                }
            }

            // 2. Check if GameId or InstallDirectory matches a registered UWP package
            if (!string.IsNullOrEmpty(game.GameId) || !string.IsNullOrEmpty(game.InstallDirectory))
            {
                Bitmap uwpBmp = ExtractUwpPackageLogo(game.GameId, game.InstallDirectory);
                if (uwpBmp != null) return uwpBmp;
            }

            // 3. Search InstallDirectory (bounded depth) for the main game executable
            if (!string.IsNullOrEmpty(game.InstallDirectory) && Directory.Exists(game.InstallDirectory))
            {
                try
                {
                    var exes = new List<string>();
                    CollectExecutablesBounded(game.InstallDirectory, 0, 2, exes);
                    string bestExe = exes
                        .Where(e => !IsHelperExecutable(Path.GetFileName(e)))
                        .OrderByDescending(e => ScoreExecutableForGame(e, game.Name))
                        .FirstOrDefault();

                    if (!string.IsNullOrEmpty(bestExe))
                    {
                        Bitmap bmp = LoadBitmapFromAnyFile(bestExe, 0);
                        if (bmp != null) return bmp;
                    }
                }
                catch (Exception) { }
            }

            return null;
        }

        private static void CollectExecutablesBounded(string dir, int depth, int maxDepth, List<string> results)
        {
            if (depth > maxDepth || string.IsNullOrEmpty(dir)) return;
            try
            {
                results.AddRange(Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly));
                if (depth < maxDepth)
                {
                    foreach (string sub in Directory.GetDirectories(dir))
                    {
                        CollectExecutablesBounded(sub, depth + 1, maxDepth, results);
                    }
                }
            }
            catch (Exception) { }
        }

        private static bool IsHelperExecutable(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return true;
            string lower = fileName.ToLowerInvariant();
            return lower.Contains("unitycrash") ||
                   lower.Contains("crashreport") ||
                   lower.Contains("crashhandler") ||
                   lower.StartsWith("unins") ||
                   lower.Contains("vc_redist") ||
                   lower.Contains("dxsetup") ||
                   lower.Contains("EasyAntiCheat") ||
                   lower.Contains("easyanticheat") ||
                   lower.Contains("battleye");
        }

        private static int ScoreExecutableForGame(string exePath, string gameName)
        {
            int score = 0;
            string exeName = Path.GetFileNameWithoutExtension(exePath);
            string normExe = NormalizeName(exeName);
            string normGame = NormalizeName(gameName);

            if (normExe.Length > 0 && normGame.Length > 0)
            {
                if (normExe == normGame) score += 1000;
                else if (normExe.Contains(normGame) || normGame.Contains(normExe)) score += 500;
            }

            try
            {
                long size = new FileInfo(exePath).Length;
                score += (int)Math.Min(300L, size / (1024 * 1024));
            }
            catch (Exception) { }

            return score;
        }

        private static Bitmap ExtractUwpPackageLogo(string gameIdOrAumid, string installDir)
        {
            try
            {
                string familyPrefix = gameIdOrAumid;
                if (!string.IsNullOrEmpty(familyPrefix) && familyPrefix.Contains("!"))
                {
                    familyPrefix = familyPrefix.Substring(0, familyPrefix.IndexOf('!'));
                    if (familyPrefix.Contains("_"))
                        familyPrefix = familyPrefix.Substring(0, familyPrefix.LastIndexOf('_'));
                }

                using (RegistryKey packagesKey = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                {
                    if (packagesKey == null) return null;

                    foreach (string pkgName in packagesKey.GetSubKeyNames())
                    {
                        using (RegistryKey pkgKey = packagesKey.OpenSubKey(pkgName))
                        {
                            if (pkgKey == null) continue;
                            string rootFolder = pkgKey.GetValue("PackageRootFolder") as string;
                            if (string.IsNullOrEmpty(rootFolder) || !Directory.Exists(rootFolder)) continue;

                            bool matched = (!string.IsNullOrEmpty(familyPrefix) &&
                                            pkgName.IndexOf(familyPrefix, StringComparison.OrdinalIgnoreCase) >= 0) ||
                                           (!string.IsNullOrEmpty(installDir) &&
                                            PathsOverlap(installDir, rootFolder));
                            if (!matched) continue;

                            string assetsDir = Path.Combine(rootFolder, "Assets");
                            string searchDir = Directory.Exists(assetsDir) ? assetsDir : rootFolder;
                            string[] pngs = Directory.GetFiles(searchDir, "*.png", SearchOption.TopDirectoryOnly);
                            if (pngs.Length == 0) continue;

                            string bestPng =
                                pngs.FirstOrDefault(p => p.IndexOf("targetsize-256", StringComparison.OrdinalIgnoreCase) >= 0) ??
                                pngs.FirstOrDefault(p => p.IndexOf("Square310x310Logo", StringComparison.OrdinalIgnoreCase) >= 0) ??
                                pngs.FirstOrDefault(p => p.IndexOf("Square150x150Logo", StringComparison.OrdinalIgnoreCase) >= 0) ??
                                pngs.FirstOrDefault(p => p.IndexOf("StoreLogo", StringComparison.OrdinalIgnoreCase) >= 0) ??
                                pngs.FirstOrDefault(p => p.IndexOf("Square44x44Logo", StringComparison.OrdinalIgnoreCase) >= 0);

                            if (!string.IsNullOrEmpty(bestPng))
                            {
                                Bitmap bmp = LoadBitmapFromAnyFile(bestPng, 0);
                                if (bmp != null) return bmp;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        private static Bitmap ExtractShortcutBitmap(UnmanagedShortcutInfo info)
        {
            if (!string.IsNullOrEmpty(info.IconFile) && File.Exists(info.IconFile))
            {
                Bitmap bmp = LoadBitmapFromAnyFile(info.IconFile, info.IconIndex);
                if (bmp != null) return bmp;
            }

            if (!string.IsNullOrEmpty(info.TargetPath) && File.Exists(info.TargetPath))
            {
                Bitmap bmp = LoadBitmapFromAnyFile(info.TargetPath, 0);
                if (bmp != null) return bmp;
            }

            if (!string.IsNullOrEmpty(info.ParsingName) && info.ParsingName.Contains("!"))
            {
                Bitmap bmp = ExtractUwpPackageLogo(info.ParsingName, null);
                if (bmp != null) return bmp;
            }

            return ExtractFromShell(info.FilePath);
        }

        private static Bitmap LoadBitmapFromAnyFile(string file, int index)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;

            string ext = Path.GetExtension(file);
            if (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (Image img = Image.FromFile(file))
                        return new Bitmap(img);
                }
                catch (Exception) { return null; }
            }

            var handles = new IntPtr[1];
            var ids = new int[1];
            if (PrivateExtractIcons(file, index, 256, 256, handles, ids, 1, 0) >= 1 &&
                handles[0] != IntPtr.Zero)
            {
                try
                {
                    using (Icon ic = Icon.FromHandle(handles[0]))
                        return ic.ToBitmap();
                }
                catch (Exception) { }
                finally
                {
                    DestroyIcon(handles[0]);
                }
            }

            return ExtractFromShell(file);
        }

        private static Bitmap ExtractFromShell(string parsingPath)
        {
            if (string.IsNullOrEmpty(parsingPath)) return null;

            const int IconOnly = 0x4;
            IntPtr handle = IntPtr.Zero;
            try
            {
                IShellItemImageFactory factory;
                SHCreateItemFromParsingName(parsingPath, IntPtr.Zero,
                    typeof(IShellItemImageFactory).GUID, out factory);

                if (factory.GetImage(new ShellSize { Width = 256, Height = 256 }, IconOnly, out handle) != 0 ||
                    handle == IntPtr.Zero)
                    return null;

                BitmapInfo info;
                if (GetObject(handle, Marshal.SizeOf(typeof(BitmapInfo)), out info) == 0 ||
                    info.Bits == IntPtr.Zero || info.BitsPixel != 32)
                    return null;

                int height = Math.Abs(info.Height);
                var result = new Bitmap(info.Width, height, PixelFormat.Format32bppPArgb);
                var data = result.LockBits(new Rectangle(0, 0, info.Width, height),
                    ImageLockMode.WriteOnly, result.PixelFormat);
                try
                {
                    var row = new byte[info.WidthBytes];
                    for (int y = 0; y < height; y++)
                    {
                        int from = info.Height > 0 ? height - 1 - y : y;
                        Marshal.Copy(IntPtr.Add(info.Bits, from * info.WidthBytes), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), Math.Min(row.Length, data.Stride));
                    }
                }
                finally
                {
                    result.UnlockBits(data);
                }
                return result;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero) DeleteObject(handle);
            }
        }

        private static void SaveBitmapAsIco(Bitmap source, string icoPath)
        {
            const int TargetSize = 256;
            using (var canvas = new Bitmap(TargetSize, TargetSize, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    float scale = Math.Min((float)TargetSize / source.Width, (float)TargetSize / source.Height);
                    int w = Math.Max(1, (int)Math.Round(source.Width * scale));
                    int h = Math.Max(1, (int)Math.Round(source.Height * scale));
                    int x = (TargetSize - w) / 2;
                    int y = (TargetSize - h) / 2;

                    g.DrawImage(source, new Rectangle(x, y, w, h));
                }

                byte[] pngBytes;
                using (var ms = new MemoryStream())
                {
                    canvas.Save(ms, ImageFormat.Png);
                    pngBytes = ms.ToArray();
                }

                using (var fs = new FileStream(icoPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                using (var bw = new BinaryWriter(fs))
                {
                    // ICONDIR header (6 bytes)
                    bw.Write((ushort)0); // Reserved
                    bw.Write((ushort)1); // Type: 1 = ICO
                    bw.Write((ushort)1); // Count: 1 image

                    // ICONDIRENTRY (16 bytes)
                    bw.Write((byte)0);   // Width: 0 means 256
                    bw.Write((byte)0);   // Height: 0 means 256
                    bw.Write((byte)0);   // ColorCount
                    bw.Write((byte)0);   // Reserved
                    bw.Write((ushort)1); // Planes
                    bw.Write((ushort)32);// BitCount
                    bw.Write((uint)pngBytes.Length); // BytesInRes
                    bw.Write((uint)22);  // ImageOffset (6 + 16)

                    bw.Write(pngBytes);
                }
            }
        }

        #endregion

        #region Helpers & Native P/Invoke

        private static bool TryReadManagedGuid(string urlFilePath, out Guid gameId)
        {
            gameId = Guid.Empty;
            if (!File.Exists(urlFilePath)) return false;

            try
            {
                foreach (string line in File.ReadAllLines(urlFilePath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("PlayniteId=", StringComparison.OrdinalIgnoreCase))
                    {
                        string rawGuid = trimmed.Substring("PlayniteId=".Length).Trim();
                        if (Guid.TryParse(rawGuid, out gameId)) return true;
                    }
                    if (trimmed.StartsWith("URL=" + PlayniteUrlPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string rawGuid = trimmed.Substring(("URL=" + PlayniteUrlPrefix).Length).Trim();
                        if (Guid.TryParse(rawGuid, out gameId)) return true;
                    }
                }
            }
            catch (Exception) { }
            return false;
        }

        private static string GetCurrentDesktopName()
        {
            try
            {
                IntPtr hDesk = GetThreadDesktop(GetCurrentThreadId());
                if (hDesk == IntPtr.Zero) return "unknown";
                var sb = new StringBuilder(256);
                int needed;
                if (GetUserObjectInformationW(hDesk, 2, sb, sb.Capacity * 2, out needed))
                {
                    return sb.ToString();
                }
            }
            catch (Exception) { }
            return "unknown";
        }

        private static string ExpandGamePath(Game game, string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath)) return rawPath;
            string result = rawPath;
            if (game != null && !string.IsNullOrEmpty(game.InstallDirectory))
            {
                result = result.Replace("{InstallDir}", game.InstallDirectory.TrimEnd('\\', '/'));
            }
            return Environment.ExpandEnvironmentVariables(result.Trim('"'));
        }

        private static bool PathsOverlap(string pathA, string pathB)
        {
            try
            {
                string fullA = Path.GetFullPath(pathA).TrimEnd('\\', '/') + "\\";
                string fullB = Path.GetFullPath(pathB).TrimEnd('\\', '/') + "\\";
                return fullA.StartsWith(fullB, StringComparison.OrdinalIgnoreCase) ||
                       fullB.StartsWith(fullA, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string NormalizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return string.Empty;
            string replaced = name.Replace(":", " -");
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(replaced.Length);
            foreach (char c in replaced)
            {
                if (Array.IndexOf(invalid, c) < 0) sb.Append(c);
            }

            string cleaned = sb.ToString().Trim().TrimEnd('.');
            while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
            return cleaned;
        }

        private void WriteLog(string message)
        {
            try
            {
                string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine;
                File.AppendAllText(_logPath, line, Encoding.UTF8);
            }
            catch (Exception) { }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHGetNameFromIDList(IntPtr pidl, uint sigdnName, out IntPtr ppszName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int PrivateExtractIcons(string file, int index, int cx, int cy,
                                                      IntPtr[] icons, int[] ids, int count, int flags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext,
            [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll")]
        private static extern int GetObject(IntPtr handle, int size, out BitmapInfo info);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDesktop(uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetUserObjectInformationW(IntPtr hObj, int nIndex,
            [Out] StringBuilder pvInfo, int nLength, out int lpnLengthNeeded);

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public int Type, Width, Height, WidthBytes;
            public short Planes, BitsPixel;
            public IntPtr Bits;
        }

        #endregion
    }
}
