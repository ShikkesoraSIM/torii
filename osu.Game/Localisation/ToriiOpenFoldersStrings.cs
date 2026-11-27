using osu.Framework.Localisation;

namespace osu.Game.Localisation
{
    public static class ToriiOpenFoldersStrings
    {
        public static LocalisableString SectionHeader => "Open Folders";

        public static LocalisableString SectionDescription => "Store newly imported skins and beatmaps in regular Skins/ and Songs/ folders instead of hashed files. You can edit skin images, audio, and skin.ini directly in your file manager and see changes live without restarting. Turning these options off never deletes existing files.";

        public static LocalisableString StoreNewSkinsCaption => "Store new skins in Skins/ folder";

        public static LocalisableString StoreNewSkinsHint => "Newly imported skins are extracted into readable folders under Skins/. Editing files in your active skin folder reloads automatically.";

        public static LocalisableString StoreNewSongsCaption => "Store new beatmaps in Songs/ folder";

        public static LocalisableString StoreNewSongsHint => "Newly imported beatmaps are extracted into readable folders under Songs/. Dropping maps or folders into Songs/ imports them live without pressing F5.";

        public static LocalisableString OpenSkinsFolder => "Open Skins folder";

        public static LocalisableString OpenSongsFolder => "Open Songs folder";

        public static LocalisableString OpenCurrentSkinFolder => "Open skin folder";

        public static LocalisableString ReloadCurrentSkin => "Reload skin";

        public static LocalisableString ReloadCurrentSkinTooltip => "Reloads the active skin from disk (Ctrl+Shift+Alt+S).";

        public static LocalisableString UnpackSkinsButton => "Unpack existing skins to Skins/ folder";

        public static LocalisableString UnpackSongsButton => "Unpack existing beatmaps to Songs/ folder";

        public static LocalisableString UnpackSkinsDialogHeader => "Unpack existing skins?";

        public static LocalisableString UnpackSongsDialogHeader => "Unpack existing beatmaps?";

        public static LocalisableString UnpackSkinsBody(int count, string sizeText)
            => $"{count} skins ({sizeText}) will be copied into the Skins/ folder. This is usually fast and takes only a few seconds. Existing database files stay safe as a fallback, so you can cancel at any time.";

        public static LocalisableString UnpackSongsBody(int count, string sizeText)
            => $"{count} beatmaps ({sizeText}) will be copied into the Songs/ folder. This may take several minutes for large libraries and uses additional disk space. It is not recommended on slow drives. Existing database files stay safe as a fallback, and you can cancel at any time.";

        public static LocalisableString UnpackConfirm => "Yes, unpack now";

        public static LocalisableString UnpackCancel => "Cancel";

        public static LocalisableString AlreadyUnpackedSkins => "All your skins are already in the Skins/ folder.";

        public static LocalisableString AlreadyUnpackedSongs => "All your beatmaps are already in the Songs/ folder.";

        public static LocalisableString BuiltInSkinNoFolder => "Built-in skins do not have a folder on disk. Select a custom skin first.";

        public static LocalisableString SkinReloaded(string name) => $"Reloaded skin \"{name}\".";

        public static LocalisableString DesktopOnlyNotice => "Open Folders is available on desktop platforms only.";

        public static LocalisableString LocallyModifiedBeatmapNotice => "This beatmap was modified in the Songs/ folder, so this play will be saved locally only.";

        public static LocalisableString FirstRunStartFreshRecommendedButton => "Start fresh (Recommended)";

        public static LocalisableString FirstRunStartFreshNotice => "Recommended: Start fresh to give Torii its own clean data folder without interfering with another installation. You can still import beatmaps, skins, and scores during setup or later in Settings.";

        public static LocalisableString FirstRunOpenFoldersHeader => "Recommended: Open Folders";

        public static LocalisableString FirstRunOpenFoldersNotice => "Normally, skins and beatmaps are stored inside hashed internal storage. We recommend enabling Open Folders below so newly imported skins and beatmaps are saved in regular Skins/ and Songs/ folders instead. This lets you browse files in your file manager, edit skin images, audio, and skin.ini with live reload, and drop new beatmaps directly into Songs/ without restarting.";
    }
}
