# Signing temporary RDP files

Native `mstsc` mode generates a new temporary `.rdp` file for every connection launch. Recent Windows versions can display a security confirmation dialog for unsigned `.rdp` files, especially when the file enables clipboard, drive, printer, smart-card, audio, or other device redirection.

mRemoteNG can sign every generated file with `rdpsign.exe` before launching `mstsc.exe`. A trusted signature prevents the repeated unsigned-file warning on machines that trust the signing certificate.

## Configuration sources

The signing certificate thumbprint can be supplied through either of these sources:

1. Environment variable:

   ```text
   MREMOTENG_RDP_SIGN_CERT_THUMBPRINT
   ```

2. Text file:

   ```text
   %LOCALAPPDATA%\mRemoteNG\native-rdp-signing-thumbprint.txt
   ```

The environment variable has priority. Spaces and other non-hexadecimal characters are removed automatically and hexadecimal letters are normalized to uppercase.

Supported thumbprint formats:

- 40 hexadecimal characters: SHA-1 certificate thumbprint, passed to `rdpsign.exe /sha1`;
- 64 hexadecimal characters: SHA-256 certificate thumbprint, passed to `rdpsign.exe /sha256`.

The SHA-1 thumbprint is only used to locate the certificate in the Windows certificate store. It does not change the certificate's signature algorithm; the certificate created by the script below uses SHA-256.

When no thumbprint is configured, mRemoteNG launches the generated file without signing it. When a thumbprint is configured but signing fails, mRemoteNG records a warning and continues by launching the file unsigned. This keeps native RDP usable even when the certificate configuration is missing or invalid.

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

# Use the certificate thumbprint, not Get-FileHash of the exported .cer file.
$thumbprint = $cert.Thumbprint.Replace(" ", "")

$configDirectory = Join-Path $env:LOCALAPPDATA "mRemoteNG"
New-Item $configDirectory -ItemType Directory -Force | Out-Null

Set-Content `
    -Path (Join-Path $configDirectory "native-rdp-signing-thumbprint.txt") `
    -Value $thumbprint `
    -NoNewline

Write-Host "RDP signing certificate configured:"
Write-Host $thumbprint
```

This creates the certificate in `CurrentUser\My`, trusts its public certificate in `CurrentUser\Root`, and adds it to `CurrentUser\TrustedPublisher`. No machine-wide certificate installation is required.

Do not use this value:

```powershell
(Get-FileHash $cerPath -Algorithm SHA256).Hash
```

That command returns the hash of the exported `.cer` file itself, not the certificate thumbprint expected by `rdpsign.exe`.

## Configure through an environment variable

Instead of the text file, set the environment variable for the current user:

```powershell
[Environment]::SetEnvironmentVariable(
    "MREMOTENG_RDP_SIGN_CERT_THUMBPRINT",
    $thumbprint,
    "User")
```

Restart mRemoteNG after changing a persistent environment variable.

## Signing flow

For every native launch, mRemoteNG performs the following steps:

1. Generate a uniquely named UTF-16LE `.rdp` file under:

   ```text
   %LOCALAPPDATA%\mRemoteNG\Temp\Rdp
   ```

2. Resolve and normalize the configured thumbprint.
3. Select `/sha1` for a 40-character thumbprint or `/sha256` for a 64-character thumbprint.
4. Run:

   ```text
   %SystemRoot%\System32\rdpsign.exe /sha1 <thumbprint> /q <file.rdp>
   ```

   or:

   ```text
   %SystemRoot%\System32\rdpsign.exe /sha256 <thumbprint> /q <file.rdp>
   ```

5. Launch the signed file with `mstsc.exe` when signing succeeds.
6. If signing fails, write a warning and launch the file unsigned.
7. Schedule normal temporary-file cleanup.

A successful signed launch is recorded in the log as:

```text
Launched mstsc.exe for RDP connection '<name>' using a signed RDP file.
```

A signing failure is recorded as a warning similar to:

```text
Unable to sign the temporary RDP file. It will be launched unsigned. ...
```

## Verify the certificate

List candidate certificates with private keys:

```powershell
Get-ChildItem Cert:\CurrentUser\My |
    Where-Object HasPrivateKey |
    Select-Object Subject, FriendlyName, Thumbprint, NotAfter
```

Check the configured file:

```powershell
Get-Content "$env:LOCALAPPDATA\mRemoteNG\native-rdp-signing-thumbprint.txt"
```

The value should match the `Thumbprint` displayed for the certificate named `mRemoteNG RDP Publisher` and should normally contain 40 hexadecimal characters.

You can test signing manually without modifying the RDP file by using `/l`:

```powershell
$thumbprint = Get-Content "$env:LOCALAPPDATA\mRemoteNG\native-rdp-signing-thumbprint.txt"
& "$env:SystemRoot\System32\rdpsign.exe" /sha1 $thumbprint /l "C:\Path\To\Test.rdp"
```

## Troubleshooting

### Native mode does nothing after enabling signing

Older builds aborted the complete launch when `rdpsign.exe` returned an error. Update to a build containing the signing fallback fix.

Also replace any value generated with `Get-FileHash` by the real certificate thumbprint:

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object FriendlyName -eq "mRemoteNG RDP Publisher" |
    Where-Object HasPrivateKey |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

$cert.Thumbprint | Set-Content `
    "$env:LOCALAPPDATA\mRemoteNG\native-rdp-signing-thumbprint.txt" `
    -NoNewline
```

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
