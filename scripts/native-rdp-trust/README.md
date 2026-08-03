# Native RDP trust bootstrap

This folder contains the one-time workstation setup for native `mstsc` mode.

## Install

Double-click:

```text
Install-MRemoteNgNativeRdpTrust.cmd
```

Accept the UAC prompt. The final status must be:

```text
READY
```

Restart mRemoteNG if it was already running.

## What the installer does

The PowerShell script:

- creates or reuses `CN=mRemoteNG RDP File Publisher` in `LocalMachine\My`;
- imports its public certificate into `LocalMachine\Root` and `LocalMachine\TrustedPublisher`;
- grants the currently logged-on Windows user read access to the private key;
- writes the certificate SHA-256 hash to the machine environment;
- writes the same SHA-256 hash to the target user's environment;
- writes `%LOCALAPPDATA%\mRemoteNG\native-rdp-signing-thumbprint.txt` for the target user;
- sets `AllowSignedFiles=1` in the machine RDP client policy;
- adds the certificate SHA-1 thumbprint to `TrustedCertThumbprints` while preserving existing publishers;
- deliberately leaves `AllowUnsignedFiles` unchanged;
- runs `gpupdate /target:computer /force`;
- validates all stores, hashes, private-key permissions, environment values, policy values and `mstsc.exe`.

The generated RDP files and CMS signature use SHA-256. The classic Group Policy allow-list uses the certificate SHA-1 thumbprint for compatibility with Windows builds that expose `TrustedCertThumbprints`.

## Target user

The script automatically detects the currently logged-on interactive user. To configure a different account, run an elevated PowerShell window:

```powershell
.\Install-MRemoteNgNativeRdpTrust.ps1 `
    -TargetUser 'COMPUTER\UserName'
```

That user's registry hive must be loaded, so the user should be logged on.

## Validate only

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
    -File .\Install-MRemoteNgNativeRdpTrust.ps1 `
    -ValidateOnly
```

## Skip gpupdate

For diagnostics only:

```powershell
.\Install-MRemoteNgNativeRdpTrust.ps1 -SkipGpUpdate
```

## Log

```text
%ProgramData%\mRemoteNG\NativeRdpTrust\native-rdp-trust.log
```

## Modified policy values

```text
HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\AllowSignedFiles
HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services\TrustedCertThumbprints
```

The script does not enable unknown or unsigned RDP publishers and does not export the signing private key.
