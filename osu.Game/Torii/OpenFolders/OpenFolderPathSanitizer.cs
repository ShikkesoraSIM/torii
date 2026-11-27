// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace osu.Game.Torii.OpenFolders
{
    /// <summary>
    /// Utilities for producing folder and file names that are safe on Windows, macOS and Linux,
    /// and for resolving relative paths in a way that cannot escape a configured root directory.
    /// </summary>
    public static class OpenFolderPathSanitizer
    {
        /// <summary>
        /// Default maximum length used when truncating a single segment.
        /// </summary>
        private const int default_max_length = 80;

        /// <summary>
        /// Default fallback name used when a sanitised segment ends up empty.
        /// </summary>
        private const string default_fallback = "Untitled";

        /// <summary>
        /// Filenames that are reserved on Windows and cannot be used as a directory or file.
        /// </summary>
        private static readonly HashSet<string> reserved_names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON",
            "PRN",
            "AUX",
            "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>
        /// Sanitises a single file or directory name so it is safe across Windows, macOS and Linux.
        /// Invalid characters are replaced with spaces, whitespace is collapsed, leading and trailing
        /// spaces and dots are trimmed, reserved Windows names get an underscore appended, and the
        /// result is truncated to <paramref name="maxLength"/> characters.
        /// </summary>
        /// <param name="raw">The raw segment to sanitise.</param>
        /// <param name="fallback">The fallback used when the resulting segment is empty.</param>
        /// <param name="maxLength">The maximum length of the resulting segment. A value of <c>0</c> or less disables truncation.</param>
        /// <returns>A sanitised, non-empty directory or file name.</returns>
        public static string SanitizeSegment(string raw, string fallback = default_fallback, int maxLength = default_max_length)
        {
            string sanitizedFallback = buildFallback(fallback);

            string cleaned = trimSpacesAndDots(replaceInvalidAndCollapseWhitespace(raw));
            if (cleaned.Length == 0)
                return sanitizedFallback;

            if (maxLength > 0 && cleaned.Length > maxLength)
                cleaned = truncateWithoutSplittingSurrogates(cleaned, maxLength);

            cleaned = trimSpacesAndDots(cleaned);
            if (cleaned.Length == 0)
                return sanitizedFallback;

            if (isReservedName(cleaned))
            {
                // Ensure we still fit within the requested length after appending the safety underscore.
                if (maxLength > 0 && cleaned.Length + 1 > maxLength)
                {
                    cleaned = truncateWithoutSplittingSurrogates(cleaned, Math.Max(0, maxLength - 1));
                    cleaned = trimSpacesAndDots(cleaned);
                    if (cleaned.Length == 0)
                        return sanitizedFallback;
                }

                cleaned += "_";
            }

            return cleaned;
        }

        /// <summary>
        /// Builds a unique, sanitised folder name for a skin inside the open <c>Skins/</c> directory.
        /// If the sanitised skin name collides with an existing folder, a short identifier suffix is
        /// appended so the folder name remains stable and readable.
        /// </summary>
        /// <param name="skinName">The human readable skin name.</param>
        /// <param name="id">The unique skin identifier.</param>
        /// <param name="folderExists">Predicate used to check whether a candidate name is already taken.</param>
        /// <returns>A unique, sanitised folder name.</returns>
        public static string BuildSkinFolderName(string skinName, Guid id, Func<string, bool> folderExists)
        {
            folderExists ??= _ => false;

            string baseName = SanitizeSegment(skinName, "Skin", 60);
            if (!folderExists(baseName))
                return baseName;

            string shortId = id.ToString("N");
            if (shortId.Length > 8)
                shortId = shortId.Substring(0, 8);

            string candidate = SanitizeSegment($"{baseName} [{shortId}]", "Skin", 100);
            int counter = 2;

            while (folderExists(candidate) && counter <= 9999)
            {
                candidate = SanitizeSegment($"{baseName} [{shortId}-{counter}]", "Skin", 100);
                counter++;
            }

            return candidate;
        }

        /// <summary>
        /// Builds a unique, sanitised folder name for a beatmap set inside the open <c>Songs/</c> directory.
        /// The online identifier is preferred as suffix when available, otherwise a short identifier is used.
        /// </summary>
        /// <param name="onlineId">The online beatmap set identifier, or <c>0</c> when not available.</param>
        /// <param name="artist">The beatmap artist name.</param>
        /// <param name="title">The beatmap title.</param>
        /// <param name="id">The local unique beatmap set identifier.</param>
        /// <param name="folderExists">Predicate used to check whether a candidate name is already taken.</param>
        /// <returns>A unique, sanitised folder name.</returns>
        public static string BuildBeatmapSetFolderName(int onlineId, string artist, string title, Guid id, Func<string, bool> folderExists)
        {
            folderExists ??= _ => false;

            string artistPart = SanitizeSegment(artist, "Unknown Artist", 40);
            string titlePart = SanitizeSegment(title, "Untitled", 40);
            string baseName = SanitizeSegment($"{artistPart} - {titlePart}", "Beatmap Set", 70);

            if (!folderExists(baseName))
                return baseName;

            string suffix;
            if (onlineId > 0)
            {
                suffix = onlineId.ToString();
            }
            else
            {
                suffix = id.ToString("N");
                if (suffix.Length > 8)
                    suffix = suffix.Substring(0, 8);
            }

            string candidate = SanitizeSegment($"{baseName} [{suffix}]", "Beatmap Set", 120);
            int counter = 2;

            while (folderExists(candidate) && counter <= 9999)
            {
                candidate = SanitizeSegment($"{baseName} [{suffix}-{counter}]", "Beatmap Set", 120);
                counter++;
            }

            return candidate;
        }

        /// <summary>
        /// Resolves a relative path against a root directory, ensuring the resolved path cannot
        /// escape the root. Rooted paths and any segment containing <c>..</c> are rejected outright.
        /// Invalid characters in individual segments are sanitised where possible.
        /// </summary>
        /// <param name="rootDir">The absolute or current-working-directory relative root.</param>
        /// <param name="relativePath">The candidate child path, using either <c>/</c> or <c>\</c> separators.</param>
        /// <param name="fullPath">The fully resolved child path when this method returns <c>true</c>.</param>
        /// <returns><c>true</c> if the child path is safe and strictly inside <paramref name="rootDir"/>.</returns>
        public static bool TryResolveSafeChildPath(string rootDir, string relativePath, out string fullPath)
        {
            fullPath = string.Empty;

            if (string.IsNullOrWhiteSpace(rootDir))
                return false;

            if (string.IsNullOrWhiteSpace(relativePath))
                return false;

            try
            {
                if (Path.IsPathRooted(relativePath))
                    return false;
            }
            catch
            {
                return false;
            }

            string normalized = relativePath.Replace('\\', '/').Trim();
            if (normalized.Length == 0)
                return false;

            string[] rawSegments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawSegments.Length == 0)
                return false;

            var cleanSegments = new List<string>(rawSegments.Length);

            foreach (string seg in rawSegments)
            {
                if (seg == ".")
                    continue;

                // Any traversal segment is rejected outright, regardless of what follows it.
                if (seg == "..")
                    return false;

                string cleaned = trimSpacesAndDots(replaceInvalidAndCollapseWhitespace(seg));
                if (cleaned.Length == 0)
                    return false;

                // Also reject segments that resolved to "." or ".." after cleaning.
                if (cleaned == "." || cleaned == "..")
                    return false;

                cleanSegments.Add(cleaned);
            }

            if (cleanSegments.Count == 0)
                return false;

            string fullRoot;
            string combined;
            string resolved;

            try
            {
                fullRoot = Path.GetFullPath(rootDir);
                combined = Path.Combine(fullRoot, string.Join(Path.DirectorySeparatorChar.ToString(), cleanSegments));
                resolved = Path.GetFullPath(combined);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrEmpty(resolved))
                return false;

            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            string trimmedRoot = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string trimmedResolved = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Must be strictly inside the root, never equal to it.
            if (string.Equals(trimmedResolved, trimmedRoot, comparison))
                return false;

            string rootWithSeparator = trimmedRoot + Path.DirectorySeparatorChar;
            if (!trimmedResolved.StartsWith(rootWithSeparator, comparison))
                return false;

            fullPath = resolved;
            return true;
        }

        /// <summary>
        /// Determines whether a filename should be ignored by the open-folder file watchers.
        /// Temporary download and editor artefacts such as <c>.tmp</c>, <c>.part</c>,
        /// <c>.crdownload</c>, <c>~$*</c>, <c>.DS_Store</c>, <c>Thumbs.db</c> and
        /// <c>desktop.ini</c> are ignored.
        /// </summary>
        /// <param name="fileName">The filename (or full path) to inspect.</param>
        /// <returns><c>true</c> if the filename should be ignored by watchers.</returns>
        public static bool IsIgnoredWatcherFilename(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return true;

            string name;
            try
            {
                name = Path.GetFileName(fileName);
            }
            catch
            {
                return true;
            }

            if (string.IsNullOrEmpty(name))
                return true;

            // Office temporary lock files.
            if (name.StartsWith("~$", StringComparison.Ordinal))
                return true;

            string lower = name.ToLowerInvariant();

            if (lower.EndsWith(".tmp", StringComparison.Ordinal))
                return true;

            if (lower.EndsWith(".part", StringComparison.Ordinal))
                return true;

            if (lower.EndsWith(".crdownload", StringComparison.Ordinal))
                return true;

            if (lower == ".ds_store")
                return true;

            if (lower == "thumbs.db")
                return true;

            if (lower == "desktop.ini")
                return true;

            return false;
        }

        /// <summary>
        /// Builds a fully sanitised, non-empty fallback name.
        /// </summary>
        private static string buildFallback(string fallback)
        {
            if (string.IsNullOrWhiteSpace(fallback))
                return default_fallback;

            string cleaned = trimSpacesAndDots(replaceInvalidAndCollapseWhitespace(fallback));
            return cleaned.Length == 0 ? default_fallback : cleaned;
        }

        /// <summary>
        /// Determines whether a filename is reserved on Windows. Checks the stem (before the first dot)
        /// so names such as <c>CON.txt</c> are also caught.
        /// </summary>
        private static bool isReservedName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            string stem = name;
            int dotIndex = stem.IndexOf('.');
            if (dotIndex >= 0)
                stem = stem.Substring(0, dotIndex);

            stem = stem.Trim();
            if (stem.Length == 0)
                return false;

            return reserved_names.Contains(stem);
        }

        /// <summary>
        /// Determines whether a character is invalid in a file or directory segment across the
        /// Windows, macOS and Linux families.
        /// </summary>
        private static bool isInvalidSegmentChar(char c)
        {
            if (c < 32)
                return true;

            switch (c)
            {
                case '<':
                case '>':
                case ':':
                case '"':
                case '/':
                case '\\':
                case '|':
                case '?':
                case '*':
                    return true;

                default:
                    return false;
            }
        }

        /// <summary>
        /// Replaces invalid characters with a single space and collapses all consecutive whitespace
        /// into a single space. Returns an empty string when <paramref name="raw"/> is null or empty.
        /// </summary>
        private static string replaceInvalidAndCollapseWhitespace(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return string.Empty;

            var sb = new StringBuilder(raw!.Length);
            bool lastWasSpace = false;

            foreach (char c in raw)
            {
                if (isInvalidSegmentChar(c) || char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }

                    continue;
                }

                sb.Append(c);
                lastWasSpace = false;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Trims leading and trailing spaces, tabs and dots (the characters Windows disallows at the
        /// boundaries of a name), returning an empty string for null input.
        /// </summary>
        private static string trimSpacesAndDots(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            int start = 0;
            int end = value.Length;

            while (start < end && isTrimChar(value[start]))
                start++;

            while (end > start && isTrimChar(value[end - 1]))
                end--;

            return start == 0 && end == value.Length ? value : value.Substring(start, end - start);
        }

        /// <summary>
        /// Characters trimmed at the boundaries of a segment.
        /// </summary>
        private static bool isTrimChar(char c) => c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '.';

        /// <summary>
        /// Truncates a string to the requested length without splitting a surrogate pair.
        /// </summary>
        private static string truncateWithoutSplittingSurrogates(string value, int maxLength)
        {
            if (maxLength <= 0)
                return string.Empty;

            if (value.Length <= maxLength)
                return value;

            int length = maxLength;

            if (length > 0 && char.IsHighSurrogate(value[length - 1]) && length < value.Length && char.IsLowSurrogate(value[length]))
                length--;

            return value.Substring(0, length);
        }
    }
}
