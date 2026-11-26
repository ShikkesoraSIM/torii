// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.IO;
using osu.Framework.Audio;
using osu.Game.Beatmaps;
using osu.Game.Extensions;
using osu.Game.Models;
using osu.Game.Rulesets.Edit.Checks.Components;

namespace osu.Game.Rulesets.Edit.Checks
{
    public class CheckHitsoundsFormat : ICheck
    {
        public CheckMetadata Metadata => new CheckMetadata(CheckCategory.Audio, "Checks for hitsound formats.", CheckScope.BeatmapSet);

        public IEnumerable<IssueTemplate> PossibleTemplates => new IssueTemplate[]
        {
            new IssueTemplateFormatUnsupported(this),
            new IssueTemplateIncorrectFormat(this),
        };

        public IEnumerable<Issue> Run(BeatmapVerifierContext context)
        {
            var beatmapSet = context.CurrentDifficulty.Playable.BeatmapInfo.BeatmapSet;

            if (beatmapSet == null) yield break;

            // Collect all audio files from all difficulties to exclude them from the check, as they aren't hitsounds.
            var audioFiles = new HashSet<RealmNamedFileUsage>(ReferenceEqualityComparer.Instance);

            foreach (var difficulty in context.AllDifficulties)
            {
                var audioFile = beatmapSet.GetFile(difficulty.Playable.Metadata.AudioFile);
                if (audioFile != null)
                    audioFiles.Add(audioFile);
            }

            foreach (var file in beatmapSet.Files)
            {
                if (audioFiles.Contains(file)) continue;

                using (Stream data = context.CurrentDifficulty.Working.GetStream(file.File.GetStoragePath()))
                {
                    if (data == null)
                        continue;

                    if (!AudioCheckUtils.HasAudioExtension(file.Filename) || !probablyHasAudioData(data))
                        continue;

                    var format = AudioInspector.GetFormat(data);

                    if (format == AudioFormat.Unknown)
                    {
                        yield return new IssueTemplateFormatUnsupported(this).Create(file.Filename);
                        continue;
                    }

                    if (format != AudioFormat.Wave && format != AudioFormat.Ogg)
                        yield return new IssueTemplateIncorrectFormat(this).Create(file.Filename);
                }
            }
        }

        private bool probablyHasAudioData(Stream data) => data.Length > 100;

        public class IssueTemplateFormatUnsupported : IssueTemplate
        {
            public IssueTemplateFormatUnsupported(ICheck check)
                : base(check, IssueType.Problem, "\"{0}\" may be corrupt or using a unsupported audio format. Use wav or ogg for hitsounds.")
            {
            }

            public Issue Create(string file) => new Issue(this, file);
        }

        public class IssueTemplateIncorrectFormat : IssueTemplate
        {
            public IssueTemplateIncorrectFormat(ICheck check)
                : base(check, IssueType.Problem, "\"{0}\" is using a incorrect format. Use wav or ogg for hitsounds.")
            {
            }

            public Issue Create(string file) => new Issue(this, file);
        }
    }
}
