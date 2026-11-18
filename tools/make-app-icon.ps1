# Draws the app icon (mint waves on a navy rounded square) and writes:
#   AutoAudioAdjust.App/Assets/AppIcon.ico      (16-256 px, PNG-compressed entries)
#   AutoAudioAdjust.App/Assets/AppIcon-256.png  (used inside the app's top bar)
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File tools\make-app-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'AutoAudioAdjust.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function Render-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Background: navy rounded square with a subtle border
    $m = [Math]::Max(1, $s * 0.03)
    $rect = New-RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ($s * 0.22)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF($s, $s)),
        (C '#163250'), (C '#08111F'))
    $g.FillPath($bg, $rect)
    if ($s -ge 32) {
        $g.DrawPath((New-Object System.Drawing.Pen((C '#2A4664'), [Math]::Max(1, $s * 0.012))), $rect)
    }

    # Mint sine waves: three lines, two at very small sizes so they stay readable
    $lines = if ($s -le 24) { 2 } else { 3 }
    $mint = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, ($s * 0.2))), (New-Object System.Drawing.PointF(0, ($s * 0.8))),
        (C '#5CF0CB'), (C '#22B894'))
    $pen = New-Object System.Drawing.Pen($mint, [Math]::Max(1.6, $s * 0.075))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $x0 = $s * 0.22; $x1 = $s * 0.78
    $amp = $s * 0.055
    $gap = if ($lines -eq 3) { $s * 0.17 } else { $s * 0.2 }
    $cy = $s / 2 - $gap * ($lines - 1) / 2
    for ($l = 0; $l -lt $lines; $l++) {
        $pts = New-Object System.Collections.Generic.List[System.Drawing.PointF]
        for ($i = 0; $i -le 48; $i++) {
            $t = $i / 48.0
            $pts.Add((New-Object System.Drawing.PointF(($x0 + ($x1 - $x0) * $t),
                ($cy + $l * $gap + [Math]::Sin($t * 2 * [Math]::PI) * $amp))))
        }
        $g.DrawLines($pen, $pts.ToArray())
    }

    $g.Dispose()
    $bmp
}

# Build an .ico whose entries are PNG images (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) {
    $bmp = Render-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { $bmp.Save((Join-Path $assets 'AppIcon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $out.ToArray())
"Wrote $(Join-Path $assets 'AppIcon.ico') ($($sizes -join ', ') px)"
