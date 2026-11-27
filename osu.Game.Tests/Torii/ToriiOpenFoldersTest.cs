// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using osu.Framework.IO.Stores;
using osu.Framework.Platform;
using osu.Game.Localisation;
using osu.Game.Torii.OpenFolders;

namespace osu.Game.Tests.Torii
{
    [TestFixture]
    public class ToriiOpenFoldersTest
    {
        private string tempRoot = null!;

        [SetUp]
        public void SetUp()
        {
            tempRoot = Path.Combine(Path.GetTempPath(), "torii-open-folders-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempRoot);
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
            }
        }

        [Test]
        public void TestPathSanitizerHandlesReservedNamesAndInvalidChars()
        {
            Assert.That(OpenFolderPathSanitizer.SanitizeSegment("CON"), Does.Not.EqualTo("CON").IgnoreCase);
            Assert.That(OpenFolderPathSanitizer.SanitizeSegment("nul"), Does.Not.EqualTo("nul").IgnoreCase);
            Assert.That(OpenFolderPathSanitizer.SanitizeSegment("COM1"), Does.Not.EqualTo("COM1").IgnoreCase);
            Assert.That(OpenFolderPathSanitizer.SanitizeSegment("LPT9"), Does.Not.EqualTo("LPT9").IgnoreCase);

            string cleaned = OpenFolderPathSanitizer.SanitizeSegment("  Artist: <Best> / Title*? . .  ");
            Assert.That(cleaned, Does.Not.Contain(":"));
            Assert.That(cleaned, Does.Not.Contain("<"));
            Assert.That(cleaned, Does.Not.Contain(">"));
            Assert.That(cleaned, Does.Not.Contain("/"));
            Assert.That(cleaned, Does.Not.Contain("*"));
            Assert.That(cleaned, Does.Not.Contain("?"));
            Assert.That(cleaned.EndsWith('.'), Is.False);
            Assert.That(cleaned.EndsWith(' '), Is.False);

            string empty = OpenFolderPathSanitizer.SanitizeSegment("   ...   ", "Fallback");
            Assert.That(empty, Is.EqualTo("Fallback"));

            string longName = OpenFolderPathSanitizer.SanitizeSegment(new string('A', 200), maxLength: 60);
            Assert.That(longName.Length, Is.LessThanOrEqualTo(60));
        }

        [Test]
        public void TestPathSanitizerBlocksTraversal()
        {
            Assert.That(OpenFolderPathSanitizer.TryResolveSafeChildPath(tempRoot, "../escape.txt", out _), Is.False);
            Assert.That(OpenFolderPathSanitizer.TryResolveSafeChildPath(tempRoot, @"sub\..\..\escape.txt", out _), Is.False);
            Assert.That(OpenFolderPathSanitizer.TryResolveSafeChildPath(tempRoot, "/etc/passwd", out _), Is.False);
            Assert.That(OpenFolderPathSanitizer.TryResolveSafeChildPath(tempRoot, @"C:\Windows\system.ini", out _), Is.False);

            Assert.That(OpenFolderPathSanitizer.TryResolveSafeChildPath(tempRoot, "sb/bg.jpg", out string safe), Is.True);
            Assert.That(Path.GetFullPath(safe), Does.StartWith(Path.GetFullPath(tempRoot)));
        }

        [Test]
        public void TestIgnoredWatcherFilenames()
        {
            Assert.That(OpenFolderPathSanitizer.IsIgnoredWatcherFilename("cursor.png.tmp"), Is.True);
            Assert.That(OpenFolderPathSanitizer.IsIgnoredWatcherFilename("~$skin.ini"), Is.True);
            Assert.That(OpenFolderPathSanitizer.IsIgnoredWatcherFilename(".DS_Store"), Is.True);
            Assert.That(OpenFolderPathSanitizer.IsIgnoredWatcherFilename("download.part"), Is.True);
            Assert.That(OpenFolderPathSanitizer.IsIgnoredWatcherFilename("cursor.png"), Is.False);
            Assert.That(OpenFolderPathSanitizer.IsIgnoredWatcherFilename("skin.ini"), Is.False);
        }

        [Test]
        public void TestOpenFoldersStoreRoundtripAndHashResolution()
        {
            var storage = new NativeStorage(tempRoot);
            var store = new OpenFoldersStore(storage);
            store.EnsureRootDirectories();

            Guid skinId = Guid.NewGuid();
            Guid setId = Guid.NewGuid();
            Guid beatmapId = Guid.NewGuid();

            string skinDir = Path.Combine(store.SkinsRootPath, "My Cool Skin");
            Directory.CreateDirectory(skinDir);
            store.RegisterSkinFolder(skinId, "My Cool Skin");

            string songDir = Path.Combine(store.SongsRootPath, "12345 Torii - Sample");
            Directory.CreateDirectory(songDir);
            string mapPath = Path.Combine(songDir, "sample.osu");
            File.WriteAllText(mapPath, "sample beatmap data", Encoding.UTF8);

            const string fakeHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
            store.RegisterBeatmapSetFolder(setId, "12345 Torii - Sample", new[] { ("sample.osu", fakeHash) });
            store.MarkLocallyModified(beatmapId, true);

            // Reload from disk in a fresh OpenFoldersStore instance to prove atomic persistence.
            var reloaded = new OpenFoldersStore(storage);
            Assert.That(reloaded.GetSkinFolderName(skinId), Is.EqualTo("My Cool Skin"));
            Assert.That(reloaded.GetBeatmapSetFolderName(setId), Is.EqualTo("12345 Torii - Sample"));
            Assert.That(reloaded.TryFindSkinIdByFolderName("my cool skin", out Guid foundSkin), Is.True);
            Assert.That(foundSkin, Is.EqualTo(skinId));
            Assert.That(reloaded.IsLocallyModified(beatmapId), Is.True);

            reloaded.IndexBeatmapSetHashes(setId, new[] { ("sample.osu", fakeHash) });
            string storageStylePath = Path.Combine("a", "ab", fakeHash);
            string? resolved = reloaded.TryResolveSongFile(storageStylePath);
            Assert.That(resolved, Is.Not.Null);
            Assert.That(Path.GetFullPath(resolved!), Is.EqualTo(Path.GetFullPath(mapPath)));
        }

