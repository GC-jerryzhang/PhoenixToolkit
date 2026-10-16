using System.Globalization;
using System.Xml.Linq;
using PhoenixToolkit.Models;
using PhoenixToolkit.Services;

namespace PhoenixToolkit.Tests;

internal static partial class Program
{
    private static void FetchNotificationsIdentifyChannelsAndUseCompactPackageSummaries()
    {
        foreach (var channel in new[] { PackageChannel.Develop, PackageChannel.Release })
        {
            var plan = NotificationFixture(channel);
            var xml = NotificationXml(plan);
            var text = xml.Descendants("text").Select(element => element.Value).ToArray();
            Assert.Equal(channel == PackageChannel.Release ? "RELEASE · 发布版 · v1.0.0" : "DEVELOP · 开发版",
                text[0], "Channel must lead the title, including the release source version.");
            Assert.Equal(3, text.Length, "A dual-package notification needs one title and two summaries.");
            Assert.Equal("设计器：v1.0.0 · 文件号 12", text[1], "Designer summary must retain version and build.");
            Assert.Equal("服务端：文件号 12 · 10-15 10:33", text[2], "Server summary must retain build and timestamp.");
            Assert.False(text.Any(value => value.Contains(".exe", StringComparison.OrdinalIgnoreCase) || value.Contains("织启")),
                "Notification text must not expose long package filenames or package branding.");
            Assert.Null(xml.Root!.Attribute("duration"), "Default notification duration must be preserved.");
            Assert.Null(xml.Root.Attribute("scenario"), "Do not introduce alarm or reminder behavior.");
            Assert.False(xml.Descendants("audio").Any(), "Do not introduce custom audio.");
        }
    }

    private static void FetchNotificationBodyAndButtonTargetTheSameChannelPlan()
    {
        foreach (var channel in new[] { PackageChannel.Develop, PackageChannel.Release })
        {
            var plan = NotificationFixture(channel);
            var xml = NotificationXml(plan);
            var expectedUri = ProtocolActivationService.BuildInstallUri(plan.Id).AbsoluteUri;
            var button = xml.Descendants("action").Single();
            Assert.Equal("protocol", xml.Root!.Attribute("activationType")!.Value, "Body must use existing protocol activation.");
            Assert.Equal(expectedUri, xml.Root.Attribute("launch")!.Value, "Body must retain the exact plan identifier.");
            Assert.Equal("protocol", button.Attribute("activationType")!.Value, "Button must use protocol activation too.");
            Assert.Equal(expectedUri, button.Attribute("arguments")!.Value, "Button and body must target the same plan.");
            Assert.Equal(channel == PackageChannel.Release ? "安装 RELEASE" : "安装 DEVELOP",
                button.Attribute("content")!.Value, "Button must explicitly identify the installation channel.");
        }
    }

    private static void FetchNotificationsOnlyShowPackagesPresentInThePlan()
    {
        foreach (var channel in new[] { PackageChannel.Develop, PackageChannel.Release })
        {
            var plan = NotificationFixture(channel);
            foreach (var single in new[] { plan with { ServerPackage = null }, plan with { DesignerPackage = null } })
            {
                var text = NotificationXml(single).Descendants("text").Select(element => element.Value).ToArray();
                Assert.Equal(2, text.Length, "Single-package notifications must not imply a second package is available.");
                Assert.True(text[1].StartsWith(single.DesignerPackage is null ? "服务端：" : "设计器：", StringComparison.Ordinal),
                    "Summary must describe the package actually fetched.");
                Assert.True(text[0].StartsWith(channel == PackageChannel.Release ? "RELEASE" : "DEVELOP", StringComparison.Ordinal),
                    "Server-only plans must still identify the correct channel.");
            }
        }
        Assert.Throws<ArgumentException>(() => NotificationXml(new InstallPlan("empty", null, null)),
            "An empty plan cannot produce an install action.");
    }

