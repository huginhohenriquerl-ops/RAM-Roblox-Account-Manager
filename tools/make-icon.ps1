# Renders Clawd into Assets/ram.ico (PNG-compressed ICO, 16–256 px).
param([string]$Out = (Join-Path $PSScriptRoot "..\Assets\ram.ico"))
Add-Type -AssemblyName System.Drawing

function Render-Clawd([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'None'
    $g.PixelOffsetMode = 'Half'
    $g.Clear([System.Drawing.Color]::Transparent)
    $body = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0xD9, 0x77, 0x57))
    $eye = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x14, 0x14, 0x13))
    # sprite is 18 x 10 units (5 rows of 2 units); center it in the square
    $u = $size / 19.0
    $ox = ($size - 18 * $u) / 2
    $oy = ($size - 10 * $u) / 2
    $rect = { param($x, $y, $w, $h, $b) $g.FillRectangle($b, [single]($ox + $x * $u), [single]($oy + $y * 2 * $u), [single]($w * $u), [single]($h * 2 * $u)) }
    & $rect 3 0 12 4 $body
    & $rect 1 2 16 1 $body
    foreach ($lx in 4, 6, 11, 13) { & $rect $lx 4 1 1 $body }
    & $rect 5 1 1 1 $eye
    & $rect 12 1 1 1 $eye
    $g.Dispose()
    return $bmp
}

$sizes = 256, 128, 64, 48, 32, 24, 16
$master = Render-Clawd 512
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = if ($s -ge 64) { 'NearestNeighbor' } else { 'HighQualityBicubic' }
    $g.PixelOffsetMode = 'Half'
    $g.DrawImage($master, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write($d) }
$w.Close()
"wrote $Out"

# Installer splash: Clawd on the Claude cream background.
$splashPath = Join-Path (Split-Path $Out) "splash.png"
$splash = New-Object System.Drawing.Bitmap 480, 300
$g = [System.Drawing.Graphics]::FromImage($splash)
$g.Clear([System.Drawing.Color]::FromArgb(255, 0xFA, 0xF9, 0xF5))
$g.InterpolationMode = 'NearestNeighbor'; $g.PixelOffsetMode = 'Half'
$g.DrawImage($master, 150, 30, 180, 180)
$g.TextRenderingHint = 'AntiAliasGridFit'
$font = New-Object System.Drawing.Font "Georgia", 26
$fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'
$g.DrawString("R.A.M", $font, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x14, 0x14, 0x13))), (New-Object System.Drawing.RectangleF 0, 205, 480, 40), $fmt)
$small = New-Object System.Drawing.Font "Segoe UI", 10
$g.DrawString("Roblox Account Manager", $small, (New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x73, 0x72, 0x6C))), (New-Object System.Drawing.RectangleF 0, 248, 480, 24), $fmt)
$g.Dispose()
$splash.Save($splashPath, [System.Drawing.Imaging.ImageFormat]::Png)
"wrote $splashPath"
