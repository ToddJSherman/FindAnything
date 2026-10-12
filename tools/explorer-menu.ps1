# Adds or removes "Find Anything here" on Explorer's right-click menu for folders, folder backgrounds and drives,
# and "Find similar pictures" for picture files.
# Per-user (HKCU), no admin needed. On Windows 11 it appears under "Show more options" (or Shift+F10).
#
#   powershell -ExecutionPolicy Bypass -File tools\explorer-menu.ps1             install (uses bin\FindAnything.exe)
#   powershell -ExecutionPolicy Bypass -File tools\explorer-menu.ps1 -Uninstall  remove
param(
    [switch]$Uninstall,
    [string]$Exe = ""
)

$ErrorActionPreference = "Stop"
if ($Exe -eq "") { $Exe = Join-Path (Split-Path -Parent $PSScriptRoot) "bin\FindAnything.exe" }

# Where each menu lives, and the placeholder Explorer fills in with the clicked path.
$targets = @(
    @{ Key = "HKCU:\Software\Classes\Directory\shell\FindAnything";            Arg = "`"%1`"";           Label = "Find Anything here" },   # a folder
    @{ Key = "HKCU:\Software\Classes\Directory\Background\shell\FindAnything"; Arg = "`"%V`"";           Label = "Find Anything here" },   # empty space in a folder
    @{ Key = "HKCU:\Software\Classes\Drive\shell\FindAnything";                Arg = "`"%1`"";           Label = "Find Anything here" },   # a drive
    @{ Key = "HKCU:\Software\Classes\SystemFileAssociations\image\shell\FindAnythingSimilar"; Arg = "--similar `"%1`""; Label = "Find similar pictures" }   # a picture
)

if ($Uninstall) {
    foreach ($t in $targets) {
        if (Test-Path $t.Key) { Remove-Item $t.Key -Recurse -Force; "removed  $($t.Key)" }
    }
    return
}

if (-not (Test-Path $Exe)) { throw "Not found: $Exe  (run build.cmd first, or pass -Exe)" }
$Exe = (Resolve-Path $Exe).Path

foreach ($t in $targets) {
    New-Item -Path $t.Key -Force | Out-Null
    Set-ItemProperty -Path $t.Key -Name "(default)" -Value $t.Label
    Set-ItemProperty -Path $t.Key -Name "Icon" -Value "`"$Exe`",0"
    New-Item -Path "$($t.Key)\command" -Force | Out-Null
    Set-ItemProperty -Path "$($t.Key)\command" -Name "(default)" -Value "`"$Exe`" $($t.Arg)"
    "added    $($t.Key)"
}
