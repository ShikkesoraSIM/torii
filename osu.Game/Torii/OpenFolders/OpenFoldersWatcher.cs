using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Bindables;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Skinning;

namespace osu.Game.Torii.OpenFolders
{
    public sealed class OpenFoldersWatcher : IDisposable
    {
        private readonly OpenFoldersStore openFolders;
        private readonly SkinManager skinManager;
        private readonly BeatmapManager beatmapManager;
        private readonly Scheduler scheduler;

        private IBindable<bool>? skinsEnabledBindable;
        private IBindable<bool>? songsEnabledBindable;

        private readonly object rootWatchersLock = new object();
        private FileSystemWatcher? skinsRootWatcher;
        private FileSystemWatcher? songsRootWatcher;

        private readonly object activeWatcherLock = new object();
        private FileSystemWatcher? activeSkinWatcher;
        private Timer? activeSkinDebounceTimer;
        private long activeSkinEpoch;
        private bool activeSkinPendingAudioOnly = true;

        private readonly ConcurrentDictionary<string, DebounceEntry> skinRootDebounces = new();
        private readonly ConcurrentDictionary<string, DebounceEntry> songRootDebounces = new();

        private volatile bool disposed;

        public OpenFoldersWatcher(OpenFoldersStore openFolders, SkinManager skinManager, BeatmapManager beatmapManager, OsuConfigManager config, Scheduler scheduler)
        {
            this.openFolders = openFolders;
            this.skinManager = skinManager;
            this.beatmapManager = beatmapManager;
            this.scheduler = scheduler;

            if (!OpenFoldersStore.IsSupportedOnCurrentPlatform)
                return;

            skinsEnabledBindable = config.GetBindable<bool>(OsuSetting.ToriiOpenFoldersSkins);
            songsEnabledBindable = config.GetBindable<bool>(OsuSetting.ToriiOpenFoldersSongs);

            if (skinsEnabledBindable.Value || songsEnabledBindable.Value)
                openFolders.EnsureRootDirectories();

            skinsEnabledBindable.ValueChanged += OnSkinsEnabledChanged;
            songsEnabledBindable.ValueChanged += OnSongsEnabledChanged;
            skinManager.CurrentSkinInfo.ValueChanged += OnCurrentSkinInfoChanged;
            openFolders.Changed += OnOpenFoldersChanged;

            UpdateWatchers();
        }

        private void OnSkinsEnabledChanged(ValueChangedEvent<bool> e)
        {
            if (disposed) return;
            if (e.NewValue)
                openFolders.EnsureRootDirectories();
            UpdateWatchers();
        }

        private void OnSongsEnabledChanged(ValueChangedEvent<bool> e)
        {
            if (disposed) return;
            if (e.NewValue)
                openFolders.EnsureRootDirectories();
            UpdateWatchers();
        }

        private void OnCurrentSkinInfoChanged(ValueChangedEvent<Live<SkinInfo>> e)
        {
            if (disposed) return;
            UpdateActiveSkinWatcher();
        }

        private void OnOpenFoldersChanged()
        {
            if (disposed) return;
            scheduler.Add(UpdateActiveSkinWatcher);
        }

        private void UpdateWatchers()
        {
            if (disposed) return;

            bool skinsEnabled = skinsEnabledBindable?.Value ?? false;
            bool songsEnabled = songsEnabledBindable?.Value ?? false;
            bool skinsDirExists = Directory.Exists(openFolders.SkinsRootPath);
            bool songsDirExists = Directory.Exists(openFolders.SongsRootPath);

            if (skinsEnabled || skinsDirExists)
                EnsureSkinsRootWatcher();
            else
                DisposeSkinsRootWatcher();

            if (songsEnabled || songsDirExists)
                EnsureSongsRootWatcher();
            else
                DisposeSongsRootWatcher();

            UpdateActiveSkinWatcher();
        }

