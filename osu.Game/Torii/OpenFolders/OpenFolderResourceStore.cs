#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.IO.Stores;

namespace osu.Game.Torii.OpenFolders
{
    public sealed class UnlockedDirectoryResourceStore : IResourceStore<byte[]>
    {
        private readonly string rootDirectory;
        private readonly Dictionary<string, string> resourceMap;
        private readonly object syncRoot = new object();

        public UnlockedDirectoryResourceStore(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory))
                throw new ArgumentException("A root directory is required.", nameof(rootDirectory));

            this.rootDirectory = Path.GetFullPath(rootDirectory);
            resourceMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            buildMap();
        }

        private void buildMap()
        {
            lock (syncRoot)
            {
                resourceMap.Clear();

                if (!Directory.Exists(rootDirectory))
                    return;

                try
                {
                    foreach (string file in Directory.EnumerateFiles(rootDirectory, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            string relative = Path.GetRelativePath(rootDirectory, file).Replace('\\', '/');

                            if (string.IsNullOrWhiteSpace(relative))
                                continue;

                            resourceMap[relative.ToLowerInvariant()] = relative;
                        }
                        catch
                        {
                            // Skip files that disappear or cannot be represented.
                        }
                    }
                }
                catch
                {
                    // The root is temporarily unavailable. Direct lookup can still be attempted.
                }
            }
        }

        private static string normaliseRelativePath(string path)
        {
            if (path == null)
                return null;

            path = path.Replace('\\', '/').Trim();

            while (path.StartsWith("./", StringComparison.Ordinal))
                path = path.Substring(2);

            return path.TrimStart('/');
        }

        private bool tryGetFullPath(string name, out string fullPath)
        {
            fullPath = null;

            string relative = normaliseRelativePath(name);

            if (string.IsNullOrWhiteSpace(relative))
                return false;

            string key = relative.ToLowerInvariant();

            lock (syncRoot)
            {
                if (resourceMap.TryGetValue(key, out string mapped))
                {
                    string candidate = Path.Combine(rootDirectory, mapped.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(candidate))
                    {
                        fullPath = candidate;
                        return true;
                    }

                    resourceMap.Remove(key);
                }
            }

            if (OpenFolderPathSanitizer.TryResolveSafeChildPath(rootDirectory, relative, out string safePath) && File.Exists(safePath))
            {
                fullPath = safePath;

                try
                {
                    string actualRelative = Path.GetRelativePath(rootDirectory, safePath).Replace('\\', '/');

                    if (!string.IsNullOrWhiteSpace(actualRelative))
                    {
                        lock (syncRoot)
                            resourceMap[actualRelative.ToLowerInvariant()] = actualRelative;
                    }
                }
                catch
                {
                    // Mapping the file is best effort only.
                }

                return true;
            }

            return false;
        }

        public byte[] Get(string name)
        {
            if (!tryGetFullPath(name, out string fullPath))
                return null;

            try
            {
                return OpenFolderResourceIo.ReadAllBytesWithRetry(fullPath);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        public Stream GetStream(string name)
        {
            byte[] bytes = Get(name);

            return bytes == null ? null : new MemoryStream(bytes, writable: false);
        }

        public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<byte[]>(cancellationToken);

            return Task.Run(() => Get(name), cancellationToken);
        }

        public IEnumerable<string> GetAvailableResources()
        {
            lock (syncRoot)
                return resourceMap.Keys.ToArray();
        }

        public void Dispose()
        {
        }
    }

    public sealed class OpenFolderBeatmapResourceStore : IResourceStore<byte[]>
    {
        private readonly OpenFoldersStore openFolders;
        private readonly IResourceStore<byte[]> fallbackStore;

        public OpenFolderBeatmapResourceStore(OpenFoldersStore openFolders, IResourceStore<byte[]> fallbackStore)
        {
            this.openFolders = openFolders ?? throw new ArgumentNullException(nameof(openFolders));
            this.fallbackStore = fallbackStore ?? throw new ArgumentNullException(nameof(fallbackStore));
        }

        public byte[] Get(string name)
        {
            try
            {
                string fullPath = openFolders.TryResolveSongFile(name);

                if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                    return OpenFolderResourceIo.ReadAllBytesWithRetry(fullPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return fallbackStore.Get(name);
        }

        public Stream GetStream(string name)
        {
            try
            {
                string fullPath = openFolders.TryResolveSongFile(name);

                if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                {
                    FileStream stream = OpenFolderResourceIo.OpenSharedFileStreamWithRetry(fullPath);

                    try
                    {
                        if (stream.Length <= OpenFolderResourceIo.MaxInMemoryCopySize)
                        {
                            using (stream)
                            {
                                var memory = new MemoryStream((int)stream.Length);
                                stream.CopyTo(memory);
                                memory.Position = 0;
                                return memory;
                            }
                        }

                        return stream;
                    }
                    catch
                    {
                        stream.Dispose();
                        throw;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return fallbackStore.GetStream(name);
        }

        public Task<byte[]> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled<byte[]>(cancellationToken);

            try
            {
                string fullPath = openFolders.TryResolveSongFile(name);

                if (!string.IsNullOrEmpty(fullPath) && File.Exists(fullPath))
                    return Task.FromResult(OpenFolderResourceIo.ReadAllBytesWithRetry(fullPath));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return fallbackStore.GetAsync(name, cancellationToken);
        }

        public IEnumerable<string> GetAvailableResources()
        {
            return fallbackStore.GetAvailableResources();
        }

        public void Dispose()
        {
        }
    }

    internal static class OpenFolderResourceIo
    {
        internal const long MaxInMemoryCopySize = 4L * 1024L * 1024L;

        private static readonly int[] retryDelaysMilliseconds = { 15, 40, 80 };

        internal static byte[] ReadAllBytesWithRetry(string fullPath)
        {
            for (int attempt = 0;; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        return memory.ToArray();
                    }
                }
                catch (IOException) when (attempt < retryDelaysMilliseconds.Length)
                {
                    Thread.Sleep(retryDelaysMilliseconds[attempt]);
                }
            }
        }

        internal static FileStream OpenSharedFileStreamWithRetry(string fullPath)
        {
            for (int attempt = 0;; attempt++)
            {
                try
                {
                    return new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                }
                catch (IOException) when (attempt < retryDelaysMilliseconds.Length)
                {
                    Thread.Sleep(retryDelaysMilliseconds[attempt]);
                }
            }
        }
    }
}
