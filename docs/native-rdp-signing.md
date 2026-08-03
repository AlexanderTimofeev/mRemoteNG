# Signing temporary RDP files

Native `mstsc` mode generates a temporary `.rdp` file for every connection launch. Windows can display a security confirmation dialog for unsigned files, especially when clipboard, drives, printers, smart cards, audio, or other device redirection is enabled.

mRemoteNG signs generated files in-process by using the configured Windows certificate and the standard CMS/PKCS#7 signature format understood by `mstsc.exe`. The normal signing path does not invoke `rdpsign.exe`.

## Required configuration value

The configured value must be the 64-character SHA-256 certificate hash returned by:

```powershell
$certificate.GetCertHashString(
    [System.Security.Cryptography.HashAlgorithmName]::SHA256
)
```

Do not use either of these values:

```powershell
$certificate.Thumbprint
```

That is normally the 40-character SHA-1 thumbprint.

```powershell
(Get-FileHash $cerPath -Algorithm SHA256).Hash
```

That is the hash of the exported `.cer` file rather than the certificate itself.

## Configuration sources

The SHA-256 certificate hash can be supplied through either source:

1. Environment variable:

   ```text
   MREMOTENG_RDP_SIGN_CERT_THUMBPRINT
   ```

2. Text file:

   ```text
   %LOCALAPPDATA%\mRemoteNG\native-rdp-signing-thumbprint.txt
   ```

The process environment variable has priority over the text file. Spaces and other non-hexadecimal characters are removed automatically, and hexadecimal letters are normalized to uppercase.

The final normalized value must contain exactly 64 hexadecimal characters.

## Create and configure a local signing certificate

Run this PowerShell script as the same Windows user that runs mRemoteNG:

```powershell
$cert = New-SelfSignedCertificate `
    -Subject "CN=mRemoteNG RDP Publisher" `
    -FriendlyName "mRemoteNG RDP Publisher" `
    -Type Custom `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature `
    -KeyExportPolicy Exportable `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears(10) `
    -TextExtension @(
        "2.5.29.37={text}1.3.6.1.4.1.311.54.1.2"
    )

$cerPath = Join-Path $env:TEMP "mRemoteNG-RDP-Publisher.cer"

Export-Certificate `
    -Cert $cert `
    -FilePath $cerPath `
    -Force | Out-Null

Import-Certificate `
    -FilePath $cerPath `
    -CertStoreLocation "Cert:\CurrentUser\Root" | Out-Null

Import-Certificate `
    -FilePath $cerPath `
    -CertStoreLocation "Cert:\CurrentUser\TrustedPublisher" | Out-Null

$sha256 = $cert.GetCertHashString(
    [System.Security.Cryptography.HashAlgorithmName]::SHA256
)

$configDirectory = Join-Path $env:LOCALAPPDATA "mRemoteNG"
New-Item $configDirectory -ItemType Directory -Force | Out-Null

Set-Content `
    -Path (Join-Path $configDirectory "native-rdp-signing-thumbprint.txt") `
    -Value $sha256 `
    -NoNewline

Write-Host "RDP signing certificate SHA-256 hash:"
Write-Host $sha256
Write-Host "Length: $($sha256.Length)"
```

Expected length:

```text
64
```

The certificate remains in `CurrentUser\My` with its private key. Its public certificate is trusted in `CurrentUser\Root` and `CurrentUser\TrustedPublisher`.

## Configure through an environment variable

Instead of the text file:

```powershell
[Environment]::SetEnvironmentVariable(
    "MREMOTENG_RDP_SIGN_CERT_THUMBPRINT",
    $sha256,
    "User")
```

Restart mRemoteNG after changing a persistent environment variable.

## Signing flow

For every native launch, mRemoteNG:

1. Creates a UTF-16LE `.rdp` file under:

   ```text
   %LOCALAPPDATA%\mRemoteNG\Temp\Rdp
   ```

2. Reads and normalizes the configured SHA-256 certificate hash.
3. Finds the matching certificate in `CurrentUser\My` or `LocalMachine\My`.
4. Checks that the certificate has a private key and is currently valid.
5. Selects the security-sensitive RDP settings and creates `signscope:s:`.
6. Creates a detached SHA-256 CMS/PKCS#7 signature with the certificate private key.
7. Adds `signature:s:` to the generated RDP file.
8. Starts `mstsc.exe` with the signed file.

The signature implementation follows the RDP signature envelope reverse engineered by the open-source `nfedera/rdpsign` project and later .NET implementations of the same format.

When signing fails:

- `mstsc` still starts with the unsigned file;
- a warning is shown once per mRemoteNG application session;
- every failure is written to the dedicated signing diagnostics log.

A successful launch is recorded in the normal mRemoteNG log as:

```text
Launched mstsc.exe for RDP connection '<name>' using a signed RDP file.
```

## Verify the generated file

After starting a native connection, inspect the newest temporary RDP file before it is deleted:

```powershell
$rdp = Get-ChildItem `
    "$env:LOCALAPPDATA\mRemoteNG\Temp\Rdp" `
    -Filter *.rdp |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

Select-String `
    -Path $rdp.FullName `
    -Pattern '^signscope:s:','^signature:s:'
```

Both lines should be present.

## Replace an old 40-character configured value

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object FriendlyName -eq "mRemoteNG RDP Publisher" |
    Where-Object HasPrivateKey |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

$sha256 = $cert.GetCertHashString(
    [System.Security.Cryptography.HashAlgorithmName]::SHA256
)

$sha256 | Set-Content `
    "$env:LOCALAPPDATA\mRemoteNG\native-rdp-signing-thumbprint.txt" `
    -NoNewline
```

## Disable signing

```powershell
Remove-Item `
    "$env:LOCALAPPDATA\mRemoteNG\native-rdp-signing-thumbprint.txt" `
    -Force `
    -ErrorAction SilentlyContinue

[Environment]::SetEnvironmentVariable(
    "MREMOTENG_RDP_SIGN_CERT_THUMBPRINT",
    $null,
    "User")
```

## References

- Microsoft `rdpsign` command documentation: <https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/rdpsign>
- Reverse-engineered RDP signature format: <https://github.com/nfedera/rdpsign>
