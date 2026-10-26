[CmdletBinding()]
param([switch]$Device, [switch]$DirectDevice, [switch]$SkipAndroid, [switch]$SkipBrowser)
. "$PSScriptRoot\Environment.ps1"
Push-Location $script:RepositoryRoot
try {
    Invoke-KeyBridgeDotnet @('run','--project','tests/KeyBridge.Usb.Tests','-c','Release','--nologo')
    Invoke-KeyBridgeDotnet @('run','--project','tests/KeyBridge.Core.Tests','-c','Release','--nologo')
    if (!$SkipBrowser) {
        $taskNode = Find-KeyBridgeTool 'node' @()
        & $taskNode --test 'tests/webauthn/helpers.test.mjs'
        if ($LASTEXITCODE -ne 0) { throw 'WebAuthn helper tests failed.' }
        $taskPreviousDotnet = $env:KEYBRIDGE_DOTNET
        try {
            $env:KEYBRIDGE_DOTNET = Get-KeyBridgeDotnet
            & $taskNode 'tests/webauthn/server.test.mjs'
            if ($LASTEXITCODE -ne 0) { throw 'WebAuthn local server tests failed.' }
        } finally { $env:KEYBRIDGE_DOTNET = $taskPreviousDotnet }
    }
    if (!$SkipAndroid) { Invoke-KeyBridgeGradle @(':core:test','--no-daemon') }
    if ($Device) { Invoke-KeyBridgeGradle @(':app:connectedDebugAndroidTest','--no-daemon') }
    if ($DirectDevice) {
        # Explicit alternative runner for hosts whose Gradle UTP gRPC reporting channel is broken.
        Invoke-KeyBridgeGradle @(':app:assembleDebug',':app:assembleDebugAndroidTest','--no-daemon')
        $taskAdb = Find-KeyBridgeTool 'adb' @("$env:ANDROID_HOME\platform-tools\adb.exe")
        & $taskAdb -d install -r 'android/app/build/outputs/apk/debug/app-debug.apk'
        if ($LASTEXITCODE -ne 0) { throw 'Device application installation failed.' }
        & $taskAdb -d install -r 'android/app/build/outputs/apk/androidTest/debug/app-debug-androidTest.apk'
        if ($LASTEXITCODE -ne 0) { throw 'Device test application installation failed.' }
        $taskResult = & $taskAdb -d shell am instrument -w 'dev.keybridge.android.test/androidx.test.runner.AndroidJUnitRunner'
        $taskResult | Write-Output
        if ($LASTEXITCODE -ne 0 -or ($taskResult -join "`n") -notmatch 'OK \(\d+ tests?\)' -or ($taskResult -join "`n") -match 'FAILURES!!!|INSTRUMENTATION_FAILED') { throw 'Device instrumentation did not report a passing test run.' }
    }
}
finally { Pop-Location }
