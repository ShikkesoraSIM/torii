// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Difficulty.Preprocessing;
using osu.Game.Rulesets.Difficulty.Skills;
using osu.Game.Rulesets.Difficulty.Utils;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu.Difficulty.Preprocessing;
using osu.Game.Rulesets.Osu.Difficulty.Skills;
using osu.Game.Rulesets.Osu.Difficulty.Utils;
using osu.Game.Rulesets.Osu.Mods;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;
using osu.Game.Utils;

namespace osu.Game.Rulesets.Osu.Difficulty
{
    public class OsuDifficultyCalculator : DifficultyCalculator
    {
        public override int Version => 20260621;

        public OsuDifficultyCalculator(IRulesetInfo ruleset, IWorkingBeatmap beatmap)
            : base(ruleset, beatmap)
        {
        }

        public static double CalculateRateAdjustedApproachRate(double approachRate, double clockRate)
        {
            double preempt = IBeatmapDifficultyInfo.DifficultyRange(approachRate, OsuHitObject.PREEMPT_MAX, OsuHitObject.PREEMPT_MID, OsuHitObject.PREEMPT_MIN) / clockRate;
            return IBeatmapDifficultyInfo.InverseDifficultyRange(preempt, OsuHitObject.PREEMPT_MAX, OsuHitObject.PREEMPT_MID, OsuHitObject.PREEMPT_MIN);
        }

        public static double CalculateRateAdjustedOverallDifficulty(double overallDifficulty, double clockRate)
        {
            HitWindows hitWindows = new OsuHitWindows();
            hitWindows.SetDifficulty(overallDifficulty);

            double hitWindowGreat = hitWindows.WindowFor(HitResult.Great) / clockRate;

            return (79.5 - hitWindowGreat) / 6;
        }

        protected override DifficultyAttributes CreateDifficultyAttributes(IBeatmap beatmap, Mod[] mods, Skill[] skills)
        {
            if (beatmap.HitObjects.Count == 0)
                return new OsuDifficultyAttributes { Mods = mods };

            Aim? aim = null;
            Aim? aimWithoutSliders = null;
            Speed? speed = null;
            Flashlight? flashlight = null;
            Reading? reading = null;

            for (int i = 0; i < skills.Length; i++)
            {
                switch (skills[i])
                {
                    case Aim a when a.IncludeSliders:
                        aim = a;
                        break;

                    case Aim a:
                        aimWithoutSliders = a;
                        break;

                    case Speed s:
                        speed = s;
                        break;

                    case Flashlight f:
                        flashlight = f;
                        break;

                    case Reading r:
                        reading = r;
                        break;
                }
            }

            if (aim == null || aimWithoutSliders == null || speed == null || reading == null)
                throw new InvalidOperationException("Required osu! difficulty skills were not present.");

            double aimDifficultyValue = aim.DifficultyValue();
            double aimNoSlidersDifficultyValue = aimWithoutSliders.DifficultyValue();
            double speedDifficultyValue = speed.DifficultyValue();
            double readingDifficultyValue = reading.DifficultyValue();

            double aimDifficultStrainCount = aim.CountTopWeightedStrains(aimDifficultyValue);
            double speedDifficultStrainCount = speed.CountTopWeightedObjectDifficulties(speedDifficultyValue);
            double readingDifficultNoteCount = reading.CountTopWeightedObjectDifficulties(readingDifficultyValue);

            double speedNotes = speed.RelevantNoteCount();

            double aimNoSlidersTopWeightedSliderCount = aimWithoutSliders.CountTopWeightedSliders(aimNoSlidersDifficultyValue);
            double aimNoSlidersDifficultStrainCount = aimWithoutSliders.CountTopWeightedStrains(aimNoSlidersDifficultyValue);

            double aimTopWeightedSliderFactor = aimNoSlidersTopWeightedSliderCount / Math.Max(1, aimNoSlidersDifficultStrainCount - aimNoSlidersTopWeightedSliderCount);

            double speedTopWeightedSliderCount = speed.CountTopWeightedSliders(speedDifficultyValue);
            double speedTopWeightedSliderFactor = speedTopWeightedSliderCount / Math.Max(1, speedDifficultStrainCount - speedTopWeightedSliderCount);

            double difficultSliders = aim.GetDifficultSliders();

            double overallDifficulty = CalculateRateAdjustedOverallDifficulty(beatmap.Difficulty.OverallDifficulty, ModUtils.CalculateRateWithMods(mods));

            int hitCircleCount = 0;
            int sliderCount = 0;
            int spinnerCount = 0;

            var hitObjects = beatmap.HitObjects;
            for (int i = 0; i < hitObjects.Count; i++)
            {
                switch (hitObjects[i])
                {
                    case HitCircle:
                        hitCircleCount++;
                        break;

                    case Slider:
                        sliderCount++;
                        break;

                    case Spinner:
                        spinnerCount++;
                        break;
                }
            }

            int totalHits = hitObjects.Count;

            double sliderFactor = aimDifficultyValue > 0
                ? OsuRatingCalculator.CalculateDifficultyRating(aimNoSlidersDifficultyValue) / OsuRatingCalculator.CalculateDifficultyRating(aimDifficultyValue)
                : 1;

            var osuRatingCalculator = new OsuRatingCalculator(totalHits, overallDifficulty);

            double aimRating = osuRatingCalculator.ComputeAimRating(aimDifficultyValue);
            double speedRating = osuRatingCalculator.ComputeSpeedRating(speedDifficultyValue);
            double readingRating = osuRatingCalculator.ComputeReadingRating(readingDifficultyValue);

            double flashlightRating = 0.0;

            if (flashlight is not null)
                flashlightRating = osuRatingCalculator.ComputeFlashlightRating(flashlight.DifficultyValue());

            double sliderNestedScorePerObject = LegacyScoreUtils.CalculateNestedScorePerObject(beatmap, totalHits);
            double legacyScoreBaseMultiplier = LegacyScoreUtils.CalculateDifficultyPeppyStars(WorkingBeatmap.Beatmap);

            var simulator = new OsuLegacyScoreSimulator();
            var scoreAttributes = simulator.Simulate(WorkingBeatmap, beatmap);

            double baseAimPerformance = OsuPerformanceCalculator.DifficultyToPerformance(aimRating);
            double baseSpeedPerformance = HarmonicSkill.DifficultyToPerformance(speedRating);
            double baseReadingPerformance = HarmonicSkill.DifficultyToPerformance(readingRating);
            double baseFlashlightPerformance = Flashlight.DifficultyToPerformance(flashlightRating);
            double baseCognitionPerformance = SumCognitionDifficulty(baseReadingPerformance, baseFlashlightPerformance);

            double basePerformance = DifficultyCalculationUtils.Norm(OsuPerformanceCalculator.PERFORMANCE_NORM_EXPONENT, baseAimPerformance, baseSpeedPerformance, baseCognitionPerformance);

            double starRating = calculateStarRating(basePerformance);

            OsuDifficultyAttributes attributes = new OsuDifficultyAttributes
            {
                StarRating = starRating,
                Mods = mods,
                AimDifficulty = aimRating,
                AimDifficultSliderCount = difficultSliders,
                SpeedDifficulty = speedRating,
                SpeedNoteCount = speedNotes,
                FlashlightDifficulty = flashlightRating,
                ReadingDifficulty = readingRating,
                SliderFactor = sliderFactor,
                AimDifficultStrainCount = aimDifficultStrainCount,
                SpeedDifficultStrainCount = speedDifficultStrainCount,
                ReadingDifficultNoteCount = readingDifficultNoteCount,
                AimTopWeightedSliderFactor = aimTopWeightedSliderFactor,
                SpeedTopWeightedSliderFactor = speedTopWeightedSliderFactor,
                MaxCombo = beatmap.GetMaxCombo(),
                HitCircleCount = hitCircleCount,
                SliderCount = sliderCount,
                SpinnerCount = spinnerCount,
                NestedScorePerObject = sliderNestedScorePerObject,
                LegacyScoreBaseMultiplier = legacyScoreBaseMultiplier,
                MaximumLegacyComboScore = scoreAttributes.ComboScore
            };

            return attributes;
        }

