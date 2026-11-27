// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Localisation;
using osu.Game.Overlays.Dialog;
using osu.Game.Overlays.Notifications;
using osu.Game.Overlays.Settings.Entries;
using osu.Game.Skinning;
using osu.Game.Torii.OpenFolders;

namespace osu.Game.Overlays.Settings.Sections.Torii
{
    /// <summary>
    /// Torii: Open Folders settings subsection (readable Skins/ and Songs/ folders, live reload, and optional unpack).
    /// Placed near the top of Settings -> Torii and mirrored at the bottom of Settings -> Maintenance.
    /// </summary>
    public partial class ToriiOpenFoldersSettings : SettingsSubsection
    {
        protected override LocalisableString Header => ToriiOpenFoldersStrings.SectionHeader;

        private OpenFoldersStore? openFolders;
        private OpenFoldersMigrator? migrator;
        private SkinManager? skinManager;
        private IDialogOverlay? dialogOverlay;
        private INotificationOverlay? notificationOverlay;

        private Bindable<bool>? skinsEnabled;
        private Bindable<bool>? songsEnabled;

        private ButtonEntry? unpackSkinsButton;
        private ButtonEntry? unpackSongsButton;

        [BackgroundDependencyLoader(permitNulls: true)]
        private void load(OsuConfigManager? config, OpenFoldersStore? openFolders, OpenFoldersMigrator? migrator, SkinManager? skinManager,
                          IDialogOverlay? dialogOverlay, INotificationOverlay? notificationOverlay)
        {
            this.openFolders = openFolders;
            this.migrator = migrator;
            this.skinManager = skinManager;
            this.dialogOverlay = dialogOverlay;
            this.notificationOverlay = notificationOverlay;

            if (!OpenFoldersStore.IsSupportedOnCurrentPlatform)
            {
                Add(createDescription(ToriiOpenFoldersStrings.DesktopOnlyNotice));
                return;
            }

            Add(createDescription(ToriiOpenFoldersStrings.SectionDescription));

            if (config != null)
            {
                skinsEnabled = config.GetBindable<bool>(OsuSetting.ToriiOpenFoldersSkins);
                songsEnabled = config.GetBindable<bool>(OsuSetting.ToriiOpenFoldersSongs);

                skinsEnabled.BindValueChanged(e =>
                {
                    if (e.NewValue)
                        openFolders?.EnsureRootDirectories();
                });

                songsEnabled.BindValueChanged(e =>
                {
                    if (e.NewValue)
                        openFolders?.EnsureRootDirectories();
                });

                Add(CreateItem(new CheckboxEntry
                {
                    Caption = ToriiOpenFoldersStrings.StoreNewSkinsCaption,
                    HintText = ToriiOpenFoldersStrings.StoreNewSkinsHint,
                    Current = skinsEnabled,
                    Keywords = new[] { "skin", "skins", "folder", "open", "storage", "editable", "torii" },
                }));

                Add(CreateItem(new CheckboxEntry
                {
                    Caption = ToriiOpenFoldersStrings.StoreNewSongsCaption,
                    HintText = ToriiOpenFoldersStrings.StoreNewSongsHint,
                    Current = songsEnabled,
                    Keywords = new[] { "song", "songs", "beatmap", "beatmaps", "folder", "open", "storage", "torii" },
                }));
            }

            AddRange(new Drawable[]
            {
                CreateItem(new ButtonEntry
                {
                    Caption = ToriiOpenFoldersStrings.OpenSkinsFolder,
                    Action = openSkinsFolder,
                    Keywords = new[] { "skin", "skins", "folder", "explorer", "open", "torii" },
                }),
                CreateItem(new ButtonEntry
                {
                    Caption = ToriiOpenFoldersStrings.OpenSongsFolder,
                    Action = openSongsFolder,
                    Keywords = new[] { "song", "songs", "beatmap", "folder", "explorer", "open", "torii" },
                }),
                CreateItem(new ButtonEntry
                {
                    Caption = ToriiOpenFoldersStrings.ReloadCurrentSkin,
                    HintText = ToriiOpenFoldersStrings.ReloadCurrentSkinTooltip,
                    Action = () => skinManager?.ReloadCurrentSkin(audioOnly: false, notify: true),
                    Keywords = new[] { "skin", "reload", "refresh", "hot", "torii" },
                }),
                CreateItem(unpackSkinsButton = new ButtonEntry
                {
                    Caption = ToriiOpenFoldersStrings.UnpackSkinsButton,
                    Action = requestUnpackSkins,
                    Keywords = new[] { "unpack", "unhash", "extract", "skins", "folder", "torii" },
                }),
                CreateItem(unpackSongsButton = new ButtonEntry
                {
                    Caption = ToriiOpenFoldersStrings.UnpackSongsButton,
                    Action = requestUnpackBeatmaps,
                    Keywords = new[] { "unpack", "unhash", "extract", "songs", "beatmaps", "folder", "torii" },
                }),
            });
        }

