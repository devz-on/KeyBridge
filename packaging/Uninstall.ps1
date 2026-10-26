[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param([switch]$RemovePreferences)
$ErrorActionPreference = 'Stop'
$taskTarget = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\KeyBridge'))
$taskExpectedParent = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs')) + [IO.Path]::DirectorySeparatorChar
if (!$taskTarget.StartsWith($taskExpectedParent, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $taskTarget) -ne 'KeyBridge') { throw 'Uninstall path safety check failed.' }
if (Get-Process -Name KeyBridge -ErrorAction SilentlyContinue) { throw 'Exit KeyBridge from its tray menu before uninstalling.' }
if (!$PSCmdlet.ShouldProcess($taskTarget, 'Remove the per-user KeyBridge application')) { return }
$taskShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'KeyBridge.lnk'
if (Test-Path -LiteralPath $taskShortcut) { Remove-Item -LiteralPath $taskShortcut }
Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name KeyBridge -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath $taskTarget) { Remove-Item -LiteralPath $taskTarget -Recurse -Force }
if ($RemovePreferences) {
    $taskPreferences = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'KeyBridge'))
    if ((Split-Path -Parent $taskPreferences) -ne [IO.Path]::GetFullPath($env:LOCALAPPDATA)) { throw 'Preference path safety check failed.' }
    if (Test-Path -LiteralPath $taskPreferences) { Remove-Item -LiteralPath $taskPreferences -Recurse -Force }
}
Write-Output 'KeyBridge removed. Android credentials, phone host authorization, Windows transport certificate and external USB/IP driver remain. Revoke the computer on the phone and follow docs/installation.md for deliberate cleanup.'
