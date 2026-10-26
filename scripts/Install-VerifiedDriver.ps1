[CmdletBinding(SupportsShouldProcess, ConfirmImpact='High')]
param([Parameter(Mandatory)][string]$Installer, [switch]$Consent)
$ErrorActionPreference = 'Stop'
if (!$Consent) { throw 'Explicit -Consent is required to show the independently verified driver installer. Review docs/driver-verification.md first.' }
$taskInstaller = (Resolve-Path -LiteralPath $Installer).Path
$taskExpectedSha = '38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A'
if ((Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash -ne $taskExpectedSha) { throw 'Hash mismatch. This script accepts only the inspected upstream USBip-0.9.8.1-x64.exe package.' }
$taskSignature = Get-AuthenticodeSignature -LiteralPath $taskInstaller
if ($taskSignature.Status -ne 'Valid' -or $taskSignature.SignerCertificate.Thumbprint -ne '9AC56B6C76141395D74FFF6652818376E80B9C95') { throw 'Upstream Authenticode publisher/signature mismatch. No installer launched.' }
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') { throw 'This verified package is x64 only.' }
Write-Warning 'Installer authenticity is verified. Compatibility with your Windows build and installed drivers has not been established. Installing this package can restart all USB devices and may require a reboot. Review its own prompts.'
if ($PSCmdlet.ShouldProcess($taskInstaller, 'Show the signed USB/IP driver installer with administrator elevation')) {
    # -Consent explicitly requests an interactive installer; never invoke silent installation or security-setting changes.
    Start-Process -FilePath $taskInstaller -ArgumentList '/NORESTART' -Verb RunAs -WindowStyle Normal -Wait
}
