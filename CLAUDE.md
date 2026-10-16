# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```powershell
# Build (debug)
dotnet build

# Run GUI in development mode (recommended for local iteration)
dotnet run -- --dev

# Publish single-file EXE (framework-dependent, ~206KB)
dotnet publish .\PhoenixToolkit.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./publish

# Run GUI
dotnet run

# Run CLI modes (used by scheduled tasks)
dotnet run -- --dev --fetch
dotnet run -- --dev --cleanup
```

Requires .NET 8 SDK. Target: `net8.0-windows10.0.17763.0`. The repo includes a local test harness project under `tests/PhoenixToolkit.Tests/`.

## Architecture

PhoenixToolkit is a Windows Forms app that fetches installer packages from a network share and cleans up old packages on a schedule. Source matching accepts current and legacy package names.

**Runtime entry point** (`Program.cs`):
- **Release mode** (default): Published runtime behavior with protocol registration and scheduled-task support
- **Development mode** (`--dev`): Source-run workflow for fast local iteration with isolated config/state storage
- **CLI mode** (`--fetch` / `--cleanup`): Silent execution invoked by Windows Task Scheduler, and also available in dev mode for quick validation

**Service layer** (all static classes in `Services/`):
- `ConfigService` — JSON persistence via source-generated serializer (`AppConfigJsonContext`). Config file lives next to the EXE as `config.json`.
- `FetchService` — Fetches Windows installers from both `develop` and the highest numeric version folder under `release` in the configured Installers root. Designer names use file version/build; Server identity uses the filename timestamp, plus file build when available. Uses a 5s size/mtime stability check, temporary-file copy and exclusive per-cache fetch lock. Channels have separate install plans and notifications.
- `CleanupService` — Age-based history cleanup, independently per product/channel/release version. Always preserves the latest package of each product in each cached release version. Also cleans logs older than 30 days.
- `SchedulerService` — Creates/removes Windows scheduled tasks via `schtasks.exe` with XML task definitions. Task names: `PhoenixToolkit_Fetch`, `PhoenixToolkit_Cleanup`.
- `NotificationService` — Windows toast notifications when new packages are fetched.

**Configuration model** (`Models/AppConfig.cs`): Immutable C# record with defaults. `SourceDir` is the Installers root; `FetchDevelop` and `FetchRelease` default to true. Develop uses `develop/designer|server`; release uses `release/<version>/designer|server`, with LocalBaseDir unchanged. ConfigService migrates legacy develop source paths. PackageCacheMigrationService moves old root-level installer caches during fetch; install plans resolve moved paths when activated.

**Key constraint**: Release builds require Administrator elevation (`app.manifest` → `requireAdministrator`) because `schtasks.exe` needs it. Dev mode is intended for fast iteration and avoids release-only side effects such as task installation and automatic protocol registration.

## Conventions

- Immutable records for data models (C# `record` types)
- Source-generated JSON serialization (no reflection)
- All services are static — no DI container
- Namespace: `PhoenixToolkit` (root), `PhoenixToolkit.Services`, `PhoenixToolkit.Models`
- UI text is in Chinese (简体中文)
- Single NuGet dependency: `Microsoft.Toolkit.Uwp.Notifications` v7.1.3
