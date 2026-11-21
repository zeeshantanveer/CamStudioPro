# Renders the MSIX logo assets (packaging\Assets\*.png) from the CamStudio Pro mark: an amber lens
# ring on a dark rounded tile. Re-run after changing the design; the PNGs are checked in.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "Assets"
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Filter *.png | Remove-Item

$amber = [System.Drawing.Color]::FromArgb(255, 0xFF, 0xB0, 0x20)
$tile = [System.Drawing.Color]::FromArgb(255, 0x18, 0x1B, 0x21)

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

# Draws the mark (tile + ring + dot) of side $s centred at ($cx, $cy).
function Mark($g, [float]$cx, [float]$cy, [float]$s) {
    $u = $s / 34.0
    $path = RoundRect ($cx - $s / 2) ($cy - $s / 2) $s $s (10 * $u)
    $g.FillPath((New-Object System.Drawing.SolidBrush $tile), $path)
    $pen = New-Object System.Drawing.Pen $amber, ([Math]::Max(1.0, 2.2 * $u))
    $g.DrawEllipse($pen, $cx - 9 * $u, $cy - 9 * $u, 18 * $u, 18 * $u)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $amber), $cx - 3.5 * $u, $cy - 3.5 * $u, 7 * $u, 7 * $u)
}

# $fill = fraction of the shorter side the mark occupies.
function Render([string]$name, [int]$w, [int]$h, [double]$fill) {
    $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.PixelOffsetMode = "HighQuality"; $g.InterpolationMode = "HighQualityBicubic"
    $g.Clear([System.Drawing.Color]::Transparent)
    Mark $g ($w / 2.0) ($h / 2.0) ([Math]::Min($w, $h) * $fill)
    $g.Dispose()
    $bmp.Save((Join-Path $out $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

foreach ($scale in 100, 200, 400) {
    $k = $scale / 100.0
    Render "StoreLogo.scale-$scale.png" (50 * $k) (50 * $k) 1.0
    Render "Square44x44Logo.scale-$scale.png" (44 * $k) (44 * $k) 1.0
    Render "Square71x71Logo.scale-$scale.png" (71 * $k) (71 * $k) 0.62
    Render "Square150x150Logo.scale-$scale.png" (150 * $k) (150 * $k) 0.5
    Render "Wide310x150Logo.scale-$scale.png" (310 * $k) (150 * $k) 0.5
    Render "Square310x310Logo.scale-$scale.png" (310 * $k) (310 * $k) 0.5
}
# Taskbar / Start list / Explorer icons: exact pixel sizes, plated and unplated.
foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    Render "Square44x44Logo.targetsize-$size.png" $size $size 1.0
    Render "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size 1.0
    Render "Square44x44Logo.targetsize-${size}_altform-lightunplated.png" $size $size 1.0
}
Write-Host "Assets written to $out"
