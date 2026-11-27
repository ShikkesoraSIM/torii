using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Models;
using osu.Game.Overlays.Notifications;
using osu.Game.Skinning;
using Realms;

namespace osu.Game.Torii.OpenFolders
{
    public class OpenFoldersMigrator
    {
        private readonly Storage storage;
        private readonly RealmAccess realm;
        private readonly OpenFoldersStore openFolders;
        private readonly RealmFileStore realmFileStore;
        private readonly object batchLock = new object();
        private readonly object folderAllocLock = new object();

        private int skinsUnpackRunning;
        private int beatmapsUnpackRunning;

        public bool IsRunning => Volatile.Read(ref skinsUnpackRunning) != 0 || Volatile.Read(ref beatmapsUnpackRunning) != 0;

        public OpenFoldersMigrator(Storage storage, RealmAccess realm, OpenFoldersStore openFolders)
        {
            this.storage = storage;
            this.realm = realm;
            this.openFolders = openFolders;
            realmFileStore = new RealmFileStore(realm, storage);
        }

        public string? ExtractSkinToOpenFolder(SkinInfo skin, RealmFileStore? fileStore = null)
        {
            if (skin == null || skin.Protected || skin.DeletePending)
                return null;

            var files = readSkinFiles(skin, includeSizes: false);
            if (files.Count == 0)
                return null;

            var data = new SkinData
            {
                Id = skin.ID,
                Name = skin.Name ?? string.Empty,
                Files = files,
            };

            return extractSkinData(data, fileStore ?? realmFileStore);
        }

        public string? ExtractBeatmapSetToOpenFolder(BeatmapSetInfo beatmapSet, RealmFileStore? fileStore = null)
        {
            if (beatmapSet == null || beatmapSet.Protected || beatmapSet.DeletePending)
                return null;

            var files = readBeatmapSetFiles(beatmapSet, includeSizes: false);
            if (files.Count == 0)
                return null;

            var data = new BeatmapSetData
            {
                Id = beatmapSet.ID,
                OnlineId = beatmapSet.OnlineID,
                Artist = beatmapSet.Metadata?.Artist ?? string.Empty,
                Title = beatmapSet.Metadata?.Title ?? string.Empty,
                Files = files,
            };

            return extractBeatmapSetData(data, fileStore ?? realmFileStore);
        }

        public void SyncSkinFileToOpenFolder(Guid skinId, string relativeFilename, byte[] content)
        {
            if (string.IsNullOrWhiteSpace(relativeFilename) || content == null)
                return;

            string? folderPath = openFolders.GetSkinFolderPath(skinId);
            if (string.IsNullOrEmpty(folderPath))
                return;

            if (!OpenFolderPathSanitizer.TryResolveSafeChildPath(folderPath, relativeFilename, out string fullPath))
                return;

            writeFileAtomic(fullPath, content);
        }

        public void WarmupSongHashIndex()
        {
            if (!OpenFoldersStore.IsSupportedOnCurrentPlatform)
                return;

            if (openFolders.TrackedBeatmapSetCount == 0)
                return;

            try
            {
                // Never touch disk inside the Realm callback. Collect the metadata first,
                // then perform the index updates outside the transaction.
                var collected = realm.Run(r =>
                {
                    var result = new List<(Guid Id, List<FileEntry> Files)>();

                    foreach (var beatmapSet in r.All<BeatmapSetInfo>())
                    {
                        if (beatmapSet.Protected || beatmapSet.DeletePending)
                            continue;

                        var files = readBeatmapSetFiles(beatmapSet, includeSizes: false);
                        if (files.Count == 0)
                            continue;

                        result.Add((beatmapSet.ID, files));
                    }

                    return result;
                });

                foreach (var entry in collected)
                {
                    if (string.IsNullOrEmpty(openFolders.GetBeatmapSetFolderPath(entry.Id)))
                        continue;

                    openFolders.IndexBeatmapSetHashes(entry.Id, entry.Files.Select(f => (f.Filename, f.Hash)));
                }
            }
            catch
            {
                // Startup warmup must never break the game if a database read fails.
            }
        }

