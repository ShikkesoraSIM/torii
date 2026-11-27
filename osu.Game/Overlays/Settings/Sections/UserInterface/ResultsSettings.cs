// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Localisation;
using osu.Game.Overlays.Settings.Entries;

namespace osu.Game.Overlays.Settings.Sections.UserInterface
{
    public partial class ResultsSettings : SettingsSubsection
    {
        protected override LocalisableString Header => UserInterfaceStrings.ResultsHeader;

        private Bindable<bool> legacyResults = null!;
        private Bindable<bool> v2Results = null!;
        private Bindable<UIThemeOption> theme = null!;
        private NoteEntry classicNote = null!;
        private readonly Bindable<ResultsScreenStyle> style = new Bindable<ResultsScreenStyle>();
        private bool syncingStyle;

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            theme = config.GetBindable<UIThemeOption>(OsuSetting.UITheme);
            legacyResults = config.GetBindable<bool>(OsuSetting.ToriiStableUIResults);
            v2Results = config.GetBindable<bool>(OsuSetting.UseV2ResultsScreen);

            Children = new Drawable[]
            {
                CreateItem(new EnumDropdownEntry<ResultsScreenStyle>
                {
                    Caption = UserInterfaceStrings.ResultsScreenStyle,
                    HintText = "Choose a results layout. Changes to the Torii Classic UI layout take effect after restarting the game.",
                    Current = style,
                    Keywords = new[] { @"results", @"ranking", @"score", @"modern", @"legacy", @"classic", @"layout", @"skin" },
                }),
                CreateItem(classicNote = new NoteEntry
                {
                    AttachedToPrevious = true,
                }),
            };

            legacyResults.BindValueChanged(_ => syncStyle(), true);
            theme.BindValueChanged(_ => syncStyle());
            v2Results.BindValueChanged(_ => syncStyle());
            style.BindValueChanged(e =>
            {
                if (syncingStyle)
                    return;

                syncingStyle = true;
                legacyResults.Value = e.NewValue == ResultsScreenStyle.Legacy;
                v2Results.Value = e.NewValue == ResultsScreenStyle.V2;
                syncingStyle = false;
                updateClassicNote();
            });
        }

        private void syncStyle()
        {
            if (syncingStyle)
                return;

            syncingStyle = true;
            // Only show Legacy as active when Torii Classic theme is selected and Classic results are enabled.
            bool classicActive = theme.Value == UIThemeOption.StableUI && legacyResults.Value;
            style.Value = classicActive ? ResultsScreenStyle.Legacy : v2Results.Value ? ResultsScreenStyle.V2 : ResultsScreenStyle.Default;
            syncingStyle = false;
            updateClassicNote();
        }

        private void updateClassicNote()
        {
            classicNote.Note.Value = style.Value == ResultsScreenStyle.Legacy && theme.Value != UIThemeOption.StableUI
                ? new SettingsNote.Data("Classic results require the Torii Classic theme. Enable Torii Classic under Torii > Torii Classic and restart.", SettingsNote.Type.Informational)
                : null;
        }
    }
}
