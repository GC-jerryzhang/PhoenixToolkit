namespace PhoenixToolkit.Models;

public sealed record CleanupWeeks(
    int KeepAllWeeks = 3,
    int KeepDailyWeeks = 6,
    int DeleteAfterWeeks = 9
);

public sealed record AppConfig(
    string SourceDir = @"\\xa-tools-file\ToolsFile1\Projects\Phoenix\Installers",
    string LocalBaseDir = @"D:\TestReceive\historyPackage",
    int FetchIntervalMinutes = 5,
    string CleanupTime = "09:30",
    CleanupWeeks? CleanupWeeks = null,
    CloseBehaviorPreference CloseBehaviorPreference = CloseBehaviorPreference.AskEveryTime,
    bool FetchDevelop = true,
    bool FetchRelease = true
)
{
    public CleanupWeeks CleanupWeeks { get; init; } = CleanupWeeks ?? new CleanupWeeks();

    public string DevelopDir => Path.Combine(LocalBaseDir, "develop");
    public string DesignerDir => Path.Combine(DevelopDir, "designer");
    public string ServerDir => Path.Combine(DevelopDir, "server");
    public string ReleaseDir => Path.Combine(LocalBaseDir, "release");
    public string LogDir => Path.Combine(LocalBaseDir, "log");
    public string StateDir => Path.Combine(LocalBaseDir, "state");
    public string InstallPlanDir => Path.Combine(StateDir, "install-plans");
    public string ServerMigrationDir => Path.Combine(StateDir, "server-migrations");
}
