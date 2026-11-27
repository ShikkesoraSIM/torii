using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using osu.Framework;
using osu.Framework.Logging;
using osu.Framework.Platform;

namespace osu.Game.Torii.OpenFolders
{
    public interface IOpenFolderSkinProvider
    {
        OpenFoldersStore? OpenFolders { get; }
    }

    /// <summary>
    /// Persistent mapping between Realm object identifiers (skins and beatmap sets) and their
    /// human readable folders on disk under <c>Skins/</c> and <c>Songs/</c>. Backed by a JSON
    /// sidecar written with an atomic temp + fsync + rename pattern so it can never be left
    /// half written by a crash. All members are thread safe.
    /// </summary>
    public sealed class OpenFoldersStore
    {
        private const string sidecar_filename = "open-folders.json";

        private static readonly TimeSpan default_watcher_suppression = TimeSpan.FromSeconds(2);

        private readonly object lockObject = new object();

        private readonly Storage toriiStorage;
        private readonly string sidecarPath;

        private readonly Dictionary<string, string> skinFolders = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> beatmapSetFolders = new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly Dictionary<string, Guid> skinIdByFolderName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Guid> beatmapSetIdByFolderName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        // hash (or filename) -> list of paths relative to the Songs/ root.
        private readonly Dictionary<string, List<string>> hashToSongPaths = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        // setId -> list of (key, relativePath) tuples recorded, used to cleanly unindex on unregister.
        private readonly Dictionary<string, List<(string Key, string Rel)>> indexedEntriesBySet = new Dictionary<string, List<(string Key, string Rel)>>(StringComparer.Ordinal);

        // normalized path -> expiry instant (UTC).
        private readonly Dictionary<string, DateTime> watcherSuppression = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // beatmap id -> locally modified flag.
        private readonly HashSet<string> locallyModified = new HashSet<string>(StringComparer.Ordinal);

        private bool dirty;
        private int batchDepth;

        public static bool IsSupportedOnCurrentPlatform => RuntimeInfo.IsDesktop;

        public string SkinsRootPath { get; }

        public string SongsRootPath { get; }

        public event Action? Changed;

        public OpenFoldersStore(Storage baseStorage)
        {
            ArgumentNullException.ThrowIfNull(baseStorage);

            SkinsRootPath = baseStorage.GetFullPath("Skins", createIfNotExisting: false);
            SongsRootPath = baseStorage.GetFullPath("Songs", createIfNotExisting: false);

            toriiStorage = baseStorage.GetStorageForDirectory("torii");
            sidecarPath = toriiStorage.GetFullPath(sidecar_filename, createIfNotExisting: false);

            LoadFromDisk();
        }

        #region Root directories

