[CmdletBinding()]
param([string]$IsccPath, [string]$OutputDirectory, [switch]$SkipPackage)
. "$PSScriptRoot\Environment.ps1"
$taskCache = Join-Path $script:RepositoryRoot '.cache\installer'
$taskDependencies = Join-Path $taskCache 'dependencies'
New-Item -ItemType Directory -Force -Path $taskDependencies | Out-Null

function Get-VerifiedPayload([string]$Url, [string]$Filename, [string]$Sha256) {
    $taskFile = Join-Path $taskCache $Filename
    if (!(Test-Path -LiteralPath $taskFile)) { Invoke-WebRequest -Uri $Url -OutFile $taskFile }
    if ((Get-FileHash -LiteralPath $taskFile -Algorithm SHA256).Hash -ne $Sha256) { throw "Payload hash mismatch: $Filename. No installer is built with unverified payloads." }
    return $taskFile
}

if (!$IsccPath) {
    $taskCandidates = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "$taskCache\compiler\{app}\ISCC.exe")
    $IsccPath = $taskCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$IsccPath -or !(Test-Path -LiteralPath $IsccPath)) { throw 'Install official Inno Setup 6.7.3, or supply -IsccPath to its ISCC.exe. See docs/installer.md.' }
$taskCompilerSignature = Get-AuthenticodeSignature -LiteralPath $IsccPath
if ($taskCompilerSignature.Status -ne 'Valid' -or $taskCompilerSignature.SignerCertificate.Subject -notmatch 'O=Pyrsys B.V.') { throw 'Inno Setup compiler signature is not the expected valid Pyrsys B.V. publisher.' }
if (!$SkipPackage) { & "$PSScriptRoot\Package.ps1" -SignAndroid }

$taskAdbZip = Get-VerifiedPayload 'https://dl.google.com/android/repository/platform-tools_r37.0.1-win.zip' 'platform-tools_r37.0.1-win.zip' '45F4D63113E895EBDE0C90F194099A4676B6AC653BD28D54314A9E022BBC1A99'
$taskDriver = Get-VerifiedPayload 'https://github.com/vadimgrn/usbip-win2/releases/download/v.0.9.8.1/USBip-0.9.8.1-x64.exe' 'USBip-0.9.8.1-x64.exe' '38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A'
$taskDriverSignature = Get-AuthenticodeSignature -LiteralPath $taskDriver
if ($taskDriverSignature.Status -ne 'Valid' -or $taskDriverSignature.SignerCertificate.Thumbprint -ne '9AC56B6C76141395D74FFF6652818376E80B9C95') { throw 'Driver Authenticode signature/publisher does not match the verified upstream release.' }
$taskDriverLicense = Get-VerifiedPayload 'https://raw.githubusercontent.com/vadimgrn/usbip-win2/v.0.9.8.1/LICENSE.txt' 'usbip-LICENSE.txt' '723572B1487231EDCC3A2594DA30961C693DA7A0D7B676CA718FE3391920B8CA'
Expand-Archive -LiteralPath $taskAdbZip -DestinationPath "$taskCache\adb" -Force
New-Item -ItemType Directory -Force -Path "$taskDependencies\adb" | Out-Null
foreach ($taskAdbFile in 'adb.exe','AdbWinApi.dll','AdbWinUsbApi.dll','libwinpthread-1.dll','NOTICE.txt','source.properties') {
    Copy-Item -LiteralPath "$taskCache\adb\platform-tools\$taskAdbFile" -Destination "$taskDependencies\adb" -Force
}
Copy-Item -LiteralPath $taskDriver -Destination "$taskDependencies\USBip-0.9.8.1-x64.exe" -Force
Copy-Item -LiteralPath $taskDriverLicense -Destination "$taskDependencies\usbip-LICENSE.txt" -Force
$taskCompilerLicense = Join-Path (Split-Path -Parent $IsccPath) 'License.txt'
$taskOutput = Join-Path $script:RepositoryRoot 'artifacts'
$taskSetupOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskOutput 'releases\0.1.2' }
New-Item -ItemType Directory -Force -Path $taskSetupOutput | Out-Null
$taskWindows = Join-Path $taskOutput 'windows-0.1.2'
$taskAndroid = Join-Path $taskOutput 'android\KeyBridge-release.apk'
if (!(Test-Path -LiteralPath "$taskWindows\app\KeyBridge.exe") -or !(Test-Path -LiteralPath $taskAndroid)) { throw 'Run Package.ps1 to build the Windows package and Android APK first.' }
Set-KeyBridgeAndroidEnvironment
$taskSigner = Join-Path $env:ANDROID_HOME 'build-tools\36.0.0\lib\apksigner.jar'
$taskApkVerification = & "$env:JAVA_HOME\bin\java.exe" -jar $taskSigner verify --verbose --print-certs $taskAndroid
if ($LASTEXITCODE -ne 0 -or ($taskApkVerification -match '^Signer #\d+ certificate DN: .*CN=Android Debug')) { throw 'Setup requires a verified APK signed with a dedicated release identity.' }
$taskApkBadging = & "$env:ANDROID_HOME\build-tools\36.0.0\aapt2.exe" dump badging $taskAndroid
if ($LASTEXITCODE -ne 0 -or ($taskApkBadging -match '^application-debuggable')) { throw 'Setup cannot bundle a debuggable APK.' }
& $IsccPath "/DPayloadRoot=$taskWindows" "/DDependencyRoot=$taskDependencies" "/DAndroidApk=$taskAndroid" "/DCompilerLicense=$taskCompilerLicense" "/DOutputRoot=$taskSetupOutput" "$script:RepositoryRoot\packaging\KeyBridge.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed ($LASTEXITCODE)." }
Get-ChildItem -LiteralPath $taskOutput -File -Recurse | Where-Object { $_.Extension -in '.zip','.apk','.exe' } | Get-FileHash -Algorithm SHA256 | ForEach-Object { "$($_.Hash)  $($_.Path.Substring($taskOutput.Length + 1))" } | Set-Content -LiteralPath "$taskOutput\SHA256SUMS.txt"
Write-Output "All-in-one installer: $taskSetupOutput\KeyBridge-0.1.2-Setup-x64.exe"
