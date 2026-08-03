# Signing temporary RDP files

Native `mstsc` mode generates a new temporary `.rdp` file for every connection launch. Recent Windows versions can display a security confirmation dialog for unsigned `.rdp` files, especially when the file enables clipboard, drive, printer, smart-card, audio, or other device redirection.

mRemoteNG can sign every generated file with `rdpsign.exe` before launching `mstsc.exe`. A trusted signature prevents the repeated unsigned-file warning on machines that trust the signing certificate.

## Configuration sources

The signing certificate SHA-256 thumbprint can be supplied through either of these sources:

1. Environment variable:

   ```text
   MREMOTENG_RDP_SIGN_CERT_THUMBPRINT
   ```

2. Text file:

   ```text
   %LOCALAPPDATA%\mRemoteNG\native-rdp-signing-thumbprint.txt
   ```

The environment variable has priority. Spaces and other non-hexadecimal characters are removed automatically and hexadecimal letters are normalized to uppercase.

When no thumbprint is configured, mRemoteNG launches the generated file without signing it. When a thumbprint is configured but signing fails, the connection is not launched with an unsigned file; the failure is written to the mRemoteNG log.

## Create a local signing certificate

Run the following PowerShell commands as the same Windows user that runs mRemoteNG:

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

$sha256 = (Get-FileHash $cerPath -Algorithm SHA256).Hash

$configDirectory = Join-Path $env:LOCALAPPDATA "mRemoteNG"
New-Item $configDirectory -ItemType Directory -Force | Out-Null

Set-Content `
    -Path (Join-Path $configDirectory "native-rdp-signing-thumbprint.txt") `
    -Value $sha256 `
    -NoNewline

Write-Host "RDP signing certificate configured:"
Write-Host $sha256
```

This creates the certificate in `CurrentUser\My`, trusts its public certificate in `CurrentUser\Root`, and adds it to `CurrentUser\TrustedPublisher`. No machine-wide certificate installation is required.

## Configure through an environment variable

Instead of the text file, set the environment variable for the current user:

```powershell
[Environment]::SetEnvironmentVariable(
    "MREMOTENG_RDP_SIGN_CERT_THUMBPRINT",
    $sha256,
    "User")
```

Restart mRemoteNG after changing a persistent environment variable.

## Signing flow

For every native launch, mRemoteNG performs the following steps:

1. Generate a uniquely named UTF-16LE `.rdp` file under:

   ```text
   %LOCALAPPDATA%\mRemoteNG\Temp\Rdp
   ```

2. Resolve the configured thumbprint.
3. Run:

   ```text
   %SystemRoot%\System32\rdpsign.exe /sha256 <thumbprint> /q <file.rdp>
   ```

4. Abort if `rdpsign.exe` is missing, times out after 30 seconds, or returns a non-zero exit code.
5. Launch the signed file with `mstsc.exe`.
6. Schedule normal temporary-file cleanup.

A successful signed launch is recorded in the log as:

```text
Launched mstsc.exe for RDP connection '<name>' using a signed RDP file.
```

## Verify the certificate

List candidate certificates with private keys:

```powershell
Get-ChildItem Cert:\CurrentUser\My |
    Where-Object HasPrivateKey |
    Select-Object Subject, FriendlyName, Thumbprint, NotAfter
```

Verify that the configured value matches the SHA-256 thumbprint expected by `rdpsign.exe` and that the certificate is still valid.

## Troubleshooting

### `rdpsign.exe was not found`

Verify that this file exists:

```text
%SystemRoot%\System32\rdpsign.exe
```

### Certificate cannot be found

Confirm that the signing certificate is installed under:

```text
Cert:\CurrentUser\My
```

and that it has an accessible private key.

### Windows still shows a warning

Confirm that the same certificate is trusted under both:

```text
Cert:\CurrentUser\Root
Cert:\CurrentUser\TrustedPublisher
```

For managed domain computers, Group Policy can override local trust and RDP file publisher rules. The certificate may need to be deployed by an administrator to the appropriate trusted stores or publisher policy.

### Disable signing

Delete the thumbprint file and remove the environment variable:

```powershell
Remove-Item "$env:LOCALAPPDATA\mRemoteNG\native-rdp-signing-thumbprint.txt" `
    -Force `
    -ErrorAction SilentlyContinue

[Environment]::SetEnvironmentVariable(
    "MREMOTENG_RDP_SIGN_CERT_THUMBPRINT",
    $null,
    "User")
```

After restarting mRemoteNG, native RDP files are generated and launched without signing.

## Microsoft reference

- `rdpsign`: <https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/rdpsign>
