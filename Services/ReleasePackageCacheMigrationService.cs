using System.Diagnostics;
using PhoenixToolkit.Models;

namespace PhoenixToolkit.Services;

internal static class ReleasePackageCacheMigrationService
{
    private static readonly (PackageKind Kind, string DirectoryName)[] ProductDirectories =
    [
        (PackageKind.Designer, "designer"),
        (PackageKind.Server, "server")
    ];

    public static void Migrate(AppConfig config, Action<string, string> log) =>
        Migrate(config,
            path => ReleasePackageVersionService.GetArchiveVersion(FileVersionInfo.GetVersionInfo(path)),
            log);

    internal static void Migrate(
        AppConfig config,
        Func<string, string?> resolveArchiveVersion,
        Action<string, string> log)
    {
        if (!Directory.Exists(config.ReleaseDir))
            return;

        try
        {
            var versionDirectories = Directory.GetDirectories(config.ReleaseDir);
            foreach (var versionDirectory in versionDirectories)
            {
                foreach (var (_, productDirectory) in ProductDirectories)
                {
                    var packageDirectory = Path.Combine(versionDirectory, productDirectory);
                    if (!Directory.Exists(packageDirectory))
                        continue;

                    foreach (var packagePath in Directory.GetFiles(packageDirectory, "*.exe"))
                    {
                        MigratePackage(config, packagePath, productDirectory, resolveArchiveVersion, log);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Release cache migration skipped: {ex.Message}", "WARNING");
        }
    }

    public static FetchedPackageInfo? ResolveMovedPackage(AppConfig config, FetchedPackageInfo package)
    {
        if (File.Exists(package.FullPath) || !Directory.Exists(config.ReleaseDir))
            return package;

        var productDirectory = package.Kind == PackageKind.Designer ? "designer" : "server";
        var fileName = Path.GetFileName(package.FileName);

        try
        {
            var candidates = Directory.GetDirectories(config.ReleaseDir)
                .Select(versionDirectory =>
                {
                    var path = Path.Combine(versionDirectory, productDirectory, fileName);
                    return File.Exists(path) ? (VersionDirectory: versionDirectory, Path: path) : default;
                })
                .Where(candidate => candidate.Path is not null)
                .ToList();

            if (candidates.Count != 1)
                return package;

            var candidate = candidates[0];
            var archiveVersion = ReleasePackageVersionService.NormalizeSourceVersion(
                Path.GetFileName(candidate.VersionDirectory));
            return package with
            {
                FullPath = candidate.Path!,
                ArchiveVersion = archiveVersion ?? Path.GetFileName(candidate.VersionDirectory)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return package;
        }
    }

    private static void MigratePackage(
        AppConfig config,
        string packagePath,
        string productDirectory,
        Func<string, string?> resolveArchiveVersion,
        Action<string, string> log)
    {
        try
        {
            var archiveVersion = ReleasePackageVersionService.NormalizeArchiveVersion(
                resolveArchiveVersion(packagePath));
            if (archiveVersion is null)
            {
                log($"Release cache kept (package version unavailable): {packagePath}", "WARNING");
                return;
            }

            var destinationDirectory = Path.Combine(config.ReleaseDir, archiveVersion, productDirectory);
            if (Path.GetFullPath(Path.GetDirectoryName(packagePath)!).Equals(
                    Path.GetFullPath(destinationDirectory), StringComparison.OrdinalIgnoreCase))
                return;

            var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(packagePath));
            if (File.Exists(destinationPath))
            {
                log($"Release cache kept (destination already exists): {packagePath}", "WARNING");
                return;
            }

            Directory.CreateDirectory(destinationDirectory);
            File.Move(packagePath, destinationPath);
            log($"Moved release package to its product version directory: {packagePath} -> {destinationPath}", "INFO");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log($"Release cache migration skipped: {packagePath} ({ex.Message})", "WARNING");
        }
    }
}
