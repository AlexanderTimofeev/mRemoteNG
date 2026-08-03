# Native RDP client: `mstsc.exe`

## Goal

Allow a normal mRemoteNG RDP connection to open in the native Windows Remote Desktop client while retaining the existing RDP connection editor and `ConnectionInfo` model.

The user never creates, selects, validates, updates, or maintains an `.rdp` file. For every native launch, mRemoteNG resolves the effective connection, generates a temporary file, supplies eligible credentials through Windows Credential Manager, starts `mstsc.exe`, and performs best-effort cleanup.

## User-facing behavior

Each RDP connection has an **RDP Client** property:

- `Embedded mRemoteNG tab` — existing ActiveX/MSTSCLib behavior.
- `mstsc (Native Windows Remote Desktop client)` — opens the system client as a separate top-level window.

The protocol remains `RDP`; it is not converted to `Ext.App`, so the existing RDP properties remain available.

Compatibility rules:

- old XML and SQL connections without `RdpClientMode` load as `Embedded`;
- invalid stored values fall back to `Embedded`;
- the mode is stored per connection and is intentionally not folder-inherited in the first implementation.

## Launch architecture

Native mode branches before mRemoteNG creates a panel, tab, protocol instance, ActiveX control, or MSTSCLib COM object.

High-level flow:

1. Resolve the runtime hostname, alternate address, inherited/linked values, and wait-for-host behavior.
2. Run the configured pre-connection external application.
3. Validate that the selected connection options have a safe native equivalent.
4. Resolve destination and RD Gateway credentials.
5. Write eligible password credentials through `CredWriteW`.
6. Generate a unique temporary UTF-16LE `.rdp` file.
7. Launch `%SystemRoot%\System32\mstsc.exe` using `ProcessStartInfo.ArgumentList`.
8. Schedule temporary-file cleanup and return without entering the embedded protocol lifecycle.

`NativeRdpLauncher` is deliberately not a `ProtocolBase`. `ProtocolBase` assumes a hosted WinForms control and an mRemoteNG-managed connection lifecycle that a separate `mstsc.exe` process cannot reliably expose.

## Components

### `RdpFileSerializer`

Creates an mstsc-compatible `.rdp` document from effective `ConnectionInfo` values.

Implemented mappings include:

- hostname, IPv6 formatting, custom port, username/domain hints;
- fullscreen, predefined/custom resolution, SmartSize, dynamic resolution and multimonitor;
- color depth, desktop scaling and bitmap cache;
- wallpaper, themes, font smoothing, desktop composition and performance flags;
- keyboard handling, clipboard, printers, COM ports, smart cards and drive redirection;
- `None`, all drives, local fixed drives and custom drive-letter modes;
- remote audio, audio quality, microphone and WebAuthn;
- authentication level, CredSSP, prompt behavior, load-balance information, redirection-server name and Entra/AAD authentication;
- explicit RD Gateway profile, usage mode, password/smart-card/token credential source and shared-credential behavior;
- alternate shell, working directory and RemoteApp program/arguments.

Security rules:

- no destination or gateway password is written to the file;
- `password 51` is never generated;
- CR, LF and NUL characters in string values are replaced so imported configuration values cannot inject additional RDP directives;
- a gateway access token is serialized only for Access Token mode and only when the launch is allowed to inject credentials;
- a stored `RDPSignScope`/`RDPSignature` is not copied because regenerating the payload invalidates the signature.

Microsoft references:

- <https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties>
- <https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc>

### `RdpCredentialResolver`

Resolves credentials without requiring an ActiveX control.

Supported sources:

- direct connection values;
- inherited and linked credential values through normal `ConnectionInfo` getters;
- Windows current-user defaults;
- configured default username, domain and password;
- Delinea Secret Server;
- Clickstudios Passwordstate;
- 1Password CLI;
- Password Safe;
- Vault/OpenBao;
- Microsoft LAPS;
- separate RD Gateway credentials and providers.

The default password/domain fallback follows embedded RDP behavior even when the connection already contains an explicit username.

Credential injection is disabled for:

- `Force.NoCredentials`;
- `AlwaysPromptForCredentials`;
- Restricted Admin;
- Remote Credential Guard;
- smart-card gateway authentication;
- access-token gateway authentication for password storage.

Restricted Admin and Remote Credential Guard do not receive username/domain hints in the generated file; Windows uses the integrated current-user security context.

A credential-provider failure aborts the native launch with an actionable error rather than silently using stale or empty values.

### `WindowsCredentialManager`

Uses Win32 Credential Manager directly.

Targets:

- destination: `TERMSRV/<normalized-destination-host>`;
- separate gateway credential: `TERMSRV/<normalized-gateway-host>`.

The implementation does not use:

- BAT or PowerShell helper files;
- `cmdkey.exe`;
- shell interpolation;
- password-bearing command-line arguments;
- plaintext passwords in `.rdp` files.

Credentials use local-machine persistence for the current Windows user. This avoids a race in which mstsc reads credentials after its bootstrap process changes or exits. It also means a successful native launch can update an existing Windows credential for the same `TERMSRV` target.

The existing **Clear Cached RDP Credentials** action removes both:

- mRemoteNG-created `Generic` credentials;
- mstsc-created `DomainPassword` credentials;
- destination and configured RD Gateway targets.

