param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePng,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$source = [Drawing.Bitmap]::FromFile($SourcePng)
try {
    $minX = $source.Width
    $minY = $source.Height
    $maxX = -1
    $maxY = -1
    for ($y = 0; $y -lt $source.Height; $y++) {
        for ($x = 0; $x -lt $source.Width; $x++) {
            if ($source.GetPixel($x, $y).A -le 16) { continue }
            $minX = [Math]::Min($minX, $x)
            $minY = [Math]::Min($minY, $y)
            $maxX = [Math]::Max($maxX, $x)
            $maxY = [Math]::Max($maxY, $y)
        }
    }

    if ($maxX -lt $minX) { throw 'Logo image is entirely transparent.' }
    $span = [Math]::Max($maxX - $minX + 1, $maxY - $minY + 1)
    $padding = [int][Math]::Ceiling($span * 0.045)
    $cropSize = $span + 2 * $padding
    $cropX = $minX - $padding - [int][Math]::Floor(($span - ($maxX - $minX + 1)) / 2)
    $cropY = $minY - $padding - [int][Math]::Floor(($span - ($maxY - $minY + 1)) / 2)

    $logo = [Drawing.Bitmap]::new(512, 512, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [Drawing.Graphics]::FromImage($logo)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage(
                $source,
                [Drawing.Rectangle]::new(0, 0, 512, 512),
                [Drawing.Rectangle]::new($cropX, $cropY, $cropSize, $cropSize),
                [Drawing.GraphicsUnit]::Pixel)
        }
        finally { $graphics.Dispose() }

        $logoPath = Join-Path $OutputDirectory 'logo.png'
        $logo.Save($logoPath, [Drawing.Imaging.ImageFormat]::Png)

        $sizes = @(16, 24, 32, 48, 64, 128, 256)
        $images = foreach ($size in $sizes) {
            $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $graphics = [Drawing.Graphics]::FromImage($bitmap)
                try {
                    $graphics.Clear([Drawing.Color]::Transparent)
                    $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::HighQuality
                    $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                    $graphics.DrawImage($logo, 0, 0, $size, $size)
                }
                finally { $graphics.Dispose() }

                $memory = [IO.MemoryStream]::new()
                try {
                    $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
                    ,$memory.ToArray()
                }
                finally { $memory.Dispose() }
            }
            finally { $bitmap.Dispose() }
        }

        $iconPath = Join-Path $OutputDirectory 'TokenMonitor.ico'
        $stream = [IO.File]::Create($iconPath)
        try {
            $writer = [IO.BinaryWriter]::new($stream)
            try {
                $writer.Write([uint16]0)
                $writer.Write([uint16]1)
                $writer.Write([uint16]$sizes.Count)
                $offset = 6 + 16 * $sizes.Count
                for ($i = 0; $i -lt $sizes.Count; $i++) {
                    $writer.Write([byte]($sizes[$i] % 256))
                    $writer.Write([byte]($sizes[$i] % 256))
                    $writer.Write([byte]0)
                    $writer.Write([byte]0)
                    $writer.Write([uint16]1)
                    $writer.Write([uint16]32)
                    $writer.Write([uint32]$images[$i].Length)
                    $writer.Write([uint32]$offset)
                    $offset += $images[$i].Length
                }
                foreach ($image in $images) { $writer.Write([byte[]]$image) }
            }
            finally { $writer.Dispose() }
        }
        finally { $stream.Dispose() }

        Write-Output $logoPath
        Write-Output $iconPath
    }
    finally { $logo.Dispose() }
}
finally { $source.Dispose() }