        private void EnsureSkinsRootWatcher()
        {
            lock (rootWatchersLock)
            {
                if (disposed) return;
                if (skinsRootWatcher != null) return;
                if (!Directory.Exists(openFolders.SkinsRootPath)) return;

                var watcher = new FileSystemWatcher(openFolders.SkinsRootPath)
                {
                    IncludeSubdirectories = false,
                    InternalBufferSize = 32768,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Created += OnSkinRootCreated;
                watcher.Renamed += OnSkinRootRenamed;

                try
                {
                    watcher.EnableRaisingEvents = true;
                    skinsRootWatcher = watcher;
                }
                catch
                {
                    watcher.Created -= OnSkinRootCreated;
                    watcher.Renamed -= OnSkinRootRenamed;
                    try { watcher.Dispose(); } catch (ObjectDisposedException) { }
                }
            }
        }

        private void DisposeSkinsRootWatcher()
        {
            lock (rootWatchersLock)
            {
                var watcher = skinsRootWatcher;
                skinsRootWatcher = null;
                if (watcher == null) return;

                try { watcher.EnableRaisingEvents = false; } catch (ObjectDisposedException) { }
                watcher.Created -= OnSkinRootCreated;
                watcher.Renamed -= OnSkinRootRenamed;
                try { watcher.Dispose(); } catch (ObjectDisposedException) { }
            }
        }

        private void EnsureSongsRootWatcher()
        {
            lock (rootWatchersLock)
            {
                if (disposed) return;
                if (songsRootWatcher != null) return;
                if (!Directory.Exists(openFolders.SongsRootPath)) return;

                var watcher = new FileSystemWatcher(openFolders.SongsRootPath)
                {
                    IncludeSubdirectories = false,
                    InternalBufferSize = 65536,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };
                watcher.Created += OnSongRootCreated;
                watcher.Renamed += OnSongRootRenamed;

                try
                {
                    watcher.EnableRaisingEvents = true;
                    songsRootWatcher = watcher;
                }
                catch
                {
                    watcher.Created -= OnSongRootCreated;
                    watcher.Renamed -= OnSongRootRenamed;
                    try { watcher.Dispose(); } catch (ObjectDisposedException) { }
                }
            }
        }

        private void DisposeSongsRootWatcher()
        {
            lock (rootWatchersLock)
            {
                var watcher = songsRootWatcher;
                songsRootWatcher = null;
                if (watcher == null) return;

                try { watcher.EnableRaisingEvents = false; } catch (ObjectDisposedException) { }
                watcher.Created -= OnSongRootCreated;
                watcher.Renamed -= OnSongRootRenamed;
                try { watcher.Dispose(); } catch (ObjectDisposedException) { }
            }
        }

        private void UpdateActiveSkinWatcher()
        {
            if (disposed) return;

            var skin = skinManager.CurrentSkinInfo.Value;
            string? path = skin != null ? openFolders.GetSkinFolderPath(skin.ID) : null;

            if (path == null || !Directory.Exists(path))
            {
                DisposeActiveSkinWatcher();
                return;
            }

            lock (activeWatcherLock)
            {
                if (disposed) return;

                if (activeSkinWatcher != null && string.Equals(activeSkinWatcher.Path, path, StringComparison.OrdinalIgnoreCase))
                    return;

                disposeActiveSkinWatcherLocked();

                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 32768,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                };
                watcher.Changed += OnActiveSkinChanged;
                watcher.Created += OnActiveSkinChanged;
                watcher.Deleted += OnActiveSkinChanged;
                watcher.Renamed += OnActiveSkinRenamed;

                activeSkinEpoch++;
                activeSkinWatcher = watcher;

                try
                {
                    watcher.EnableRaisingEvents = true;
                }
                catch
                {
                    disposeActiveSkinWatcherLocked();
                }
            }
        }

        private void DisposeActiveSkinWatcher()
        {
            lock (activeWatcherLock)
                disposeActiveSkinWatcherLocked();
        }