    private static void PackageSummariesSupportLegacyNamesAndOmitUnavailableMetadata()
    {
        var cases = new[]
        {
            (PackageKind.Designer, "25Phoenix-Windows-1.0.0-Setup.exe", "设计器：v1.0.0 · 文件号 25"),
            (PackageKind.Designer, "25织启-Windows-1.0.0-Setup.exe", "设计器：v1.0.0 · 文件号 25"),
            (PackageKind.Designer, "25Phoenix-Setup.exe", "设计器：文件号 25"),
            (PackageKind.Designer, "Phoenix-Windows-1.2.3-Setup.exe", "设计器：v1.2.3"),
            (PackageKind.Designer, "0Phoenix-Windows-1.2.3-Setup.exe", "设计器：v1.2.3"),
            (PackageKind.Designer, "unknown-v99-build123.exe", "设计器：已就绪"),
            (PackageKind.Server, "202610151033Phoenix-Server-Windows-build12-Setup.exe", "服务端：文件号 12 · 10-15 10:33"),
            (PackageKind.Server, "202610151033织启-Server-Windows-Setup.exe", "服务端：10-15 10:33"),
            (PackageKind.Server, "202610151033Phoenix-Server-Windows-build0-Setup.exe", "服务端：10-15 10:33"),
            (PackageKind.Server, "202602301033Phoenix-Server-Windows-build12-Setup.exe", "服务端：文件号 12"),
            (PackageKind.Server, "202602301033Phoenix-Server-Windows-Setup.exe", "服务端：已就绪"),
            (PackageKind.Server, "unknown-v99-build123.exe", "服务端：已就绪")
        };
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            foreach (var (kind, fileName, expected) in cases)
            {
                var package = new FetchedPackageInfo(kind, fileName, "unused", DateTimeOffset.Now);
                Assert.Equal(expected, NotificationService.BuildPackageSummary(package),
                    $"Summary must use only recognized filename metadata: {fileName}");
            }
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    private static void FetchNotificationsKeepChannelVisibleWithLongOrMissingReleaseVersions()
    {
        var plan = NotificationFixture(PackageChannel.Release);
        const string version = "v12345.23456.34567.45678";
        var longPlan = plan with
        {
            DesignerPackage = plan.DesignerPackage! with { ReleaseVersion = version },
            ServerPackage = plan.ServerPackage! with { ReleaseVersion = version }
        };
        var xml = NotificationXml(longPlan);
        var text = xml.Descendants("text").ToArray();
        Assert.Equal($"RELEASE · 发布版 · {version}", text[0].Value, "Keep channel first and preserve the full source version.");
        Assert.Equal("2", text[0].Attribute("hint-maxLines")!.Value, "Title needs room for a long release version.");
        Assert.True(text.Skip(1).All(element => element.Attribute("hint-maxLines")?.Value == "1"),
            "Package summaries must remain compact so channel and action remain prominent.");
        Assert.Equal("RELEASE · 发布版", NotificationService.BuildFetchTitle(
            plan with { DesignerPackage = plan.DesignerPackage! with { ReleaseVersion = null }, ServerPackage = null }),
            "Do not invent a release version when metadata is unavailable.");
    }

    private static void NotificationIconsAreEmbeddedDistinctAndRepairTheirCache()
    {
        RunIsolated(config =>
        {
            var cache = Path.Combine(config.LocalBaseDir, "notification-icons");
            var release = NotificationService.TryGetChannelIcon(PackageChannel.Release, cache);
            var develop = NotificationService.TryGetChannelIcon(PackageChannel.Develop, cache);
            Assert.NotNull(release, "Release icon must be available from embedded resources.");
            Assert.NotNull(develop, "Develop icon must be available from embedded resources.");
            Assert.True(release!.IsFile && develop!.IsFile, "Toast images require persistent local file URIs.");
            Assert.False(release.Equals(develop), "Channel icons must have distinct cache paths.");
            using (var releaseImage = new Bitmap(release.LocalPath))
            using (var developImage = new Bitmap(develop!.LocalPath))
            {
                Assert.Equal(new Size(144, 144), releaseImage.Size, "Embedded icon must support high DPI display.");
                Assert.Equal(releaseImage.Size, developImage.Size, "Channel icons must have matching dimensions.");
                Assert.Equal(Color.FromArgb(21, 128, 61).ToArgb(), releaseImage.GetPixel(20, 72).ToArgb(), "R icon must use green.");
                Assert.Equal(Color.FromArgb(29, 78, 216).ToArgb(), developImage.GetPixel(20, 72).ToArgb(), "D icon must use blue.");
            }
            var original = File.ReadAllBytes(release.LocalPath);
            File.WriteAllText(release.LocalPath, "incomplete icon");
            Assert.NotNull(NotificationService.TryGetChannelIcon(PackageChannel.Release, cache), "Damaged cached icons must be replaced.");
            Assert.True(original.SequenceEqual(File.ReadAllBytes(release.LocalPath)), "Repair must restore the embedded PNG bytes.");
            File.Delete(develop!.LocalPath);
            Assert.NotNull(NotificationService.TryGetChannelIcon(PackageChannel.Develop, cache), "Deleted icons must be restored.");
            Assert.False(Directory.EnumerateFiles(cache, "*.tmp").Any(), "Icon extraction must clean temporary files.");

            var xml = NotificationXml(NotificationFixture(PackageChannel.Release), channel => NotificationService.TryGetChannelIcon(channel, cache));
            var image = xml.Descendants("image").Single();
            Assert.Equal("appLogoOverride", image.Attribute("placement")!.Value, "Badge must occupy the native app logo position.");
            Assert.Equal(release.AbsoluteUri, image.Attribute("src")!.Value, "Toast must use the cached image URI.");
            Assert.Equal("R 发布版", image.Attribute("alt")!.Value, "Icon needs an accessible channel description.");
        });
    }

    private static void FetchNotificationsRemainActionableWhenIconCacheIsUnavailable()
    {
        RunIsolated(config =>
        {
            var blocker = Path.Combine(config.LocalBaseDir, "not-a-directory");
            File.WriteAllText(blocker, "blocked");
            var cache = Path.Combine(blocker, "icons");
            foreach (var channel in new[] { PackageChannel.Develop, PackageChannel.Release })
            {
                Assert.Null(NotificationService.TryGetChannelIcon(channel, cache), "Icon write failure must be nonfatal.");
                var plan = NotificationFixture(channel);
                var xml = NotificationXml(plan, value => NotificationService.TryGetChannelIcon(value, cache));
                Assert.False(xml.Descendants("image").Any(), "Unavailable icons must be omitted.");
                Assert.Equal(NotificationService.BuildFetchTitle(plan), xml.Descendants("text").First().Value,
                    "Channel identification must survive without an icon.");
                Assert.Equal(ProtocolActivationService.BuildInstallUri(plan.Id).AbsoluteUri,
                    xml.Descendants("action").Single().Attribute("arguments")!.Value,
                    "Icon failures must not remove or alter the install action.");
            }
        });
    }

    private static InstallPlan NotificationFixture(PackageChannel channel) => new(
        channel == PackageChannel.Release ? "release-notification-plan" : "develop-notification-plan",
        new FetchedPackageInfo(PackageKind.Designer, "12织启-Windows-1.0.0-Setup.exe", "unused-designer",
            DateTimeOffset.Now, channel, channel == PackageChannel.Release ? "v1.0.0" : null),
        new FetchedPackageInfo(PackageKind.Server, "202610151033织启-Server-Windows-build12-Setup.exe", "unused-server",
            DateTimeOffset.Now, channel, channel == PackageChannel.Release ? "v1.0.0" : null));

    private static XDocument NotificationXml(InstallPlan plan, Func<PackageChannel, Uri?>? resolveIcon = null) =>
        XDocument.Parse(NotificationService.BuildFetchNotification(plan, resolveIcon ?? (_ => null)).GetToastContent().GetContent());
}
