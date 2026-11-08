# Rasterize assets/icon.svg into every plugin, Yak, favicon and GitHub image.
# The SVG must stay one rounded <rect> plus one <path> using only absolute M/H/V/Z.
Add-Type -AssemblyName System.Drawing

$root = (Resolve-Path -LiteralPath "$PSScriptRoot/..").Path
[xml]$svg = Get-Content -LiteralPath "$root/assets/icon.svg"
$unit = [float]($svg.svg.viewBox -split '\s+')[2]
$cornerRadius = [float]$svg.svg.rect.rx
$tileColor = [System.Drawing.ColorTranslator]::FromHtml($svg.svg.rect.fill)
$markData = $svg.svg.path.d

if ($markData -notmatch '^[MHVZ\d\s.]+$') {
    throw "assets/icon.svg path may only use absolute M/H/V/Z commands: $markData"
}

function New-MarkPath {
    $x = 0.0
    $y = 0.0
    $points = [System.Collections.Generic.List[System.Drawing.PointF]]::new()
    foreach ($match in [regex]::Matches($markData, '([MHV])([\d.\s]+)')) {
        $values = @($match.Groups[2].Value.Trim() -split '\s+' | ForEach-Object { [float]$_ })
        switch ($match.Groups[1].Value) {
            'M' { $x = $values[0]; $y = $values[1] }
            'H' { $x = $values[0] }
            'V' { $y = $values[0] }
        }
        $points.Add([System.Drawing.PointF]::new($x, $y))
    }
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddPolygon($points.ToArray())
    return , $path
}

function New-TilePath {
    $d = 2 * $cornerRadius
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($unit - $d, 0, $d, $d, 270, 90)
    $path.AddArc($unit - $d, $unit - $d, $d, $d, 0, 90)
    $path.AddArc(0, $unit - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return , $path
}

function New-Canvas([int]$width, [int]$height) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    $graphics.PixelOffsetMode = 'HighQuality'
    return @{ Bitmap = $bitmap; Graphics = $graphics }
}

# Returns PNG bytes for the icon. -FullBleed fills the corners for platforms that mask icons themselves.
function Get-IconPng([int]$size, [switch]$FullBleed) {
    $canvas = New-Canvas $size $size
    $tileBrush = [System.Drawing.SolidBrush]::new($tileColor)
    $tile = New-TilePath
    $mark = New-MarkPath
    try {
        if ($FullBleed) { $canvas.Graphics.Clear($tileColor) }
        $canvas.Graphics.ScaleTransform($size / $unit, $size / $unit)
        if (-not $FullBleed) { $canvas.Graphics.FillPath($tileBrush, $tile) }
        $canvas.Graphics.FillPath([System.Drawing.Brushes]::White, $mark)
        $stream = [System.IO.MemoryStream]::new()
        $canvas.Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return , $stream.ToArray()
    }
    finally {
        $mark.Dispose(); $tile.Dispose(); $tileBrush.Dispose()
        $canvas.Graphics.Dispose(); $canvas.Bitmap.Dispose()
    }
}

# PNG-compressed .ico: one directory entry per size, then the PNG payloads.
function Write-Ico([string]$destination, [int[]]$sizes) {
    $images = @($sizes | ForEach-Object { , (Get-IconPng $_) })
    $stream = [System.IO.File]::Create($destination)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Length)
        $offset = 6 + 16 * $sizes.Length
        for ($i = 0; $i -lt $sizes.Length; $i++) {
            $writer.Write([byte]$sizes[$i]); $writer.Write([byte]$sizes[$i])
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($image in $images) { $writer.Write([byte[]]$image) }
    }
    finally {
        $writer.Dispose(); $stream.Dispose()
    }
}

# GitHub's repository social preview: 1280x640 with content clear of a 40pt border.
function Write-SocialPreview([string]$destination) {
    $width = 1280
    $height = 640
    $markScale = 13.0
    $markWidth = 18 * $markScale
    $markHeight = 14 * $markScale
    $gap = 56

    $canvas = New-Canvas $width $height
    $titleFont = [System.Drawing.Font]::new('Segoe UI', 92, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $taglineFont = [System.Drawing.Font]::new('Segoe UI', 34, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $taglineBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#aab2ac'))
    $format = [System.Drawing.StringFormat]::GenericTypographic
    $mark = New-MarkPath
    $matrix = [System.Drawing.Drawing2D.Matrix]::new()
    try {
        $title = 'HopperFormat'
        $tagline = 'Left-to-right dataflow layouts for Grasshopper'
        $graphics = $canvas.Graphics
        $graphics.Clear($tileColor)
        $graphics.TextRenderingHint = 'AntiAlias'

        $textWidth = [Math]::Max(
            $graphics.MeasureString($title, $titleFont, [System.Drawing.PointF]::Empty, $format).Width,
            $graphics.MeasureString($tagline, $taglineFont, [System.Drawing.PointF]::Empty, $format).Width)
        $left = ($width - ($markWidth + $gap + $textWidth)) / 2
        $top = ($height - $markHeight) / 2

        $matrix.Translate($left - 3 * $markScale, $top - 5 * $markScale)
        $matrix.Scale($markScale, $markScale)
        $mark.Transform($matrix)
        $graphics.FillPath([System.Drawing.Brushes]::White, $mark)

        $textLeft = $left + $markWidth + $gap
        $graphics.DrawString($title, $titleFont, [System.Drawing.Brushes]::White, $textLeft, $top + 4, $format)
        $graphics.DrawString($tagline, $taglineFont, $taglineBrush, $textLeft + 2, $top + 132, $format)
        $canvas.Bitmap.Save($destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $matrix.Dispose(); $mark.Dispose(); $format.Dispose(); $taglineBrush.Dispose()
        $taglineFont.Dispose(); $titleFont.Dispose()
        $canvas.Graphics.Dispose(); $canvas.Bitmap.Dispose()
    }
}

[System.IO.File]::WriteAllBytes("$root/icon.png", (Get-IconPng 24))
[System.IO.File]::WriteAllBytes("$root/yak/icon.png", (Get-IconPng 256))
[System.IO.File]::WriteAllBytes("$root/assets/icon.png", (Get-IconPng 512))
[System.IO.File]::WriteAllBytes("$root/assets/apple-touch-icon.png", (Get-IconPng 180 -FullBleed))
Write-Ico "$root/assets/favicon.ico" @(16, 32, 48)
Write-SocialPreview "$root/assets/social-preview.png"