        private void disposeActiveSkinWatcherLocked()
        {
            activeSkinEpoch++;

            var watcher = activeSkinWatcher;
            activeSkinWatcher = null;

            if (watcher != null)
            {
                try { watcher.EnableRaisingEvents = false; } catch (ObjectDisposedException) { }
                watcher.Changed -= OnActiveSkinChanged;
                watcher.Created -= OnActiveSkinChanged;
                watcher.Deleted -= OnActiveSkinChanged;
                watcher.Renamed -= OnActiveSkinRenamed;
                try { watcher.Dispose(); } catch (ObjectDisposedException) { }
            }

            var timer = activeSkinDebounceTimer;
            activeSkinDebounceTimer = null;

            if (timer != null)
            {
                try { timer.Dispose(); } catch (ObjectDisposedException) { }
            }

            activeSkinPendingAudioOnly = true;
        }

        private void OnSkinRootCreated(object sender, FileSystemEventArgs e) => HandleSkinRootPath(e.FullPath);

        private void OnSkinRootRenamed(object sender, RenamedEventArgs e) => HandleSkinRootPath(e.FullPath);

        private void HandleSkinRootPath(string fullPath)
        {
            if (disposed) return;

            string fileName = Path.GetFileName(fullPath);
            if (OpenFolderPathSanitizer.IsIgnoredWatcherFilename(fileName)) return;
            if (openFolders.IsWatcherSuppressed(fullPath)) return;

            if (Directory.Exists(fullPath))
            {
                if (openFolders.TryFindSkinIdByFolderName(fileName, out _)) return;
                ScheduleDebouncedImport(skinRootDebounces, fullPath, 500, ImportSkinPath);
            }
            else if (string.Equals(Path.GetExtension(fullPath), ".osk", StringComparison.OrdinalIgnoreCase))
            {
                ScheduleDebouncedImport(skinRootDebounces, fullPath, 500, ImportSkinPath);
            }
        }

        private void OnSongRootCreated(object sender, FileSystemEventArgs e) => HandleSongRootPath(e.FullPath);

        private void OnSongRootRenamed(object sender, RenamedEventArgs e) => HandleSongRootPath(e.FullPath);

        private void HandleSongRootPath(string fullPath)
        {
            if (disposed) return;

            string fileName = Path.GetFileName(fullPath);
            if (OpenFolderPathSanitizer.IsIgnoredWatcherFilename(fileName)) return;
            if (openFolders.IsWatcherSuppressed(fullPath)) return;

            if (Directory.Exists(fullPath))
            {
                if (openFolders.TryFindBeatmapSetIdByFolderName(fileName, out _)) return;
                ScheduleDebouncedImport(songRootDebounces, fullPath, 600, ImportSongPath);
            }
            else
            {
                string extension = Path.GetExtension(fullPath);
                if (string.Equals(extension, ".osz", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".olz", StringComparison.OrdinalIgnoreCase))
                {
                    ScheduleDebouncedImport(songRootDebounces, fullPath, 600, ImportSongPath);
                }
            }
        }

        private void OnActiveSkinChanged(object sender, FileSystemEventArgs e) => HandleActiveSkinChange(e.FullPath);

        private void OnActiveSkinRenamed(object sender, RenamedEventArgs e) => HandleActiveSkinChange(e.FullPath);

        private void HandleActiveSkinChange(string fullPath)
        {
            if (disposed) return;

            string fileName = Path.GetFileName(fullPath);
            if (OpenFolderPathSanitizer.IsIgnoredWatcherFilename(fileName)) return;
            if (openFolders.IsWatcherSuppressed(fullPath)) return;

            string extension = Path.GetExtension(fullPath).ToLowerInvariant();
            bool isAudio = extension == ".wav" || extension == ".ogg" || extension == ".mp3";

            lock (activeWatcherLock)
            {
                if (disposed) return;

                if (!isAudio)
                    activeSkinPendingAudioOnly = false;

                var oldTimer = activeSkinDebounceTimer;
                activeSkinDebounceTimer = null;

                if (oldTimer != null)
                {
                    try { oldTimer.Dispose(); } catch (ObjectDisposedException) { }
                }

                long myEpoch = activeSkinEpoch;
                Timer? timer = null;
                timer = new Timer(_ =>
                {
                    if (disposed) return;
                    if (myEpoch != Volatile.Read(ref activeSkinEpoch)) return;
                    FlushActiveSkinReload(myEpoch, timer);
                }, null, 300, Timeout.Infinite);

                activeSkinDebounceTimer = timer;
            }
        }

