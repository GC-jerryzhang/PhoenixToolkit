using System.Diagnostics;
using System.Text.RegularExpressions;
using PhoenixToolkit.Models;

namespace PhoenixToolkit.Services;

public static class FetchService
{
    private const int StabilityWaitMs = 5000;
    private static readonly string[] PlatformFolders =
        ["Designer-Linux", "Designer-Windows", "Server-Linux", "Server-Windows"];
    private static readonly Regex DesignerSourceNamePattern = new(
        @"^(?:Phoenix-Windows-|织启(?:-Windows)?-).+\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ServerSourceNamePattern = new(
        @"^(?:Phoenix-Server-Windows-|织启(?:服务管理器|(?:-Server)?(?:-Windows)?)-).+\.exe$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Execute(AppConfig config) => Execute(
        config, IsFileStable, FileVersionInfo.GetVersionInfo, NotificationService.ShowFetchResult);

    internal static string Execute(
        AppConfig config,
        Func<string, bool> isFileStable,
        Func<string, FileVersionInfo> readVersion,
        Action<InstallPlan?> notify)
    {
        config = ConfigService.NormalizeSourceDirectory(config);
        var logFile = Path.Combine(config.LogDir, $"fetch-phoenix_{DateTime.Now:yyyyMMdd}.log");
        var log = new List<string>();
        void Log(string message, string level = "INFO") =>
            log.Add($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}");

        try
        {
            Directory.CreateDirectory(config.LogDir);
            Directory.CreateDirectory(config.StateDir);
            FileStream fetchLock;
            try
            {
                fetchLock = new FileStream(Path.Combine(config.StateDir, "fetch.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Log("Another fetch cycle is running; skipping this cycle.", "WARNING");
                return string.Join(Environment.NewLine, log);
            }

            using (fetchLock)
            {
                Log("=== Fetch cycle started ===");
                PackageCacheMigrationService.Migrate(config, Log);
                if (!Directory.Exists(config.SourceDir))
                {
                    Log($"Source directory not accessible: {config.SourceDir}", "ERROR");
                }
                else
                {
                    var sources = ResolvePackageSources(config, Log);
                    foreach (var source in sources)
                    {
                        Log($"--- {source.Label} ---");
                        var designer = FetchPackage(config, source, PackageKind.Designer, Log, isFileStable, readVersion);
                        var server = FetchPackage(config, source, PackageKind.Server, Log, isFileStable, readVersion);
                        try
                        {
                            var plan = InstallPlanService.CreatePlan(config, designer, server);
                            notify(plan);
                        }
                        catch (Exception ex)
                        {
                            Log($"[{source.Label}] Install plan/notification failed: {ex.Message}", "ERROR");
                        }
                    }
                }
                Log("=== Fetch cycle completed ===");
            }
        }
        catch (Exception ex)
        {
            Log($"Unexpected error: {ex.Message}", "ERROR");
        }
        finally
        {
            Directory.CreateDirectory(config.LogDir);
            File.AppendAllLines(logFile, log);
        }

        return string.Join(Environment.NewLine, log);
    }

    internal static IReadOnlyList<PackageSource> ResolvePackageSources(AppConfig config, Action<string, string> log)
    {
        var sources = new List<PackageSource>();
        var developDir = Path.Combine(config.SourceDir, "develop");
        var releaseDir = Path.Combine(config.SourceDir, "release");

        if (config.FetchDevelop)
        {
            if (Directory.Exists(developDir))
                sources.Add(new PackageSource(PackageChannel.Develop, developDir));
            else if (HasPlatformSpecificLayout(config.SourceDir) ||
                     Directory.EnumerateFiles(config.SourceDir, "*.exe")
                         .Any(path => IsSupportedSourcePackageName(PackageKind.Designer, Path.GetFileName(path)) ||
                                      IsSupportedSourcePackageName(PackageKind.Server, Path.GetFileName(path))))
                sources.Add(new PackageSource(PackageChannel.Develop, config.SourceDir));
            else
                log($"develop source directory not accessible: {developDir}", "WARNING");
        }

        if (config.FetchRelease)
        {
            try
            {
                if (!Directory.Exists(releaseDir))
                    log($"release source directory not accessible: {releaseDir}", "WARNING");
                else
                {
                    var latest = Directory.EnumerateDirectories(releaseDir)
                        .Select(path => (Path: path, Name: Path.GetFileName(path)))
                        .Select(item => (item.Path, item.Name, Version: ParseReleaseVersion(item.Name)))
                        .Where(item => item.Version is not null)
                        .OrderByDescending(item => item.Version)
                        .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();
                    if (latest.Path is null)
                        log($"No version directory found in release: {releaseDir}", "WARNING");
                    else
                        sources.Add(new PackageSource(PackageChannel.Release, latest.Path, latest.Name));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"release source check failed: {ex.Message}", "ERROR");
            }
        }
        return sources;
    }

    private static Version? ParseReleaseVersion(string name) =>
        Version.TryParse(name.StartsWith('v') || name.StartsWith('V') ? name[1..] : name, out var version)
            ? version : null;

    private static FetchedPackageInfo? FetchPackage(
        AppConfig config, PackageSource source, PackageKind kind, Action<string, string> log,
        Func<string, bool> isFileStable, Func<string, FileVersionInfo> readVersion)
    {
        var label = $"[{source.Label}] {kind}";
        try
        {
            var sourceDirectory = ResolveWindowsPackageSourceDirectory(source.Directory, kind);
            if (!Directory.Exists(sourceDirectory))
            {
                log($"{label} source directory not accessible: {sourceDirectory}", "WARNING");
                return null;
            }

            var latest = Directory.EnumerateFiles(sourceDirectory, "*.exe")
                .Where(path => IsSupportedSourcePackageName(kind, Path.GetFileName(path)))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (latest is null)
            {
                log($"{label}: no Windows installer found in {sourceDirectory}", "WARNING");
                return null;
            }

            var version = readVersion(latest.FullName);
            log($"{label}: {latest.Name} (FileVersion: {version.FileVersion}, Build: {version.FilePrivatePart})", "INFO");
            var destName = BuildDestinationFileName(kind, latest.Name, version);
            var destinationDirectory = GetDestinationDirectory(config, source, kind);
            var destPath = Path.Combine(destinationDirectory, destName);
            if (GetCompatibleDestinationPaths(config, source, kind, latest.Name, version, destName)
                .Any(File.Exists))
            {
                log($"{label} already exists: {destName} (skipped)", "INFO");
                return null;
            }

            if (!isFileStable(latest.FullName))
            {
                log($"{label} file still being written, skipping this cycle.", "WARNING");
                return null;
            }

            // Read metadata again after the upload has settled.
            version = readVersion(latest.FullName);
            destName = BuildDestinationFileName(kind, latest.Name, version);
            destPath = Path.Combine(destinationDirectory, destName);
            if (GetCompatibleDestinationPaths(config, source, kind, latest.Name, version, destName)
                .Any(File.Exists))
                return null;

            Directory.CreateDirectory(destinationDirectory);
            var temporaryPath = destPath + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                log($"Copying {label} -> {destPath}", "INFO");
                // Deny writes during the copy; a partial installer is never exposed as an .exe.
                using (var input = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                File.SetLastWriteTimeUtc(temporaryPath, File.GetLastWriteTimeUtc(latest.FullName));
                File.Move(temporaryPath, destPath);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            log($"{label} copied successfully: {destName} (Size: {new FileInfo(destPath).Length} bytes)", "INFO");
            return new FetchedPackageInfo(kind, destName, destPath, DateTimeOffset.Now, source.Channel, source.ReleaseVersion);
        }
        catch (Exception ex)
        {
            log($"{label} fetch failed: {ex.Message}", "ERROR");
            return null;
        }
    }

    internal static string BuildDestinationFileName(PackageKind kind, string sourceName, FileVersionInfo version)
    {
        var productVersion = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}";
        if (kind == PackageKind.Designer)
        {
            if (version.FilePrivatePart <= 0)
                throw new InvalidDataException($"Could not read Designer build number from {sourceName}.");
            return $"{version.FilePrivatePart}织启-Windows-{productVersion}-Setup.exe";
        }

        var timestamp = Regex.Match(sourceName, @"(?<!\d)(\d{12})(?=\.exe$)", RegexOptions.IgnoreCase);
        if (!timestamp.Success)
            throw new InvalidDataException($"Could not parse timestamp from Server filename: {sourceName}.");
        var buildSuffix = version.FilePrivatePart > 0 ? $"-build{version.FilePrivatePart}" : string.Empty;
        return $"{timestamp.Groups[1].Value}织启-Server-Windows{buildSuffix}-Setup.exe";
    }

    private static bool IsSupportedSourcePackageName(PackageKind kind, string fileName) =>
        (kind == PackageKind.Designer ? DesignerSourceNamePattern : ServerSourceNamePattern)
        .IsMatch(fileName);

    private static IEnumerable<string> GetCompatibleDestinationPaths(
        AppConfig config,
        PackageSource source,
        PackageKind kind,
        string sourceName,
        FileVersionInfo version,
        string destinationName)
    {
        var destinationDirectory = GetDestinationDirectory(config, source, kind);
        yield return Path.Combine(destinationDirectory, destinationName);

        var productVersion = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}";
        var legacyName = kind == PackageKind.Designer
            ? $"{version.FilePrivatePart}Phoenix-Windows-{productVersion}-Setup.exe"
            : BuildLegacyServerDestinationFileName(sourceName, version);
        yield return Path.Combine(destinationDirectory, legacyName);

        if (source.Channel == PackageChannel.Develop && kind == PackageKind.Designer)
        {
            var legacyDirectory = PackageCacheMigrationService.GetLegacyDirectory(config, kind);
            yield return Path.Combine(legacyDirectory, destinationName);
            yield return Path.Combine(legacyDirectory, legacyName);
            yield return Path.Combine(config.DesignerDir,
                $"{version.FilePrivatePart}Phoenix-Windows-0.0.1-Setup.exe");
        }
    }

    private static string BuildLegacyServerDestinationFileName(string sourceName, FileVersionInfo version)
    {
        var timestamp = Regex.Match(sourceName, @"(?<!\d)(\d{12})(?=\.exe$)");
        if (!timestamp.Success)
            return sourceName;
        var buildSuffix = version.FilePrivatePart > 0 ? $"-build{version.FilePrivatePart}" : string.Empty;
        return $"{timestamp.Groups[1].Value}Phoenix-Server-Windows{buildSuffix}-Setup.exe";
    }

    internal static string GetDestinationDirectory(AppConfig config, PackageSource source, PackageKind kind) =>
        source.Channel == PackageChannel.Release
            ? Path.Combine(config.ReleaseDir, source.ReleaseVersion!, kind == PackageKind.Designer ? "designer" : "server")
            : kind == PackageKind.Designer ? config.DesignerDir : config.ServerDir;

    private static bool IsFileStable(string path)
    {
        var before = new FileInfo(path);
        var length = before.Length;
        var modified = before.LastWriteTimeUtc;
        Thread.Sleep(StabilityWaitMs);
        var after = new FileInfo(path);
        return length > 0 && length == after.Length && modified == after.LastWriteTimeUtc;
    }

    internal static string ResolveWindowsPackageSourceDirectory(AppConfig config, PackageKind kind) =>
        ResolveWindowsPackageSourceDirectory(config.SourceDir, kind);

    private static string ResolveWindowsPackageSourceDirectory(string sourceDirectory, PackageKind kind)
    {
        var platformFolder = kind switch
        {
            PackageKind.Designer => "Designer-Windows",
            PackageKind.Server => "Server-Windows",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported package kind.")
        };
        return HasPlatformSpecificLayout(sourceDirectory) ? Path.Combine(sourceDirectory, platformFolder) : sourceDirectory;
    }

    private static bool HasPlatformSpecificLayout(string sourceDirectory) =>
        PlatformFolders.Any(folder => Directory.Exists(Path.Combine(sourceDirectory, folder)));
}