        public Task<(int Count, long EstimatedBytes)> EstimatePendingSkinsAsync()
        {
            return Task.Run(() =>
            {
                var skins = readAllSkins(includeSizes: true);
                int count = 0;
                long bytes = 0;

                foreach (var skin in skins)
                {
                    if (!string.IsNullOrEmpty(openFolders.GetSkinFolderPath(skin.Id)))
                        continue;

                    count++;
                    bytes += skin.Files.Sum(f => f.Size);
                }

                return (count, bytes);
            });
        }

        public Task<(int Count, long EstimatedBytes)> EstimatePendingBeatmapsAsync()
        {
            return Task.Run(() =>
            {
                var ids = readBeatmapSetIds();
                int count = 0;
                long bytes = 0;
                const int chunkSize = 100;

                for (int offset = 0; offset < ids.Length; offset += chunkSize)
                {
                    var chunkIds = ids.Skip(offset).Take(chunkSize).ToArray();
                    var chunk = readBeatmapSetChunk(chunkIds, includeSizes: true);

                    foreach (var beatmapSet in chunk)
                    {
                        if (!string.IsNullOrEmpty(openFolders.GetBeatmapSetFolderPath(beatmapSet.Id)))
                            continue;

                        count++;
                        bytes += beatmapSet.Files.Sum(f => f.Size);
                    }
                }

                return (count, bytes);
            });
        }

        public async Task UnpackExistingSkinsAsync(Action<Notification>? postNotification = null)
        {
            if (Interlocked.CompareExchange(ref skinsUnpackRunning, 1, 0) != 0)
                return;

            var notification = new ProgressNotification
            {
                Text = "Unpacking skins...",
                State = ProgressNotificationState.Active,
                Progress = 0,
            };

            postNotification?.Invoke(notification);

            try
            {
                var skins = await Task.Run(() => readAllSkins()).ConfigureAwait(false);
                skins = skins.Where(s => string.IsNullOrEmpty(openFolders.GetSkinFolderPath(s.Id))).ToList();

                if (skins.Count == 0)
                {
                    notification.Progress = 1;
                    notification.State = ProgressNotificationState.Completed;
                    notification.CompletionText = "All skins are already in the Skins folder.";
                    notification.CompletionClickAction = () => openFolders.OpenFolderInExplorer(openFolders.SkinsRootPath);
                    return;
                }

                openFolders.EnsureRootDirectories();
                openFolders.BeginBatch();

                var token = notification.CancellationToken;
                int completed = 0;
                int failed = 0;

                foreach (var skin in skins)
                {
                    if (token.IsCancellationRequested)
                    {
                        notification.State = ProgressNotificationState.Cancelled;
                        notification.CompletionText = $"Skin unpacking cancelled after unpacking {completed} skins ({failed} failed).";
                        break;
                    }

                    bool success = false;

                    try
                    {
                        success = extractSkinData(skin, realmFileStore) != null;
                    }
                    catch
                    {
                        success = false;
                    }

                    if (success)
                        completed++;
                    else
                        failed++;

                    int processed = completed + failed;
                    notification.Progress = Math.Min(1f, processed / (float)skins.Count);

                    if (processed % 20 == 0)
                        flushBatch();
                }

                if (!token.IsCancellationRequested)
                {
                    notification.Progress = 1;
                    notification.State = ProgressNotificationState.Completed;
                    notification.CompletionText = failed > 0
                        ? $"Unpacked {completed} skins into the Skins folder; {failed} failed."
                        : $"Unpacked {completed} skins into the Skins folder.";
                }

                notification.CompletionClickAction = () => openFolders.OpenFolderInExplorer(openFolders.SkinsRootPath);
            }
            catch
            {
                notification.State = ProgressNotificationState.Cancelled;
                notification.CompletionText = "Skin unpacking failed.";
                notification.CompletionClickAction = () => openFolders.OpenFolderInExplorer(openFolders.SkinsRootPath);
            }
            finally
            {
                try
                {
                    openFolders.EndBatch();
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Failed to end open-folders batch after skin unpacking.");
                }

                Interlocked.Exchange(ref skinsUnpackRunning, 0);
            }
        }

