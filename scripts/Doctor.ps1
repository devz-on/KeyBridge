[CmdletBinding()]
param()
. "$PSScriptRoot\Environment.ps1"
Write-Output "Windows: $([Environment]::OSVersion.VersionString)"
Write-Output "Architecture: $([Runtime.InteropServices.RuntimeInformation]::OSArchitecture)"
try { $taskDotnet = Get-KeyBridgeDotnet; & $taskDotnet --version } catch { Write-Warning $_.Exception.Message }
try {
    $taskAdb = Find-KeyBridgeTool 'adb' @("$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe")
    & $taskAdb devices -l
} catch { Write-Warning $_.Exception.Message }
$taskService = Get-Service -Name usbip2_ude -ErrorAction SilentlyContinue
if ($taskService) { Write-Output "USB/IP service: $($taskService.Status); signature and compatibility still require verification." }
else { Write-Output 'USB/IP VHCI driver: absent. Windows virtual HID enumeration is blocked.' }
Write-Output 'This checker does not install software or alter security settings.'
