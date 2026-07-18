# MiniFences

[中文](#中文) | [English](#english)

## 中文

MiniFences 是一款适用于 Windows 10/11 的轻量桌面分区管理器。它可以把个人桌面和公共桌面中的图标按 Fence、页面和标签组进行整理，同时保留文件原本的位置。

### 下载与运行

请从 [GitHub Releases](https://github.com/dskiiii/minifence/releases) 下载：

- `MiniFences-win-x64-<版本号>.zip`：推荐版本，自带 .NET 运行库；解压后直接运行 `MiniFences.exe`。
- `MiniFences-win-x64-<版本号>-slim.zip`：轻量版本；需要预先安装 Microsoft .NET 8 Desktop Runtime。

首次运行未签名版本时，Windows SmartScreen 可能显示“未知发布者”。请只从本项目的 GitHub Releases 下载，并使用同版本的 `.sha256` 文件核对压缩包。

### 主要功能

- 新建、删除、重命名、锁定、拖动和缩放 Fence。
- 多页面桌面、快捷键翻页、页面预览和跨页移动。
- 标签组合并、排序、拆分以及两种标签栏样式。
- 自动网格排列桌面图标，拖动仅调整显示顺序。
- 独立的显示、卷起、标签页和 Fence 外观设置与交互预览。
- 命名布局和自动快照，可恢复页面、位置、归属和图标顺序。
- Windows Shell 原生右键菜单，兼容系统与第三方扩展。
- 同时读取个人桌面与公共桌面，并按 Windows 规则合并同名项目。

### 文件安全

- DesktopGroup 只记录图标归属，不移动个人桌面或公共桌面的源文件。
- 布局恢复不会创建、移动或删除桌面源文件。
- Folder Portal 中的文件操作仍遵循 Windows 的正常文件行为。
- 配置保存在 `%APPDATA%\MiniFences\config.json`。
- 日志保存在 `%APPDATA%\MiniFences\logs\app.log`。

### 从源码构建

需要 Windows 和 .NET 8 SDK：

```powershell
.\.dotnet\dotnet.exe build MiniFences\MiniFences.csproj -c Release
.\.dotnet\dotnet.exe run --project MiniFences.SmokeTests\MiniFences.SmokeTests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-minifences.ps1
```

## English

MiniFences is a lightweight desktop organization tool for Windows 10 and 11. It groups icons from both the personal and public desktops into fences, pages, and tab groups while keeping the original files in place.

### Download and run

Download the latest build from [GitHub Releases](https://github.com/dskiiii/minifence/releases):

- `MiniFences-win-x64-<version>.zip`: recommended, self-contained build. Extract it and run `MiniFences.exe`; no separate .NET installation is required.
- `MiniFences-win-x64-<version>-slim.zip`: smaller framework-dependent build. Microsoft .NET 8 Desktop Runtime must be installed first.

Because the application is currently unsigned, Windows SmartScreen may display an “Unknown publisher” warning. Download only from this repository and verify the archive with the matching `.sha256` file.

### Features

- Create, delete, rename, lock, move, and resize fences.
- Multiple desktop pages with keyboard navigation, previews, and cross-page moves.
- Merge, sort, and split tab groups with two tab-bar styles.
- Automatically arrange desktop icons on a grid while preserving their source locations.
- Separate appearance settings and interactive previews for fences, tabs, roll-up behavior, and visibility.
- Named layouts and automatic snapshots that restore pages, positions, assignments, and icon order.
- Native Windows Shell context menus, including system and third-party extensions.
- Reads both personal and public desktop folders and merges duplicate names using Windows desktop rules.

### File safety

- DesktopGroup stores icon assignments only; it does not move source files on the personal or public desktop.
- Restoring a layout does not create, move, or delete desktop source files.
- File operations inside a Folder Portal follow normal Windows file behavior.
- Configuration: `%APPDATA%\MiniFences\config.json`
- Logs: `%APPDATA%\MiniFences\logs\app.log`

### Build from source

Windows and the .NET 8 SDK are required:

```powershell
.\.dotnet\dotnet.exe build MiniFences\MiniFences.csproj -c Release
.\.dotnet\dotnet.exe run --project MiniFences.SmokeTests\MiniFences.SmokeTests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-minifences.ps1
```
