// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.IO;
using System.Linq;
using osu.Framework.Audio;
using osu.Game.Beatmaps;
using osu.Game.Extensions;
using osu.Game.Utils;

namespace osu.Game.Rulesets.Edit.Checks.Components
{
    public static class AudioCheckUtils
    {
        public static bool HasAudioExtension(string filename) => SupportedExtensions.AUDIO_EXTENSIONS.Contains(Path.GetExtension(filename).ToLowerInvariant());

        /// <summary>
        /// Gets the content-sniffed <see cref="AudioFormat"/> from a stream.
        /// </summary>
        public static AudioFormat GetAudioFormat(Stream data) => AudioInspector.GetFormat(data);

        /// <summary>
        /// Gets the audio format for a specific file in a beatmapset.
        /// </summary>
        public static AudioFormat GetAudioFormatFromFile(BeatmapVerifierContext context, string filename)
        {
            var beatmapSet = context.CurrentDifficulty.Playable.BeatmapInfo.BeatmapSet;
            var audioFile = beatmapSet?.GetFile(filename);

            if (beatmapSet == null || audioFile == null)
                return AudioFormat.Unknown;

            using (Stream data = context.CurrentDifficulty.Working.GetStream(audioFile.File.GetStoragePath()))
                return GetAudioFormat(data);
        }
    }
}
