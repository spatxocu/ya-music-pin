# Builds dist\YaMiniPlayer.exe using the C# compiler that ships with Windows (no SDK needed),
# then packs dist\YaMiniPlayer.zip: the exe, the readme and the install/uninstall scripts.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$md = Join-Path $env:WINDIR 'System32\WinMetadata'
$icon = Join-Path $root 'src\app.ico'
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# App icon: black vinyl record with a yellow label on a yellow tile, stored as PNG frames inside an .ico
if (-not (Test-Path $icon)) {
    Add-Type -AssemblyName System.Drawing
    $frames = foreach ($size in 16, 32, 48, 256) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.SmoothingMode = 'AntiAlias'
        $g.Clear([System.Drawing.Color]::Transparent)
        $yellow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 204, 0))
        $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 22, 22, 24))
        # Rounded tile
        $r = $size * 0.44
        $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
        $tile.AddArc(0, 0, $r, $r, 180, 90)
        $tile.AddArc($size - $r, 0, $r, $r, 270, 90)
        $tile.AddArc($size - $r, $size - $r, $r, $r, 0, 90)
        $tile.AddArc(0, $size - $r, $r, $r, 90, 90)
        $tile.CloseFigure()
        $g.FillPath($yellow, $tile)
        # Record, grooves (too fine to draw on the small frames), label, spindle hole
        $disc = { param($brush, $share) $d = $size * $share; $g.FillEllipse($brush, ($size - $d) / 2, ($size - $d) / 2, $d, $d) }
        & $disc $dark 0.80
        if ($size -ge 48) {
            $groove = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 255, 255)), ($size / 128)
            foreach ($share in 0.70, 0.60, 0.50) { $d = $size * $share; $g.DrawEllipse($groove, ($size - $d) / 2, ($size - $d) / 2, $d, $d) }
        }
        & $disc $yellow 0.34
        & $disc $dark 0.09
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        if ($size -eq 256) {
            $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        } else {
            # Small frames as classic 32-bit bitmaps, which every icon reader understands:
            # header (height doubled), BGRA rows bottom-up, then an empty 1-bit mask
            $bw = New-Object System.IO.BinaryWriter $ms
            $bw.Write([uint32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
            $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0); $bw.Write([uint32]($size * $size * 4))
            $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
            for ($y = $size - 1; $y -ge 0; $y--) {
                for ($x = 0; $x -lt $size; $x++) { $bw.Write([int32]$bmp.GetPixel($x, $y).ToArgb()) }
            }
            $bw.Write((New-Object byte[] ([int]([math]::Ceiling($size / 32) * 4 * $size))))
        }
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

# Text files go out with Windows line endings; cmd.exe misreads batch files that lack them
foreach ($file in 'README.txt', 'installer\Install.cmd', 'installer\Uninstall.cmd') {
    $text = [System.IO.File]::ReadAllText((Join-Path $root $file)) -replace "`r?`n", "`r`n"
    $encoding = if ($file -like '*.cmd') { [System.Text.Encoding]::ASCII } else { New-Object System.Text.UTF8Encoding $true }
    [System.IO.File]::WriteAllText((Join-Path $dist (Split-Path $file -Leaf)), $text, $encoding)
}
$zip = Join-Path $dist 'YaMiniPlayer.zip'
Compress-Archive -Force -DestinationPath $zip -Path `
    "$dist\YaMiniPlayer.exe", "$dist\README.txt", "$dist\Install.cmd", "$dist\Uninstall.cmd"
"Packed $zip"
