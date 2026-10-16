using System.Diagnostics;
using System.Text.Json;
using PhoenixToolkit.Models;
using PhoenixToolkit.Services;

namespace PhoenixToolkit.Tests;

internal static partial class Program
{
    private static void ReleaseSourceUsesNumericVersionsAndIgnoresReleaseDashDirectory()
    {
        RunIsolated(config =>
        {
            var source = Path.Combine(config.LocalBaseDir, "source");
            Directory.CreateDirectory(Path.Combine(source, "develop", "Designer-Windows"));
            foreach (var version in new[] { "v1.0.0", "v1.9.0", "v1.10.0", "in-progress" })
                Directory.CreateDirectory(Path.Combine(source, "release", version));
            Directory.CreateDirectory(Path.Combine(source, "release-1.0", "v99.0.0"));
            File.WriteAllText(Path.Combine(source, "Phoenix-Windows-legacy.exe"), "old");
            var sources = FetchService.ResolvePackageSources(config with { SourceDir = source }, (_, _) => { });
            Assert.Equal(2, sources.Count, "Both channels should be resolved.");
            Assert.Equal(Path.Combine(source, "develop"), sources[0].Directory, "Channel directory should override flat packages.");
            Assert.Equal("v1.10.0", sources[1].ReleaseVersion!, "Version ordering must be numeric.");
            Assert.Equal(Path.Combine(source, "release", "v1.10.0"), sources[1].Directory,
                "Only the release directory should supply release packages.");
        });
    }

    private static void ChannelSelectionLimitsSources()
    {
        RunIsolated(config =>
        {
            var source = CreateChannelFixture(config);
            var releaseOnly = FetchService.ResolvePackageSources(config with { SourceDir = source, FetchDevelop = false }, (_, _) => { });
            Assert.Equal(PackageChannel.Release, releaseOnly.Single().Channel, "Develop must honor its off switch.");
            var developOnly = FetchService.ResolvePackageSources(config with { SourceDir = source, FetchRelease = false }, (_, _) => { });
            Assert.Equal(PackageChannel.Develop, developOnly.Single().Channel, "Release must honor its off switch.");
            var none = FetchService.ResolvePackageSources(config with { SourceDir = source, FetchDevelop = false, FetchRelease = false }, (_, _) => { });
            Assert.Equal(0, none.Count, "Disabling both channels should skip all sources.");
        });
    }

