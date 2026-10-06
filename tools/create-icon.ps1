$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
$destination=Join-Path (Split-Path -Parent $PSScriptRoot) 'src/TypePilot.App/Assets/TypePilot.ico'
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
$images=[Collections.Generic.List[byte[]]]::new()
foreach ($size in @(16,32,48,64,128,256)) {
  $bmp=[Drawing.Bitmap]::new($size,$size)
  $g=[Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.ScaleTransform($size/256.0,$size/256.0)
  $g.Clear([Drawing.Color]::Transparent)
  $background=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#293665'))
  $shape=[Drawing.Drawing2D.GraphicsPath]::new()
  $shape.AddArc(8,8,64,64,180,90); $shape.AddArc(184,8,64,64,270,90); $shape.AddArc(184,184,64,64,0,90); $shape.AddArc(8,184,64,64,90,90); $shape.CloseFigure()
  $g.FillPath($background,$shape)
  $pen=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#B7C0FF'),12)
  $pen.StartCap=[Drawing.Drawing2D.LineCap]::Round; $pen.EndCap=[Drawing.Drawing2D.LineCap]::Round
  $g.DrawRectangle($pen,50,68,156,104)
  $g.DrawLine($pen,78,172,78,199); $g.DrawLine($pen,78,199,111,172)
  $pen.Width=8
  foreach ($x in @(76,102,128,154,180)) {$g.DrawLine($pen,$x,96,$x+2,96); $g.DrawLine($pen,$x,120,$x+2,120)}
  $g.DrawLine($pen,92,147,164,147)
  $star=[Drawing.SolidBrush]::new([Drawing.Color]::White)
  $g.FillPolygon($star,[Drawing.PointF[]]@([Drawing.PointF]::new(208,25),[Drawing.PointF]::new(214,42),[Drawing.PointF]::new(231,48),[Drawing.PointF]::new(214,54),[Drawing.PointF]::new(208,71),[Drawing.PointF]::new(202,54),[Drawing.PointF]::new(185,48),[Drawing.PointF]::new(202,42)))
  $star.Dispose()
  $memory=[IO.MemoryStream]::new(); $bmp.Save($memory,[Drawing.Imaging.ImageFormat]::Png); $images.Add($memory.ToArray())
  if ($size -eq 256) { $bmp.Save((Join-Path (Split-Path -Parent $destination) 'TypePilot.png'),[Drawing.Imaging.ImageFormat]::Png) }
  $memory.Dispose(); $pen.Dispose(); $shape.Dispose(); $background.Dispose(); $g.Dispose(); $bmp.Dispose()
}
$stream=[IO.File]::Create($destination); $writer=[IO.BinaryWriter]::new($stream)
try {
  $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$images.Count)
  $offset=6+16*$images.Count; $index=0
  foreach ($bytes in $images) {
    $size=@(16,32,48,64,128,256)[$index++]; $dimension=if ($size -eq 256) {0} else {$size}
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$bytes.Length); $writer.Write([uint32]$offset); $offset+=$bytes.Length
  }
  foreach ($bytes in $images) {$writer.Write($bytes)}
} finally { $writer.Dispose(); $stream.Dispose() }
