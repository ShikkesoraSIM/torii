// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Game.Online.API.Requests.Responses;

namespace osu.Game.Online.API.Requests
{
    public class GetMenuContentRequest : OsuJsonWebRequest<APIMenuContent>
    {
        public GetMenuContentRequest()
            : base((Environment.GetEnvironmentVariable("TORII_WEB_URL")?.TrimEnd('/') ?? "https://lazer.shikkesora.com") + "/menu-content.json")
        {
        }
    }
}