    private static void FetchCopiesBothChannelsAndSkipsExistingPackages()
    {
        RunIsolated(config =>
        {
            config = config with { SourceDir = CreateChannelFixture(config) };
            var plans = new List<InstallPlan>();
            var stabilityChecks = 0;
            bool Stable(string _) { stabilityChecks++; return true; }
            void Notify(InstallPlan? plan) { if (plan is not null) plans.Add(plan); }
            var output = FetchService.Execute(config, Stable, FileVersionInfo.GetVersionInfo, Notify);
            Assert.False(output.Contains("[ERROR]"), output);
            Assert.Equal(2, plans.Count, "A separate install plan should be made for each channel.");
            Assert.Equal(4, stabilityChecks, "Only new packages need upload stability checks.");
            foreach (var plan in plans)
            {
                Assert.NotNull(plan.DesignerPackage, "Designer should be fetched.");
                Assert.NotNull(plan.ServerPackage, "Server should be fetched.");
                var designer = plan.DesignerPackage!;
                var server = plan.ServerPackage!;
                Assert.Equal(designer.Channel, server.Channel, "Install plans must stay within one channel.");
                Assert.Equal("12织启-Windows-1.0.0-Setup.exe", designer.FileName, "Designer should use real version and build.");
                Assert.Contains(server.FileName, "build12", "Server build should be included when available.");
                var expectedDir = designer.Channel == PackageChannel.Release
                    ? Path.Combine(config.ReleaseDir, "v1.0.0", "designer") : config.DesignerDir;
                Assert.Equal(expectedDir, Path.GetDirectoryName(designer.FullPath)!, "Caches must be separate.");
                Assert.True(File.ReadAllBytes(designer.FullPath).SequenceEqual(File.ReadAllBytes(typeof(Program).Assembly.Location)),
                    "Installer payload must be copied completely.");
                var sourceChannelDir = designer.Channel == PackageChannel.Release
                    ? Path.Combine(config.SourceDir, "release", "v1.0.0")
                    : Path.Combine(config.SourceDir, "develop");
                var sourceDesigner = Path.Combine(sourceChannelDir, "Designer-Windows", "Phoenix-Windows-202610141445.exe");
                var sourceServer = Path.Combine(sourceChannelDir, "Server-Windows", "Phoenix-Server-Windows-202610141438.exe");
                Assert.True(File.Exists(sourceDesigner) && File.Exists(sourceServer),
                    "Legacy Phoenix source filenames must remain unchanged after fetching.");
                Assert.True(File.ReadAllBytes(designer.FullPath).SequenceEqual(File.ReadAllBytes(sourceDesigner)),
                    "Renaming the local Designer cache must not alter its package bytes.");
                Assert.True(File.ReadAllBytes(server.FullPath).SequenceEqual(File.ReadAllBytes(sourceServer)),
                    "Renaming the local Server cache must not alter its package bytes.");
                Assert.Contains(NotificationService.BuildFetchTitle(plan), designer.Channel == PackageChannel.Release ? "release v1.0.0" : "develop",
                    "Notification should identify channel and release version.");
                Assert.True(InstallPlanService.TryStartPlan(config, plan.Id, out var persisted, out var error), error);
                Assert.Equal(designer.Channel, persisted!.DesignerPackage!.Channel, "Channel must survive JSON serialization.");
                Assert.Equal(designer.ReleaseVersion ?? "", persisted.DesignerPackage.ReleaseVersion ?? "", "Release version must persist.");
            }
            var repeat = FetchService.Execute(config, Stable, FileVersionInfo.GetVersionInfo, Notify);
            Assert.False(repeat.Contains("[ERROR]"), repeat);
            Assert.Equal(2, plans.Count, "Repeated fetch must not create more plans.");
            Assert.Equal(4, stabilityChecks, "Existing packages should bypass the five-second stability check.");
            Assert.False(Directory.EnumerateFiles(config.LocalBaseDir, "*.partial", SearchOption.AllDirectories).Any(),
                "Successful copies should not leave temporary files.");

            // Existing develop cache names remain recognized after the naming upgrade.
            File.Move(plans[0].DesignerPackage!.FullPath, Path.Combine(config.DesignerDir, "12Phoenix-Windows-0.0.1-Setup.exe"));
            FetchService.Execute(config, Stable, FileVersionInfo.GetVersionInfo, Notify);
            Assert.Equal(2, plans.Count, "Legacy Designer cache should prevent a redundant download.");
        });
    }

    private static void FetchFailureDoesNotBlockOtherPackagesOrChannels()
    {
        RunIsolated(config =>
        {
            config = config with { SourceDir = CreateChannelFixture(config) };
            var plans = new List<InstallPlan>();
            var output = FetchService.Execute(config, _ => true, path =>
            {
                if (path.Contains(Path.Combine("develop", "Designer-Windows")))
                    throw new IOException("metadata unavailable");
                return FileVersionInfo.GetVersionInfo(path);
            }, plan => { if (plan is not null) plans.Add(plan); });
            Assert.Contains(output, "metadata unavailable", "Failure should be logged.");
            Assert.Equal(2, plans.Count, "Both channels should still yield install plans.");
            Assert.Null(plans[0].DesignerPackage, "Failed Designer must not enter an install plan.");
            Assert.NotNull(plans[0].ServerPackage, "Develop Server should still copy after Designer failure.");
            Assert.NotNull(plans[1].DesignerPackage, "Release Designer should copy independently.");
            Assert.NotNull(plans[1].ServerPackage, "Release Server should copy independently.");
        });
    }

