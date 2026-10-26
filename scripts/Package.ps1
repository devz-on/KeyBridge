[CmdletBinding()]
param([switch]$SkipAndroid)
. "$PSScriptRoot\Environment.ps1"
$taskOutput = Join-Path $script:RepositoryRoot 'artifacts'
$taskWindows = Join-Path $taskOutput 'windows-0.1.2'
New-Item -ItemType Directory -Force -Path $taskWindows | Out-Null
Push-Location $script:RepositoryRoot
try {
    Invoke-KeyBridgeDotnet @('publish','windows/KeyBridge.App','-c','Release','-r','win-x64','--self-contained','true','-o',"$taskWindows\app",'--nologo')
    Invoke-KeyBridgeDotnet @('publish','windows/KeyBridge.Tool','-c','Release','-r','win-x64','--self-contained','true','-o',"$taskWindows\tools",'--nologo')
    Invoke-KeyBridgeDotnet @('publish','tests/webauthn/server','-c','Release','-r','win-x64','--self-contained','true','-o',"$taskWindows\diagnostic\server",'--nologo')
    Copy-Item -LiteralPath '.\packaging\Install.ps1','.\packaging\Uninstall.ps1','.\LICENSE','.\README.md','.\SECURITY.md','.\CHANGELOG.md','.\CONTRIBUTING.md','.\CODE_OF_CONDUCT.md','.\THIRD_PARTY_NOTICES.md' -Destination $taskWindows
    Copy-Item -LiteralPath '.\docs' -Destination $taskWindows -Recurse -Force
    Copy-Item -LiteralPath '.\assets' -Destination $taskWindows -Recurse -Force
    Copy-Item -LiteralPath '.\protocol' -Destination $taskWindows -Recurse -Force
    Copy-Item -LiteralPath '.\packaging\Run-WebAuthn-Diagnostic.cmd' -Destination $taskWindows -Force
    Copy-Item -LiteralPath '.\packaging\PORTABLE.md' -Destination "$taskWindows\START_HERE.md" -Force
    New-Item -ItemType Directory -Force -Path "$taskWindows\diagnostic\web","$taskWindows\scripts" | Out-Null
    Copy-Item -LiteralPath '.\scripts\Install-VerifiedDriver.ps1' -Destination "$taskWindows\scripts" -Force
    Copy-Item -LiteralPath '.\tests\webauthn\index.html','.\tests\webauthn\styles.css','.\tests\webauthn\app.mjs','.\tests\webauthn\webauthn.mjs','.\tests\webauthn\cbor.mjs' -Destination "$taskWindows\diagnostic\web" -Force
    if (!$SkipAndroid) {
        Invoke-KeyBridgeGradle @(':app:assembleDebug',':app:assembleRelease','--no-daemon')
        $taskAndroid = Join-Path $taskOutput 'android'
        New-Item -ItemType Directory -Force -Path $taskAndroid | Out-Null
        Copy-Item -LiteralPath '.\android\app\build\outputs\apk\debug\app-debug.apk' -Destination "$taskAndroid\KeyBridge-debug.apk"
        Copy-Item -LiteralPath '.\android\app\build\outputs\apk\release\app-release-unsigned.apk' -Destination "$taskAndroid\KeyBridge-release-unsigned.apk"
        Copy-Item -LiteralPath '.\packaging\ANDROID.md' -Destination "$taskAndroid\README.md" -Force
    }
    Compress-Archive -Path "$taskWindows\*" -DestinationPath "$taskOutput\KeyBridge-0.1.2-windows-x64.zip" -Force
    Get-ChildItem -LiteralPath $taskOutput -File -Recurse | Where-Object { $_.Extension -in '.zip','.apk','.exe' } | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $($_.Path.Substring($taskOutput.Length + 1))" } | Set-Content -LiteralPath "$taskOutput\SHA256SUMS.txt"
}
finally { Pop-Location }
