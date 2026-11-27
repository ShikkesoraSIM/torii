// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Game.Overlays.Settings.Sections.Torii;

namespace osu.Game.Overlays.Settings.Sections.Maintenance
{
    /// <summary>
    /// Torii: mirrors <see cref="ToriiOpenFoldersSettings"/> near the bottom of Settings -> Maintenance,
    /// sharing the exact same config bindables, dialogs, and copy so both locations always stay in sync.
    /// </summary>
    public partial class OpenFoldersMaintenanceSettings : ToriiOpenFoldersSettings
    {
    }
}
