param(
  [Parameter(Mandatory = $true)]
  [UInt64]$Hwnd
)

$source = @"
using System;
using System.Runtime.InteropServices;

public static class DesktopHost {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

  [DllImport("user32.dll")]
  public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

  [DllImport("user32.dll")]
  public static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string lpszWindow);

  [DllImport("user32.dll")]
  public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

  [DllImport("user32.dll")]
  public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

  [DllImport("user32.dll")]
  public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

  [DllImport("user32.dll")]
  public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
"@

Add-Type -TypeDefinition $source -ErrorAction SilentlyContinue

$progman = [DesktopHost]::FindWindow("Progman", $null)
if ($progman -eq [IntPtr]::Zero) {
  exit 2
}

$result = [IntPtr]::Zero
[DesktopHost]::SendMessageTimeout($progman, 0x052C, [IntPtr]::Zero, [IntPtr]::Zero, 0, 1000, [ref]$result) | Out-Null

$workerw = [IntPtr]::Zero
[DesktopHost]::EnumWindows({
  param([IntPtr]$topHandle, [IntPtr]$param)

  $shellView = [DesktopHost]::FindWindowEx($topHandle, [IntPtr]::Zero, "SHELLDLL_DefView", $null)
  if ($shellView -ne [IntPtr]::Zero) {
    $script:workerw = [DesktopHost]::FindWindowEx([IntPtr]::Zero, $topHandle, "WorkerW", $null)
  }

  return $true
}, [IntPtr]::Zero) | Out-Null

$parent = $workerw
if ($parent -eq [IntPtr]::Zero) {
  $parent = $progman
}

$target = [IntPtr]::new([Int64]$Hwnd)
[DesktopHost]::SetParent($target, $parent) | Out-Null
[DesktopHost]::SetWindowPos($target, [IntPtr]::Zero, 0, 0, 0, 0, 0x0001 -bor 0x0002 -bor 0x0040) | Out-Null
