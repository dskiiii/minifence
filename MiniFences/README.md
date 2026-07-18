# MiniFences

MiniFences is a tiny WPF MVP for a Fences-like desktop folder container.

This first version deliberately does **not** control Windows Explorer desktop icons. One Fence maps to one real local folder and shows that folder's files, folders, and shortcuts.

## Features

- Multiple Fence panels in a borderless desktop workspace.
- First launch creates a desktop Fence plus starter category Fences for shortcuts, documents, screenshots, media, archives, installers, temporary files, folders, and other files. It creates empty category folders but does not move existing desktop items. Temporary and incomplete-download extensions such as `.tmp`, `.bak`, `.crdownload`, and `.part` are classified into the temporary-files Fence when organization is requested.
- Automatic organization skips hidden, system, and reparse-point entries to avoid moving protected or special desktop content.
- Category destinations are identified by their managed folder paths, not only by titles, so a user-created same-title Fence is never used as an automatic organization target.
- The workspace is sent behind normal app windows and transparent empty areas pass mouse input through, so it behaves more like a desktop layer without taking over Explorer desktop icons or blocking the taskbar.
- Drag the title bar to move.
- Resize by dragging the bottom-right grip.
- Double-click a Fence title bar, or use its right-click menu, to collapse or expand it without losing its saved size. The same right-click menu can optionally enable hover expansion for that individual Fence; it is off by default.
- The Fence has a title.
- The Fence is bound to one local folder.
- The content area shows files, folders, and shortcuts as an icon grid using Windows Shell icons.
- Fences automatically refresh when files are created, deleted, renamed, or changed in their bound folder.
- Double-click an item to open it with the system default app.
- Right-click an item to open it, rename it, show it in Explorer, copy its full path, or move it to the Recycle Bin.
- Ctrl/Shift multi-select is supported for dragging multiple items out of a Fence, copying selected paths, or moving selected items to the Recycle Bin.
- Common item keys are supported: Enter opens, F2 renames, Delete moves to the Recycle Bin, F5 refreshes, Ctrl+A selects all, and Esc clears selection.
- Drag an item out of a Fence as a real Windows file drop. Dropping it onto another Fence moves it into that Fence's bound folder.
- Right-click a Fence to create another Fence, create a named folder inside it, rename it, choose or open the bound folder, refresh the file list, move it to another page, change style, or delete it.
- The tray menu stays minimal: open Settings, hide/show Fences, or exit. Fence, page, organization, language, startup, and maintenance controls live in the Settings window.
- Double-click the Explorer desktop to hide or show all Fences. The hidden/shown state is saved and restored on next launch.
- Drag files or folders from Explorer into a Fence to move them into that Fence's bound folder.
- Batch drops report moved, skipped, and failed items when not everything can be moved cleanly.
- Existing files are not overwritten; duplicate names get a numeric suffix.
- Each Fence has a Style menu for background color, header color, opacity, and reset.
- MiniFences runs without a normal taskbar button and can be controlled from the system tray.
- Double-click the tray icon or choose "Open Settings" to open the configuration window; choose "Exit" to quit.
- A Fences-style settings window centralizes Fence management, pages, organization, visibility, language, startup, logs, and config access.
- The Personalization page can edit each Fence background color, title-bar color, and opacity, or restore its default appearance.
- Desktop blank-area double-click hide/show can be enabled or disabled from the Personalization page.
- The tray menu is intentionally small: open settings, show/hide Fences, and exit. English remains available and Simplified Chinese can be selected and saved in Settings.
- Launching MiniFences normally opens Settings. Windows startup uses `--background` so Settings does not appear at every sign-in.
- Settings includes entries to open the config folder and app log file.
- Pages are supported. Each Fence belongs to a page; the current page, page count, and empty pages are saved.
- Empty pages can be deleted safely. Non-empty pages are protected until their Fences are moved or deleted.
- "Create Category Fences" creates empty starter category folders/Fences without moving desktop items.
- "Organize Desktop by Type..." manually sorts desktop items into category folders and creates missing category Fences after confirmation.
- "Undo Last Organize" restores items moved by the previous desktop organization when possible.
- Save a layout snapshot from the desktop right-click menu and restore the latest snapshot after an accidental move or display-layout change.
- Position, size, title, folder path, page, current page, page count, hidden state, language, and style are saved to:

```text
%AppData%\MiniFences\config.json
```

The last desktop organization undo history is saved to:

```text
%AppData%\MiniFences\organize-history.json
```

## Build And Run

This project requires the .NET 8 SDK with Windows Desktop/WPF support.

```powershell
cd MiniFences
dotnet build
dotnet run
```

From this repository root, the included smoke tests can be run with:

```powershell
.\.dotnet-sdk\dotnet.exe run --project .\MiniFences.SmokeTests\MiniFences.SmokeTests.csproj
```

## Portable Release

Create a self-contained Windows release without changing the user's MiniFences configuration:

```powershell
.\scripts\publish-minifences.ps1 -Version 0.19.5
```

The framework-dependent release is written to `artifacts\MiniFences-win-x64-<version>-slim` with a matching ZIP archive. The target PC must have the .NET 8 Desktop Runtime installed. The script refuses to overwrite an existing release folder or archive.

## Current Scope

Do not add Explorer desktop icon control, installer, animations, unattended automatic categorization, or desktop icon takeover in this phase. MiniFences maps panels to real folders and keeps its window behind normal app windows; it does not move or rewrite real desktop icons.

## Current Status

MiniFences is currently a usable folder-container MVP. On first launch it creates a desktop Fence and starter category containers, then it can create multiple folder-backed Fence panels, persist layout and language settings, open files with the system default app, move dropped files between bound folders, refresh from folder changes, switch pages, hide/show Fences, create category containers, and manually organize desktop items by type with undo.

The current design still deliberately avoids direct Explorer desktop icon takeover. That means it behaves like desktop folder panels rather than a full Stardock Fences replacement.
