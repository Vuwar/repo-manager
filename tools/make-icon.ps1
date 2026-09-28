# Regenerates src/RepoManager.App/app.ico (multi-size, PNG entries). Same design as Shell/Icons.cs.
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\src\RepoManager.App\app.ico'

function New-Rounded([System.Drawing.RectangleF]$r, [float]$radius) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $p.AddArc($r.X, $r.Y, $d, $d, 180, 90)
    $p.AddArc($r.Right - $d, $r.Y, $d, $d, 270, 90)
    $p.AddArc($r.Right - $d, $r.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($r.X, $r.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $brand = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x4f, 0x7c, 0xff))
    $g.FillPath($brand, (New-Rounded (New-Object System.Drawing.RectangleF 0.5, 0.5, ($s - 1), ($s - 1)) ($s * 0.22)))
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $w = $s * 0.56; $h = $s * 0.16; $x = ($s - $w) / 2
    $g.FillPath($white, (New-Rounded (New-Object System.Drawing.RectangleF $x, ($s * 0.27), $w, $h) ($h / 2)))
    $g.FillPath($white, (New-Rounded (New-Object System.Drawing.RectangleF $x, ($s * 0.53), $w, $h) ($h / 2)))
    $green = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x2e, 0xb8, 0x72))
    $g.FillEllipse($green, ($s * 0.64), ($s * 0.575), ($s * 0.1), ($s * 0.1))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Dispose()
Write-Host "wrote $out"
