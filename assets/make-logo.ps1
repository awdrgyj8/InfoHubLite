# Redraws the logo (same geometry as logo.svg) at every icon size and packs InfoHubLite.ico.
# Each size is drawn as vectors, not scaled down from a big bitmap, so the 16 px tray icon stays crisp.
#   powershell -File make-logo.ps1
Add-Type -AssemblyName System.Drawing
$dir = $PSScriptRoot
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function Rounded([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

# One wave layer: two cubic segments across the tile (the SVG's "C ... S ..."), closed along the bottom.
# $pts = 14 numbers: start, c1, c2, mid, c3, c4, end.
function Wave($g, [string]$color, [single[]]$pts) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddBezier($pts[0], $pts[1], $pts[2], $pts[3], $pts[4], $pts[5], $pts[6], $pts[7])
    $p.AddBezier($pts[6], $pts[7], $pts[8], $pts[9], $pts[10], $pts[11], $pts[12], $pts[13])
    $p.AddLine(120, $pts[13], 120, 120); $p.AddLine(120, 120, 0, 120); $p.CloseFigure()
    $b = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($color))
    $g.FillPath($b, $p); $b.Dispose(); $p.Dispose()
}

function Dot($g, [string]$color, [single]$cx, [single]$cy, [single]$r) {
    $b = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($color))
    $g.FillEllipse($b, $cx - $r, $cy - $r, $r * 2, $r * 2); $b.Dispose()
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $small = $size -le 24
    # The SVG leaves a 6/120 margin around the tile. Small sizes cannot afford it, so the tile fills almost the whole icon.
    $margin = if ($small) { 0.5 } else { $size * 0.04 }
    $s = ($size - 2 * $margin) / 108.0
    $g.TranslateTransform($margin - 6 * $s, $margin - 6 * $s); $g.ScaleTransform($s, $s)

    $tile = Rounded 6 6 108 108 30
    $g.SetClip($tile)
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml('#0F172A'))
    $g.FillRectangle($bg, 0, 0, 120, 120); $bg.Dispose()
    if ($small) {
        # At tray size three wave layers turn to mush: keep two, and one larger dot.
        Wave $g '#2DD4BF' @(0, 66, 20, 56, 42, 56, 60, 66, 78, 76, 98, 78, 120, 64)
        Wave $g '#99F6E4' @(0, 90, 24, 82, 44, 94, 66, 88, 88, 82, 102, 82, 120, 90)
        Dot $g '#FB923C' 80 34 11
    } else {
        Wave $g '#155E68' @(0, 60, 22, 48, 40, 72, 62, 60, 84, 48, 100, 48, 120, 58)
        Wave $g '#2DD4BF' @(0, 74, 20, 64, 42, 64, 60, 74, 78, 84, 98, 86, 120, 72)
        Wave $g '#99F6E4' @(0, 94, 24, 86, 44, 98, 66, 92, 88, 86, 102, 86, 120, 94)
        Dot $g '#FB923C' 82 36 8
        Dot $g '#FDBA74' 64 47 3.5
    }
    $g.ResetClip()
    # hairline edge so the dark tile keeps its shape on a dark taskbar
    $edge = Rounded 7 7 106 106 29
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml($(if ($small) { '#3B4C6B' } else { '#2B3A55' }))), $(if ($small) { 5 } else { 2 })
    $g.DrawPath($pen, $edge)
    $pen.Dispose(); $edge.Dispose(); $tile.Dispose(); $g.Dispose()
    $bmp
}

function PngBytes($bmp) { $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $ms.ToArray() }

# Classic 32-bit DIB icon image (what every Windows API understands); only the 256 px entry is stored as PNG.
function DibBytes($bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $bd = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h), 'ReadOnly', [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $ms = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]($w * $h * 4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    $row = New-Object byte[] ($w * 4)
    for ($y = $h - 1; $y -ge 0; $y--) { [Runtime.InteropServices.Marshal]::Copy([IntPtr]($bd.Scan0.ToInt64() + $y * $bd.Stride), $row, 0, $row.Length); $bw.Write($row) }
    $bmp.UnlockBits($bd)
    $bw.Write((New-Object byte[] ([int]([math]::Floor(($w + 31) / 32)) * 4 * $h))) # AND mask: all zero, alpha does the work
    $bw.Flush(); $ms.ToArray()
}

$images = @()
foreach ($size in $sizes) {
    $bmp = Draw $size
    if ($size -in 32, 256) { $bmp.Save((Join-Path $dir "logo-$size.png")) }
    $images += , @($size, $(if ($size -eq 256) { PngBytes $bmp } else { DibBytes $bmp }))
    $bmp.Dispose()
}

$ico = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ico
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($im in $images) {
    $bw.Write([byte]($im[0] % 256)); $bw.Write([byte]($im[0] % 256)); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$im[1].Length); $bw.Write([int]$offset)
    $offset += $im[1].Length
}
foreach ($im in $images) { $bw.Write([byte[]]$im[1]) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $dir 'InfoHubLite.ico'), $ico.ToArray())
"wrote InfoHubLite.ico ($($ico.Length) bytes, sizes: $($sizes -join ', ')), logo-32.png, logo-256.png"