        public static double SumCognitionDifficulty(double reading, double flashlight)
        {
            if (reading <= 0)
                return flashlight;

            if (flashlight <= 0)
                return reading;

            // Nerf flashlight value in cognition sum when reading is greater than flashlight
            return DifficultyCalculationUtils.Norm(OsuPerformanceCalculator.PERFORMANCE_NORM_EXPONENT, reading, flashlight * Math.Clamp(flashlight / reading, 0.25, 1.0));
        }

        private double calculateStarRating(double basePerformance)
        {
            return Math.Cbrt(basePerformance * OsuPerformanceCalculator.PERFORMANCE_BASE_MULTIPLIER);
        }

        protected override IEnumerable<DifficultyHitObject> CreateDifficultyHitObjects(IBeatmap beatmap, Mod[] mods)
        {
            var hitObjects = beatmap.HitObjects;
            List<DifficultyHitObject> objects = new List<DifficultyHitObject>(Math.Max(0, hitObjects.Count - 1));

            double clockRate = ModUtils.CalculateRateWithMods(mods);

            // The first jump is formed by the first two hitobjects of the map.
            // If the map has less than two OsuHitObjects, the enumerator will not return anything.
            for (int i = 1; i < hitObjects.Count; i++)
            {
                objects.Add(new OsuDifficultyHitObject(hitObjects[i], hitObjects[i - 1], clockRate, objects, objects.Count));
            }

            return objects;
        }

        protected override Skill[] CreateSkills(IBeatmap beatmap, Mod[] mods)
        {
            bool hasFlashlight = false;
            for (int i = 0; i < mods.Length; i++)
            {
                if (mods[i] is OsuModFlashlight)
                {
                    hasFlashlight = true;
                    break;
                }
            }

            if (hasFlashlight)
            {
                return new Skill[]
                {
                    new Aim(mods, true),
                    new Aim(mods, false),
                    new Speed(mods),
                    new Reading(mods),
                    new Flashlight(mods)
                };
            }

            return new Skill[]
            {
                new Aim(mods, true),
                new Aim(mods, false),
                new Speed(mods),
                new Reading(mods)
            };
        }

        protected override Mod[] DifficultyAdjustmentMods => new Mod[]
        {
            new OsuModTouchDevice(),
            new OsuModDoubleTime(),
            new OsuModHalfTime(),
            new OsuModEasy(),
            new OsuModHardRock(),
            new OsuModFlashlight(),
            new OsuModHidden(),
        };
    }
}