        private static OsuTextFlowContainer createDescription(LocalisableString text)
        {
            var description = new OsuTextFlowContainer(t => t.Font = OsuFont.Style.Caption1)
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Padding = SettingsPanel.CONTENT_PADDING,
            };

            description.AddText(text);
            return description;
        }

        private void openSkinsFolder()
        {
            if (openFolders == null)
                return;

            openFolders.EnsureRootDirectories();
            openFolders.OpenFolderInExplorer(openFolders.SkinsRootPath);
        }

        private void openSongsFolder()
        {
            if (openFolders == null)
                return;

            openFolders.EnsureRootDirectories();
            openFolders.OpenFolderInExplorer(openFolders.SongsRootPath);
        }

        private void requestUnpackSkins()
        {
            if (migrator == null || unpackSkinsButton == null)
                return;

            unpackSkinsButton.Enabled.Value = false;

            Task.Run(async () =>
            {
                try
                {
                    var (count, bytes) = await migrator.EstimatePendingSkinsAsync().ConfigureAwait(false);

                    Schedule(() =>
                    {
                        unpackSkinsButton.Enabled.Value = true;

                        if (count <= 0)
                        {
                            postSimple(ToriiOpenFoldersStrings.AlreadyUnpackedSkins);
                            return;
                        }

                        var dialog = new OpenFoldersUnpackDialog(
                            ToriiOpenFoldersStrings.UnpackSkinsDialogHeader,
                            ToriiOpenFoldersStrings.UnpackSkinsBody(count, formatSize(bytes)),
                            () =>
                            {
                                unpackSkinsButton.Enabled.Value = false;
                                Task.Run(() => migrator.UnpackExistingSkinsAsync(n => notificationOverlay?.Post(n)))
                                    .ContinueWith(_ => Schedule(() => unpackSkinsButton.Enabled.Value = true));
                            });

                        if (dialogOverlay != null)
                            dialogOverlay.Push(dialog);
                        else
                        {
                            unpackSkinsButton.Enabled.Value = false;
                            Task.Run(() => migrator.UnpackExistingSkinsAsync(n => notificationOverlay?.Post(n)))
                                .ContinueWith(_ => Schedule(() => unpackSkinsButton.Enabled.Value = true));
                        }
                    });
                }
                catch
                {
                    Schedule(() => unpackSkinsButton.Enabled.Value = true);
                }
            });
        }

        private void requestUnpackBeatmaps()
        {
            if (migrator == null || unpackSongsButton == null)
                return;

            unpackSongsButton.Enabled.Value = false;

            Task.Run(async () =>
            {
                try
                {
                    var (count, bytes) = await migrator.EstimatePendingBeatmapsAsync().ConfigureAwait(false);

                    Schedule(() =>
                    {
                        unpackSongsButton.Enabled.Value = true;

                        if (count <= 0)
                        {
                            postSimple(ToriiOpenFoldersStrings.AlreadyUnpackedSongs);
                            return;
                        }

                        var dialog = new OpenFoldersUnpackDialog(
                            ToriiOpenFoldersStrings.UnpackSongsDialogHeader,
                            ToriiOpenFoldersStrings.UnpackSongsBody(count, formatSize(bytes)),
                            () =>
                            {
                                unpackSongsButton.Enabled.Value = false;
                                Task.Run(() => migrator.UnpackExistingBeatmapsAsync(n => notificationOverlay?.Post(n)))
                                    .ContinueWith(_ => Schedule(() => unpackSongsButton.Enabled.Value = true));
                            });

                        if (dialogOverlay != null)
                            dialogOverlay.Push(dialog);
                        else
                        {
                            unpackSongsButton.Enabled.Value = false;
                            Task.Run(() => migrator.UnpackExistingBeatmapsAsync(n => notificationOverlay?.Post(n)))
                                .ContinueWith(_ => Schedule(() => unpackSongsButton.Enabled.Value = true));
                        }
                    });
                }
                catch
                {
                    Schedule(() => unpackSongsButton.Enabled.Value = true);
                }
            });
        }

        private void postSimple(LocalisableString text)
        {
            notificationOverlay?.Post(new SimpleNotification
            {
                Text = text,
                Icon = FontAwesome.Solid.FolderOpen,
            });
        }

        private static string formatSize(long bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024L * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024.0):F1} MB";
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        }
    }
}
