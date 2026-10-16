using PhoenixToolkit.Models;

namespace PhoenixToolkit.Services;

internal static class PackageCacheMigrationService
{
    public static void Migrate(AppConfig config, Action<string, string> log)
    {
        foreach (var kind in new[] { PackageKind.Designer, PackageKind.Server })
        {
            var legacyDirectory = GetLegacyDirectory(config, kind);
            if (!Directory.Exists(legacyDirectory))
                continue;

            try
            {
                var destination = kind == PackageKind.Designer ? config.DesignerDir : config.ServerDir;
                Directory.CreateDirectory(destination);
                foreach (var path in Directory.EnumerateFiles(legacyDirectory, "*.exe"))
                {
                    var destinationPath = Path.Combine(destination, Path.GetFileName(path));
                    try
                    {
                        if (File.Exists(destinationPath))
                        {
                            log($"Legacy cache kept (destination already exists): {path}", "WARNING");
                            continue;
                        }
                        File.Move(path, destinationPath);
                        log($"Moved legacy develop package: {path} -> {destinationPath}", "INFO");
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        log($"Legacy cache migration skipped: {path} ({ex.Message})", "WARNING");
                    }
                }
                if (!Directory.EnumerateFileSystemEntries(legacyDirectory).Any())
                    Directory.Delete(legacyDirectory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"Legacy cache migration skipped: {legacyDirectory} ({ex.Message})", "WARNING");
            }
        }
    }

    public static string GetLegacyDirectory(AppConfig config, PackageKind kind) =>
        Path.Combine(config.LocalBaseDir, kind == PackageKind.Designer ? "designer" : "server");

    public static FetchedPackageInfo? ResolveMovedPackage(AppConfig config, FetchedPackageInfo? package)
    {
        if (package is null || package.Channel != PackageChannel.Develop || File.Exists(package.FullPath))
            return package;

        var legacyPath = Path.Combine(GetLegacyDirectory(config, package.Kind), Path.GetFileName(package.FileName));
        if (!Path.GetFullPath(package.FullPath).Equals(Path.GetFullPath(legacyPath), StringComparison.OrdinalIgnoreCase))
            return package;

        var destination = package.Kind == PackageKind.Designer ? config.DesignerDir : config.ServerDir;
        var movedPath = Path.Combine(destination, Path.GetFileName(package.FileName));
        return File.Exists(movedPath) ? package with { FullPath = movedPath } : package;
    }
}
