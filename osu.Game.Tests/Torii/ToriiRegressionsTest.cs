// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using NUnit.Framework;
using osu.Game.Graphics;
using osu.Game.Online.Chat;

namespace osu.Game.Tests.Torii
{
    [TestFixture]
    public class ToriiRegressionsTest
    {
        [Test]
        public void TestOsuFontWeightFallbacksMatchShippedFonts()
        {
            // Inter only ships Light, Regular, SemiBold, Bold (no Medium or Black).
            Assert.That(OsuFont.GetWeightString("Inter", FontWeight.Light), Is.EqualTo("Light"));
            Assert.That(OsuFont.GetWeightString("Inter", FontWeight.Regular), Is.EqualTo("Regular"));
            Assert.That(OsuFont.GetWeightString("Inter", FontWeight.Medium), Is.EqualTo("Regular"));
            Assert.That(OsuFont.GetWeightString("Inter", FontWeight.SemiBold), Is.EqualTo("SemiBold"));
            Assert.That(OsuFont.GetWeightString("Inter", FontWeight.Bold), Is.EqualTo("Bold"));
            Assert.That(OsuFont.GetWeightString("Inter", FontWeight.Black), Is.EqualTo("Bold"));

            // Nunito ships Light, Regular, Medium, SemiBold, Bold (no Black).
            Assert.That(OsuFont.GetWeightString("Nunito", FontWeight.Medium), Is.EqualTo("Medium"));
            Assert.That(OsuFont.GetWeightString("Nunito", FontWeight.Black), Is.EqualTo("Bold"));

            // MapleMono registers Regular, Medium, Bold.
            Assert.That(OsuFont.GetWeightString("MapleMono", FontWeight.Light), Is.EqualTo("Regular"));
            Assert.That(OsuFont.GetWeightString("MapleMono", FontWeight.Medium), Is.EqualTo("Medium"));
            Assert.That(OsuFont.GetWeightString("MapleMono", FontWeight.SemiBold), Is.EqualTo("Bold"));
            Assert.That(OsuFont.GetWeightString("MapleMono", FontWeight.Black), Is.EqualTo("Bold"));
        }

        [TestCase("torii://b/12345", LinkAction.OpenBeatmap, "12345")]
        [TestCase("torii://s/67890", LinkAction.OpenBeatmapSet, "67890")]
        [TestCase("torii://room/42", LinkAction.JoinRoom, "42")]
        [TestCase("torii://chan/%23osu", LinkAction.OpenChannel, "#osu")]
        [TestCase("osu://b/12345", LinkAction.OpenBeatmap, "12345")]
        [TestCase("osu://s/67890", LinkAction.OpenBeatmapSet, "67890")]
        public void TestToriiAndOsuProtocolLinksResolveInGame(string url, LinkAction expectedAction, string expectedArg)
        {
            Assert.That(OsuGameBase.IsSupportedProtocolLink(url), Is.True);

            var details = MessageFormatter.GetLinkDetails(url);
            Assert.That(details.Action, Is.EqualTo(expectedAction));
            Assert.That(details.Argument, Is.EqualTo(expectedArg));
        }

        [TestCase("torii://b/12345")]
        [TestCase("toriimp://12345")]
        [TestCase("osu://b/12345")]
        [TestCase("osump://12345")]
        public void TestChatFormatterRecognisesToriiAndOsuLinks(string url)
        {
            var result = MessageFormatter.FormatText($"check {url} out");
            Assert.That(result.Links.Count, Is.EqualTo(1));
            Assert.That(result.Links[0].Url, Is.EqualTo(url));
        }
    }
}
