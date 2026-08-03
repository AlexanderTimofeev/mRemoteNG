# Signing temporary RDP files

Native `mstsc` mode generates a new temporary `.rdp` file for every connection launch. Windows can display a security confirmation dialog for unsigned files, especially when clipboard, drives, printers, smart cards, audio, or other device redirection is enabled.

mRemoteNG can sign each generated file with `%SystemRoot%\System32\rdpsign.exe` before starting `mstsc.exe`.

## Required configuration value

The configured value must be the **64-character SHA-256 certificate hash** returned by:

```powershell
$certificate.GetCertHashString(
    [System.Security.Cryptography.HashAlgorithmName]::SHA256
)
```

Do not use either of these values:

```powershell
$certificate.Thumbprint
```

That is normally the 40-character SHA-1 thumbprint. mRemoteNG derives it automatically from the matching certificate only when a Windows `rdpsign.exe` compatibility fallback is required.

```powershell
(Get-FileHash $cerPath -Algorithm SHA256).Hash
```

That is the hash of the exported `.cer` file, not the certificate hash expected by `rdpsign.exe`.

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

The environment variable has priority. Spaces and other non-hexadecimal characters are removed automatically, and hexadecimal letters are normalized to uppercase.

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

2. Reads and normalizes the configured SHA-256 hash.
3. Requires exactly 64 hexadecimal characters.
4. Finds the matching certificate in `CurrentUser\My` or `LocalMachine\My`.
5. Checks that the certificate has a private key and is currently valid.
6. Runs a hidden verbose signing command and captures all output:

   ```text
   rdpsign.exe /sha256 <64-character-hash> /v <file.rdp>
   ```

7. If that call fails with `0x80092004 (CRYPT_E_NOT_FOUND)`, retries with the matching certificate's 40-character SHA-1 thumbprint while keeping the `/sha256` option:

   ```text
   rdpsign.exe /sha256 <SHA-1-thumbprint> /v <file.rdp>
   ```

   Some Windows `rdpsign.exe` implementations use `/sha256` to select the signing mode but still locate the certificate by its SHA-1 store thumbprint.

8. Starts `mstsc.exe`.

When signing fails:

- `mstsc` still starts with the unsigned file;
- a warning is shown once per mRemoteNG application session;
- every failure is logged with both decimal and hexadecimal exit codes, the file path, certificate hashes, and full `rdpsign.exe` output.

When the compatibility retry succeeds, mRemoteNG logs a warning explaining that the SHA-1 thumbprint fallback was used.

## Verify manually

Read the configured value:

```powershell
$sha256 = Get-Content `
    "$env:LOCALAPPDATA\mRemoteNG\native-rdp-signing-thumbprint.txt"

$sha256
$sha256.Length
```

Find a generated file and test signing:

```powershell
$rdp = Get-ChildItem `
    "$env:LOCALAPPDATA\mRemoteNG\Temp\Rdp" `
    -Filter *.rdp |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

& "$env:SystemRoot\System32\rdpsign.exe" `
    /sha256 $sha256 `
    /v `
    $rdp.FullName

$LASTEXITCODE
```

Expected output includes:

```text
All rdp file(s) have been succesfully signed.
```

Expected exit code:

```text
0
```

To test the compatibility form manually:

```powershell
$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object FriendlyName -eq "mRemoteNG RDP Publisher" |
    Where-Object HasPrivateKey |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

& "$env:SystemRoot\System32\rdpsign.exe" `
    /sha256 $cert.Thumbprint `
    /v `
    $rdp.FullName
```

## Replace an old 40-character configured value

The configuration file must still contain the 64-character SHA-256 hash:

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

## Microsoft reference

- `rdpsign`: <https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/rdpsign>
