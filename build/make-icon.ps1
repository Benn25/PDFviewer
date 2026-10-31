# Draws the app icon: build/icon.ico (every size Windows asks for) and build/icon.png (256 px preview).
#   powershell -ExecutionPolicy Bypass -File build\make-icon.ps1
Add-Type -AssemblyName System.Drawing

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }
function P([float]$x, [float]$y) { New-Object System.Drawing.PointF $x, $y }
function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
  $p = New-Object System.Drawing.Drawing2D.GraphicsPath
  $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
  $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
  $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
  $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
  $p.CloseFigure()
  $p
}

# Blue tile, white page, navy "PDF" tag. Drawn in a 256 px space and scaled.
function Render([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $g.ScaleTransform($size / 256, $size / 256)

  $tile = New-Object System.Drawing.Drawing2D.LinearGradientBrush (P 0 8), (P 0 248), (C "#5b9bff"), (C "#2c62d6")
  $g.FillPath($tile, (RoundRect 8 8 240 240 52))
  $page = [System.Drawing.PointF[]]@((P 66 34), (P 156 34), (P 196 74), (P 196 222), (P 66 222))
  $g.FillPolygon((New-Object System.Drawing.SolidBrush (C "#ffffff")), $page)
  $fold = [System.Drawing.PointF[]]@((P 156 34), (P 156 74), (P 196 74))
  $g.FillPolygon((New-Object System.Drawing.SolidBrush (C "#b9d0ff")), $fold)
  $g.FillPath((New-Object System.Drawing.SolidBrush (C "#16388f")), (RoundRect 34 122 168 70 13))
  # Below 32 px the letters only blur, so the tag stays a plain band.
  if ($size -ge 32) {
    $font = New-Object System.Drawing.Font "Segoe UI", 50, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString("PDF", $font, (New-Object System.Drawing.SolidBrush (C "#ffffff")), (New-Object System.Drawing.RectangleF 34, 124, 168, 70), $fmt)
  }
  $g.Dispose()
  $bmp
}

function PngBytes($bmp) {
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  , $ms.ToArray()
}

# Classic icon bitmap: header, bottom-up BGRA pixels, empty AND mask (alpha does the masking).
function DibBytes($bmp) {
  $w = $bmp.Width; $h = $bmp.Height
  $lock = $bmp.LockBits((New-Object System.Drawing.Rectangle 0, 0, $w, $h),
    [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $pixels = New-Object byte[] ($lock.Stride * $h)
  [System.Runtime.InteropServices.Marshal]::Copy($lock.Scan0, $pixels, 0, $pixels.Length)
  $bmp.UnlockBits($lock)
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter $ms
  $bw.Write([UInt32]40); $bw.Write([Int32]$w); $bw.Write([Int32](2 * $h))
  $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0); $bw.Write([UInt32]($w * $h * 4))
  $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
  for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $lock.Stride, $w * 4) }
  $bw.Write((New-Object byte[] ([Math]::Ceiling($w / 32) * 4 * $h)))
  $bw.Flush()
  , $ms.ToArray()
}

# .ico in the usual layout: bitmaps for the small sizes, PNG for 256.
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
  $bmp = Render $s
  $images.Add($(if ($s -eq 256) { PngBytes $bmp } else { DibBytes $bmp }))
}

$ico = Join-Path $PSScriptRoot "icon.ico"
$w = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($ico))
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $dim = [byte]($sizes[$i] % 256) # 256 is stored as 0
  $w.Write($dim); $w.Write($dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([UInt16]1); $w.Write([UInt16]32)
  $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
  $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Close()

$png = Join-Path $PSScriptRoot "icon.png"
(Render 256).Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
"Wrote $ico and $png"
