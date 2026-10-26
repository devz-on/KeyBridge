[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$taskRoot = Split-Path -Parent $PSScriptRoot
[xml]$taskSvg = Get-Content -LiteralPath "$taskRoot\assets\keybridge.svg" -Raw
$taskFrames = @()
foreach ($taskSize in 16,24,32,48,64,128,256) {
    $taskVisual = New-Object System.Windows.Media.DrawingVisual
    $taskDrawing = $taskVisual.RenderOpen()
    $taskDrawing.PushTransform((New-Object System.Windows.Media.ScaleTransform ($taskSize / 48.0),($taskSize / 48.0)))
    foreach ($taskPath in $taskSvg.svg.path) {
        $taskFill = if ($taskPath.fill -eq 'none') { [System.Windows.Media.Brushes]::Transparent } else { [System.Windows.Media.BrushConverter]::new().ConvertFromString($taskPath.fill) }
        $taskPen = $null
        if ($taskPath.stroke) {
            $taskPen = [System.Windows.Media.Pen]::new([System.Windows.Media.BrushConverter]::new().ConvertFromString($taskPath.stroke),[double]$taskPath.'stroke-width')
            $taskPen.StartLineCap = $taskPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
        }
        $taskDrawing.DrawGeometry($taskFill,$taskPen,[System.Windows.Media.Geometry]::Parse($taskPath.d))
    }
    $taskDrawing.Pop(); $taskDrawing.Close()
    $taskBitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($taskSize,$taskSize,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $taskBitmap.Render($taskVisual)
    $taskEncoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $taskEncoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($taskBitmap))
    $taskStream = [IO.MemoryStream]::new()
    $taskEncoder.Save($taskStream)
    $taskFrames += [pscustomobject]@{ Size=$taskSize; Data=$taskStream.ToArray() }
    if ($taskSize -eq 256) { [IO.File]::WriteAllBytes("$taskRoot\assets\keybridge.png",$taskStream.ToArray()) }
    $taskStream.Dispose()
}
$taskIcon = [IO.File]::Create("$taskRoot\windows\KeyBridge.App\KeyBridge.ico")
$taskWriter = [IO.BinaryWriter]::new($taskIcon)
try {
    $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]$taskFrames.Count)
    $taskOffset = 6 + 16 * $taskFrames.Count
    foreach ($taskFrame in $taskFrames) {
        $taskDimension = if ($taskFrame.Size -eq 256) { 0 } else { $taskFrame.Size }
        $taskWriter.Write([byte]$taskDimension); $taskWriter.Write([byte]$taskDimension)
        $taskWriter.Write([byte]0); $taskWriter.Write([byte]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
        $taskWriter.Write([uint32]$taskFrame.Data.Length); $taskWriter.Write([uint32]$taskOffset)
        $taskOffset += $taskFrame.Data.Length
    }
    foreach ($taskFrame in $taskFrames) { $taskWriter.Write([byte[]]$taskFrame.Data) }
} finally { $taskWriter.Dispose(); $taskIcon.Dispose() }
Write-Output 'Updated the Windows multi-size icon and PNG from the shared Android design.'