    private static void FetchAcceptsCurrentChineseSourceNamesAndWritesChineseCacheNames()
    {
        RunIsolated(config =>
        {
            var source = Path.Combine(config.LocalBaseDir, "source");
            var designerSource = Path.Combine(source, "develop", "Designer-Windows");
            var serverSource = Path.Combine(source, "develop", "Server-Windows");
            Directory.CreateDirectory(designerSource);
            Directory.CreateDirectory(serverSource);
            File.Copy(typeof(Program).Assembly.Location,
                Path.Combine(designerSource, "织启-202610151732.exe"));
            File.Copy(typeof(Program).Assembly.Location,
                Path.Combine(serverSource, "织启服务管理器-202610151733.exe"));

            var plans = new List<InstallPlan>();
            var output = FetchService.Execute(
                config with { SourceDir = source, FetchRelease = false },
                _ => true,
                FileVersionInfo.GetVersionInfo,
                plan => { if (plan is not null) plans.Add(plan); });

            Assert.False(output.Contains("[ERROR]"), output);
            var plan = plans.Single();
            Assert.Equal("12织启-Windows-1.0.0-Setup.exe", plan.DesignerPackage!.FileName,
                "Current Designer source names should produce Chinese cache names.");
            Assert.Equal("202610151733织启-Server-Windows-build12-Setup.exe", plan.ServerPackage!.FileName,
                "Current Chinese Server source names should produce Chinese cache names.");
            Assert.True(File.Exists(Path.Combine(designerSource, "织启-202610151732.exe")),
                "Fetching must leave the shared Designer source name untouched.");
            Assert.True(File.Exists(Path.Combine(serverSource, "织启服务管理器-202610151733.exe")),
                "Fetching must leave the shared Server source name untouched.");
        });
    }

    private static void FetchRejectsConcurrentCycleAndWritesLog()
    {
        RunIsolated(config =>
        {
            Directory.CreateDirectory(config.StateDir);
            using var held = new FileStream(Path.Combine(config.StateDir, "fetch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var output = FetchService.Execute(config, _ => throw new InvalidOperationException("should not fetch"),
                FileVersionInfo.GetVersionInfo, _ => throw new InvalidOperationException("should not notify"));
            Assert.Contains(output, "Another fetch cycle", "Concurrent cycle should be skipped.");
            Assert.Contains(File.ReadAllText(Directory.GetFiles(config.LogDir, "*.log").Single()), "Another fetch cycle",
                "Skipped cycle must be logged.");
        });
    }

    private static void FailedCopyDoesNotLeaveInstallerOrPartialFile()
    {
        RunIsolated(config =>
        {
            config = config with { SourceDir = CreateChannelFixture(config), FetchRelease = false };
            var sourcePath = Path.Combine(config.SourceDir, "develop", "Designer-Windows", "Phoenix-Windows-202610141445.exe");
            var metadata = FileVersionInfo.GetVersionInfo(sourcePath);
            using var held = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None);
            var plans = new List<InstallPlan>();
            var output = FetchService.Execute(config, _ => true, _ => metadata, plan => { if (plan is not null) plans.Add(plan); });
            Assert.Contains(output, "fetch failed", "Copy failure should be logged.");
            Assert.False(Directory.EnumerateFiles(config.DesignerDir).Any(), "Failed copy must leave no installer or partial file.");
            Assert.NotNull(plans.Single().ServerPackage, "Other packages should still succeed.");
        });
    }

    private static void ServerWithoutBuildNumberUsesTimestamp()
    {
        var metadata = FileVersionInfo.GetVersionInfo(typeof(FetchService).Assembly.Location);
        Assert.Equal(0, metadata.FilePrivatePart, "Fixture must have a zero private part.");
        Assert.Equal("202610141438织启-Server-Windows-Setup.exe",
            FetchService.BuildDestinationFileName(PackageKind.Server, "Phoenix-Server-Windows-202610141438.exe", metadata),
            "Server with a zero build number must retain timestamp identity.");
        Assert.Throws<InvalidDataException>(() => FetchService.BuildDestinationFileName(PackageKind.Server, "Phoenix-Server-Windows-invalid.exe", metadata),
            "An invalid timestamp must not overwrite a known installer.");
    }

