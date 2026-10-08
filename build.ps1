# Builds dist\YaMiniPlayer.exe using the C# compiler that ships with Windows (no SDK needed).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fx = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$md = 'C:\Windows\System32\WinMetadata'
$icon = Join-Path $root 'src\app.ico'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# App icon: yellow disc with a play triangle, stored as PNG frames inside an .ico
if (-not (Test-Path $icon)) {
    Add-Type -AssemblyName System.Drawing
    $frames = foreach ($size in 16, 32, 48, 256) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)
        $yellow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 204, 0))
        $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 22, 24))
        $g.FillEllipse($yellow, 0.5, 0.5, $size - 1, $size - 1)
        $pts = [System.Drawing.PointF[]]@(
            (New-Object System.Drawing.PointF ($size * 0.40), ($size * 0.29)),
            (New-Object System.Drawing.PointF ($size * 0.73), ($size * 0.50)),
            (New-Object System.Drawing.PointF ($size * 0.40), ($size * 0.71)))
        $g.FillPolygon($dark, $pts)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        , @($size, $ms.ToArray())
    }
    $out = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $out
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($f in $frames) {
        $dim = if ($f[0] -eq 256) { 0 } else { $f[0] }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$f[1].Length); $w.Write([uint32]$offset)
        $offset += $f[1].Length
    }
    foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
    [System.IO.File]::WriteAllBytes($icon, $out.ToArray())
}

& "$fx\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 /platform:anycpu `
    "/out:$dist\YaMiniPlayer.exe" "/win32icon:$icon" `
    "/resource:$root\src\Player.xaml,Player.xaml" `
    "/r:$fx\WPF\PresentationFramework.dll" "/r:$fx\WPF\PresentationCore.dll" "/r:$fx\WPF\WindowsBase.dll" `
    "/r:$fx\System.Xaml.dll" "/r:$fx\System.Core.dll" "/r:$fx\Microsoft.CSharp.dll" `
    "/r:$fx\System.Runtime.WindowsRuntime.dll" `
    "/r:$fx\System.Runtime.dll" "/r:$fx\System.Runtime.InteropServices.WindowsRuntime.dll" `
    "/r:$fx\System.Threading.Tasks.dll" `
    "/r:$md\Windows.Media.winmd" "/r:$md\Windows.Foundation.winmd" "/r:$md\Windows.Storage.winmd" `
    "$root\src\Player.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
"Built $dist\YaMiniPlayer.exe"
