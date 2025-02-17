﻿using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ICSharpCode.SharpZipLib.Zip;
using ResourceRedirector;
using Shared;
using Sideloader.AutoResolver;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Sideloader
{
    /// <summary>
    /// Allows for loading mods in .zip format from the mods folder and automatically resolves ID conflicts.
    /// </summary>
    public partial class Sideloader
    {
        public const string GUID = "com.bepis.bepinex.sideloader";
        public const string PluginName = "Sideloader";
        public const string Version = BepisPlugins.Metadata.PluginsVersion;
        internal static new ManualLogSource Logger;

        public static string ModsDirectory { get; } = Path.Combine(Paths.GameRootPath, "mods");
        protected List<ZipFile> Archives = new List<ZipFile>();

        public static readonly List<Manifest> LoadedManifests = new List<Manifest>();

        protected static Dictionary<string, ZipFile> PngList = new Dictionary<string, ZipFile>();
        protected static HashSet<string> PngFolderList = new HashSet<string>();
        protected static HashSet<string> PngFolderOnlyList = new HashSet<string>();
        private readonly List<ResolveInfo> _gatheredResolutionInfos = new List<ResolveInfo>();

        public static ConfigWrapper<bool> MissingModWarning { get; private set; }
        public static ConfigWrapper<bool> DebugLogging { get; private set; }
        public static ConfigWrapper<bool> DebugResolveInfoLogging { get; private set; }
        public static ConfigWrapper<bool> KeepMissingAccessories { get; private set; }
        public static ConfigWrapper<string> AdditionalModsDirectory { get; private set; }

        private void Awake()
        {
            Logger = base.Logger;

            Hooks.InstallHooks();
            AutoResolver.Hooks.InstallHooks();
            ResourceRedirector.ResourceRedirector.AssetResolvers.Add(RedirectHook);
            ResourceRedirector.ResourceRedirector.AssetBundleResolvers.Add(AssetBundleRedirectHook);

            MissingModWarning = Config.GetSetting("Settings", "Show missing mod warnings", true, new ConfigDescription("Whether missing mod warnings will be displayed on screen. Messages will still be written to the log."));
            DebugLogging = Config.GetSetting("Settings", "Debug logging", false, new ConfigDescription("Enable additional logging useful for debugging issues with Sideloader and sideloader mods.\nWarning: Will increase load and save times noticeably and will result in very large log sizes."));
            DebugResolveInfoLogging = Config.GetSetting("Settings", "Debug resolve info logging", false, new ConfigDescription("Enable verbose logging for debugging issues with Sideloader and sideloader mods.\nWarning: Will increase game start up time and will result in very large log sizes."));
            KeepMissingAccessories = Config.GetSetting("Settings", "Keep missing accessories", false, new ConfigDescription("Missing accessories will be replaced by a default item with color and position information intact when loaded in the character maker."));
            AdditionalModsDirectory = Config.GetSetting("General", "Additional mods directory", FindKoiZipmodDir(), new ConfigDescription("Additional directory to load zipmods from."));

            if (!Directory.Exists(ModsDirectory))
                Logger.Log(LogLevel.Warning, "Could not find the mods directory: " + ModsDirectory);

            if (!AdditionalModsDirectory.Value.IsNullOrWhiteSpace() && !Directory.Exists(AdditionalModsDirectory.Value))
                Logger.Log(LogLevel.Warning, "Could not find the additional mods directory specified in config: " + AdditionalModsDirectory.Value);

            LoadModsFromDirectories(ModsDirectory, AdditionalModsDirectory.Value);
        }

        private static string GetRelativeArchiveDir(string archiveDir) => archiveDir.Length < ModsDirectory.Length ? archiveDir : archiveDir.Substring(ModsDirectory.Length).Trim(' ', '/', '\\');

        private static IEnumerable<string> GetZipmodsFromDirectory(string modDirectory)
        {
            Logger.LogInfo("Loading mods from directory: " + modDirectory);
            return Directory.GetFiles(modDirectory, "*", SearchOption.AllDirectories)
                            .Where(x => x.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                                        x.EndsWith(".zipmod", StringComparison.OrdinalIgnoreCase));
        }

        private void LoadModsFromDirectories(params string[] modDirectories)
        {
            Logger.Log(LogLevel.Info, "Scanning the \"mods\" directory...");

            var stopWatch = Stopwatch.StartNew();

            // Look for mods, load their manifests
            var allMods = modDirectories.Where(Directory.Exists).SelectMany(GetZipmodsFromDirectory);

            var archives = new Dictionary<ZipFile, Manifest>();

            foreach (var archivePath in allMods)
            {
                ZipFile archive = null;
                try
                {
                    archive = new ZipFile(archivePath);

                    if (Manifest.TryLoadFromZip(archive, out Manifest manifest))
                        if (manifest.Game.IsNullOrWhiteSpace() || GameNameList.Contains(manifest.Game.ToLower().Replace("!", "")))
                            archives.Add(archive, manifest);
                        else
                            Logger.Log(LogLevel.Info, $"Skipping archive \"{GetRelativeArchiveDir(archivePath)}\" because it's meant for {manifest.Game}");
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Error, $"Failed to load archive \"{GetRelativeArchiveDir(archivePath)}\" with error: {ex}");
                    archive?.Close();
                }
            }

            var modLoadInfoSb = new StringBuilder();

            // Handle duplicate GUIDs and load unique mods
            foreach (var modGroup in archives.GroupBy(x => x.Value.GUID))
            {
                // Order by version if available, else use modified dates (less reliable)
                // If versions match, prefer mods inside folders or with more descriptive names so modpacks are preferred
                var orderedModsQuery = modGroup.All(x => !string.IsNullOrEmpty(x.Value.Version))
                    ? modGroup.OrderByDescending(x => x.Value.Version, new ManifestVersionComparer()).ThenByDescending(x => x.Key.Name.Length)
                    : modGroup.OrderByDescending(x => File.GetLastWriteTime(x.Key.Name));

                var orderedMods = orderedModsQuery.ToList();

                if (orderedMods.Count > 1)
                {
                    var modList = string.Join(", ", orderedMods.Select(x => '"' + GetRelativeArchiveDir(x.Key.Name) + '"').ToArray());
                    Logger.Log(LogLevel.Warning, $"Archives with identical GUIDs detected! Archives: {modList}; Only \"{GetRelativeArchiveDir(orderedMods[0].Key.Name)}\" will be loaded because it's the newest");

                    // Don't keep the duplicate archives in memory
                    foreach (var dupeMod in orderedMods.Skip(1))
                        dupeMod.Key.Close();
                }

                // Actually load the mods (only one per GUID, the newest one)
                var archive = orderedMods[0].Key;
                var manifest = orderedMods[0].Value;
                try
                {
                    Archives.Add(archive);
                    LoadedManifests.Add(manifest);

                    LoadAllUnityArchives(archive, archive.Name);
                    LoadAllLists(archive, manifest);
                    BuildPngFolderList(archive);

                    var trimmedName = manifest.Name?.Trim();
                    var displayName = !string.IsNullOrEmpty(trimmedName) ? trimmedName : Path.GetFileName(archive.Name);

                    modLoadInfoSb.AppendLine($"Loaded {displayName} {manifest.Version}");
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Error, $"Failed to load archive \"{GetRelativeArchiveDir(archive.Name)}\" with error: {ex}");
                }
            }

            stopWatch.Stop();
            Logger.Log(LogLevel.Info, $"List of loaded mods:\n{modLoadInfoSb}" +
                                      $"Successfully loaded {Archives.Count} mods out of {allMods.Count()} archives in {stopWatch.ElapsedMilliseconds}ms");

            UniversalAutoResolver.SetResolveInfos(_gatheredResolutionInfos);

            BuildPngOnlyFolderList();
        }

        protected void LoadAllLists(ZipFile arc, Manifest manifest)
        {
            List<ZipEntry> BoneList = new List<ZipEntry>();
            foreach (ZipEntry entry in arc)
            {
                if (entry.Name.StartsWith("abdata/list/characustom", StringComparison.OrdinalIgnoreCase) && entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var stream = arc.GetInputStream(entry);
                        var chaListData = ListLoader.LoadCSV(stream);

                        SetPossessNew(chaListData);
                        UniversalAutoResolver.GenerateResolutionInfo(manifest, chaListData, _gatheredResolutionInfos);
                        ListLoader.ExternalDataList.Add(chaListData);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(LogLevel.Error, $"Failed to load list file \"{entry.Name}\" from archive \"{GetRelativeArchiveDir(arc.Name)}\" with error: {ex}");
                    }
                }
#if KK
                else if (entry.Name.StartsWith("abdata/studio/info", StringComparison.OrdinalIgnoreCase) && entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    if (Path.GetFileNameWithoutExtension(entry.Name).ToLower().StartsWith("itembonelist_"))
                        BoneList.Add(entry);
                    else
                    {
                        try
                        {
                            var stream = arc.GetInputStream(entry);
                            var studioListData = ListLoader.LoadStudioCSV(stream, entry.Name);

                            UniversalAutoResolver.GenerateStudioResolutionInfo(manifest, studioListData);
                            ListLoader.ExternalStudioDataList.Add(studioListData);
                        }
                        catch (Exception ex)
                        {
                            Logger.Log(LogLevel.Error, $"Failed to load list file \"{entry.Name}\" from archive \"{GetRelativeArchiveDir(arc.Name)}\" with error: {ex}");
                        }
                    }
                }
                else if (entry.Name.StartsWith("abdata/map/list/mapinfo/", StringComparison.OrdinalIgnoreCase) && entry.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var stream = arc.GetInputStream(entry);
                        MapInfo mapListData = ListLoader.LoadMapCSV(stream);

                        ListLoader.ExternalMapList.Add(mapListData);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(LogLevel.Error, $"Failed to load list file \"{entry.Name}\" from archive \"{GetRelativeArchiveDir(arc.Name)}\" with error: {ex}");
                    }
                }
