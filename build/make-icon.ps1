param(
    [Parameter(Mandatory = $true)]
    [string]$Source,
    [string]$Destination
)

$ErrorActionPreference = 'Stop'

if (-not $Destination) {
    $scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Destination = Join-Path $scriptDirectory '..\src\AzureGameAssistant.UI\Assets\app-icon.ico'
}
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$image = [System.Drawing.Image]::FromFile((Resolve-Path $Source).Path)
$pngs = @()

try {
    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($image, 0, 0, $size, $size)
        }
        finally {
            $graphics.Dispose()
        }

        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        $pngs += ,$stream.ToArray()
        $stream.Dispose()
    }
}
finally {
    $image.Dispose()
}

$destinationPath = [System.IO.Path]::GetFullPath($Destination)
$directory = [System.IO.Path]::GetDirectoryName($destinationPath)
if (-not (Test-Path $directory)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}

$file = [System.IO.File]::Create($destinationPath)
$writer = New-Object System.IO.BinaryWriter $file
try {
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $size = $sizes[$i]
        $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$pngs[$i].Length)
        $writer.Write([UInt32]$offset)
        $offset += $pngs[$i].Length
    }

    foreach ($png in $pngs) {
        $writer.Write($png)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Host "Wrote $destinationPath"
