// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Game.Beatmaps.Timing;
using osu.Game.Rulesets.Objects;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps.ControlPoints;
using Newtonsoft.Json;
using osu.Framework.Lists;
using osu.Game.Beatmaps.Formats;
using osu.Game.IO.Serialization.Converters;

namespace osu.Game.Beatmaps
{
    public class Beatmap<T> : IBeatmap<T>
        where T : HitObject
    {
        private BeatmapDifficulty difficulty = new BeatmapDifficulty();

        public BeatmapDifficulty Difficulty
        {
            get => difficulty;
            set
            {
                difficulty = value;

                beatmapInfo.Difficulty = difficulty.Clone();
            }
        }

        private BeatmapInfo beatmapInfo;

        public BeatmapInfo BeatmapInfo
        {
            get => beatmapInfo;
            set
            {
                beatmapInfo = value;

                Difficulty = beatmapInfo.Difficulty.Clone();
            }
        }

        public Beatmap()
        {
            beatmapInfo = new BeatmapInfo
            {
                Metadata = new BeatmapMetadata
                {
                    Artist = @"Unknown",
                    Title = @"Unknown",
                    Author = { Username = @"Unknown Creator" },
                },
                DifficultyName = @"Normal",
                Difficulty = Difficulty,
            };
        }

        [JsonIgnore]
        public BeatmapMetadata Metadata => BeatmapInfo.Metadata;

        public ControlPointInfo ControlPointInfo { get; set; } = new ControlPointInfo();

        public SortedList<BreakPeriod> Breaks { get; set; } = new SortedList<BreakPeriod>(Comparer<BreakPeriod>.Default);

        [JsonIgnore]
        public double TotalBreakTime => Breaks.Sum(b => b.Duration);

        [JsonConverter(typeof(TypedListConverter<HitObject>))]
        public List<T> HitObjects { get; set; } = new List<T>();

        IReadOnlyList<T> IBeatmap<T>.HitObjects => HitObjects;

        IReadOnlyList<HitObject> IBeatmap.HitObjects => HitObjects;

        public virtual IEnumerable<BeatmapStatistic> GetStatistics() => Enumerable.Empty<BeatmapStatistic>();

        public double GetMostCommonBeatLength()
        {
            var timingPoints = ControlPointInfo.TimingPoints;
            if (timingPoints.Count == 0)
                return TimingControlPoint.DEFAULT_BEAT_LENGTH;

            double lastTime;

            // The last playable time in the beatmap - the last timing point extends to this time.
            // Note: This is more accurate and may present different results because osu-stable didn't have the ability to calculate slider durations in this context.
            if (HitObjects.Count == 0)
                lastTime = timingPoints[^1].Time;
            else
                lastTime = this.GetLastObjectTime();

            var durations = new Dictionary<double, double>();
            var order = new List<double>();
            double minBeatLength = double.MaxValue;
            double maxBeatLength = double.MinValue;

            for (int i = 0; i < timingPoints.Count; i++)
            {
                var t = timingPoints[i];
                double rawBeatLength = t.BeatLength;

                if (rawBeatLength < minBeatLength) minBeatLength = rawBeatLength;
                if (rawBeatLength > maxBeatLength) maxBeatLength = rawBeatLength;

                double duration = 0;
                if (t.Time <= lastTime)
                {
                    // osu-stable forced the first control point to start at 0.
                    // This is reproduced here to maintain compatibility around osu!mania scroll speed and song select display.
                    double currentTime = i == 0 ? 0 : t.Time;
                    double nextTime = i == timingPoints.Count - 1 ? lastTime : timingPoints[i + 1].Time;
                    duration = nextTime - currentTime;
                }

                // Rounding is applied here (to 1e-3 milliseconds) to neutralise potential effects of floating point inaccuracies
                double roundedKey = Math.Round(rawBeatLength * 1000) / 1000;
                if (durations.TryGetValue(roundedKey, out double existing))
                    durations[roundedKey] = existing + duration;
                else
                {
                    durations[roundedKey] = duration;
                    order.Add(roundedKey);
                }
            }

            double bestBeatLength = 0;
            double bestDuration = double.NegativeInfinity;

            for (int i = 0; i < order.Count; i++)
            {
                double key = order[i];
                double duration = durations[key];
                if (duration > bestDuration)
                {
                    bestDuration = duration;
                    bestBeatLength = key;
                }
            }

            if (bestBeatLength == 0)
                return TimingControlPoint.DEFAULT_BEAT_LENGTH;

            // Because of the rounding applied to the beat length above, it is possible for the "most common" beat length as determined above
            // to actually be less or more than the raw range of unrounded beat lengths present in the map.
            // To ensure this does not become a problem anywhere else further, clamp the result to the known raw range.
            return Math.Clamp(bestBeatLength, minBeatLength, maxBeatLength);
        }

        public double AudioLeadIn { get; set; }

        public float StackLeniency { get; set; } = 0.7f;

        public bool SpecialStyle { get; set; }

        public bool LetterboxInBreaks { get; set; }

        public bool WidescreenStoryboard { get; set; } = true;

        public bool EpilepsyWarning { get; set; }

        public bool SamplesMatchPlaybackRate { get; set; }

        public double DistanceSpacing { get; set; } = 1.0;

        public int GridSize { get; set; }

        public double TimelineZoom { get; set; } = 1.0;

        public CountdownType Countdown { get; set; } = CountdownType.None;

        public int CountdownOffset { get; set; }

        public int[] Bookmarks { get; set; } = Array.Empty<int>();

        public double[] SliderVelocityPresets { get; set; } = [0.75, 1, 1.5];

        public int BeatmapVersion { get; set; } = LegacyBeatmapEncoder.FIRST_LAZER_VERSION;

        IBeatmap IBeatmap.Clone() => Clone();

        public Beatmap<T> Clone() => (Beatmap<T>)MemberwiseClone();

        public override string ToString() => BeatmapInfo.ToString();
    }

    public class Beatmap : Beatmap<HitObject>
    {
    }
}
