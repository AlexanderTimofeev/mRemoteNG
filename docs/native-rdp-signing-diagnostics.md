# Native RDP signing diagnostics

When temporary RDP-file signing is configured, mRemoteNG writes a dedicated diagnostic log to:

```text
%LOCALAPPDATA%\mRemoteNG\Logs\native-rdp-signing.log
```

The log is independent of the normal mRemoteNG message log. A signing failure popup shows both the log path and an eight-character operation ID. Use that operation ID to find the exact attempt.

## Recorded information

Each managed signing attempt records:

- local and UTC timestamps;
- mRemoteNG executable path and process ID;
- Windows identity;
- OS and process architecture;
- the effective signing configuration source;
- process, user, machine and file configuration values and their lengths;
- generated RDP file path, size, timestamp and SHA-256 hash before signing;
- certificate-store locations inspected;
- selected certificate subject, SHA-1 thumbprint, SHA-256 hash, validity dates, signature algorithm and private-key availability;
- number of RDP settings included in `signscope:s:`;
- output file path, size, timestamp and SHA-256 hash after signing;
- complete exception and stack trace when CMS/PKCS#7 signing fails.

The log does not record destination or gateway passwords and does not record the contents of the generated RDP file.

## Log rotation

When the current log exceeds 2 MiB it is moved to:

```text
%LOCALAPPDATA%\mRemoteNG\Logs\native-rdp-signing.log.previous
```

A new current log is then created.

## Collect the most recent attempt

After reproducing a signing failure, open the log:

```powershell
notepad "$env:LOCALAPPDATA\mRemoteNG\Logs\native-rdp-signing.log"
```

Or print the final 150 lines:

```powershell
Get-Content "$env:LOCALAPPDATA\mRemoteNG\Logs\native-rdp-signing.log" -Tail 150
```

The most useful block starts with:

```text
BEGIN native RDP managed signing attempt
```

and ends with:

```text
END native RDP managed signing attempt
```

A successful attempt contains:

```text
Managed CMS signature created.
SUCCESS: temporary RDP file was signed in-process.
```

When reporting a problem, include the complete block matching the operation ID shown in the popup.
