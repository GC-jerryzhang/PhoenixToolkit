using Microsoft.Toolkit.Uwp.Notifications;
using PhoenixToolkit.Models;

namespace PhoenixToolkit.Services;

public static class NotificationService
{
    public static void ShowFetchResult(InstallPlan? installPlan)
    {
        if (installPlan is null)
            return;

        var installUri = ProtocolActivationService.BuildInstallUri(installPlan.Id);
        var lines = new List<string>();
        if (installPlan.DesignerPackage is not null)
            lines.Add($"设计器：{installPlan.DesignerPackage.FileName}");
        if (installPlan.ServerPackage is not null)
            lines.Add($"服务端：{installPlan.ServerPackage.FileName}");

        var actionText = installPlan.DesignerPackage is not null && installPlan.ServerPackage is not null
            ? "点击通知先安装设计器，再安装服务端"
            : "点击通知开始安装";

        new ToastContentBuilder()
            .SetProtocolActivation(installUri)
            .AddText(BuildFetchTitle(installPlan))
            .AddText(string.Join(Environment.NewLine, lines))
            .AddText(actionText)
            .Show(toast =>
            {
                toast.ExpirationTime = DateTimeOffset.Now.AddMinutes(5);
            });
    }

    internal static string BuildFetchTitle(InstallPlan plan)
    {
        var package = plan.DesignerPackage ?? plan.ServerPackage!;
        var channel = package.Channel == PackageChannel.Release
            ? $"release {package.ReleaseVersion}" : "develop";
        return $"Phoenix {channel} 新安装包已拉取";
    }

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
