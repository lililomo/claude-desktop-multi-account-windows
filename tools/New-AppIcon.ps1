# Draws the launcher's own file icon (assets\app.ico): two overlapping windows on a blue tile.
# It is an original drawing; the Claude logo is never shipped. At run time the launcher builds the
# taskbar/window icon from the Claude app installed on the user's own computer.
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'assets\app.ico'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = 2 * $r
  $p.AddArc($x, $y, $d, $d, 180, 90)
  $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
  $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
  $p.CloseFigure()
  $p
}

function New-Frame([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)

  $tile = New-RoundedPath 0 0 ($s - 1) ($s - 1) ($s * 0.22)
  $top = [System.Drawing.Color]::FromArgb(255, 74, 132, 240)
  $bottom = [System.Drawing.Color]::FromArgb(255, 37, 84, 196)
  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(0, $s)), $top, $bottom
  $g.FillPath($brush, $tile)

  # back window (translucent) and front window (white), offset diagonally
  $w = $s * 0.46; $h = $s * 0.38; $r = [Math]::Max(1, $s * 0.07)
  $back = New-RoundedPath ($s * 0.20) ($s * 0.21) $w $h $r
  $front = New-RoundedPath ($s * 0.34) ($s * 0.41) $w $h $r
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 255, 255, 255))), $back)
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $front)
  if ($s -ge 32) {
    # title bar line on the front window
    $bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 37, 84, 196))
    $g.FillRectangle($bar, [float]($s * 0.34 + $r), [float]($s * 0.41 + $h * 0.22), [float]($w - 2 * $r), [float]([Math]::Max(1, $s * 0.035)))
  }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
  , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($s in $sizes) { , (New-Frame $s) }
New-Item -ItemType Directory -Force (Split-Path $Out -Parent) | Out-Null
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
  $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([int16]1); $bw.Write([int16]32)
  $bw.Write([int32]$frames[$i].Length); $bw.Write([int32]$offset)
  $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Dispose()
"Icon written: $Out"