    private static void LegacyConfigMigratesSourceAndDefaultsToBothChannels()
    {
        RunInDevelopmentMode(devRoot =>
        {
            RuntimeModeService.Initialize(new[] { "--dev" });
            var old = new { sourceDir = @"\\xafile\ToolsFile1\Projects\Phoenix\Installers\develop", localBaseDir = devRoot, fetchIntervalMinutes = 17 };
            File.WriteAllText(ConfigService.ConfigPath, JsonSerializer.Serialize(old));
            var loaded = ConfigService.Load();
            Assert.Equal(new AppConfig().SourceDir, loaded.SourceDir, "Old default UNC path should migrate to new Installers root.");
            Assert.True(loaded.FetchDevelop && loaded.FetchRelease, "Old configs should enable both channels.");
            Assert.Equal(devRoot, loaded.LocalBaseDir, "Local cache path must be preserved.");
            Assert.Equal(17, loaded.FetchIntervalMinutes, "Existing interval must be preserved.");
            Assert.Equal(loaded, ConfigService.Load(), "Migration should be idempotent.");
            Assert.Contains(File.ReadAllText(ConfigService.ConfigPath), "xa-tools-file", "Migrated source should be saved.");
            var custom = ConfigService.NormalizeSourceDirectory(loaded with { SourceDir = @"E:\custom\develop\" });
            Assert.Equal(@"E:\custom", custom.SourceDir, "Custom develop sources should migrate to their parent.");
            Assert.Equal(@"D:\", ConfigService.NormalizeSourceDirectory(loaded with { SourceDir = @"D:\" }).SourceDir,
                "Volume roots must retain the directory separator.");
        });
    }

    private static void ChannelSelectionRoundTripsAndDisplaysInMainForm()
    {
        RunInDevelopmentMode(devRoot =>
        {
            RuntimeModeService.Initialize(new[] { "--dev" });
            ConfigService.Save(new AppConfig(LocalBaseDir: devRoot, FetchDevelop: false, FetchRelease: true));
            using var form = new MainForm();
            Assert.False(FindRequiredControl<CheckBox>(form, "chkFetchDevelop").Checked, "Develop off selection should display.");
            Assert.True(FindRequiredControl<CheckBox>(form, "chkFetchRelease").Checked, "Release on selection should display.");
            var loaded = ConfigService.Load();
            Assert.False(loaded.FetchDevelop, "Develop selection should round-trip.");
            Assert.True(loaded.FetchRelease, "Release selection should round-trip.");
        });
    }

    private static void InstallPlanRejectsMixedChannelsAndVersions()
    {
        RunIsolated(config =>
        {
            var designer = new FetchedPackageInfo(PackageKind.Designer, "designer.exe", "designer.exe", DateTimeOffset.Now);
            var releaseServer = new FetchedPackageInfo(PackageKind.Server, "server.exe", "server.exe", DateTimeOffset.Now, PackageChannel.Release, "v1.0.0");
            Assert.Throws<ArgumentException>(() => InstallPlanService.CreatePlan(config, designer, releaseServer), "Mixed channels should be rejected.");
            Assert.Throws<ArgumentException>(() => InstallPlanService.CreatePlan(config,
                designer with { Channel = PackageChannel.Release, ReleaseVersion = "v2.0.0" }, releaseServer), "Mixed release versions should be rejected.");
        });
    }

    private static void InstallPlanValidationUsesChannelSpecificCacheRoots()
    {
        RunIsolated(config =>
        {
            var releaseServerDirectory = Path.Combine(config.ReleaseDir, "v1.0.0", "server");
            var releaseServerPath = Path.Combine(releaseServerDirectory, "202610151033Phoenix-Server-Windows-Setup.exe");
            Directory.CreateDirectory(releaseServerDirectory);
            File.WriteAllText(releaseServerPath, "release server");
            var releaseServerPackage = new FetchedPackageInfo(
                PackageKind.Server,
                Path.GetFileName(releaseServerPath),
                releaseServerPath,
                DateTimeOffset.Now,
                PackageChannel.Release,
                "v1.0.0");

            var releasePlan = new InstallPlan(
                "2d9f6c38a6b44023ba24d288b4ec96d7",
                null,
                releaseServerPackage);

            InstallActivationService.ValidatePlan(config, releasePlan);

            var developDesignerDirectory = config.DesignerDir;
            var developDesignerPath = Path.Combine(developDesignerDirectory, "12Phoenix-Windows-1.0.0-Setup.exe");
            Directory.CreateDirectory(developDesignerDirectory);
            File.WriteAllText(developDesignerPath, "develop designer");
            InstallActivationService.ValidatePlan(config, new InstallPlan(
                "4d9f6c38a6b44023ba24d288b4ec96d7",
                new FetchedPackageInfo(
                    PackageKind.Designer,
                    Path.GetFileName(developDesignerPath),
                    developDesignerPath,
                    DateTimeOffset.Now),
                null));

            var invalidReleasePlan = releasePlan with
            {
                ServerPackage = releaseServerPackage with
                {
                    ReleaseVersion = "v1.0.0\\..\\other"
                }
            };
            Assert.Throws<InvalidOperationException>(
                () => InstallActivationService.ValidatePlan(config, invalidReleasePlan),
                "Release versions must not escape the release cache directory.");

            var wrongProductPath = Path.Combine(config.ReleaseDir, "v1.0.0", "designer", "server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(wrongProductPath)!);
            File.WriteAllText(wrongProductPath, "wrong product");
            Assert.Throws<InvalidOperationException>(
                () => InstallActivationService.ValidatePlan(config, releasePlan with
                {
                    ServerPackage = releaseServerPackage with { FullPath = wrongProductPath }
                }),
                "A Server package must stay in the release Server cache.");
        });
    }

    private static void InstallPlanCanRetryAfterPackagePathValidationFailure()
    {
        RunIsolated(config =>
        {
            var packagePath = Path.Combine(config.ReleaseDir, "v1.0.0", "server", "server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(packagePath)!);
            File.WriteAllText(packagePath, "server");
            var package = new FetchedPackageInfo(
                PackageKind.Server,
                Path.GetFileName(packagePath),
                packagePath,
                DateTimeOffset.Now,
                PackageChannel.Release,
                "v1.0.0");
            var plan = InstallPlanService.CreatePlan(config, null, package)!;
            InstallPlanService.MarkFailed(config, plan.Id, "安装包路径不合法: server.exe");

            Assert.True(InstallPlanService.TryStartPlan(config, plan.Id, out var retry, out var retryError), retryError);
            Assert.Equal(InstallPlanStatus.Running, retry!.Status, "A plan rejected before launching the installer should be retryable.");

            InstallPlanService.MarkFailed(config, plan.Id, "Server 安装失败: installer exited with code 1");
            Assert.False(InstallPlanService.TryStartPlan(config, plan.Id, out _, out _),
                "Installer failures must remain non-retryable through the same notification.");
        });
    }

    private static void CleanupKeepsLatestReleasePackagesForEachVersion()
    {
        RunIsolated(config =>
        {
            foreach (var version in new[] { "v1.0.0", "v2.0.0" })
            {
                foreach (var kind in new[] { "designer", "server" })
                {
                    var dir = Path.Combine(config.ReleaseDir, version, kind);
                    Directory.CreateDirectory(dir);
                    var old = Path.Combine(dir, "11Phoenix-Setup.exe");
                    var latest = Path.Combine(dir, "12Phoenix-Setup.exe");
                    File.WriteAllText(old, "old");
                    File.WriteAllText(latest, "latest");
                    File.SetLastWriteTime(old, DateTime.Now.AddDays(-110));
                    File.SetLastWriteTime(latest, DateTime.Now.AddDays(-100));
                }
            }
            Directory.CreateDirectory(config.DesignerDir);
            var oldDevelop = Path.Combine(config.DesignerDir, "12Phoenix-Setup.exe");
            File.WriteAllText(oldDevelop, "old develop");
            File.SetLastWriteTime(oldDevelop, DateTime.Now.AddDays(-100));
            var output = CleanupService.Execute(config);
            Assert.False(output.Contains("[ERROR]"), output);
            Assert.False(File.Exists(oldDevelop), "Develop should retain its existing age policy.");
            Assert.Equal(4, Directory.GetFiles(config.ReleaseDir, "*.exe", SearchOption.AllDirectories).Length,
                "Latest package for every release version and product must survive age cleanup.");
            Assert.False(Directory.EnumerateFiles(config.ReleaseDir, "11*", SearchOption.AllDirectories).Any(), "Old superseded releases should expire.");
        });
    }

    private static void LegacyCachesMoveUnderDevelopAndOldInstallPlansStillResolve()
    {
        RunIsolated(config =>
        {
            config = config with { SourceDir = CreateChannelFixture(config), FetchRelease = false };
            var packages = new List<FetchedPackageInfo>();
            foreach (var kind in new[] { PackageKind.Designer, PackageKind.Server })
            {
                var directory = PackageCacheMigrationService.GetLegacyDirectory(config, kind);
                Directory.CreateDirectory(directory);
                var name = kind == PackageKind.Designer ? "12Phoenix-Windows-0.0.1-Setup.exe" : "202610141438Phoenix-Server-Windows-build12-Setup.exe";
                var path = Path.Combine(directory, name);
                File.Copy(typeof(Program).Assembly.Location, path);
                packages.Add(new FetchedPackageInfo(kind, name, path, DateTimeOffset.Now));
            }
            var plan = InstallPlanService.CreatePlan(config, packages[0], packages[1])!;
            var output = FetchService.Execute(config, _ => throw new InvalidOperationException("Existing packages should not recopy"),
                FileVersionInfo.GetVersionInfo, plan => Assert.Null(plan, "Migrated caches should prevent new installation notifications."));
            Assert.False(output.Contains("[ERROR]"), output);
            Assert.Equal(Path.Combine(config.LocalBaseDir, "develop", "designer"), config.DesignerDir, "Develop must be a direct child of the existing root.");
            Assert.False(Directory.Exists(Path.Combine(config.LocalBaseDir, "designer")), "An empty legacy folder should be removed.");
            Assert.False(Directory.Exists(Path.Combine(config.LocalBaseDir, "server")), "An empty legacy Server folder should be removed.");
            Assert.True(InstallPlanService.TryStartPlan(config, plan.Id, out var movedPlan, out var error), error);
            Assert.Equal(Path.Combine(config.DesignerDir, packages[0].FileName), movedPlan!.DesignerPackage!.FullPath,
                "An existing notification should resolve the migrated Designer package.");
            Assert.Equal(Path.Combine(config.ServerDir, packages[1].FileName), movedPlan.ServerPackage!.FullPath,
                "An existing notification should resolve the migrated Server package.");
            Assert.True(File.Exists(movedPlan.DesignerPackage.FullPath) && File.Exists(movedPlan.ServerPackage.FullPath), "Both migrated packages must exist.");
        });
    }

    private static void CacheMigrationPreservesConflictsAndUnrelatedFiles()
    {
        RunIsolated(config =>
        {
            var legacyDir = PackageCacheMigrationService.GetLegacyDirectory(config, PackageKind.Designer);
            Directory.CreateDirectory(legacyDir);
            Directory.CreateDirectory(config.DesignerDir);
            File.WriteAllText(Path.Combine(legacyDir, "same.exe"), "old payload");
            File.WriteAllText(Path.Combine(config.DesignerDir, "same.exe"), "new payload");
            File.WriteAllText(Path.Combine(legacyDir, "notes.txt"), "user notes");
            var logs = new List<string>();
            PackageCacheMigrationService.Migrate(config, (message, _) => logs.Add(message));
            PackageCacheMigrationService.Migrate(config, (message, _) => logs.Add(message));
            Assert.Equal("old payload", File.ReadAllText(Path.Combine(legacyDir, "same.exe")), "Conflicting old files must be retained.");
            Assert.Equal("new payload", File.ReadAllText(Path.Combine(config.DesignerDir, "same.exe")), "Existing target must not be overwritten.");
            Assert.Equal("user notes", File.ReadAllText(Path.Combine(legacyDir, "notes.txt")), "Unrelated files must remain untouched.");
            Assert.Contains(logs, line => line.Contains("already exists"));
        });
    }

    private static void UnstableReleasePackageDoesNotBlockOtherPackages()
    {
        RunIsolated(config =>
        {
            config = config with { SourceDir = CreateChannelFixture(config) };
            var plans = new List<InstallPlan>();
            var output = FetchService.Execute(config,
                path => !path.Contains(Path.Combine("release", "v1.0.0", "Designer-Windows")),
                FileVersionInfo.GetVersionInfo, plan => { if (plan is not null) plans.Add(plan); });
            Assert.Contains(output, "still being written", "Incomplete upload must be skipped.");
            Assert.Equal(2, plans.Count, "Develop and Release Server should remain independent.");
            Assert.Null(plans[1].DesignerPackage, "Unstable release installer must not be notified.");
            Assert.NotNull(plans[1].ServerPackage, "Stable release Server should still copy.");
        });
    }

    private static string CreateChannelFixture(AppConfig config)
    {
        var root = Path.Combine(config.LocalBaseDir, "source");
        foreach (var channel in new[] { "develop", Path.Combine("release", "v1.0.0") })
        {
            foreach (var kind in new[] { "Designer", "Server" })
            {
                var directory = Path.Combine(root, channel, $"{kind}-Windows");
                Directory.CreateDirectory(directory);
                var name = kind == "Designer" ? "Phoenix-Windows-202610141445.exe" : "Phoenix-Server-Windows-202610141438.exe";
                File.Copy(typeof(Program).Assembly.Location, Path.Combine(directory, name));
            }
        }
        return root;
    }
}
