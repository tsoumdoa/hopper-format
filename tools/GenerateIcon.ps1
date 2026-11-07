# Rasterize the simple assets/icon.svg design with supersampling.
Add-Type -AssemblyName System.Drawing
[xml]$svg = Get-Content -LiteralPath "$PSScriptRoot/../assets/icon.svg"
$bitmap = [System.Drawing.Bitmap]::new(256, 256)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = 'AntiAlias'
$graphics.ScaleTransform(4, 4)
$path = [System.Drawing.Drawing2D.GraphicsPath]::new()
$radius = [float]$svg.svg.rect.rx
$diameter = 2 * $radius
$path.AddArc(0, 0, $diameter, $diameter, 180, 90)
$path.AddArc(64-$diameter, 0, $diameter, $diameter, 270, 90)
$path.AddArc(64-$diameter, 64-$diameter, $diameter, $diameter, 0, 90)
$path.AddArc(0, 64-$diameter, $diameter, $diameter, 90, 90)
$path.CloseFigure()
$brush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($svg.svg.rect.fill))
$graphics.FillPath($brush, $path)
$font = [System.Drawing.Font]::new('Segoe UI', [float]$svg.svg.text.'font-size', [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$format = [System.Drawing.StringFormat]::new()
$format.Alignment = 'Center'
$format.LineAlignment = 'Center'
$graphics.DrawString($svg.svg.text.InnerText, $font, [System.Drawing.Brushes]::White, [System.Drawing.RectangleF]::new(0, 0, 64, 64), $format)
$small = [System.Drawing.Bitmap]::new(24, 24)
$output = [System.Drawing.Graphics]::FromImage($small)
$output.InterpolationMode = 'HighQualityBicubic'
$output.DrawImage($bitmap, 0, 0, 24, 24)
$small.Save("$PSScriptRoot/../icon.png", [System.Drawing.Imaging.ImageFormat]::Png)
Copy-Item -LiteralPath "$PSScriptRoot/../icon.png" -Destination "$PSScriptRoot/../yak/icon.png"
$output.Dispose()
$small.Dispose()
$format.Dispose()
$font.Dispose()
$brush.Dispose()
$path.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
