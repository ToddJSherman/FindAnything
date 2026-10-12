# Renders design\ui\sprites.html at 2x with headless Chrome (offline flags, throwaway profile) and cuts each
# control state into src\App\skin\<name>.png, shadow padding included. The sizes here must match the CSS cells
# and the table in src\App\Skin.cs.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-skin.ps1
param([string]$Browser = "")

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "src\App\skin"
New-Item -ItemType Directory -Force $out | Out-Null

if ($Browser -eq "") {
    foreach ($c in @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
                     "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
                     "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
                     "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe")) {
        if (Test-Path $c) { $Browser = $c; break }
    }
}
if ($Browser -eq "") { throw "Chrome or Edge not found; pass -Browser <path>" }

$scale = 2
$profile = Join-Path $env:TEMP ("fa-skin-" + [Guid]::NewGuid().ToString("N"))
$sheet = Join-Path $env:TEMP "fa-skin-sheet.png"
$url = "file:///" + (Join-Path $root "design\ui\sprites.html").Replace("\", "/")
& $Browser --headless=new --disable-gpu --hide-scrollbars --no-first-run --no-default-browser-check `
    --disable-background-networking --disable-component-update --disable-sync --disable-extensions `
    --disable-default-apps --metrics-recording-only --user-data-dir="$profile" `
    --default-background-color=00000000 --force-device-scale-factor=$scale --window-size=1600,960 `
    --screenshot="$sheet" $url 2>$null | Out-Null
Start-Sleep -Milliseconds 500
Remove-Item $profile -Recurse -Force -ErrorAction SilentlyContinue
if (-not (Test-Path $sheet)) { throw "screenshot failed" }

# name, x, y, w, h, pad (CSS px)
$sprites = @(
    @("find",       40,  30, 300, 54, 6),
    @("find-on",   440,  30, 300, 54, 6),
    @("field",     840,  30, 300, 46, 5),
    @("field-hov",1240,  30, 300, 46, 5),
    @("field-on",   40, 150, 300, 46, 5),
    @("mini",      440, 150, 200, 34, 5),
    @("mini-on",   840, 150, 200, 34, 5),
    @("pri-off",  1240, 150, 200, 54, 20),
    @("pri",        40, 270, 200, 54, 20),
    @("pri-hov",   440, 270, 200, 54, 20),
    @("pri-down",  840, 270, 200, 54, 20),
    @("sec",      1240, 270, 160, 46, 2),
    @("sec-hov",    40, 390, 160, 46, 2),
    @("sec-down",  440, 390, 160, 46, 2),
    @("chip",      840, 390, 120, 32, 2),
    @("chip-hov", 1240, 390, 120, 32, 2),
    @("chip-on",    40, 510, 120, 32, 2),
    @("chip-onh",  440, 510, 120, 32, 2),
    @("hero",        0, 630, 1180, 300, 0)
)

$src = [System.Drawing.Bitmap]::FromFile($sheet)
try {
    $corner = $src.GetPixel(2, 2)
    if ($corner.A -ne 0) { throw "sheet is not transparent (corner alpha $($corner.A)); this browser ignored --default-background-color" }
    foreach ($s in $sprites) {
        $name = $s[0]; $x = $s[1]; $y = $s[2]; $w = $s[3]; $h = $s[4]; $p = $s[5]
        $rect = New-Object System.Drawing.Rectangle (($x - $p) * $scale), (($y - $p) * $scale), (($w + 2 * $p) * $scale), (($h + 2 * $p) * $scale)
        $bmp = $src.Clone($rect, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $bmp.Save((Join-Path $out "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        "{0,-10} {1}x{2}" -f $name, $rect.Width, $rect.Height
    }
}
finally {
    $src.Dispose()
    Remove-Item $sheet -ErrorAction SilentlyContinue
}
