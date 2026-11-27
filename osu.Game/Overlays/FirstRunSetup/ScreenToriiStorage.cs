// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Localisation;
using osu.Game.Overlays.Settings;
using osu.Game.Torii.OpenFolders;
using osuTK;
using FontAwesome = osu.Framework.Graphics.Sprites.FontAwesome;

namespace osu.Game.Overlays.FirstRunSetup
{
    [LocalisableDescription(typeof(ToriiSettingsStrings), nameof(ToriiSettingsStrings.FirstRunHeader))]
    public partial class ScreenToriiStorage : WizardScreen
    {
        // torii: las opciones de esta pantalla son sus propios botones. El del pie seria una salida
        // mas, la mas grande de todas y ademas atada a Enter, y encima terminaba el first-run entero
        // salteandose los pasos clasicos, porque en ese momento es el unico paso de la lista.
        public override bool HideNextButton => true;

        [Resolved]
        private Storage storage { get; set; } = null!;

        [Resolved]
        private OsuGameBase game { get; set; } = null!;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        [Resolved(canBeNull: true)]
        private IDialogOverlay? dialogOverlay { get; set; }

        [Resolved(canBeNull: true)]
        private FirstRunSetupOverlay? firstRunOverlay { get; set; }

        [Resolved(canBeNull: true)]
        private OpenFoldersStore? openFolders { get; set; }

        private Bindable<bool>? openFoldersSkins;
        private Bindable<bool>? openFoldersSongs;

        [BackgroundDependencyLoader]
        private void load()
        {
            var intro = new List<Drawable>
            {
                new CircularContainer
                {
                    Size = new Vector2(72),
                    Masking = true,
                    CornerRadius = 18,
                    BorderThickness = 3,
                    BorderColour = OverlayColourProvider.Colour2,
                    Child = new SpriteIcon
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Icon = FontAwesome.Solid.FolderOpen,
                        Size = new Vector2(30),
                        Colour = OverlayColourProvider.Light1,
                    }
                },
                new OsuTextFlowContainer(cp => cp.Font = OsuFont.Default.With(size: CONTENT_FONT_SIZE))
                {
                    Text = ToriiSettingsStrings.NoDetectedLazerFolder,
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                },
            };

            var content = new List<Drawable>
            {
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(12),
                    ChildrenEnumerable = intro,
                },
                createNotice(ToriiOpenFoldersStrings.FirstRunStartFreshNotice),
                new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(10),
                    Children = new Drawable[]
                    {
                        new RoundedButton
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 55,
                            Text = ToriiOpenFoldersStrings.FirstRunStartFreshRecommendedButton,
                            Action = stayFresh,
                        },
                        new RoundedButton
                        {
                            RelativeSizeAxes = Axes.X,
                            Height = 50,
                            BackgroundColour = OverlayColourProvider.Background3,
                            Text = ToriiSettingsStrings.ChooseLazerFolderManually,
                            Action = showFolderPicker,
                        },
                    }
                },
            };

            if (OpenFoldersStore.IsSupportedOnCurrentPlatform)
            {
                openFoldersSkins = config.GetBindable<bool>(OsuSetting.ToriiOpenFoldersSkins);
                openFoldersSongs = config.GetBindable<bool>(OsuSetting.ToriiOpenFoldersSongs);

                openFoldersSkins.BindValueChanged(e =>
                {
                    if (e.NewValue)
                        openFolders?.EnsureRootDirectories();
                });

                openFoldersSongs.BindValueChanged(e =>
                {
                    if (e.NewValue)
                        openFolders?.EnsureRootDirectories();
                });

                content.Add(new FillFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                    Direction = FillDirection.Vertical,
                    Spacing = new Vector2(10),
                    Margin = new MarginPadding { Top = 6 },
                    Children = new Drawable[]
                    {
                        new OsuSpriteText
                        {
                            Text = ToriiOpenFoldersStrings.FirstRunOpenFoldersHeader,
                            Font = OsuFont.TorusAlternate.With(size: 18),
                            Colour = OverlayColourProvider.Light1,
                        },
                        createNotice(ToriiOpenFoldersStrings.FirstRunOpenFoldersNotice),
                        new FormCheckBox
                        {
                            RelativeSizeAxes = Axes.X,
                            Caption = ToriiOpenFoldersStrings.StoreNewSkinsCaption,
                            HintText = ToriiOpenFoldersStrings.StoreNewSkinsHint,
                            Current = openFoldersSkins,
                        },
                        new FormCheckBox
                        {
                            RelativeSizeAxes = Axes.X,
                            Caption = ToriiOpenFoldersStrings.StoreNewSongsCaption,
                            HintText = ToriiOpenFoldersStrings.StoreNewSongsHint,
                            Current = openFoldersSongs,
                        },
                    }
                });
            }

            content.Add(new Container
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Margin = new MarginPadding { Bottom = 20 },
                Child = createNotice(ToriiSettingsStrings.ChangeLaterInSettings),
            });

            Content.AddRange(content);
        }

        private static SettingsNote createNotice(LocalisableString text, SettingsNote.Type type = SettingsNote.Type.Informational)
        {
            return new SettingsNote
            {
                RelativeSizeAxes = Axes.X,
                Margin = new MarginPadding { Top = -SettingsSection.ITEM_SPACING_V2 },
                Current = { Value = new SettingsNote.Data(text, type) },
            };
        }

        private void showFolderPicker()
        {
            firstRunOverlay?.AppendFolderPickerStep();
            advanceToNextStep();
        }

        private void stayFresh()
        {
            firstRunOverlay?.AppendClassicSteps();
            advanceToNextStep();
        }

        // torii: el wizard sólo avanza por el botón del footer, y los pasos se leen recién al avanzar,
        // así que lo dejamos scheduleado para que el push del próximo screen no pase adentro del click.
        // torii: el guard no es paranoia. El boton de antes se auto-deshabilitaba al primer click, y al
        // cambiarlo por uno comun eso se perdio: la pantalla saliente sigue recibiendo input mientras se
        // desvanece, asi que dos clicks seguidos avanzaban DOS pasos y se salteaban una pantalla.
        private bool avanzando;

        private void advanceToNextStep()
        {
            if (avanzando)
                return;

            avanzando = true;
            Schedule(() => firstRunOverlay?.AdvanceStep());
        }
    }
}