        [Test]
        public void TestUnlockedDirectoryResourceStoreDoesNotLockFiles()
        {
            string skinDir = Path.Combine(tempRoot, "SkinUnlocked");
            Directory.CreateDirectory(skinDir);
            string cursorPath = Path.Combine(skinDir, "Cursor.PNG");
            File.WriteAllBytes(cursorPath, new byte[] { 1, 2, 3, 4 });

            using var store = new UnlockedDirectoryResourceStore(skinDir);

            // Case-insensitive read
            byte[]? data = store.Get("cursor.png");
            Assert.That(data, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));

            // Read stream and overwrite file on disk while stream is still open (simulating Photoshop saving!).
            using (Stream? stream = store.GetStream("cursor.png"))
            {
                Assert.That(stream, Is.Not.Null);
                Assert.DoesNotThrow(() => File.WriteAllBytes(cursorPath, new byte[] { 9, 8, 7, 6 }));
            }

            byte[]? updated = store.Get("cursor.png");
            Assert.That(updated, Is.EqualTo(new byte[] { 9, 8, 7, 6 }));
        }

        [Test]
        public void TestOpenFolderBeatmapResourceStorePrefersOpenFolderWithFallback()
        {
            var storage = new NativeStorage(tempRoot);
            var openFolders = new OpenFoldersStore(storage);
            openFolders.EnsureRootDirectories();

            string songDir = Path.Combine(openFolders.SongsRootPath, "777 Test - Song");
            Directory.CreateDirectory(songDir);
            File.WriteAllText(Path.Combine(songDir, "diff.osu"), "EDITED_CONTENT", new UTF8Encoding(false));

            const string hash = "deadbeef0123456789abcdef0123456789abcdef0123456789abcdef01234567";
            string hashedKey = Path.Combine("d", "de", hash);
            openFolders.RegisterBeatmapSetFolder(Guid.NewGuid(), "777 Test - Song", new[] { ("diff.osu", hash) });

            var fallback = new InMemoryFallbackStore(new Dictionary<string, byte[]>
            {
                [hashedKey] = Encoding.UTF8.GetBytes("HASHED_FALLBACK"),
                ["o/ot/otherhash"] = Encoding.UTF8.GetBytes("ONLY_IN_HASHED"),
            });

            using var hybrid = new OpenFolderBeatmapResourceStore(openFolders, fallback);

            // Open folder wins over hashed copy
            Assert.That(Encoding.UTF8.GetString(hybrid.Get(hashedKey)!), Is.EqualTo("EDITED_CONTENT"));

            // Unmapped file seamlessly falls back to hashed store
            Assert.That(Encoding.UTF8.GetString(hybrid.Get("o/ot/otherhash")!), Is.EqualTo("ONLY_IN_HASHED"));
        }

        [Test]
        public void TestOpenFoldersStringsContainZeroForbiddenBrandingOrEmDashes()
        {
            var forbidden = new Regex(@"(osu!|\bosu\b|lazer|stable|ppy|\u2014)", RegexOptions.IgnoreCase);
            var type = typeof(ToriiOpenFoldersStrings);

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                string val = prop.GetValue(null)?.ToString() ?? string.Empty;
                Assert.That(forbidden.IsMatch(val), Is.False, $"Forbidden branding or em-dash found in {prop.Name}: \"{val}\"");
            }

            string[] methodOutputs =
            {
                ToriiOpenFoldersStrings.UnpackSkinsBody(12, "45.2 MB").ToString(),
                ToriiOpenFoldersStrings.UnpackSongsBody(1500, "4.20 GB").ToString(),
                ToriiOpenFoldersStrings.SkinReloaded("Test Skin").ToString(),
            };

            foreach (string val in methodOutputs)
                Assert.That(forbidden.IsMatch(val), Is.False, $"Forbidden branding or em-dash found in formatted string: \"{val}\"");
        }

        private sealed class InMemoryFallbackStore : IResourceStore<byte[]>
        {
            private readonly Dictionary<string, byte[]> items;

            public InMemoryFallbackStore(Dictionary<string, byte[]> items) => this.items = items;

            public byte[]? Get(string name) => items.TryGetValue(name, out var v) ? v : null;

            public System.Threading.Tasks.Task<byte[]?> GetAsync(string name, System.Threading.CancellationToken cancellationToken = default) =>
                System.Threading.Tasks.Task.FromResult(Get(name));

            public Stream? GetStream(string name) => Get(name) is byte[] b ? new MemoryStream(b) : null;

            public IEnumerable<string> GetAvailableResources() => items.Keys;

            public void Dispose()
            {
            }
        }
    }
}
