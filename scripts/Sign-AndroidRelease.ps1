[CmdletBinding()]
param(
    [string]$InputApk,
    [string]$OutputApk,
    [string]$KeyStorePath = $env:KEYBRIDGE_ANDROID_KEYSTORE,
    [string]$KeyAlias = $env:KEYBRIDGE_ANDROID_KEY_ALIAS,
    [string]$Lineage = $env:KEYBRIDGE_ANDROID_SIGNING_LINEAGE,
    [string]$ExpectedCertificateSha256 = $env:KEYBRIDGE_ANDROID_CERT_SHA256
)
. "$PSScriptRoot\Environment.ps1"
Set-KeyBridgeAndroidEnvironment
if (!$InputApk) { $InputApk = Join-Path $script:RepositoryRoot 'android\app\build\outputs\apk\release\app-release-unsigned.apk' }
if (!$OutputApk) { $OutputApk = Join-Path $script:RepositoryRoot 'artifacts\android\KeyBridge-release.apk' }
if (!$KeyStorePath -or !(Test-Path -LiteralPath $KeyStorePath -PathType Leaf) -or !$KeyAlias -or !$env:KEYBRIDGE_ANDROID_STORE_PASSWORD) {
    throw 'Set KEYBRIDGE_ANDROID_KEYSTORE, KEYBRIDGE_ANDROID_KEY_ALIAS and KEYBRIDGE_ANDROID_STORE_PASSWORD to a protected release identity. Debug signing is never substituted.'
}
if (!(Test-Path -LiteralPath $InputApk -PathType Leaf)) { throw 'Build :app:assembleRelease before signing.' }
if ([IO.Path]::GetFullPath($InputApk) -eq [IO.Path]::GetFullPath($OutputApk)) { throw 'The signed output must differ from the unsigned input.' }
$taskBuildTools = Join-Path $env:ANDROID_HOME 'build-tools\36.0.0'
$taskSigner = Join-Path $taskBuildTools 'lib\apksigner.jar'
$taskAapt = Join-Path $taskBuildTools 'aapt2.exe'
$taskJava = Join-Path $env:JAVA_HOME 'bin\java.exe'
foreach ($taskTool in $taskSigner,$taskAapt,$taskJava) { if (!(Test-Path -LiteralPath $taskTool)) { throw "Required signing tool missing: $taskTool" } }
$taskBadging = & $taskAapt dump badging $InputApk
if ($LASTEXITCODE -ne 0 -or ($taskBadging -match '^application-debuggable')) { throw 'Release signing requires a readable, non-debuggable APK.' }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($OutputApk))) | Out-Null
$taskArguments = @('-jar',$taskSigner,'sign','--ks',$KeyStorePath,'--ks-key-alias',$KeyAlias,'--ks-pass','env:KEYBRIDGE_ANDROID_STORE_PASSWORD','--debuggable-apk-permitted','false','--v1-signing-enabled','false','--v3-signing-enabled','true','--v4-signing-enabled','false')
if ($env:KEYBRIDGE_ANDROID_KEY_PASSWORD) { $taskArguments += @('--key-pass','env:KEYBRIDGE_ANDROID_KEY_PASSWORD') }
if ($Lineage) {
    if (!(Test-Path -LiteralPath $Lineage -PathType Leaf)) { throw 'The configured signing lineage does not exist.' }
    # All supported Android versions (API 30+) verify v3. This makes the new
    # release key the only APK signer while preserving the predecessor history.
    $taskArguments += @('--lineage',$Lineage,'--rotation-min-sdk-version','28','--v2-signing-enabled','false')
} else { $taskArguments += @('--v2-signing-enabled','true') }
$taskArguments += @('--out',$OutputApk,$InputApk)
& $taskJava @taskArguments
if ($LASTEXITCODE -ne 0) { throw "Release signing failed ($LASTEXITCODE)." }
$taskVerification = & $taskJava -jar $taskSigner verify --verbose --print-certs $OutputApk
if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
$taskCertificate = $taskVerification | Select-String '^Signer #1 certificate SHA-256 digest: ([0-9a-f]+)$'
if (!$taskCertificate -or ($taskVerification -match '^Signer #\d+ certificate DN: .*CN=Android Debug')) { throw 'A dedicated release certificate is required.' }
$taskFingerprint = $taskCertificate.Matches[0].Groups[1].Value
if ($ExpectedCertificateSha256 -and $taskFingerprint -ne ($ExpectedCertificateSha256 -replace ':','')) { throw 'APK signer does not match the expected release certificate.' }
$taskVerification
Write-Output "Signed release APK: $OutputApk"