        private void FlushActiveSkinReload(long myEpoch, Timer? timer)
        {
            if (disposed) return;
            if (myEpoch != Volatile.Read(ref activeSkinEpoch)) return;

            bool audioOnly;

            lock (activeWatcherLock)
            {
                if (disposed) return;
                if (myEpoch != activeSkinEpoch) return;
                if (!ReferenceEquals(activeSkinDebounceTimer, timer)) return;

                audioOnly = activeSkinPendingAudioOnly;
                activeSkinPendingAudioOnly = true;
                activeSkinDebounceTimer = null;

                if (timer != null)
                {
                    try { timer.Dispose(); } catch (ObjectDisposedException) { }
                }
            }

            scheduler.Add(() =>
            {
                if (!disposed)
                    skinManager.ReloadCurrentSkin(audioOnly);
            });
        }

        private void ImportSkinPath(string path)
        {
            if (disposed) return;

            Task.Run(() =>
            {
                if (disposed) return;
                if (openFolders.IsWatcherSuppressed(path)) return;

                if (Directory.Exists(path))
                {
                    string folderName = Path.GetFileName(path);
                    if (openFolders.TryFindSkinIdByFolderName(folderName, out _)) return;
                }

                skinManager.Import(path);
            });
        }

        private void ImportSongPath(string path)
        {
            if (disposed) return;

            Task.Run(() =>
            {
                if (disposed) return;
                if (openFolders.IsWatcherSuppressed(path)) return;

                if (Directory.Exists(path))
                {
                    string folderName = Path.GetFileName(path);
                    if (openFolders.TryFindBeatmapSetIdByFolderName(folderName, out _)) return;
                }

                beatmapManager.Import(path);
            });
        }

        private void ScheduleDebouncedImport(ConcurrentDictionary<string, DebounceEntry> entries, string path, int delayMs, Action<string> action)
        {
            if (disposed) return;

            var entry = new DebounceEntry();
            entry.Timer = new Timer(_ =>
            {
                if (disposed) return;

                if (!entries.TryRemove(new KeyValuePair<string, DebounceEntry>(path, entry)))
                    return;

                try { entry.Timer?.Dispose(); } catch (ObjectDisposedException) { }

                if (disposed) return;
                if (openFolders.IsWatcherSuppressed(path)) return;

                action(path);
            }, null, Timeout.Infinite, Timeout.Infinite);

            entries.AddOrUpdate(path, entry, (_, existing) =>
            {
                try { existing.Timer?.Dispose(); } catch (ObjectDisposedException) { }
                return entry;
            });

            try
            {
                entry.Timer.Change(delayMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // A newer entry replaced this one before it could be scheduled.
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            if (skinsEnabledBindable != null)
                skinsEnabledBindable.ValueChanged -= OnSkinsEnabledChanged;
            if (songsEnabledBindable != null)
                songsEnabledBindable.ValueChanged -= OnSongsEnabledChanged;
            skinManager.CurrentSkinInfo.ValueChanged -= OnCurrentSkinInfoChanged;
            openFolders.Changed -= OnOpenFoldersChanged;

            DisposeSkinsRootWatcher();
            DisposeSongsRootWatcher();
            DisposeActiveSkinWatcher();

            foreach (var entry in skinRootDebounces.Values)
            {
                try { entry.Timer?.Dispose(); } catch (ObjectDisposedException) { }
            }
            skinRootDebounces.Clear();

            foreach (var entry in songRootDebounces.Values)
            {
                try { entry.Timer?.Dispose(); } catch (ObjectDisposedException) { }
            }
            songRootDebounces.Clear();
        }

        private sealed class DebounceEntry
        {
            public Timer? Timer;
        }
    }
}
