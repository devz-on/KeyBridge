[CmdletBinding(SupportsShouldProcess)]
param()
$ErrorActionPreference = 'Stop'
$taskTarget = Join-Path $env:LOCALAPPDATA 'Programs\KeyBridge'
if (!(Test-Path -LiteralPath "$PSScriptRoot\app\KeyBridge.exe")) { throw 'Run this script from the extracted KeyBridge Windows package.' }
if (!$PSCmdlet.ShouldProcess($taskTarget, 'Install KeyBridge for this Windows user and create a Start menu shortcut')) { return }
New-Item -ItemType Directory -Force -Path $taskTarget | Out-Null
Copy-Item -Path "$PSScriptRoot\app","$PSScriptRoot\tools" -Destination $taskTarget -Recurse -Force
foreach ($taskOptionalDirectory in 'diagnostic','docs','scripts','protocol') {
    $taskSourceDirectory = Join-Path $PSScriptRoot $taskOptionalDirectory
    if (Test-Path -LiteralPath $taskSourceDirectory) { Copy-Item -LiteralPath $taskSourceDirectory -Destination $taskTarget -Recurse -Force }
}
foreach ($taskOptionalFile in 'Run-WebAuthn-Diagnostic.cmd','START_HERE.md','README.md','SECURITY.md','CHANGELOG.md','CONTRIBUTING.md','LICENSE','THIRD_PARTY_NOTICES.md') {
    $taskSourceFile = Join-Path $PSScriptRoot $taskOptionalFile
    if (Test-Path -LiteralPath $taskSourceFile) { Copy-Item -LiteralPath $taskSourceFile -Destination $taskTarget -Force }
}
Copy-Item -LiteralPath "$PSScriptRoot\Uninstall.ps1" -Destination $taskTarget -Force
$taskStartMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'KeyBridge.lnk'
$taskShell = New-Object -ComObject WScript.Shell
$taskShortcut = $taskShell.CreateShortcut($taskStartMenu)
$taskShortcut.TargetPath = "$taskTarget\app\KeyBridge.exe"
$taskShortcut.WorkingDirectory = "$taskTarget\app"
$taskShortcut.Save()
Write-Output "Installed for this user at $taskTarget. No driver or startup entry was installed."
