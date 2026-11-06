using System.Diagnostics;
using PhoenixToolkit.Models;

namespace PhoenixToolkit.Services;

internal static class DevelopPackageCacheMigrationService
{
    private static readonly (PackageKind Kind, string DirectoryName)[] ProductDirectories =
    [
        (PackageKind.Designer, "designer"),
        (PackageKind.Server, "server")
    ];

    public static void Migrate(AppConfig config, Action<string, string> log)
    {
        foreach (var (kind, productDirectory) in ProductDirectories)
            MigrateLegacyRoot(config, kind, productDirectory, log);

        if (!Directory.Exists(config.DevelopDir))
            return;

        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(config.DevelopDir, "*.exe", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Develop cache migration skipped: {ex.Message}", "WARNING");
            return;
        }

        foreach (var path in paths)
        {
            var productDirectory = Path.GetFileName(Path.GetDirectoryName(path));
            var product = ProductDirectories.FirstOrDefault(item =>
                string.Equals(item.DirectoryName, productDirectory, StringComparison.OrdinalIgnoreCase));
            if (product.DirectoryName is null)
                continue;

            MigratePackage(config, path, product.Kind, product.DirectoryName, log);
        }
    }

    public static FetchedPackageInfo? ResolveMovedPackage(AppConfig config, FetchedPackageInfo package)
    {
        if (File.Exists(package.FullPath) && package.ArchiveVersion is not null)
            return package;

        var productDirectory = package.Kind == PackageKind.Designer ? "designer" : "server";
        var fileName = Path.GetFileName(package.FileName);
        var candidates = new List<string>();

        if (File.Exists(package.FullPath))
            candidates.Add(package.FullPath);

        var legacyPath = Path.Combine(GetLegacyDirectory(config, package.Kind), fileName);
        if (File.Exists(legacyPath) && !candidates.Contains(legacyPath, StringComparer.OrdinalIgnoreCase))
            candidates.Add(legacyPath);

        if (Directory.Exists(config.DevelopDir))
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(config.DevelopDir, fileName, SearchOption.AllDirectories)
                    .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), productDirectory,
                        StringComparison.OrdinalIgnoreCase))
                    .Where(path => !candidates.Contains(path, StringComparer.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return package;
            }
        }

        if (candidates.Count != 1)
            return package;

        var path = candidates[0];
        var archiveVersion = GetArchiveVersionFromPath(config, path, productDirectory);
        return package with
        {
            FullPath = path,
            ArchiveVersion = archiveVersion
        };
    }

    private static void MigrateLegacyRoot(
        AppConfig config,
        PackageKind kind,
        string productDirectory,
        Action<string, string> log)
    {
        var legacyDirectory = PackageCacheMigrationService.GetLegacyDirectory(config, kind);
        if (!Directory.Exists(legacyDirectory))
            return;

        string[] paths;
        try
        {
            paths = Directory.GetFiles(legacyDirectory, "*.exe");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Legacy develop cache migration skipped: {legacyDirectory} ({ex.Message})", "WARNING");
            return;
        }

        foreach (var path in paths)
        {
            var archiveVersion = TryReadArchiveVersion(path);
            if (archiveVersion is null)
            {
                var legacyDestination = Path.Combine(
                    kind == PackageKind.Designer ? config.DesignerDir : config.ServerDir,
                    Path.GetFileName(path));
                if (File.Exists(legacyDestination))
                {
                    log($"Legacy cache kept (destination already exists): {path}", "WARNING");
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(legacyDestination)!);
                    File.Move(path, legacyDestination);
                    log($"Moved legacy develop package to fallback cache: {path} -> {legacyDestination}", "INFO");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log($"Legacy cache migration skipped: {path} ({ex.Message})", "WARNING");
                }

                continue;
            }

            MoveToVersionDirectory(config, path, kind, productDirectory, archiveVersion, log);
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
                Directory.Delete(legacyDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Legacy develop cache cleanup skipped: {legacyDirectory} ({ex.Message})", "WARNING");
        }
    }

    private static void MigratePackage(
        AppConfig config,
        string path,
        PackageKind kind,
        string productDirectory,
        Action<string, string> log)
    {
        var archiveVersion = TryReadArchiveVersion(path);
        if (archiveVersion is null)
            return;

        MoveToVersionDirectory(config, path, kind, productDirectory, archiveVersion, log);
    }

    private static void MoveToVersionDirectory(
        AppConfig config,
        string path,
        PackageKind kind,
        string productDirectory,
        string archiveVersion,
        Action<string, string> log)
    {
        var destinationDirectory = Path.Combine(config.DevelopDir, archiveVersion, productDirectory);
        if (Path.GetFullPath(Path.GetDirectoryName(path)!).Equals(
                Path.GetFullPath(destinationDirectory), StringComparison.OrdinalIgnoreCase))
            return;

        var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(path));
        try
        {
            if (File.Exists(destinationPath))
            {
                log($"Develop cache kept (destination already exists): {path}", "WARNING");
                return;
            }

            Directory.CreateDirectory(destinationDirectory);
            File.Move(path, destinationPath);
            log($"Moved develop package to its product version directory: {path} -> {destinationPath}", "INFO");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log($"Develop cache migration skipped: {path} ({ex.Message})", "WARNING");
        }
    }

    private static string? TryReadArchiveVersion(string path)
    {
        try
        {
            return PackageVersionService.GetArchiveVersion(FileVersionInfo.GetVersionInfo(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? GetArchiveVersionFromPath(AppConfig config, string path, string productDirectory)
    {
        var productPath = Path.GetDirectoryName(path);
        if (productPath is null || !string.Equals(Path.GetFileName(productPath), productDirectory,
                StringComparison.OrdinalIgnoreCase))
            return null;

        var versionDirectory = Path.GetDirectoryName(productPath);
        if (versionDirectory is null || !Path.GetFullPath(versionDirectory).StartsWith(
                Path.GetFullPath(config.DevelopDir) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            return null;

        return PackageVersionService.NormalizeSourceVersion(Path.GetFileName(versionDirectory));
    }

    private static string GetLegacyDirectory(AppConfig config, PackageKind kind) =>
        PackageCacheMigrationService.GetLegacyDirectory(config, kind);
}