        public async Task UnpackExistingBeatmapsAsync(Action<Notification>? postNotification = null)
        {
            if (Interlocked.CompareExchange(ref beatmapsUnpackRunning, 1, 0) != 0)
                return;

            var notification = new ProgressNotification
            {
                Text = "Unpacking beatmaps...",
                State = ProgressNotificationState.Active,
                Progress = 0,
            };

            postNotification?.Invoke(notification);

            int completed = 0;
            int failed = 0;

            try
            {
                openFolders.EnsureRootDirectories();

                var ids = await Task.Run(readBeatmapSetIds).ConfigureAwait(false);
                int pendingCount = ids.Count(id => string.IsNullOrEmpty(openFolders.GetBeatmapSetFolderPath(id)));

                if (pendingCount == 0)
                {
                    notification.Progress = 1;
                    notification.State = ProgressNotificationState.Completed;
                    notification.CompletionText = "All beatmaps are already in the Songs folder.";
                    notification.CompletionClickAction = () => openFolders.OpenFolderInExplorer(openFolders.SongsRootPath);
                    return;
                }

                openFolders.BeginBatch();

                var token = notification.CancellationToken;
                int processedCount = 0;
                const int chunkSize = 100;

                for (int offset = 0; offset < ids.Length; offset += chunkSize)
                {
                    if (token.IsCancellationRequested)
                    {
                        notification.State = ProgressNotificationState.Cancelled;
                        notification.CompletionText = $"Beatmap unpacking cancelled after unpacking {completed} beatmaps ({failed} failed).";
                        break;
                    }

                    var chunkIds = ids
                        .Skip(offset)
                        .Take(chunkSize)
                        .Where(id => string.IsNullOrEmpty(openFolders.GetBeatmapSetFolderPath(id)))
                        .ToArray();

                    if (chunkIds.Length == 0)
                        continue;

                    var chunk = await Task.Run(() => readBeatmapSetChunk(chunkIds)).ConfigureAwait(false);
                    if (chunk.Count == 0)
                        continue;

                    var options = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount),
                        CancellationToken = token,
                    };

                    await Parallel.ForEachAsync(chunk, options, async (beatmapSet, cancellationToken) =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        bool success = false;

                        try
                        {
                            success = await Task.Run(() => extractBeatmapSetData(beatmapSet, realmFileStore), cancellationToken).ConfigureAwait(false) != null;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch
                        {
                            success = false;
                        }

                        if (success)
                            Interlocked.Increment(ref completed);
                        else
                            Interlocked.Increment(ref failed);

                        int done = Interlocked.Increment(ref processedCount);
                        notification.Progress = Math.Min(1f, done / (float)pendingCount);

                        if (done % 50 == 0)
                            flushBatch();
                    }).ConfigureAwait(false);
                }

                if (!token.IsCancellationRequested)
                {
                    notification.Progress = 1;
                    notification.State = ProgressNotificationState.Completed;
                    notification.CompletionText = failed > 0
                        ? $"Unpacked {completed} beatmaps into the Songs folder; {failed} failed."
                        : $"Unpacked {completed} beatmaps into the Songs folder.";
                }

