// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Localisation;
using osu.Game.Localisation;

namespace osu.Game.Overlays.Dialog
{
    /// <summary>
    /// A confirmation dialog shown before extracting existing skins or beatmaps into their open folders.
    /// </summary>
    public partial class OpenFoldersUnpackDialog : PopupDialog
    {
        public OpenFoldersUnpackDialog(LocalisableString header, LocalisableString body, Action onConfirm)
        {
            Icon = FontAwesome.Solid.FolderOpen;
            HeaderText = header;
            BodyText = body;

            Buttons = new PopupDialogButton[]
            {
                new PopupDialogOkButton
                {
                    Text = ToriiOpenFoldersStrings.UnpackConfirm,
                    Action = onConfirm,
                },
                new PopupDialogCancelButton
                {
                    Text = ToriiOpenFoldersStrings.UnpackCancel,
                },
            };
        }
    }
}
