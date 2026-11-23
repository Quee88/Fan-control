# Vygeneruje ikonu aplikace (src/FanControl/Assets/fan.ico) – čtyřlistý větrák na modrém pozadí.
# Spuštění: powershell -ExecutionPolicy Bypass -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\FanControl\Assets\fan.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-FanPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Zaoblený čtverec jako pozadí
    $r = [single]($size * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $s = [single]($size - 1)
    $path.AddArc(0, 0, $r, $r, 180, 90)
    $path.AddArc($s - $r, 0, $r, $r, 270, 90)
    $path.AddArc($s - $r, $s - $r, $r, $r, 0, 90)
    $path.AddArc(0, $s - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF $size, $size), ([System.Drawing.Color]::FromArgb(255, 0, 120, 212)), ([System.Drawing.Color]::FromArgb(255, 0, 90, 158))
    $g.FillPath($bg, $path)

    # Lopatky
    $c = $size / 2.0
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    for ($i = 0; $i -lt 4; $i++) {
        $state = $g.Save()
        $g.TranslateTransform([single]$c, [single]$c)
        $g.RotateTransform([single](45 + $i * 90))
        $blade = New-Object System.Drawing.Drawing2D.GraphicsPath
        $len = [single]($size * 0.36)
        $w = [single]($size * 0.17)
        $blade.AddBezier(0, 0, $w, [single](-$len * 0.35), $w, [single](-$len * 0.9), 0, [single](-$len))
        $blade.AddBezier(0, [single](-$len), [single](-$w * 0.55), [single](-$len * 0.8), [single](-$w * 0.45), [single](-$len * 0.3), 0, 0)
        $g.FillPath($white, $blade)
        $g.Restore($state)
    }
    # Náboj
    $hub = [single]($size * 0.16)
    $g.FillEllipse($bg, [single]($c - $hub / 2), [single]($c - $hub / 2), $hub, $hub)
    $hubInner = [single]($size * 0.08)
    $g.FillEllipse($white, [single]($c - $hubInner / 2), [single]($c - $hubInner / 2), $hubInner, $hubInner)

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 48, 64, 256
$images = foreach ($sz in $sizes) { , (New-FanPng $sz) }

# Zápis ICO s PNG obrázky
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $data = $images[$i]
    $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $bw.Write($data) }
$bw.Close()
Write-Host "Ikona uložena: $out"
