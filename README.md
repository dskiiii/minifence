# MiniFences

[![Latest release](https://img.shields.io/github/v/release/dskiiii/minifence?display_name=tag)](https://github.com/dskiiii/minifence/releases/latest)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4)](https://github.com/dskiiii/minifence/releases/latest)

[中文](#中文) | [English](#english)

## 中文

MiniFences 是一款面向 Windows 10/11 的桌面分区与图标整理工具。它通过 Fence、多个桌面页面、标签组和文件夹门户整理内容，并尽量保留 Windows 桌面与资源管理器原本的使用方式。

### 下载

请前往 [最新稳定版](https://github.com/dskiiii/minifence/releases/latest) 下载：

- `MiniFences-win-x64-<版本>.zip`：推荐，自带 .NET 运行库，解压后直接运行 `MiniFences.exe`。
- `MiniFences-win-x64-<版本>-slim.zip`：精简版，需要预先安装 Microsoft .NET 8 Desktop Runtime。

程序目前没有代码签名，Windows SmartScreen 可能显示“未知发布者”。请只从本仓库下载，并使用同名 `.sha256` 文件核对压缩包。

### 主要功能

- 创建、重命名、锁定、移动和缩放桌面 Fence。
- 使用多个桌面页面分类工作，可设置上一页、下一页和页面快捷键。
- 将 Fence 合并为标签组，支持排序、拖出、重新组合和贴边卷起。
- 使用文件夹门户直接浏览文件夹，同时保留 Windows Shell 图标和右键菜单。
- 自定义标题栏方向、颜色或渐变、透明度、对齐、网格与吸附方式。
- 保存和恢复布局，记录桌面图标归属、位置与顺序，并提供异常退出恢复。
- 文件操作历史与撤销保护、诊断包路径脱敏，以及更安全的拖放反馈。
- 自动检查 GitHub Releases，校验下载文件并在更新后自动重启。

### 自动更新

程序启动后会检查本仓库的最新稳定版 Release。确认更新后，程序会下载完整版压缩包及对应的 `.sha256` 文件，校验成功后替换程序文件并重新启动。`%APPDATA%\MiniFences` 中的个人配置不会被更新包覆盖。也可以从托盘菜单或设置页手动检查更新。

### 文件安全

- 桌面分组只记录图标归属，不会为了整理而移动个人桌面或公共桌面的源文件。
- 布局恢复只恢复显示与归属信息，不会创建、移动或删除桌面源文件。
- 文件夹门户中的复制、移动、重命名和删除仍属于真实文件操作，请根据拖动提示确认操作类型。
- 配置保存在 `%APPDATA%\MiniFences\config.json`，日志保存在 `%APPDATA%\MiniFences\logs\app.log`。

### 从源码构建

需要 Windows 和 .NET 8 SDK：

```powershell
.\.dotnet\dotnet.exe run --project MiniFences.SmokeTests\MiniFences.SmokeTests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-minifences.ps1 -Version <版本号>
```

## English

MiniFences is a desktop fence and icon organizer for Windows 10 and 11. It organizes content with fences, multiple desktop pages, tab groups, and folder portals while preserving familiar Windows desktop and File Explorer behavior.

### Download

Download the [latest stable release](https://github.com/dskiiii/minifence/releases/latest):

- `MiniFences-win-x64-<version>.zip`: recommended self-contained build; extract it and run `MiniFences.exe`.
- `MiniFences-win-x64-<version>-slim.zip`: smaller build that requires the Microsoft .NET 8 Desktop Runtime.

The application is currently unsigned, so Windows SmartScreen may display an “Unknown publisher” warning. Download only from this repository and verify the archive with the matching `.sha256` file.

### Features

- Create, rename, lock, move, and resize desktop fences.
- Organize work across multiple desktop pages with configurable page shortcuts.
- Combine fences into reorderable tab groups, detach them again, and roll groups up at screen edges.
- Browse folders through portals with native Windows Shell icons and context menus.
- Customize header position, solid or gradient colors, opacity, alignment, grids, and snapping.
- Save and restore layouts, desktop icon ownership, positions, and ordering, with crash recovery.
- Use protected file-operation history, redacted diagnostic bundles, and clear drag-and-drop feedback.
- Check GitHub Releases automatically, verify downloads, install updates, and restart safely.

### File safety

- Desktop grouping records icon ownership without moving files from the personal or public desktop.
- Layout restore changes layout metadata only; it does not create, move, or delete desktop files.
- Copy, move, rename, and delete actions inside folder portals are real file operations; always check the drag indicator.
- Configuration is stored in `%APPDATA%\MiniFences\config.json`; logs are stored in `%APPDATA%\MiniFences\logs\app.log`.

### Automatic updates

MiniFences checks this repository for the latest stable Release after startup. When a newer
version is available, the app asks for confirmation, downloads the full
`MiniFences-win-x64-version.zip` package, verifies its matching `.sha256` file, replaces the
installed files, and restarts. Configuration under `%APPDATA%\MiniFences` is kept intact.
Updates can also be checked from the tray menu or Settings.

### Build from source

Windows and the .NET 8 SDK are required:

```powershell
.\.dotnet\dotnet.exe run --project MiniFences.SmokeTests\MiniFences.SmokeTests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-minifences.ps1 -Version <version>
```
