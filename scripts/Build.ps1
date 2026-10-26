[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release', [switch]$SkipAndroid)
. "$PSScriptRoot\Environment.ps1"
Push-Location $script:RepositoryRoot
try {
    Invoke-KeyBridgeDotnet @('build','KeyBridge.sln','-c',$Configuration,'--nologo')
    if (!$SkipAndroid) { Invoke-KeyBridgeGradle @(':app:assembleDebug',':core:test','--no-daemon') }
}
finally { Pop-Location }
