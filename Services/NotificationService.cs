using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Toolkit.Uwp.Notifications;
using PhoenixToolkit.Models;

namespace PhoenixToolkit.Services;

public static class NotificationService
{
    private static readonly Regex DesignerCacheName = new(
        @"^(?<build>[0-9]+)?(?:Phoenix|织启)(?:-Windows(?:-(?<version>[0-9]+(?:\.[0-9]+){2,3}))?)?-Setup\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ServerCacheName = new(
        @"^(?<timestamp>[0-9]{12})(?:Phoenix|织启)-Server-Windows(?:-build(?<build>[0-9]+))?-Setup\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static void ShowFetchResult(InstallPlan? installPlan)
    {
        if (installPlan is null)
            return;

        BuildFetchNotification(installPlan)
            .Show(toast =>
            {
                toast.ExpirationTime = DateTimeOffset.Now.AddMinutes(5);
            });
    }

    internal static ToastContentBuilder BuildFetchNotification(
        InstallPlan plan, Func<PackageChannel, Uri?>? resolveIcon = null)
    {
        var package = GetFirstPackage(plan);
        var installUri = ProtocolActivationService.BuildInstallUri(plan.Id);
        var channel = package.Channel == PackageChannel.Release ? "RELEASE" : "DEVELOP";
        var builder = new ToastContentBuilder()
            .SetProtocolActivation(installUri)
            .AddText(BuildFetchTitle(plan), hintMaxLines: 2);

        if (plan.DesignerPackage is not null)
            builder.AddText(BuildPackageSummary(plan.DesignerPackage), hintMaxLines: 1);
        if (plan.ServerPackage is not null)
            builder.AddText(BuildPackageSummary(plan.ServerPackage), hintMaxLines: 1);

        builder.AddButton(new ToastButton()
            .SetContent($"安装 {channel}")
            .SetProtocolActivation(installUri));

        var icon = resolveIcon is null ? TryGetChannelIcon(package.Channel) : resolveIcon(package.Channel);
        if (icon is not null)
            builder.AddAppLogoOverride(icon, alternateText: channel == "RELEASE" ? "R 发布版" : "D 开发版");

        return builder;
    }

    internal static string BuildFetchTitle(InstallPlan plan)
    {
        var package = GetFirstPackage(plan);
        if (package.Channel != PackageChannel.Release)
            return "DEVELOP · 开发版";

        return string.IsNullOrWhiteSpace(package.ReleaseVersion)
            ? "RELEASE · 发布版"
            : $"RELEASE · 发布版 · {package.ReleaseVersion}";
    }

    internal static string BuildPackageSummary(FetchedPackageInfo package)
    {
        var label = package.Kind == PackageKind.Designer ? "设计器" : "服务端";
        var match = (package.Kind == PackageKind.Designer ? DesignerCacheName : ServerCacheName)
            .Match(package.FileName);
        var details = new List<string>();
        if (match.Success)
        {
            if (match.Groups["version"].Success)
                details.Add($"v{match.Groups["version"].Value}");
            if (int.TryParse(match.Groups["build"].Value, NumberStyles.None, CultureInfo.InvariantCulture,
                out var build) && build > 0)
                details.Add($"文件号 {build}");
            if (DateTime.TryParseExact(match.Groups["timestamp"].Value, "yyyyMMddHHmm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                details.Add(timestamp.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        return $"{label}：{(details.Count == 0 ? "已就绪" : string.Join(" · ", details))}";
    }

    internal static Uri? TryGetChannelIcon(PackageChannel channel, string? cacheDirectory = null)
    {
        string? temporaryPath = null;
        try
        {
            var fileName = channel == PackageChannel.Release ? "release.png" : "develop.png";
            using var resource = typeof(NotificationService).Assembly.GetManifestResourceStream(
                $"PhoenixToolkit.Assets.Notifications.{fileName}");
            if (resource is null)
                return null;

            using var content = new MemoryStream();
            resource.CopyTo(content);
            var bytes = content.ToArray();
            cacheDirectory ??= Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PhoenixToolkit", "notifications", "v1");
            Directory.CreateDirectory(cacheDirectory);
            var path = Path.GetFullPath(Path.Combine(cacheDirectory, fileName));
            if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            {
                temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporaryPath, bytes);
                File.Move(temporaryPath, path, overwrite: true);
            }

            // The toolkit serializes OriginalString; keep the file URI scheme explicit.
            return new Uri(new Uri(path).AbsoluteUri);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            System.Diagnostics.Trace.WriteLine($"Notification icon unavailable: {ex.Message}");
            return null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    System.Diagnostics.Trace.WriteLine($"Notification icon temporary file cleanup failed: {ex.Message}");
                }
            }
        }
    }

    private static FetchedPackageInfo GetFirstPackage(InstallPlan plan) =>
        plan.DesignerPackage ?? plan.ServerPackage
        ?? throw new ArgumentException("Install plan must contain at least one package.", nameof(plan));

    public static void ShowInstallFailure(string message)
    {
        new ToastContentBuilder()
            .AddText("Phoenix 安装启动失败")
            .AddText(message)
            .Show(toast =>
            {
                toast.ExpirationTime = DateTimeOffset.Now.AddMinutes(5);
            });
    }
}
