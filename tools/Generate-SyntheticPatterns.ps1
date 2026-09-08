# Generate-SyntheticPatterns.ps1
# Generates synthetic, desensitized test pattern images for Apple MVS 1011 sample capture.
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [int]$Width = 1920,
    [int]$Height = 1080
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $OutputDirectory) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $OutputDirectory = Join-Path $repoRoot 'artifacts\synthetic-patterns'
}

if (-not (Test-Path -LiteralPath $OutputDirectory)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

Add-Type -AssemblyName System.Drawing

function Save-Bitmap {
    param(
        [Drawing.Bitmap]$Bitmap,
        [string]$Path
    )
    try {
        $Bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
        Write-Host "Generated: $Path"
    }
    finally {
        $Bitmap.Dispose()
    }
}

function New-SolidColorBitmap {
    param(
        [Drawing.Color]$Color,
        [string]$FileName
    )
    $bmp = [Drawing.Bitmap]::new($Width, $Height)
    $g = [Drawing.Graphics]::FromImage($bmp)
    try {
        $g.Clear($Color)
    } finally {
        $g.Dispose()
    }
    Save-Bitmap -Bitmap $bmp -Path (Join-Path $OutputDirectory $FileName)
}

# 1. Solid Red: RGB(255, 0, 0) -> BT.601 Y=76, Cb=85, Cr=255
New-SolidColorBitmap -Color ([Drawing.Color]::FromArgb(255, 255, 0, 0)) -FileName 'solid-red.png'

# 2. Solid Green: RGB(0, 255, 0) -> BT.601 Y=150, Cb=44, Cr=21
New-SolidColorBitmap -Color ([Drawing.Color]::FromArgb(255, 0, 255, 0)) -FileName 'solid-green.png'

# 3. Solid Blue: RGB(0, 0, 255) -> BT.601 Y=29, Cb=255, Cr=107
New-SolidColorBitmap -Color ([Drawing.Color]::FromArgb(255, 0, 0, 255)) -FileName 'solid-blue.png'

# 4. Solid White: RGB(255, 255, 255) -> BT.601 Y=235, Cb=128, Cr=128
New-SolidColorBitmap -Color ([Drawing.Color]::FromArgb(255, 255, 255, 255)) -FileName 'solid-white.png'

# 5. Solid Black: RGB(0, 0, 0) -> BT.601 Y=16, Cb=128, Cr=128
New-SolidColorBitmap -Color ([Drawing.Color]::FromArgb(255, 0, 0, 0)) -FileName 'solid-black.png'

# 6. Checkerboard 16x16 (Macroblock level alternating pure black and white)
$cb16Bmp = [Drawing.Bitmap]::new($Width, $Height)
$g16 = [Drawing.Graphics]::FromImage($cb16Bmp)
try {
    $blackBrush = [Drawing.SolidBrush]::new([Drawing.Color]::Black)
    $whiteBrush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
    try {
        $cellSize = 16
        for ($y = 0; $y -lt $Height; $y += $cellSize) {
            $rowH = [Math]::Min($cellSize, $Height - $y)
            for ($x = 0; $x -lt $Width; $x += $cellSize) {
                $colW = [Math]::Min($cellSize, $Width - $x)
                $brush = if ((([int]($x / $cellSize)) + ([int]($y / $cellSize))) % 2 -eq 0) { $whiteBrush } else { $blackBrush }
                $g16.FillRectangle($brush, $x, $y, $colW, $rowH)
            }
        }
    } finally {
        $blackBrush.Dispose()
        $whiteBrush.Dispose()
    }
} finally {
    $g16.Dispose()
}
Save-Bitmap -Bitmap $cb16Bmp -Path (Join-Path $OutputDirectory 'checkerboard-16x16.png')

# 7. Checkerboard 8x8 (Block level alternating pure black and white - maximum AC frequency)
$cb8Bmp = [Drawing.Bitmap]::new($Width, $Height)
$g8 = [Drawing.Graphics]::FromImage($cb8Bmp)
try {
    $blackBrush = [Drawing.SolidBrush]::new([Drawing.Color]::Black)
    $whiteBrush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
    try {
        $cellSize = 8
        for ($y = 0; $y -lt $Height; $y += $cellSize) {
            $rowH = [Math]::Min($cellSize, $Height - $y)
            for ($x = 0; $x -lt $Width; $x += $cellSize) {
                $colW = [Math]::Min($cellSize, $Width - $x)
                $brush = if ((([int]($x / $cellSize)) + ([int]($y / $cellSize))) % 2 -eq 0) { $whiteBrush } else { $blackBrush }
                $g8.FillRectangle($brush, $x, $y, $colW, $rowH)
            }
        }
    } finally {
        $blackBrush.Dispose()
        $whiteBrush.Dispose()
    }
} finally {
    $g8.Dispose()
}
Save-Bitmap -Bitmap $cb8Bmp -Path (Join-Path $OutputDirectory 'checkerboard-8x8.png')

# 8. Grayscale Ramp: Top half 16 quantized steps, Bottom half smooth horizontal gradient
$rampBmp = [Drawing.Bitmap]::new($Width, $Height)
$gRamp = [Drawing.Graphics]::FromImage($rampBmp)
try {
    $halfH = [int]($Height / 2)
    # Top half: 16 discrete gray steps (0, 17, 34, ..., 255)
    $stepCount = 16
    $stepW = [double]$Width / $stepCount
    for ($i = 0; $i -lt $stepCount; $i++) {
        $val = [int]([Math]::Round($i * 255.0 / ($stepCount - 1)))
        $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, $val, $val, $val))
        try {
            $xStart = [int]($i * $stepW)
            $xEnd = [int](($i + 1) * $stepW)
            $gRamp.FillRectangle($brush, $xStart, 0, $xEnd - $xStart, $halfH)
        } finally {
            $brush.Dispose()
        }
    }
    # Bottom half: Smooth gradient 0 to 255
    $rect = [Drawing.Rectangle]::new(0, $halfH, $Width, $Height - $halfH)
    $gradBrush = [Drawing.Drawing2D.LinearGradientBrush]::new(
        $rect,
        [Drawing.Color]::FromArgb(255, 0, 0, 0),
        [Drawing.Color]::FromArgb(255, 255, 255, 255),
        [Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    try {
        $gRamp.FillRectangle($gradBrush, $rect)
    } finally {
        $gradBrush.Dispose()
    }
} finally {
    $gRamp.Dispose()
}
Save-Bitmap -Bitmap $rampBmp -Path (Join-Path $OutputDirectory 'grayscale-ramp.png')

# 9. Dense High-Contrast Text and Grid pattern
$textBmp = [Drawing.Bitmap]::new($Width, $Height)
$gText = [Drawing.Graphics]::FromImage($textBmp)
try {
    $gText.Clear([Drawing.Color]::Black)
    $gText.TextRenderingHint = [Drawing.Text.TextRenderingHint]::SingleBitPerPixelGridFit
    $font = [Drawing.Font]::new('Consolas', 14, [Drawing.FontStyle]::Regular)
    $textBrush = [Drawing.SolidBrush]::new([Drawing.Color]::White)
    $pen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(255, 120, 120, 120), 1)
    try {
        # Background fine grid lines every 32 pixels
        for ($x = 0; $x -lt $Width; $x += 32) {
            $gText.DrawLine($pen, $x, 0, $x, $Height)
        }
        for ($y = 0; $y -lt $Height; $y += 32) {
            $gText.DrawLine($pen, 0, $y, $Width, $y)
        }
        # High contrast diagnostic text
        $line = "MVS-1011 REVERSE-ENGINEERING | 0123456789 ABCDEFGHIJKLMNOPQRSTUVWXYZ | + - * / = # @ ! $ % ^ & *"
        for ($y = 48; $y -lt $Height - 48; $y += 32) {
            $gText.DrawString($line, $font, $textBrush, 48, $y)
        }
    } finally {
        $pen.Dispose()
        $font.Dispose()
        $textBrush.Dispose()
    }
} finally {
    $gText.Dispose()
}
Save-Bitmap -Bitmap $textBmp -Path (Join-Path $OutputDirectory 'text-grid-dense.png')

Write-Host "All 9 synthetic patterns generated successfully under: $OutputDirectory"
