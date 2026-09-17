# DPI-aware screenshot of the running app.
#
# Without SetProcessDPIAware, GetWindowRect returns logically-scaled coordinates on a
# high-DPI display and CopyFromScreen then grabs the wrong region -- you get a picture of
# whatever is up and to the left of the real window. That is the whole reason this script
# exists rather than a two-line inline capture.
#
#   .\screenshot.ps1 -Out shot.png                 # maximize, capture whole screen (most reliable)
#   .\screenshot.ps1 -Out shot.png -WindowOnly     # capture just the window rect, leave size alone
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [string]$ProcessName = 'VramMonitor',
    [int]$MaxWidth = 1920,
    [switch]$WindowOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class ShotNative {
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@

[ShotNative]::SetProcessDPIAware() | Out-Null

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue
if (-not $proc) { throw "$ProcessName is not running." }
$handle = $proc.MainWindowHandle
if ($handle -eq 0) { throw "$ProcessName has no main window (started with --start-hidden?)." }

# 9 = SW_RESTORE, 3 = SW_MAXIMIZE. Maximizing makes the window fill the screen, which
# sidesteps rect arithmetic entirely for the common "show me the whole UI" case.
[ShotNative]::ShowWindow($handle, $(if ($WindowOnly) { 9 } else { 3 })) | Out-Null
[ShotNative]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 1200   # let WPF finish laying out and repainting before the grab

if ($WindowOnly) {
    $r = New-Object 'ShotNative+RECT'
    [ShotNative]::GetWindowRect($handle, [ref]$r) | Out-Null
    $x = $r.Left; $y = $r.Top
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
} else {
    # The screen the window is actually on, not the primary one. SW_MAXIMIZE above leaves the window
    # on whichever monitor it already occupied, so capturing PrimaryScreen would hand back a picture
    # of a different display -- silently, which is the worst failure available to a check whose whole
    # purpose is to be looked at.
    $b = [System.Windows.Forms.Screen]::FromHandle($handle).Bounds
    $x = $b.X; $y = $b.Y; $w = $b.Width; $h = $b.Height
}

$full = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($full)
$g.CopyFromScreen($x, $y, 0, 0, $full.Size)
$g.Dispose()

# A 4K grab costs a lot of tokens to read and gains nothing, so downscale before saving.
if ($w -gt $MaxWidth) {
    $nh = [int]($h * $MaxWidth / $w)
    $small = New-Object System.Drawing.Bitmap $MaxWidth, $nh
    $g2 = [System.Drawing.Graphics]::FromImage($small)
    $g2.InterpolationMode = 'HighQualityBicubic'
    $g2.DrawImage($full, 0, 0, $MaxWidth, $nh)
    $small.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g2.Dispose(); $small.Dispose()
    "saved $Out (${MaxWidth}x${nh}, downscaled from ${w}x${h})"
} else {
    $full.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    "saved $Out (${w}x${h})"
}
$full.Dispose()