#endif
            }

#if KK
            //ItemBoneList data must be resolved after the corresponding item so they can be resolved to the same ID
            foreach (ZipEntry entry in BoneList)
            {
                try
                {
                    var stream = arc.GetInputStream(entry);
                    var studioListData = ListLoader.LoadStudioCSV(stream, entry.Name);

                    UniversalAutoResolver.GenerateStudioResolutionInfo(manifest, studioListData);
                    ListLoader.ExternalStudioDataList.Add(studioListData);
                }
                catch (Exception ex)
                {
                    Logger.Log(LogLevel.Error, $"Failed to load list file \"{entry.Name}\" from archive \"{GetRelativeArchiveDir(arc.Name)}\" with error: {ex}");
                }
            }
#endif
        }

        protected void SetPossessNew(ChaListData data)
        {
            for (int i = 0; i < data.lstKey.Count; i++)
            {
                if (data.lstKey[i] == "Possess")
                {
                    foreach (var kv in data.dictList)
                        kv.Value[i] = "1";
                    break;
                }
            }
        }
        /// <summary>
        /// Construct a list of all folders that contain a .png
        /// </summary>
        protected void BuildPngFolderList(ZipFile arc)
        {
            foreach (ZipEntry entry in arc)
            {
                //Only list folders for .pngs in abdata folder
                //i.e. skip preview pics or character cards that might be included with the mod
                if (entry.Name.StartsWith("abdata/", StringComparison.OrdinalIgnoreCase) && entry.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    string assetBundlePath = entry.Name;
                    assetBundlePath = assetBundlePath.Remove(0, assetBundlePath.IndexOf('/') + 1); //Remove "abdata/"

                    //Make a list of all the .png files and archive they come from
                    if (PngList.ContainsKey(entry.Name))
                        Logger.Log(LogLevel.Warning, $"Duplicate .png asset detected! {assetBundlePath} in \"{GetRelativeArchiveDir(arc.Name)}\"");
                    else
                        PngList.Add(entry.Name, arc);

                    assetBundlePath = assetBundlePath.Remove(assetBundlePath.LastIndexOf('/')); //Remove the .png filename
                    if (!PngFolderList.Contains(assetBundlePath))
                    {
                        //Make a unique list of all folders that contain a .png
                        PngFolderList.Add(assetBundlePath);
                    }
                }
            }
        }
        /// <summary>
        /// Build a list of folders that contain .pngs but do not match an existing asset bundle
        /// </summary>
        protected void BuildPngOnlyFolderList()
        {
            foreach (string folder in PngFolderList) //assetBundlePath
            {
                string assetBundlePath = folder + ".unity3d";

                //The file exists at this location, no need to add a bundle
                if (File.Exists(Application.dataPath + "/../abdata/" + assetBundlePath))
                    continue;

                //Bundle has already been added by LoadAllUnityArchives
                if (BundleManager.Bundles.ContainsKey(assetBundlePath))
                    continue;

                PngFolderOnlyList.Add(folder);
            }
        }
        /// <summary>
        /// Check whether the asset bundle matches a folder that contains .png files and does not match an existing asset bundle
        /// </summary>
        public static bool IsPngFolderOnly(string assetBundleName)
        {
            var extStart = assetBundleName.LastIndexOf('.');
            var trimmedName = extStart >= 0 ? assetBundleName.Remove(extStart) : assetBundleName;
            return PngFolderOnlyList.Contains(trimmedName);
        }
        [Obsolete]
        public bool IsModLoaded(string guid) => GetManifest(guid) != null;

        /// <summary>
        /// Check if a mod with specified GUID has been loaded and fetch its manifest.
        /// Returns null if there was no mod with this guid loaded.
        /// </summary>
        public static Manifest GetManifest(string guid)
        {
            if (string.IsNullOrEmpty(guid))
                return null;
            return LoadedManifests.FirstOrDefault(x => x.GUID == guid);
        }
        /// <summary>
        /// Get a list of file paths to all png files inside the loaded mods
        /// </summary>
        public static IEnumerable<string> GetPngNames() => PngList.Keys;
        /// <summary>
        /// Get a new copy of the png file if it exists in any of the loaded zipmods
        /// </summary>
        public static Texture2D GetPng(string pngPath, TextureFormat format = TextureFormat.RGBA32, bool mipmap = true)
        {
            if (string.IsNullOrEmpty(pngPath))
                return null;

            //Only search the archives for a .png that can actually be found
            if (PngList.TryGetValue(pngPath, out ZipFile archive))
            {
                var entry = archive.GetEntry(pngPath);

                if (entry != null)
                {
                    var stream = archive.GetInputStream(entry);

                    var tex = ResourceRedirector.AssetLoader.LoadTexture(stream, (int)entry.Size, format, mipmap);

                    if (pngPath.Contains("clamp"))
                        tex.wrapMode = TextureWrapMode.Clamp;
                    else if (pngPath.Contains("repeat"))
                        tex.wrapMode = TextureWrapMode.Repeat;

                    return tex;
                }
            }

            return null;
        }
        /// <summary>
        /// Check whether the .png file comes from a sideloader mod
        /// </summary>
        public static bool IsPng(string pngFile) => PngList.ContainsKey(pngFile);

        private static readonly MethodInfo locateZipEntryMethodInfo = typeof(ZipFile).GetMethod("LocateEntry", AccessTools.all);

        protected void LoadAllUnityArchives(ZipFile arc, string archiveFilename)
        {
            foreach (ZipEntry entry in arc)
            {
                if (entry.Name.EndsWith(".unity3d", StringComparison.OrdinalIgnoreCase))
                {
                    string assetBundlePath = entry.Name;

                    if (assetBundlePath.Contains('/'))
                        assetBundlePath = assetBundlePath.Remove(0, assetBundlePath.IndexOf('/') + 1);

                    AssetBundle getBundleFunc()
                    {
                        AssetBundle bundle;

                        if (entry.CompressionMethod == CompressionMethod.Stored)
                        {
                            long index = (long)locateZipEntryMethodInfo.Invoke(arc, new object[] { entry });

                            if (DebugLogging.Value)
                                Logger.Log(LogLevel.Debug, $"Streaming \"{entry.Name}\" ({GetRelativeArchiveDir(archiveFilename)}) unity3d file from disk, offset {index}");

                            bundle = AssetBundle.LoadFromFile(archiveFilename, 0, (ulong)index);
                        }
                        else
                        {
                            Logger.Log(LogLevel.Debug, $"Cannot stream \"{entry.Name}\" ({GetRelativeArchiveDir(archiveFilename)}) unity3d file from disk, loading to RAM instead");
                            var stream = arc.GetInputStream(entry);

                            byte[] buffer = new byte[entry.Size];

                            stream.Read(buffer, 0, (int)entry.Size);

                            BundleManager.RandomizeCAB(buffer);

                            bundle = AssetBundle.LoadFromMemory(buffer);
                        }

                        if (bundle == null)
                        {
                            Logger.Log(LogLevel.Error, $"Asset bundle \"{entry.Name}\" ({GetRelativeArchiveDir(archiveFilename)}) failed to load. It might have a conflicting CAB string.");
                        }

                        return bundle;
                    }

                    BundleManager.AddBundleLoader(getBundleFunc, assetBundlePath, out string warning);

                    if (!string.IsNullOrEmpty(warning))
                        Logger.Log(LogLevel.Warning, $"{warning} in \"{GetRelativeArchiveDir(archiveFilename)}\"");
                }
            }
        }

        protected bool RedirectHook(string assetBundleName, string assetName, Type type, string manifestAssetBundleName, out AssetBundleLoadAssetOperation result)
        {
            string zipPath = $"{manifestAssetBundleName ?? "abdata"}/{assetBundleName.Replace(".unity3d", "", StringComparison.OrdinalIgnoreCase)}/{assetName}";

            if (type == typeof(Texture2D))
            {
                zipPath = $"{zipPath}.png";

                var tex = GetPng(zipPath);
                if (tex != null)
                {
                    result = new AssetBundleLoadAssetOperationSimulation(tex);
                    return true;
                }
            }

            if (BundleManager.TryGetObjectFromName(assetName, assetBundleName, type, out UnityEngine.Object obj))
            {
                result = new AssetBundleLoadAssetOperationSimulation(obj);
                return true;
            }

            result = null;
            return false;
        }

        protected bool AssetBundleRedirectHook(string assetBundleName, out AssetBundle result)
        {
            string bundle = assetBundleName.Remove(0, assetBundleName.IndexOf("/abdata/")).Replace("/abdata/", "");

            //The only asset bundles that need to be loaded are maps
            //Loading asset bundles unnecessarily can interfere with normal sideloader asset handling so avoid it whenever possible
            if (bundle.StartsWith("map/scene/"))
            {
                if (!File.Exists(assetBundleName))
                {
                    if (BundleManager.Bundles.TryGetValue(bundle, out List<LazyCustom<AssetBundle>> lazyList))
                    {
                        //If more than one exist, only the first will be loaded.
                        result = lazyList[0].Instance;
                        return true;
                    }
                }
            }

            result = null;
            return false;
        }
    }
}
