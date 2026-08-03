# Native RDP signing diagnostics

When temporary RDP-file signing is configured, mRemoteNG writes a dedicated diagnostic log to:

```text
%LOCALAPPDATA%\mRemoteNG\Logs\native-rdp-signing.log
```

The log is independent of the normal mRemoteNG message log. A signing failure popup shows both the log path and an eight-character operation ID. Use that operation ID to find the exact attempt.

## Recorded information

Each signing attempt records:

- local and UTC timestamps;
- mRemoteNG executable path and process ID;
- Windows identity;
- OS and process architecture;
- the effective signing configuration source;
- process, user, machine and file configuration values and their lengths;
- the generated RDP file path, size, timestamp and SHA-256 hash;
- certificate-store locations inspected;
- selected certificate subject, SHA-1 thumbprint, SHA-256 hash, validity dates and private-key availability;
- `rdpsign.exe` path and file version;
- the exact command line used for every signing attempt;
- process ID, duration, exit code in hexadecimal and decimal forms;
- complete stdout and stderr from `rdpsign.exe`;
- whether the SHA-1 compatibility attempt was started and its result.

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
BEGIN native RDP signing attempt
```

and ends with:

```text
END native RDP signing attempt
```

When reporting a problem, include the complete block matching the operation ID shown in the popup.
