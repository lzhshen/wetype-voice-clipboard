[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$iconRoot = Split-Path -Parent $PSScriptRoot
$iconSource = [Drawing.Bitmap]::FromFile((Join-Path $iconRoot 'assets\app-icon.png'))
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$iconFrames = [Collections.Generic.List[byte[]]]::new()
try {
    if ($iconSource.Width -ne $iconSource.Height -or $iconSource.GetPixel(0, 0).A -ne 0) {
        throw 'The source icon must be square with a transparent background.'
    }
    foreach ($iconSize in $iconSizes) {
        $iconBitmap = [Drawing.Bitmap]::new($iconSize, $iconSize, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $iconGraphics = [Drawing.Graphics]::FromImage($iconBitmap)
        $iconBuffer = [IO.MemoryStream]::new()
        try {
            $iconGraphics.Clear([Drawing.Color]::Transparent)
            $iconGraphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $iconGraphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $iconGraphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $iconGraphics.DrawImage($iconSource, [Drawing.Rectangle]::new(0, 0, $iconSize, $iconSize))
            if ($iconSize -eq 256) {
                # Explorer supports the standard compressed 256px frame.
                $iconBitmap.Save($iconBuffer, [Drawing.Imaging.ImageFormat]::Png)
                $iconFrames.Add($iconBuffer.ToArray())
                continue
            }
            # Use classic 32-bit DIB frames: .NET Framework's Icon rendering path
            # does not reliably render PNG-compressed small frames.
            $iconFrameWriter = [IO.BinaryWriter]::new($iconBuffer, [Text.Encoding]::UTF8, $true)
            try {
                $iconMaskStride = [int]([Math]::Ceiling($iconSize / 32.0) * 4)
                $iconMask = [byte[]]::new($iconMaskStride * $iconSize)
                $iconFrameWriter.Write([uint32]40)
                $iconFrameWriter.Write([int]$iconSize)
                $iconFrameWriter.Write([int]($iconSize * 2))
                $iconFrameWriter.Write([uint16]1)
                $iconFrameWriter.Write([uint16]32)
                $iconFrameWriter.Write([uint32]0)
                $iconFrameWriter.Write([uint32]($iconSize * $iconSize * 4 + $iconMask.Length))
                foreach ($iconUnused in 1..4) { $iconFrameWriter.Write([uint32]0) }
                for ($iconRow = $iconSize - 1; $iconRow -ge 0; $iconRow--) {
                    for ($iconColumn = 0; $iconColumn -lt $iconSize; $iconColumn++) {
                        $iconPixel = $iconBitmap.GetPixel($iconColumn, $iconRow)
                        $iconFrameWriter.Write([byte]$iconPixel.B)
                        $iconFrameWriter.Write([byte]$iconPixel.G)
                        $iconFrameWriter.Write([byte]$iconPixel.R)
                        $iconFrameWriter.Write([byte]$iconPixel.A)
                        if ($iconPixel.A -eq 0) {
                            $iconMaskIndex = ($iconSize - 1 - $iconRow) * $iconMaskStride + [int][Math]::Floor($iconColumn / 8.0)
                            $iconMask[$iconMaskIndex] = $iconMask[$iconMaskIndex] -bor (128 -shr ($iconColumn % 8))
                        }
                    }
                }
                $iconFrameWriter.Write($iconMask)
            } finally { $iconFrameWriter.Dispose() }
            $iconFrames.Add($iconBuffer.ToArray())
        } finally {
            $iconGraphics.Dispose()
            $iconBitmap.Dispose()
            $iconBuffer.Dispose()
        }
    }
} finally { $iconSource.Dispose() }

# 32-bit ICO frames retain alpha at every Windows display scale.
$iconOutput = [IO.File]::Create((Join-Path $iconRoot 'assets\app.ico'))
$iconWriter = [IO.BinaryWriter]::new($iconOutput)
try {
    $iconWriter.Write([uint16]0)
    $iconWriter.Write([uint16]1)
    $iconWriter.Write([uint16]$iconSizes.Count)
    $iconOffset = 6 + 16 * $iconSizes.Count
    for ($iconIndex = 0; $iconIndex -lt $iconSizes.Count; $iconIndex++) {
        $iconDimension = $iconSizes[$iconIndex] % 256
        $iconWriter.Write([byte]$iconDimension)
        $iconWriter.Write([byte]$iconDimension)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([byte]0)
        $iconWriter.Write([uint16]1)
        $iconWriter.Write([uint16]32)
        $iconWriter.Write([uint32]$iconFrames[$iconIndex].Length)
        $iconWriter.Write([uint32]$iconOffset)
        $iconOffset += $iconFrames[$iconIndex].Length
    }
    foreach ($iconFrame in $iconFrames) { $iconWriter.Write([byte[]]$iconFrame) }
} finally { $iconWriter.Dispose() }
Write-Output 'Built the application and tray icon (16 through 256 pixels).'
