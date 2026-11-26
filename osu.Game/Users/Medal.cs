// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;

namespace osu.Game.Users
{
    public class Medal
    {
        public string Name { get; set; }
        public string InternalName { get; set; }
        public string ImageUrl => (Environment.GetEnvironmentVariable("TORII_WEB_URL")?.TrimEnd('/') ?? "https://lazer.shikkesora.com") + $"/image/achievement_images/{InternalName}@2x.png";
        public string Description { get; set; }
    }
}