        public void EnsureRootDirectories()
        {
            if (!IsSupportedOnCurrentPlatform) return;

            try
            {
                if (!string.IsNullOrEmpty(SkinsRootPath))
                    Directory.CreateDirectory(SkinsRootPath);
                if (!string.IsNullOrEmpty(SongsRootPath))
                    Directory.CreateDirectory(SongsRootPath);
            }
            catch (Exception ex)
            {
                Logger.Log($"Open folders: failed to create root directories: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        #endregion

        #region Lookups

        public string? GetSkinFolderName(Guid skinId)
        {
            lock (lockObject)
            {
                return skinFolders.TryGetValue(skinId.ToString(), out var name) ? name : null;
            }
        }

        public string? GetSkinFolderPath(Guid skinId)
        {
            var name = GetSkinFolderName(skinId);
            return name == null ? null : Path.Combine(SkinsRootPath, name);
        }

        public string? GetBeatmapSetFolderName(Guid setId)
        {
            lock (lockObject)
            {
                return beatmapSetFolders.TryGetValue(setId.ToString(), out var name) ? name : null;
            }
        }

        public string? GetBeatmapSetFolderPath(Guid setId)
        {
            var name = GetBeatmapSetFolderName(setId);
            return name == null ? null : Path.Combine(SongsRootPath, name);
        }

        public bool TryFindSkinIdByFolderName(string folderName, out Guid skinId)
        {
            if (string.IsNullOrEmpty(folderName))
            {
                skinId = Guid.Empty;
                return false;
            }

            lock (lockObject)
            {
                return skinIdByFolderName.TryGetValue(folderName, out skinId);
            }
        }

        public bool TryFindBeatmapSetIdByFolderName(string folderName, out Guid setId)
        {
            if (string.IsNullOrEmpty(folderName))
            {
                setId = Guid.Empty;
                return false;
            }

            lock (lockObject)
            {
                return beatmapSetIdByFolderName.TryGetValue(folderName, out setId);
            }
        }

        public int TrackedSkinCount
        {
            get
            {
                lock (lockObject)
                    return skinFolders.Count;
            }
        }

        public int TrackedBeatmapSetCount
        {
            get
            {
                lock (lockObject)
                    return beatmapSetFolders.Count;
            }
        }

        #endregion

        #region Registration

        public string RegisterSkinFolder(Guid skinId, string folderName)
        {
            string fullPath;

            lock (lockObject)
            {
                SetSkinFolderLocked(skinId.ToString(), folderName);
                dirty = true;
                PersistIfNotBatchingLocked();
                fullPath = Path.Combine(SkinsRootPath, folderName);
            }

            Changed?.Invoke();
            return fullPath;
        }

        public string RegisterBeatmapSetFolder(Guid setId, string folderName, IEnumerable<(string Filename, string Hash)> files)
        {
            string fullPath;

            lock (lockObject)
            {
                SetBeatmapSetFolderLocked(setId.ToString(), folderName);
                IndexBeatmapSetHashesLocked(setId, files);
                dirty = true;
                PersistIfNotBatchingLocked();
                fullPath = Path.Combine(SongsRootPath, folderName);
            }

            Changed?.Invoke();
            return fullPath;
        }

        public void UnregisterSkin(Guid skinId)
        {
            bool changed = false;

            lock (lockObject)
            {
                var key = skinId.ToString();

                if (skinFolders.TryGetValue(key, out var folder))
                {
                    skinFolders.Remove(key);

                    if (skinIdByFolderName.TryGetValue(folder, out var mapped) && mapped == skinId)
                        skinIdByFolderName.Remove(folder);

                    dirty = true;
                    PersistIfNotBatchingLocked();
                    changed = true;
                }
            }

            if (changed)
                Changed?.Invoke();
        }

        public void UnregisterBeatmapSet(Guid setId)
        {
            bool changed = false;

            lock (lockObject)
            {
                var key = setId.ToString();

                if (beatmapSetFolders.TryGetValue(key, out var folder))
                {
                    beatmapSetFolders.Remove(key);

                    if (beatmapSetIdByFolderName.TryGetValue(folder, out var mapped) && mapped == setId)
                        beatmapSetIdByFolderName.Remove(folder);

                    UnindexBeatmapSetLocked(key);

                    dirty = true;
                    PersistIfNotBatchingLocked();
                    changed = true;
                }
            }

            if (changed)
                Changed?.Invoke();
        }

        private void SetSkinFolderLocked(string guidKey, string folderName)
        {
            if (skinFolders.TryGetValue(guidKey, out var old))
            {
                if (skinIdByFolderName.TryGetValue(old, out var existing) && existing.ToString() == guidKey)
                    skinIdByFolderName.Remove(old);
            }

            skinFolders[guidKey] = folderName;

            if (Guid.TryParse(guidKey, out var id))
                skinIdByFolderName[folderName] = id;
        }

        private void SetBeatmapSetFolderLocked(string guidKey, string folderName)
        {
            if (beatmapSetFolders.TryGetValue(guidKey, out var old))
            {
                if (beatmapSetIdByFolderName.TryGetValue(old, out var existing) && existing.ToString() == guidKey)
                    beatmapSetIdByFolderName.Remove(old);
            }

            beatmapSetFolders[guidKey] = folderName;

            if (Guid.TryParse(guidKey, out var id))
                beatmapSetIdByFolderName[folderName] = id;
        }

        #endregion

        #region Hash index

        public void IndexBeatmapSetHashes(Guid setId, IEnumerable<(string Filename, string Hash)> files)
        {
            lock (lockObject)
            {
                IndexBeatmapSetHashesLocked(setId, files);
            }
        }

        private void IndexBeatmapSetHashesLocked(Guid setId, IEnumerable<(string Filename, string Hash)> files)
        {
            if (files == null) return;

            var setKey = setId.ToString();

            // Remove any previous entries for this set before re-indexing, so updates never leave stale paths behind.
            UnindexBeatmapSetLocked(setKey);

            if (!beatmapSetFolders.TryGetValue(setKey, out var folderName))
                return;

            var folder = folderName.Replace('\\', '/').Trim('/');

            if (!indexedEntriesBySet.TryGetValue(setKey, out var entries))
            {
                entries = new List<(string Key, string Rel)>();
                indexedEntriesBySet[setKey] = entries;
            }

            foreach (var (filename, hash) in files)
            {
                if (string.IsNullOrEmpty(filename)) continue;

                var relName = filename.Replace('\\', '/').TrimStart('/');
                var rel = folder.Length == 0 ? relName : folder + "/" + relName;

                if (!string.IsNullOrEmpty(hash))
                {
                    if (AddHashMapping(hash, rel))
                        entries.Add((hash, rel));
                }

                var bareFilename = Path.GetFileName(relName);

                if (!string.IsNullOrEmpty(bareFilename))
                {
                    if (AddHashMapping(bareFilename, rel))
                        entries.Add((bareFilename, rel));
                }
            }
        }

        private bool AddHashMapping(string key, string relativePath)
        {
            if (string.IsNullOrEmpty(key)) return false;

            if (!hashToSongPaths.TryGetValue(key, out var list))
            {
                list = new List<string>();
                hashToSongPaths[key] = list;
            }

            if (list.Contains(relativePath))
                return false;

            list.Add(relativePath);
            return true;
        }

        private void UnindexBeatmapSetLocked(string setKey)
        {
            if (!indexedEntriesBySet.TryGetValue(setKey, out var entries))
                return;

            foreach (var (key, rel) in entries)
            {
                if (hashToSongPaths.TryGetValue(key, out var list))
                {
                    list.Remove(rel);

                    if (list.Count == 0)
                        hashToSongPaths.Remove(key);
                }
            }

            indexedEntriesBySet.Remove(setKey);
        }

        public string? TryResolveSongFile(string hashOrStoragePath)
        {
            if (string.IsNullOrWhiteSpace(hashOrStoragePath))
                return null;

            string bareHash;

            try
            {
                bareHash = Path.GetFileName(hashOrStoragePath);
            }
            catch
            {
                bareHash = hashOrStoragePath;
            }

            if (string.IsNullOrEmpty(bareHash))
                return null;

            lock (lockObject)
            {
                if (!hashToSongPaths.TryGetValue(bareHash, out var list) || list.Count == 0)
                    return null;

                foreach (var rel in list)
                {
                    try
                    {
                        var relativePath = rel.Replace('/', Path.DirectorySeparatorChar);

                        if (OpenFolderPathSanitizer.TryResolveSafeChildPath(SongsRootPath, relativePath, out var full) && File.Exists(full))
                            return full;
                    }
                    catch
                    {
                        // Ignore malformed or unsafe indexed paths and try the next entry.
                    }
                }
            }

            return null;
        }

        #endregion

        #region Batch persistence

        public void BeginBatch()
        {
            lock (lockObject)
            {
                batchDepth++;
            }
        }

        public void EndBatch()
        {
            lock (lockObject)
            {
                batchDepth = Math.Max(0, batchDepth - 1);

                if (batchDepth == 0 && dirty)
                    PersistLocked();
            }
        }

        public void FlushBatchIfDirty()
        {
            lock (lockObject)
            {
                if (dirty)
                    PersistLocked();
            }
        }

        private void PersistIfNotBatchingLocked()
        {
            dirty = true;

            if (batchDepth > 0)
                return;

            PersistLocked();
        }

        #endregion

        #region Watcher suppression

        public void SuppressWatcher(string path, TimeSpan? duration = null)
        {
            if (string.IsNullOrEmpty(path)) return;

            var key = NormalizePath(path);
            var expiry = DateTime.UtcNow + (duration ?? default_watcher_suppression);

            lock (lockObject)
            {
                watcherSuppression[key] = expiry;
            }
        }

        public bool IsWatcherSuppressed(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;

            var key = NormalizePath(path);
            var now = DateTime.UtcNow;

            lock (lockObject)
            {
                PruneExpiredSuppressionsLocked(now);

                if (watcherSuppression.TryGetValue(key, out var direct) && direct > now)
                    return true;

                var parent = key;

                while (!string.IsNullOrEmpty(parent))
                {
                    var idx = parent.LastIndexOf(Path.DirectorySeparatorChar);

                    if (idx <= 0)
                        break;

                    parent = parent.Substring(0, idx);

                    if (watcherSuppression.TryGetValue(parent, out var expiry) && expiry > now)
                        return true;
                }
            }

            return false;
        }

        private void PruneExpiredSuppressionsLocked(DateTime now)
        {
            if (watcherSuppression.Count == 0)
                return;

            List<string>? expired = null;

            foreach (var kv in watcherSuppression)
            {
                if (kv.Value <= now)
                {
                    expired ??= new List<string>();
                    expired.Add(kv.Key);
                }
            }

            if (expired == null)
                return;

            foreach (var key in expired)
                watcherSuppression.Remove(key);
        }

        private static string NormalizePath(string path)
        {
            string result;

            try
            {
                result = Path.GetFullPath(path);
            }
            catch
            {
                result = path;
            }

            return result.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        #endregion

        #region Local modification tracking

        public void MarkLocallyModified(Guid beatmapId, bool modified)
        {
            lock (lockObject)
            {
                var key = beatmapId.ToString();

                bool changed = modified
                    ? locallyModified.Add(key)
                    : locallyModified.Remove(key);

                if (!changed)
                    return;

                dirty = true;
                PersistIfNotBatchingLocked();
            }
        }

        public bool IsLocallyModified(Guid beatmapId)
        {
            lock (lockObject)
            {
                return locallyModified.Contains(beatmapId.ToString());
            }
        }

        #endregion

        #region Explorer opener

        public bool OpenFolderInExplorer(string directoryPath)
        {
            if (!IsSupportedOnCurrentPlatform) return false;
            if (string.IsNullOrWhiteSpace(directoryPath)) return false;

            try
            {
                if (!Directory.Exists(directoryPath))
                    Directory.CreateDirectory(directoryPath);

                ProcessStartInfo startInfo;

                if (OperatingSystem.IsWindows())
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = directoryPath,
                        UseShellExecute = true,
                    };
                }
                else if (OperatingSystem.IsMacOS())
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = "open",
                        Arguments = $"\"{directoryPath}\"",
                        UseShellExecute = true,
                    };
                }
                else
                {
                    startInfo = new ProcessStartInfo
                    {
                        FileName = "xdg-open",
                        Arguments = $"\"{directoryPath}\"",
                        UseShellExecute = true,
                    };
                }

                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Open folders: failed to open folder in file manager: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
                return false;
            }
        }