                notification.CompletionClickAction = () => openFolders.OpenFolderInExplorer(openFolders.SongsRootPath);
            }
            catch (OperationCanceledException)
            {
                notification.State = ProgressNotificationState.Cancelled;
                notification.CompletionText = $"Beatmap unpacking cancelled after unpacking {completed} beatmaps ({failed} failed).";
                notification.CompletionClickAction = () => openFolders.OpenFolderInExplorer(openFolders.SongsRootPath);
            }
            catch
            {
                notification.State = ProgressNotificationState.Cancelled;
                notification.CompletionText = "Beatmap unpacking failed.";
                notification.CompletionClickAction = () => openFolders.OpenFolderInExplorer(openFolders.SongsRootPath);
            }
            finally
            {
                try
                {
                    openFolders.EndBatch();
                }
                catch (Exception e)
                {
                    Logger.Error(e, "Failed to end open-folders batch after beatmap unpacking.");
                }

                Interlocked.Exchange(ref beatmapsUnpackRunning, 0);
            }
        }

        private string? extractSkinData(SkinData skin, RealmFileStore fileStore)
        {
            string root = openFolders.SkinsRootPath;
            openFolders.EnsureRootDirectories();

            string folderName;
            string folderPath;

            lock (folderAllocLock)
            {
                string? existingFolderPath = openFolders.GetSkinFolderPath(skin.Id);

                if (!string.IsNullOrEmpty(existingFolderPath))
                {
                    folderPath = existingFolderPath;
                    folderName = Path.GetFileName(folderPath);
                }
                else
                {
                    folderName = OpenFolderPathSanitizer.BuildSkinFolderName(
                        skin.Name,
                        skin.Id,
                        name => isSkinFolderNameTaken(root, name, skin.Id));

                    if (string.IsNullOrEmpty(folderName))
                        return null;

                    folderPath = Path.Combine(root, folderName);
                }

                if (string.IsNullOrEmpty(folderName))
                    return null;

                // Suppress the watcher before creating the directory so the newly created
                // folder is never picked up and re-imported by FileSystemWatcher.
                openFolders.SuppressWatcher(folderPath, TimeSpan.FromSeconds(5));
                Directory.CreateDirectory(folderPath);
            }

            var writtenFiles = new List<(string Filename, string Hash)>();

            foreach (var file in skin.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Filename) || string.IsNullOrWhiteSpace(file.Hash))
                    continue;

                if (!OpenFolderPathSanitizer.TryResolveSafeChildPath(folderPath, file.Filename, out string fullPath))
                    continue;

                try
                {
                    string storagePath = Path.Combine(file.Hash.Remove(1), file.Hash.Remove(2), file.Hash);
                    using var stream = fileStore.Store.GetStream(storagePath);
                    if (stream == null)
                        continue;

                    writeFileAtomic(fullPath, stream);
                    writtenFiles.Add((file.Filename, file.Hash));
                }
                catch (IOException ioException) when (isDiskFull(ioException))
                {
                    Logger.Error(ioException, $"Failed to unpack skin \"{skin.Name}\" ({skin.Id}) to \"{folderPath}\": the disk may be full.");
                    return null;
                }
                catch
                {
                    // Ignore individual file failures and continue with the rest of the skin.
                }
            }

            if (writtenFiles.Count == 0)
                return null;

            openFolders.RegisterSkinFolder(skin.Id, folderName);
            return folderPath;
        }

        private string? extractBeatmapSetData(BeatmapSetData beatmapSet, RealmFileStore fileStore)
        {
            string root = openFolders.SongsRootPath;
            openFolders.EnsureRootDirectories();

            string folderName;
            string folderPath;

            lock (folderAllocLock)
            {
                string? existingFolderPath = openFolders.GetBeatmapSetFolderPath(beatmapSet.Id);

                if (!string.IsNullOrEmpty(existingFolderPath))
                {
                    folderPath = existingFolderPath;
                    folderName = Path.GetFileName(folderPath);
                }
                else
                {
                    folderName = OpenFolderPathSanitizer.BuildBeatmapSetFolderName(
                        beatmapSet.OnlineId,
                        beatmapSet.Artist,
                        beatmapSet.Title,
                        beatmapSet.Id,
                        name => isBeatmapSetFolderNameTaken(root, name, beatmapSet.Id));

                    if (string.IsNullOrEmpty(folderName))
                        return null;

                    folderPath = Path.Combine(root, folderName);
                }

                if (string.IsNullOrEmpty(folderName))
                    return null;

                // Suppress the watcher before creating the directory so the newly created
                // folder is never picked up and re-imported by FileSystemWatcher.
                openFolders.SuppressWatcher(folderPath, TimeSpan.FromSeconds(5));
                Directory.CreateDirectory(folderPath);
            }

            var writtenFiles = new List<(string Filename, string Hash)>();

            foreach (var file in beatmapSet.Files)
            {
                if (string.IsNullOrWhiteSpace(file.Filename) || string.IsNullOrWhiteSpace(file.Hash))
                    continue;

                if (!OpenFolderPathSanitizer.TryResolveSafeChildPath(folderPath, file.Filename, out string fullPath))
                    continue;

                try
                {
                    string storagePath = Path.Combine(file.Hash.Remove(1), file.Hash.Remove(2), file.Hash);
                    using var stream = fileStore.Store.GetStream(storagePath);
                    if (stream == null)
                        continue;

                    writeFileAtomic(fullPath, stream);
                    writtenFiles.Add((file.Filename, file.Hash));
                }
                catch (IOException ioException) when (isDiskFull(ioException))
                {
                    Logger.Error(ioException, $"Failed to unpack beatmap set \"{beatmapSet.Artist} - {beatmapSet.Title}\" ({beatmapSet.Id}) to \"{folderPath}\": the disk may be full.");
                    return null;
                }
                catch
                {
                    // Ignore individual file failures and continue with the rest of the beatmap set.
                }
            }

            if (writtenFiles.Count == 0)
                return null;

            lock (batchLock)
            {
                openFolders.RegisterBeatmapSetFolder(
                    beatmapSet.Id,
                    folderName,
                    writtenFiles);
            }

            return folderPath;
        }

        private bool isSkinFolderNameTaken(string root, string folderName, Guid skinId)
        {
            if (Directory.Exists(Path.Combine(root, folderName)))
                return true;

            return openFolders.TryFindSkinIdByFolderName(folderName, out Guid existingId) && existingId != skinId;
        }

        private bool isBeatmapSetFolderNameTaken(string root, string folderName, Guid beatmapSetId)
        {
            if (Directory.Exists(Path.Combine(root, folderName)))
                return true;

            return openFolders.TryFindBeatmapSetIdByFolderName(folderName, out Guid existingId) && existingId != beatmapSetId;
        }

        private List<SkinData> readAllSkins(bool includeSizes = false)
        {
            return realm.Run(r =>
            {
                var result = new List<SkinData>();

                foreach (var skin in r.All<SkinInfo>())
                {
                    if (skin.Protected || skin.DeletePending)
                        continue;

                    var files = readSkinFiles(skin, includeSizes);
                    if (files.Count == 0)
                        continue;

                    result.Add(new SkinData
                    {
                        Id = skin.ID,
                        Name = skin.Name ?? string.Empty,
                        Files = files,
                    });
                }

                return result;
            });
        }

        private List<FileEntry> readSkinFiles(SkinInfo skin, bool includeSizes = false)
        {
            var files = new List<FileEntry>();

            foreach (var usage in skin.Files)
            {
                var file = usage.File;
                if (file == null || string.IsNullOrEmpty(file.Hash) || string.IsNullOrEmpty(usage.Filename))
                    continue;

                files.Add(new FileEntry
                {
                    Filename = usage.Filename,
                    Hash = file.Hash,
                    Size = includeSizes ? getHashedFileSize(file.Hash) : 0,
                });
            }

            return files;
        }

        private List<FileEntry> readBeatmapSetFiles(BeatmapSetInfo beatmapSet, bool includeSizes = false)
        {
            var files = new List<FileEntry>();

            foreach (var usage in beatmapSet.Files)
            {
                var file = usage.File;
                if (file == null || string.IsNullOrEmpty(file.Hash) || string.IsNullOrEmpty(usage.Filename))
                    continue;

                files.Add(new FileEntry
                {
                    Filename = usage.Filename,
                    Hash = file.Hash,
                    Size = includeSizes ? getHashedFileSize(file.Hash) : 0,
                });
            }

            return files;
        }

        private long getHashedFileSize(string hash)
        {
            if (hash.Length < 2)
                return 0;

            try
            {
                string fullPath = realmFileStore.Storage.GetFullPath(Path.Combine(hash.Remove(1), hash.Remove(2), hash));
                var fi = new FileInfo(fullPath);
                return fi.Exists ? fi.Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private Guid[] readBeatmapSetIds()
        {
            return realm.Run(r =>
                r.All<BeatmapSetInfo>()
                    .Where(s => s.Protected == false && s.DeletePending == false)
                    .ToList()
                    .Select(s => s.ID)
                    .ToArray());
        }

        private List<BeatmapSetData> readBeatmapSetChunk(Guid[] ids, bool includeSizes = false)
        {
            return realm.Run(r =>
            {
                var result = new List<BeatmapSetData>();

                foreach (var id in ids)
                {
                    var data = readBeatmapSet(r, id, includeSizes);
                    if (data != null)
                        result.Add(data);
                }

                return result;
            });
        }

        private BeatmapSetData? readBeatmapSet(Realm r, Guid id, bool includeSizes)
        {
            var beatmapSet = r.Find<BeatmapSetInfo>(id);
            if (beatmapSet == null || beatmapSet.Protected || beatmapSet.DeletePending)
                return null;

            var files = readBeatmapSetFiles(beatmapSet, includeSizes);
            if (files.Count == 0)
                return null;

            return new BeatmapSetData
            {
                Id = beatmapSet.ID,
                OnlineId = beatmapSet.OnlineID,
                Artist = beatmapSet.Metadata?.Artist ?? string.Empty,
                Title = beatmapSet.Metadata?.Title ?? string.Empty,
                Files = files,
            };
        }

        private void writeFileAtomic(string fullPath, byte[] content)
        {
            using var stream = new MemoryStream(content, writable: false);
            writeFileAtomic(fullPath, stream);
        }

        private void writeFileAtomic(string fullPath, Stream source)
        {
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            openFolders.SuppressWatcher(fullPath);

            string tempPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
            openFolders.SuppressWatcher(tempPath);

            try
            {
                using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.None))
                {
                    byte[] buffer = new byte[81920];
                    int read;

                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        output.Write(buffer, 0, read);

                    output.Flush(true);
                }

                File.Move(tempPath, fullPath, true);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // Best effort temp cleanup, only for this unique temp path.
                }
            }
        }

        private static bool isDiskFull(IOException exception)
        {
            const int errorHandleDiskFull = 0x27;
            const int errorDiskFull = 0x70;
            const int enospc = 28;

            int code = exception.HResult & 0xFFFF;

            if (code == errorHandleDiskFull || code == errorDiskFull || code == enospc)
                return true;

            string message = exception.Message ?? string.Empty;

            return message.IndexOf("disk full", StringComparison.OrdinalIgnoreCase) >= 0
                   || message.IndexOf("no space left", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void flushBatch()
        {
            lock (batchLock)
                openFolders.FlushBatchIfDirty();
        }

        private sealed class FileEntry
        {
            public string Filename { get; set; } = string.Empty;
            public string Hash { get; set; } = string.Empty;
            public long Size { get; set; }
        }

        private sealed class SkinData
        {
            public Guid Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public List<FileEntry> Files { get; set; } = new List<FileEntry>();
        }

        private sealed class BeatmapSetData
        {
            public Guid Id { get; set; }
            public int OnlineId { get; set; }
            public string Artist { get; set; } = string.Empty;
            public string Title { get; set; } = string.Empty;
            public List<FileEntry> Files { get; set; } = new List<FileEntry>();
        }
    }
}