### `TemporaryRdpFileStore`

Files are created under:

`%LOCALAPPDATA%\mRemoteNG\Temp\Rdp\<guid>.rdp`

Behavior:

- unique file per launch;
- never modifies `Documents\Default.rdp`;
- immediate best-effort deletion on launch failure;
- scheduled deletion 30 seconds after a successful process start;
- files older than five minutes are removed before a later native launch;
- a process crash can temporarily leave a file, but no manual maintenance is required.

The file can contain connection settings and, specifically for gateway Access Token mode, a gateway token. It never contains a destination or gateway password.

### `NativeRdpLauncher`

Coordinates validation, credential resolution, Credential Manager writes, serialization, process launch, logging and cleanup.

Native switches are added when applicable:

- `/admin`;
- `/f`;
- `/restrictedAdmin`;
- `/remoteGuard`;
- `/prompt`.

The remaining settings are supplied by the temporary `.rdp` file.

## RD Gateway behavior

- explicit gateway settings include `gatewayprofileusagemethod:i:1`;
- password-based gateway credentials use `gatewaycredentialssource:i:0`;
- smart card uses source `1`;
- access token uses source `5`;
- shared destination/gateway credentials set `promptcredentialonce:i:1`;
- separate credentials are stored under the gateway `TERMSRV` target;
- stale access-token values are not serialized when another gateway authentication mode is selected;
- prompt, no-credentials and integrated-security launches omit gateway tokens;
- if destination and gateway resolve to the same target with different credentials, the destination credential is retained and mstsc can prompt for the gateway credential.

## Persistence

### XML

The optional value is stored as:

`RdpClientMode="Embedded|NativeMstsc"`

Changes include:

- model property and enum;
- XML serialization/deserialization;
- optional XSD attribute;
- backward-compatible defaulting;
- normal clone/copy behavior.

### SQL database mode

`RdpClientMode` is included in:

- the expected `tblCons` DataTable schema;
- dirty checking;
- serialization;
- backward-compatible deserialization.

Existing MSSQL and MySQL schema upgraders consume `DataTableSerializer.GetExpectedSchema()`, so they add a missing column automatically. No manual SQL migration is required.

## Explicitly unsupported combinations

Native launch fails with a clear message instead of silently ignoring these settings:

- mRemoteNG-managed SSH tunnel;
- View Only;
- Hyper-V VM ID;
- Hyper-V Enhanced Session;
- simultaneous Restricted Admin and Remote Credential Guard.

These connections remain available in `Embedded` mode.

## Lifecycle and audit limitations

mRemoteNG does not own the native mstsc window and cannot reliably map the bootstrap process to the actual RDP session.

Native mode therefore does not provide:

- an mRemoteNG tab;
- `OpenConnections`/active-session state;
- embedded connected/disconnected/closed events;
- a reliable session-close event;
- established/closed audit events based on the real remote session;
- `RDPMinutesToIdleTimeout` or `RDPAlertIdleTimeout` callbacks;
- embedded reconnect and `RetryOnFirstConnect` handling after mstsc starts;
- `PostExtApp` execution when the external session exits;
- View Only input filtering.

The launcher logs that mstsc was started, but it must not claim that remote authentication or session establishment succeeded.

## Unsigned RDP policy compatibility

Generated files are intentionally unsigned. An existing signature cannot be reused after the payload changes.

Enterprise policy can warn about or block unsigned `.rdp` files. Supporting environments that require trusted publishers needs a separate certificate-selection/signing feature.

Starting with newer Windows security updates, RDP file warnings and policies can be stricter, so this must be included in managed-device validation.

## Error handling

Native launch reports failures for:

- missing `mstsc.exe`;
- missing hostname or invalid port;
- unsupported native combination;
- external credential-provider failure;
- Credential Manager Win32 failure;
- temporary-file creation failure;
- process-start failure.

A failed native launch does not silently fall back to Embedded mode.

## Validation

Automated test sources cover:

- backward-compatible `Embedded` defaults;
- XML/SQL persistence and SQL round trip;
- address, port, username and resolution mappings;
- scaling, SmartSize, drive modes and command-line switches;
- absence of passwords/signatures from generated files;
- direct/default/provider credential resolution;
- domain-prefix parsing;
- shared and separate gateway behavior;
- correct gateway credential source/profile values;
- stale and suppressed gateway tokens;
- CR/LF/NUL directive-injection prevention.

Repository `PR_Validation` currently compiles the application for x86, x64 and ARM64, compiles the test/spec projects for x86 and x64, and runs the configured application smoke checks. It does not execute the complete NUnit suite; `dotnet test` remains a separate validation step.

Manual validation completed:

- native mstsc launch from a configured RDP connection;
- automatic temporary-file generation;
- automatic credential handoff for the tested connection;
- no user-managed BAT or `.rdp` file required.

Manual scenarios still recommended before merge/release:

- non-default port and IPv6;
- domain, UPN and local accounts;
- configured default password/domain fallback;
- each external credential provider used in production;
- Restricted Admin and Remote Credential Guard;
- fullscreen and mixed-DPI multimonitor;
- all/local/custom drives and device redirects;
- separate RD Gateway credentials and Access Token mode;
- RemoteApp/start program;
- parallel native launches;
- crash followed by stale-file cleanup;
- unsigned-RDP enterprise policy behavior.