        #endregion

        #region Persistence

        private void LoadFromDisk()
        {
            try
            {
                if (!File.Exists(sidecarPath))
                    return;

                var json = File.ReadAllText(sidecarPath, Encoding.UTF8);

                if (string.IsNullOrWhiteSpace(json))
                    return;

                var data = JsonConvert.DeserializeObject<OpenFoldersData>(json);

                if (data == null)
                    return;

                if (data.Skins != null)
                {
                    foreach (var kv in data.Skins)
                    {
                        if (kv.Key == null || kv.Value == null) continue;
                        if (!Guid.TryParse(kv.Key, out var id)) continue;

                        skinFolders[kv.Key] = kv.Value;
                        skinIdByFolderName[kv.Value] = id;
                    }
                }

                if (data.BeatmapSets != null)
                {
                    foreach (var kv in data.BeatmapSets)
                    {
                        if (kv.Key == null || kv.Value == null) continue;
                        if (!Guid.TryParse(kv.Key, out var id)) continue;

                        beatmapSetFolders[kv.Key] = kv.Value;
                        beatmapSetIdByFolderName[kv.Value] = id;
                    }
                }

                if (data.LocallyModified != null)
                {
                    foreach (var key in data.LocallyModified)
                    {
                        if (!string.IsNullOrEmpty(key))
                            locallyModified.Add(key);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Open folders: failed to read sidecar file: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        private void PersistLocked()
        {
            string? tmpPath = null;

            try
            {
                var data = new OpenFoldersData
                {
                    Skins = new Dictionary<string, string>(skinFolders, StringComparer.Ordinal),
                    BeatmapSets = new Dictionary<string, string>(beatmapSetFolders, StringComparer.Ordinal),
                    LocallyModified = locallyModified.ToList(),
                };

                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                var bytes = Encoding.UTF8.GetBytes(json);

                var directory = Path.GetDirectoryName(sidecarPath);

                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                tmpPath = sidecarPath + ".tmp." + Guid.NewGuid().ToString("N");

                using (var stream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tmpPath, sidecarPath, overwrite: true);
                tmpPath = null;

                dirty = false;
            }
            catch (Exception ex)
            {
                if (!string.IsNullOrEmpty(tmpPath))
                {
                    try
                    {
                        File.Delete(tmpPath);
                    }
                    catch
                    {
                        // Best-effort cleanup of the temporary sidecar file.
                    }
                }

                Logger.Log($"Open folders: failed to persist sidecar file: {ex.Message}", LoggingTarget.Runtime, LogLevel.Error);
            }
        }

        private sealed class OpenFoldersData
        {
            public Dictionary<string, string> Skins { get; set; } = new Dictionary<string, string>();

            public Dictionary<string, string> BeatmapSets { get; set; } = new Dictionary<string, string>();

            public List<string> LocallyModified { get; set; } = new List<string>();
        }

        #endregion
    }
}
