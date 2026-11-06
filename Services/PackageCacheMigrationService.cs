using PhoenixToolkit.Models;

namespace PhoenixToolkit.Services;

internal static class PackageCacheMigrationService
{
    public static void Migrate(AppConfig config, Action<string, string> log)
    {
        DevelopPackageCacheMigrationService.Migrate(config, log);
        ReleasePackageCacheMigrationService.Migrate(config, log);
    }

    public static string GetLegacyDirectory(AppConfig config, PackageKind kind) =>
        Path.Combine(config.LocalBaseDir, kind == PackageKind.Designer ? "designer" : "server");

    public static FetchedPackageInfo? ResolveMovedPackage(AppConfig config, FetchedPackageInfo? package)
    {
        if (package is null)
            return package;

        if (package.Channel == PackageChannel.Release)
            return ReleasePackageCacheMigrationService.ResolveMovedPackage(config, package);

        if (package.Channel != PackageChannel.Develop)
            return package;

        return DevelopPackageCacheMigrationService.ResolveMovedPackage(config, package);
    }
}
