[CmdletBinding()]
param(
    [string]$TargetUser,
    [switch]$ValidateOnly,
    [switch]$SkipGpUpdate
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Subject = 'CN=mRemoteNG RDP File Publisher'
$FriendlyName = 'mRemoteNG RDP File Publisher'
$EnvName = 'MREMOTENG_RDP_SIGN_CERT_THUMBPRINT'
$PolicyPath = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
$LogPath = Join-Path $env:ProgramData 'mRemoteNG\NativeRdpTrust\native-rdp-trust.log'
$Results = New-Object System.Collections.Generic.List[object]

function Log([string]$Message, [string]$Level = 'INFO') {
    $line = '[{0}] [{1}] {2}' -f (Get-Date).ToString('o'), $Level, $Message
    Write-Host $line
    $dir = Split-Path -Parent $LogPath
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
}

function Result([string]$Check, [bool]$Passed, [string]$Details) {
    $Results.Add([pscustomobject]@{
        Check = $Check
        Status = $(if ($Passed) { 'PASS' } else { 'FAIL' })
        Details = $Details
    })
}

function Is-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $p = New-Object Security.Principal.WindowsPrincipal($id)
    return $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Normalize-Hex([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return (($Value -replace '[^0-9A-Fa-f]', '').ToUpperInvariant())
}

function Resolve-User([string]$Requested) {
    $name = $Requested
    if ([string]::IsNullOrWhiteSpace($name)) {
        $name = (Get-CimInstance Win32_ComputerSystem).UserName
    }
    if ([string]::IsNullOrWhiteSpace($name)) { throw 'No interactive Windows user was found.' }
    $account = New-Object Security.Principal.NTAccount($name)
    $sid = $account.Translate([Security.Principal.SecurityIdentifier]).Value
    $profileKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{0}' -f $sid
    $profile = [Environment]::ExpandEnvironmentVariables(
        (Get-ItemProperty -LiteralPath $profileKey -Name ProfileImagePath).ProfileImagePath
    )
    return [pscustomobject]@{ Name = $name; Sid = $sid; Profile = $profile }
}

function Get-SigningCert {
    $now = Get-Date
    Get-ChildItem Cert:\LocalMachine\My |
        Where-Object {
            $_.Subject -eq $Subject -and
            $_.FriendlyName -eq $FriendlyName -and
            $_.HasPrivateKey -and
            $_.NotBefore -le $now -and
            $_.NotAfter -gt $now.AddDays(30)
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
}

function Ensure-Cert {
    $cert = Get-SigningCert
    if ($null -ne $cert) {
        Log "Reusing certificate $($cert.Thumbprint), valid through $($cert.NotAfter)."
        return $cert
    }
    Log 'Creating machine-wide RDP file publisher certificate.'
    $created = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -FriendlyName $FriendlyName `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature `
        -CertStoreLocation 'Cert:\LocalMachine\My' `
        -NotAfter (Get-Date).AddYears(10)
    return $created
}

function Has-Cert([string]$Store, [string]$Thumbprint) {
    return Test-Path -LiteralPath ('Cert:\LocalMachine\{0}\{1}' -f $Store, (Normalize-Hex $Thumbprint))
}

function Ensure-TrustedStore($Cert, [string]$StoreName) {
    if (Has-Cert $StoreName $Cert.Thumbprint) { return }
    $public = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2(
        $Cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    )
    try {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
            $StoreName,
            [System.Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine
        )
        try {
            $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
            $store.Add($public)
        } finally { $store.Close() }
    } finally { $public.Dispose() }
    Log "Imported certificate into LocalMachine\\$StoreName."
}

function Key-Path($Cert) {
    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($Cert)
    if ($null -eq $rsa) { throw 'Certificate has no RSA private key.' }
    try {
        if ($rsa -is [System.Security.Cryptography.RSACng]) {
            return (Join-Path (Join-Path $env:ProgramData 'Microsoft\Crypto\Keys') $rsa.Key.UniqueName)
        }
        if ($rsa -is [System.Security.Cryptography.RSACryptoServiceProvider]) {
            return (Join-Path (Join-Path $env:ProgramData 'Microsoft\Crypto\RSA\MachineKeys') $rsa.CspKeyContainerInfo.UniqueKeyContainerName)
        }
        throw "Unsupported private key provider: $($rsa.GetType().FullName)"
    } finally { $rsa.Dispose() }
}

function Has-KeyRead([string]$Path, [string]$Sid) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    foreach ($rule in (Get-Acl -LiteralPath $Path).Access) {
        try { $ruleSid = $rule.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value }
        catch { continue }
        if ($ruleSid -eq $Sid -and
            $rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            (($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::Read) -ne 0)) { return $true }
    }
    return $false
}

function Ensure-KeyRead($Cert, [string]$Sid) {
    $path = Key-Path $Cert
    if (-not (Test-Path -LiteralPath $path)) { throw "Private key file not found: $path" }
    if (-not (Has-KeyRead $path $Sid)) {
        $acl = Get-Acl -LiteralPath $path
        $rule = New-Object Security.AccessControl.FileSystemAccessRule(
            (New-Object Security.Principal.SecurityIdentifier($Sid)),
            [Security.AccessControl.FileSystemRights]::Read,
            [Security.AccessControl.AccessControlType]::Allow
        )
        $acl.AddAccessRule($rule) | Out-Null
        Set-Acl -LiteralPath $path -AclObject $acl
        Log "Granted private-key read access to SID $Sid."
    }
    return $path
}

function Reg-Value([string]$Path, [string]$Name) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $key = Get-Item -LiteralPath $Path
    return $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
}

function Broadcast-Environment {
    try {
        if (-not ('MRemoteNg.NativeRdpTrust.NativeMethods' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace MRemoteNg.NativeRdpTrust {
    public static class NativeMethods {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint Msg, UIntPtr wParam, string lParam,
            uint flags, uint timeout, out UIntPtr result);
    }
}
'@
        }
        $result = [UIntPtr]::Zero
        [void][MRemoteNg.NativeRdpTrust.NativeMethods]::SendMessageTimeout(
            [IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$result
        )
    } catch { Log "Environment broadcast failed: $($_.Exception.Message)" 'WARN' }
}

function Existing-Publishers {
    if (-not (Test-Path -LiteralPath $PolicyPath)) { return @() }
    $value = Reg-Value $PolicyPath 'TrustedCertThumbprints'
    if ([string]::IsNullOrWhiteSpace([string]$value)) { return @() }
    return @($value -split '[,;]' | ForEach-Object { Normalize-Hex $_ } | Where-Object { $_ })
}

function Configure($Cert, $User) {
    $sha1 = Normalize-Hex $Cert.Thumbprint
    $sha256 = Normalize-Hex $Cert.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    if ($sha1.Length -ne 40 -or $sha256.Length -ne 64) { throw 'Unexpected certificate hash length.' }

    Ensure-TrustedStore $Cert 'Root'
    Ensure-TrustedStore $Cert 'TrustedPublisher'
    [void](Ensure-KeyRead $Cert $User.Sid)

    [Environment]::SetEnvironmentVariable($EnvName, $sha256, [EnvironmentVariableTarget]::Machine)
    Set-Item -Path ('Env:' + $EnvName) -Value $sha256

    $userEnvPath = 'Registry::HKEY_USERS\{0}\Environment' -f $User.Sid
    if (-not (Test-Path -LiteralPath $userEnvPath)) { throw "Target user registry hive is not loaded: $($User.Name)" }
    New-ItemProperty -Path $userEnvPath -Name $EnvName -Value $sha256 -PropertyType String -Force | Out-Null

    $configDir = Join-Path $User.Profile 'AppData\Local\mRemoteNG'
    $configPath = Join-Path $configDir 'native-rdp-signing-thumbprint.txt'
    if (-not (Test-Path -LiteralPath $configDir)) { New-Item -ItemType Directory -Path $configDir -Force | Out-Null }
    [IO.File]::WriteAllText($configPath, $sha256, (New-Object System.Text.UTF8Encoding($false)))
    Broadcast-Environment

    if (-not (Test-Path -LiteralPath $PolicyPath)) { New-Item -Path $PolicyPath -Force | Out-Null }
    $publishers = New-Object System.Collections.Generic.List[string]
    foreach ($item in (Existing-Publishers)) { if (-not $publishers.Contains($item)) { $publishers.Add($item) } }
    if (-not $publishers.Contains($sha1)) { $publishers.Add($sha1) }
    New-ItemProperty -Path $PolicyPath -Name AllowSignedFiles -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $PolicyPath -Name TrustedCertThumbprints -Value ([string]::Join(',', $publishers.ToArray())) -PropertyType String -Force | Out-Null
    Log 'AllowUnsignedFiles was intentionally left unchanged.'

    if (-not $SkipGpUpdate) {
        $p = Start-Process (Join-Path $env:SystemRoot 'System32\gpupdate.exe') -ArgumentList '/target:computer','/force' -Wait -PassThru -NoNewWindow
        if ($p.ExitCode -ne 0) { throw "gpupdate failed with exit code $($p.ExitCode)." }
    }
}

function Validate($User) {
    $Results.Clear()
    $cert = Get-SigningCert
    Result 'Certificate in LocalMachine\My' ($null -ne $cert) $(if ($cert) { $cert.Subject } else { 'Not found' })
    if ($null -eq $cert) { return $false }

    $sha1 = Normalize-Hex $cert.Thumbprint
    $sha256 = Normalize-Hex $cert.GetCertHashString([System.Security.Cryptography.HashAlgorithmName]::SHA256)
    $key = Key-Path $cert
    Result 'Private key available' $cert.HasPrivateKey "HasPrivateKey=$($cert.HasPrivateKey)"
    Result 'Certificate valid' ($cert.NotBefore -le (Get-Date) -and $cert.NotAfter -gt (Get-Date)) "$($cert.NotBefore) - $($cert.NotAfter)"
    Result 'Certificate in LocalMachine\Root' (Has-Cert 'Root' $sha1) $sha1
    Result 'Certificate in LocalMachine\TrustedPublisher' (Has-Cert 'TrustedPublisher' $sha1) $sha1
    Result 'Private-key ACL' (Has-KeyRead $key $User.Sid) "$($User.Name) -> $key"

    $machine = Normalize-Hex ([Environment]::GetEnvironmentVariable($EnvName, [EnvironmentVariableTarget]::Machine))
    Result 'Machine signing environment' ($machine -eq $sha256) $machine
    $userEnvPath = 'Registry::HKEY_USERS\{0}\Environment' -f $User.Sid
    $userValue = Normalize-Hex ([string](Reg-Value $userEnvPath $EnvName))
    Result 'User signing environment' ($userValue -eq $sha256) $userValue

    $configPath = Join-Path $User.Profile 'AppData\Local\mRemoteNG\native-rdp-signing-thumbprint.txt'
    $fileValue = ''
    if (Test-Path -LiteralPath $configPath) { $fileValue = Normalize-Hex (Get-Content -LiteralPath $configPath -Raw) }
    Result 'mRemoteNG signing hash file' ($fileValue -eq $sha256) $configPath

    $allow = Reg-Value $PolicyPath 'AllowSignedFiles'
    Result 'Allow signed RDP files policy' ($allow -eq 1) ([string]$allow)
    $trusted = Existing-Publishers
    Result 'Trusted RDP publisher policy' ($trusted -contains $sha1) ([string]::Join(',', $trusted))
    Result 'Unknown-publisher policy unchanged' $true 'AllowUnsignedFiles was not written by this script.'
    Result 'mstsc.exe present' (Test-Path (Join-Path $env:SystemRoot 'System32\mstsc.exe')) (Join-Path $env:SystemRoot 'System32\mstsc.exe')

    return (@($Results | Where-Object Status -eq 'FAIL').Count -eq 0)
}

try {
    if (-not (Is-Admin)) { throw 'Administrator rights are required. Run Install-MRemoteNgNativeRdpTrust.cmd.' }
    $user = Resolve-User $TargetUser
    Log "Target mRemoteNG user: $($user.Name) [$($user.Sid)]."
    if (-not $ValidateOnly) {
        $cert = Ensure-Cert
        Configure $cert $user
    }
    $ready = Validate $user
    $Results | Format-Table -AutoSize -Wrap
    Write-Host ''
    if ($ready) {
        Write-Host 'READY' -ForegroundColor Green
        Write-Host 'Restart mRemoteNG if it was already running.' -ForegroundColor Yellow
        Log 'READY: all checks passed.'
        exit 0
    }
    Write-Host 'NOT READY' -ForegroundColor Red
    Log 'NOT READY: one or more checks failed.' 'ERROR'
    exit 1
}
catch {
    Log $_.Exception.ToString() 'ERROR'
    Write-Host 'NOT READY' -ForegroundColor Red
    exit 1
}
