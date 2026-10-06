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
  $background=[Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#84DFC5'))
  $shape=[Drawing.Drawing2D.GraphicsPath]::new()
  $shape.AddArc(8,8,64,64,180,90); $shape.AddArc(184,8,64,64,270,90); $shape.AddArc(184,184,64,64,0,90); $shape.AddArc(8,184,64,64,90,90); $shape.CloseFigure()
  $g.FillPath($background,$shape)
  $pen=[Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#082B25'),20)
  $pen.StartCap=[Drawing.Drawing2D.LineCap]::Round; $pen.EndCap=[Drawing.Drawing2D.LineCap]::Round
  $g.DrawLine($pen,64,76,192,76); $g.DrawLine($pen,128,76,128,196)
  $pen.Width=12; $g.DrawLine($pen,168,178,204,142); $g.DrawLine($pen,168,142,204,142); $g.DrawLine($pen,204,142,204,178)
  $memory=[IO.MemoryStream]::new(); $bmp.Save($memory,[Drawing.Imaging.ImageFormat]::Png); $images.Add($memory.ToArray())
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
