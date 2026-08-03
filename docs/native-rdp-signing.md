# Signing temporary RDP files

Native `mstsc` mode generates a temporary `.rdp` file for every connection launch. Windows can display a security confirmation dialog for unsigned files, especially when clipboard, drives, printers, smart cards, audio, or other device redirection is enabled.

mRemoteNG signs generated files in-process by using the configured Windows certificate and the CMS/PKCS#7 signature format understood by `mstsc.exe`.

## Certificate requirement

Microsoft documents the signing certificate as a trusted `.rdp` file publisher certificate. The public `rdpsign` documentation does not require one specific Enhanced Key Usage OID.

The certificate must:

- contain a private key available to the mRemoteNG user;
- allow digital signatures;
- be currently valid;
- be trusted in `CurrentUser\Root` or through a normal trusted certificate chain;
- be present in `CurrentUser\TrustedPublisher` when the publisher should be trusted locally.

An EKU extension may be absent. An absent EKU normally means the certificate is not restricted to a particular enhanced usage. Do not reject an otherwise valid publisher certificate solely because `$certificate.Extensions` contains no `2.5.29.37` extension.

The open-source reverse-engineered `nfedera/rdpsign` reference demonstrates a certificate with `serverAuth`, while other operational guidance commonly uses code-signing-style certificates. Because Microsoft does not publish an EKU requirement for `.rdp` publishers, EKU should be treated as diagnostic information rather than the cause of a signature-format verification failure.

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

The process environment variable has priority over the text file. A process can inherit an older value from PowerShell, Visual Studio, or Explorer. Always check the `Effective source` and `Effective hash` fields in `native-rdp-signing.log` after changing certificates.

## Create and configure a local publisher certificate

This example creates a code-signing-style certificate. That is a practical publisher certificate choice, but mRemoteNG does not require a specific EKU.

Run the script as the same Windows user that runs mRemoteNG:

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

## Inspect certificate usage extensions

Use `EnhancedKeyUsageList` first:

```powershell
$cert.EnhancedKeyUsageList |
    Select-Object FriendlyName, ObjectId
```

Inspect the raw EKU extension when present:

```powershell
$cert.Extensions |
    Where-Object Oid.Value -eq '2.5.29.37' |
    ForEach-Object { $_.Format($true) }
```

No output from both commands means the certificate has no EKU restriction. This is not by itself a signing error.

## Ensure the new hash reaches mRemoteNG

When replacing a certificate, set both the current PowerShell process value and the persistent user value before launching mRemoteNG from that shell:

```powershell
$env:MREMOTENG_RDP_SIGN_CERT_THUMBPRINT = $sha256

[Environment]::SetEnvironmentVariable(
    "MREMOTENG_RDP_SIGN_CERT_THUMBPRINT",
    $sha256,
    "User")

$sha256 | Set-Content `
    "$env:LOCALAPPDATA\mRemoteNG\native-rdp-signing-thumbprint.txt" `
    -NoNewline
```

Completely close all mRemoteNG processes before starting it again.

## Trust the publisher and suppress the security dialog

A valid signature proves who published the RDP file, but Windows can still show a one-time trust dialog for a publisher that has not yet been approved for RDP launches.

For one Windows user on one computer:

1. Select the required redirected resources, such as Clipboard or WebAuthn.
2. Select **Remember my choice for remote connections from this publisher**.
3. Select **Connect**.

The remembered choice is tied to the signing certificate. Replacing the certificate creates a new publisher identity and requires approval again.

For centrally managed or repeatable deployment, configure this policy:

```text
Computer Configuration or User Configuration
  Administrative Templates
    Windows Components
      Remote Desktop Services
        Remote Desktop Connection Client
          Specify thumbprints of certificates representing trusted .rdp publishers
```

When a matching signing certificate is listed in this policy, Remote Desktop Connection skips the security warning and automatically enables the redirections requested by the signed RDP file.

On systems whose policy still uses the older name **Specify SHA1 thumbprints of certificates representing trusted .rdp publishers**, use the certificate's normal 40-character SHA-1 thumbprint:

```powershell
$cert.Thumbprint
```

On Windows with the July 2026 or later RDP security policy update, the policy also supports SHA-2 thumbprints. Follow the Help text shown in the local Group Policy editor for the required SHA-2 prefix and format on that Windows build.

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
