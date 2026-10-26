$ErrorActionPreference = 'Stop'
$script:RepositoryRoot = Split-Path -Parent $PSScriptRoot

function Find-KeyBridgeTool([string]$Name, [string[]]$Candidates) {
    $taskCommand = Get-Command $Name -ErrorAction SilentlyContinue
    if ($taskCommand) { return $taskCommand.Source }
    foreach ($taskCandidate in $Candidates) { if (Test-Path -LiteralPath $taskCandidate -PathType Leaf) { return $taskCandidate } }
    throw "Required tool $Name was not found. Install the documented SDK and add it to PATH."
}
function Get-KeyBridgeDotnet {
    Find-KeyBridgeTool 'dotnet' @("$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:USERPROFILE\Tools\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe")
}
function Set-KeyBridgeAndroidEnvironment {
    if (!$env:ANDROID_HOME) { $env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk" }
    if (!(Test-Path -LiteralPath "$env:ANDROID_HOME\platforms\android-36")) { throw 'Android SDK platform 36 is required. Install it through Android Studio SDK Manager.' }
    if (!$env:JAVA_HOME) {
        $taskJava = Get-Command java -ErrorAction SilentlyContinue
        if ($taskJava) { $env:JAVA_HOME = Split-Path -Parent (Split-Path -Parent $taskJava.Source) }
        elseif (Test-Path -LiteralPath "$env:ProgramFiles\Android\Android Studio\jbr") { $env:JAVA_HOME = "$env:ProgramFiles\Android\Android Studio\jbr" }
        else { throw 'JDK 21 is required. Set JAVA_HOME to its installation directory.' }
    }
}
function Invoke-KeyBridgeDotnet([string[]]$Arguments) {
    $taskDotnet = Get-KeyBridgeDotnet
    & $taskDotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet command failed ($LASTEXITCODE)." }
}
function Invoke-KeyBridgeGradle([string[]]$Arguments) {
    Set-KeyBridgeAndroidEnvironment
    Push-Location (Join-Path $script:RepositoryRoot 'android')
    try { & '.\gradlew.bat' @Arguments; if ($LASTEXITCODE -ne 0) { throw "Gradle command failed ($LASTEXITCODE)." } }
    finally { Pop-Location }
}
