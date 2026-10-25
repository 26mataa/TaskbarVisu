# Génère src/TaskbarVisu/Assets/TaskbarVisu.ico (16, 24, 32, 48, 64, 256 px, entrées PNG).
param([string]$Out = (Join-Path $PSScriptRoot '..\src\TaskbarVisu\Assets\TaskbarVisu.ico'))

Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Fond arrondi sombre.
    $r = [Math]::Max(2, $size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d - 1, $size - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 24, 26, 38))
    $g.FillPath($bg, $path)

    # Barres de spectre : bleu -> rose.
    $heights = @(0.35, 0.62, 0.86, 0.55, 0.74, 0.42)
    $n = $heights.Count
    $margin = $size * 0.16
    $gap = [Math]::Max(1, $size * 0.04)
    $bw = ($size - 2 * $margin - ($n - 1) * $gap) / $n
    $base = $size - $margin
    $c0 = [System.Drawing.Color]::FromArgb(255, 127, 178, 255)
    $c1 = [System.Drawing.Color]::FromArgb(255, 255, 92, 138)
    for ($i = 0; $i -lt $n; $i++) {
        $t = $i / ($n - 1)
        $col = [System.Drawing.Color]::FromArgb(255,
            [int]($c0.R + ($c1.R - $c0.R) * $t),
            [int]($c0.G + ($c1.G - $c0.G) * $t),
            [int]($c0.B + ($c1.B - $c0.B) * $t))
        $h = ($size - 2 * $margin) * $heights[$i]
        $x = $margin + $i * ($bw + $gap)
        $brush = New-Object System.Drawing.SolidBrush $col
        $g.FillRectangle($brush, [single]$x, [single]($base - $h), [single]$bw, [single]$h)
        $brush.Dispose()
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = @(16, 24, 32, 48, 64, 256)
$images = @($sizes | ForEach-Object { ,(New-IconPng $_) })

$ico = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()

$dir = Split-Path $Out
New-Item -ItemType Directory -Force $dir | Out-Null
$target = Join-Path (Resolve-Path $dir).Path (Split-Path $Out -Leaf)
[System.IO.File]::WriteAllBytes($target, $ico.ToArray())
Write-Output "Icone ecrite : $target"
