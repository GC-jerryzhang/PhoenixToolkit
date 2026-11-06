# Phoenix 测试辅助工具 (PhoenixToolkit)

安装包自动拉取/清理 + 产品日志快速打开。单文件 exe，双击即用。

## 运行

1. 安装 [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0/runtime)（如未安装）
2. 双击 `PhoenixToolkit.exe` 运行
3. 填写共享源目录和本地保存目录，选择拉取渠道，点击「安装服务」注册计划任务

> 需要管理员权限（注册计划任务需要）。

## develop / release 安装包

- 默认共享源目录：`\\xa-tools-file\ToolsFile1\Projects\Phoenix\Installers`。选择包含 `develop` 和 `release` 的根目录；默认同时拉取两个渠道。
- develop 从 `develop\Designer-Windows` 和 `develop\Server-Windows` 拉取最新 Windows 安装包。
- release 拉取仍从共享 `release` 的版本文件夹中按数字版本选择最新源版本，例如 `v1.10.0` 高于 `v1.9.0`，再读取其中的 `Designer-Windows` / `Server-Windows`。本地归档目录独立按每个安装包的版本资源生成，例如从源 `v1.0.0` 拉到版本 `1.0.1` 的包时保存到 `release\v1.0.1\designer` 或 `server`；后续产品版本会自动进入对应目录。
- 设计器从安装包版本资源读取真实版本和文件号，例如 `25Phoenix-Windows-1.0.0-Setup.exe`。共享源包兼容新旧命名和缓存名；只调整本地缓存文件名，不改动源安装包内容。服务端兼容 Windows 安装包的时间戳命名。文件号为 0 时使用构建时间戳，非零时也记录到本地文件名。
- 本地根目录仍为 `D:\TestReceive\historyPackage`。develop 和 release 都按安装包版本归档，分别保存在 `develop\<安装包版本>\designer` / `server` 与 `release\<安装包版本>\designer` / `server`。通知仍按渠道显示，Release 通知继续显示共享源版本，每个通知只安装该渠道拉取的包。

旧配置会自动将原默认共享地址或以 `develop` 结尾的共享源目录升级到 Installers 根目录，保留本地目录、计划任务间隔和其他设置。首次拉取会将旧 `designer` / `server` 下的安装包按版本移动到 `develop\<安装包版本>\designer` / `server`，无需重新下载；遇到同名目标或占用文件时保留原文件。已有安装通知也能找到迁移后的包。

首次拉取会按安装包元数据将旧 develop / Release 缓存归入对应产品版本目录；遇到同名目标时保留原文件，并兼容迁移前已发出的安装通知。两个渠道按目录分别清理，Release 每个归档版本、每种安装包始终保留最新的一份，即使超过清理周期。复制使用临时文件，成功后再改名；并发拉取会跳过，避免保存未完成的安装包。

## 本地验证

```powershell
dotnet build
dotnet run --project tests/PhoenixToolkit.Tests/PhoenixToolkit.Tests.csproj
```

## 命令行

```
PhoenixToolkit.exe              # 打开 GUI
PhoenixToolkit.exe --fetch      # 静默执行一次拉取
PhoenixToolkit.exe --cleanup    # 静默执行一次清理
```

## 开发调试

```powershell
dotnet run -- --dev
dotnet run -- --dev --fetch
dotnet run -- --dev --cleanup
```

- `--dev` 会进入开发模式，避免每次改动都先发布打包再验证。
- 开发模式的配置和本地状态会写入 `%LOCALAPPDATA%\PhoenixToolkit\dev\`。
- 开发模式下不会自动注册协议，也不会安装/卸载计划任务；这些系统级行为请使用正式发布版验证。

## 构建

```powershell
dotnet publish .\PhoenixToolkit.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./publish
```
