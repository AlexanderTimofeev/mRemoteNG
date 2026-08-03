# Signing temporary RDP files

Native `mstsc` mode generates a temporary `.rdp` file for every connection launch. Windows can display a security confirmation dialog for unsigned files, especially when clipboard, drives, printers, smart cards, audio, or other device redirection is enabled.

mRemoteNG signs generated files in-process by using the configured Windows certificate and the standard CMS/PKCS#7 signature format understood by `mstsc.exe`.

## Certificate requirement

The certificate is an **RDP file publisher certificate**. Use a certificate with the **Code Signing** enhanced key usage:

```text
1.3.6.1.5.5.7.3.3
```

Do not use the Remote Desktop Authentication EKU:

```text
1.3.6.1.4.1.311.54.1.2
```

That EKU identifies a certificate used by an RDP server to authenticate the remote endpoint. It is not the publishing purpose used to sign an `.rdp` configuration file. Modern Windows clients can reject an RDP-file signature created with a certificate that has an incompatible EKU.

The certificate must also:

- contain a private key available to the mRemoteNG user;
- allow digital signatures;
- be currently valid;
- be trusted in `CurrentUser\Root` or through a normal trusted certificate chain;
- be present in `CurrentUser\TrustedPublisher` for publisher trust.

## Required configuration value

The configured value must be the 64-character SHA-256 certificate hash returned by:

```powershell
$certificate.GetCertHashString(
    [System.Security.Cryptography.HashAlgorithmName]::SHA256
)
```

Do not use `$certificate.Thumbprint`; that is normally the 40-character SHA-1 thumbprint. Do not use `Get-FileHash` on an exported `.cer`; that hashes the file container rather than the certificate.

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

The process environment variable has priority over the text file. Restart mRemoteNG after changing a persistent environment variable.

## Create and configure a local publisher certificate

Run this PowerShell script as the same Windows user that runs mRemoteNG:

```powershell
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=mRemoteNG RDP File Publisher" `
    -FriendlyName "mRemoteNG RDP File Publisher" `
    -KeyAlgorithm RSA `
    -KeyLength 2048 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -NotAfter (Get-Date).AddYears(10)

$cerPath = Join-Path $env:TEMP "mRemoteNG-RDP-File-Publisher.cer"

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

[Environment]::SetEnvironmentVariable(
    "MREMOTENG_RDP_SIGN_CERT_THUMBPRINT",
    $sha256,
    "User")

$cert | Select-Object Subject, FriendlyName, Thumbprint, HasPrivateKey, NotAfter
Write-Host "SHA-256 certificate hash: $sha256"
Write-Host "Length: $($sha256.Length)"
```

Expected SHA-256 hash length:

```text
64
```

Completely close and restart mRemoteNG after running the script.

## Verify the EKU

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object FriendlyName -eq "mRemoteNG RDP File Publisher" |
    Where-Object HasPrivateKey |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

$cert.Extensions |
    Where-Object Oid.Value -eq '2.5.29.37' |
    ForEach-Object { $_.Format($true) }
```

The output must include Code Signing (`1.3.6.1.5.5.7.3.3`).

## Signing flow

For every native launch, mRemoteNG:

1. Creates a UTF-16LE `.rdp` file under `%LOCALAPPDATA%\mRemoteNG\Temp\Rdp`.
2. Reads and normalizes the configured SHA-256 certificate hash.
3. Finds the matching certificate in `CurrentUser\My` or `LocalMachine\My`.
4. Checks that the certificate has a private key and is currently valid.
5. Selects the security-sensitive RDP settings and creates `signscope:s:`.
6. Creates a detached SHA-256 CMS/PKCS#7 signature.
7. Adds `signature:s:` to the generated RDP file.
8. Starts `mstsc.exe` with the signed file.

When signing fails, `mstsc` still starts with the unsigned file, a warning is shown once per application session, and the complete exception is written to `%LOCALAPPDATA%\mRemoteNG\Logs\native-rdp-signing.log`.

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
- Microsoft RDP file security policy documentation: <https://learn.microsoft.com/en-us/windows-server/remote/remote-desktop-services/remotepc/manage-rdp-file-security-settings-with-group-policy>
- Reverse-engineered RDP signature format: <https://github.com/nfedera/rdpsign>
