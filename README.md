# MiniFences

[中文](#中文) | [English](#english)

## 中文

MiniFences 是一款适用于 Windows 10/11 的轻量桌面分区管理器。它可以把个人桌面和公共桌面的图标按 Fence、页面和标签组整理，同时保留源文件原本的位置。

### 下载与运行

请从 [GitHub Releases](https://github.com/dskiiii/minifence/releases) 下载 0.23.0：

- `MiniFences-win-x64-0.23.0.zip`：推荐，自带 .NET 运行库；解压后直接运行 `MiniFences.exe`。
- `MiniFences-win-x64-0.23.0-slim.zip`：精简版；需要预先安装 Microsoft .NET 8 Desktop Runtime。

程序目前没有代码签名，Windows SmartScreen 可能显示“未知发布者”。请只从本仓库下载，并使用同名 `.sha256` 文件核对压缩包。

### 主要功能

- 新建、删除、重命名、锁定、平滑拖动和缩放 Fence。
- 自定义网格大小、拖动时吸附和松手后吸附。
- 多页面桌面以及可自定义的 F1–F12、上一页和下一页快捷键。
- 标签组合并、排序、拖出、重新合并和组合状态同步。
- 顶部/底部贴边卷起、标题栏方向和展开行为设置。
- 标题栏纯色或渐变色、透明度、对齐和简洁样式。
- Windows Shell 原生图标和右键菜单。
- 桌面图标归属、位置、顺序和异常退出恢复。

### 文件安全

- Desktop Group 只记录图标归属，不移动个人桌面或公共桌面的源文件。
- 布局恢复不会创建、移动或删除桌面源文件。
- Folder Portal 中的文件操作遵循 Windows 的正常文件行为。
- 配置：`%APPDATA%\MiniFences\config.json`
- 日志：`%APPDATA%\MiniFences\logs\app.log`

### 从源码构建

需要 Windows 和 .NET 8 SDK：

```powershell
.\.dotnet\dotnet.exe run --project MiniFences.SmokeTests\MiniFences.SmokeTests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-minifences.ps1 -Version 0.23.0
```

## English

MiniFences is a lightweight desktop organizer for Windows 10 and 11. It groups icons from the personal and public desktops into fences, pages, and tab groups while preserving the original files.

### Download and run

Download 0.23.0 from [GitHub Releases](https://github.com/dskiiii/minifence/releases):

- `MiniFences-win-x64-0.23.0.zip`: recommended self-contained build; no separate .NET installation is required.
- `MiniFences-win-x64-0.23.0-slim.zip`: smaller framework-dependent build; Microsoft .NET 8 Desktop Runtime must be installed first.

The application is currently unsigned, so Windows SmartScreen may display an “Unknown publisher” warning. Download only from this repository and verify the archive with the matching `.sha256` file.

### Build from source

Windows and the .NET 8 SDK are required:

```powershell
.\.dotnet\dotnet.exe run --project MiniFences.SmokeTests\MiniFences.SmokeTests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\publish-minifences.ps1 -Version 0.23.0
```
