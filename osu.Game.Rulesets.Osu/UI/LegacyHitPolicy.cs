// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Linq;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.Osu.UI
{
    /// <summary>
    /// Ensures that <see cref="HitObject"/>s are hit in order of appearance. The classic note lock.
    /// <remarks>
    /// Hits will be blocked until the previous <see cref="HitObject"/>s have been judged.
    /// </remarks>
    /// </summary>
    public class LegacyHitPolicy : IHitPolicy
    {
        public IHitObjectContainer? HitObjectContainer { get; set; }

        private readonly double hittableRange;

        public LegacyHitPolicy(double hittableRange = OsuHitWindows.MISS_WINDOW)
        {
            this.hittableRange = hittableRange;
        }

        public void HandleHit(DrawableHitObject hitObject)
        {
        }

        public virtual ClickAction CheckHittable(DrawableHitObject hitObject, double time, HitResult result)
        {
            if (HitObjectContainer == null)
                throw new InvalidOperationException($"{nameof(HitObjectContainer)} should be set before {nameof(CheckHittable)} is called.");

            DrawableHitObject? previous = null;
            bool found = false;
            bool hasBlockingEarlierObject = false;

            foreach (DrawableHitObject testObject in HitObjectContainer.AliveObjects)
            {
                if (testObject == hitObject)
                {
                    found = true;
                    break;
                }

                previous = testObject;

                // for all other objects, we check for validity and block the hit if any are still valid.
                // 3ms of extra leniency to account for slightly unsnapped objects.
                if (!hasBlockingEarlierObject && !testObject.AllJudged && testObject.HitObject.GetEndTime() + 3 < hitObject.HitObject.StartTime)
                    hasBlockingEarlierObject = true;
            }

            if (found && previous != null)
            {
                var previousHitObject = (DrawableOsuHitObject)previous;
                if (previousHitObject.HitObject.StackHeight > 0 && !previousHitObject.AllJudged)
                    return ClickAction.Ignore;
            }

            if (result == HitResult.None)
                return ClickAction.Shake;

            if (hasBlockingEarlierObject)
                return ClickAction.Shake;

            return Math.Abs(hitObject.HitObject.StartTime - time) < hittableRange ? ClickAction.Hit : ClickAction.Shake;
        }
    }
}
