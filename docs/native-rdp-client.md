# Native Windows RDP client (`mstsc.exe`)

## Goal

Allow an ordinary mRemoteNG RDP connection to open in the native Windows Remote Desktop client while retaining the existing RDP property UI and `ConnectionInfo` model.

The user never creates, edits, validates, selects, or maintains an `.rdp` file. On every native launch, mRemoteNG generates a fresh temporary file from the effective connection settings, supplies credentials through Windows Credential Manager when appropriate, starts `mstsc.exe`, and performs best-effort cleanup.

## User-facing behavior

Each RDP connection has an **RDP Client** property:

- `Embedded` — existing ActiveX/MSTSCLib behavior inside an mRemoteNG tab.
- `NativeMstsc` — launch the system `mstsc.exe` as a top-level Windows window.

The protocol remains `RDP`; it is not converted to `Ext.App`, so the complete RDP property set remains visible in the connection editor.

Compatibility rules:

- existing XML or SQL connections without `RdpClientMode` load as `Embedded`;
- invalid stored values also fall back to `Embedded`;
- the setting is per connection and is intentionally not inherited in the first implementation.

## Launch architecture

Native launch is selected before mRemoteNG creates an embedded protocol control, panel, tab, or MSTSCLib COM object.

High-level flow:

1. Resolve effective connection values, including inherited values, linked credentials, alternate address, runtime hostname resolution, and `Force.NoCredentials`.
2. Run the configured pre-connection external application.
3. For `RDP + NativeMstsc`, invoke `NativeRdpLauncher` and return from the embedded connection path.
4. Otherwise continue through the existing `RdpProtocol` path.

`NativeRdpLauncher` is deliberately not a `ProtocolBase` implementation. `ProtocolBase` assumes a hosted WinForms control, mRemoteNG tab lifecycle, and embedded connection events that a top-level `mstsc.exe` process cannot reliably provide.

## Components

### `RdpFileSerializer`

Serializes effective `ConnectionInfo` values into a deterministic UTF-16LE `.rdp` document. It never writes a plaintext password or `password 51` value.

Implemented mappings include:

- hostname, custom port, username and domain hint;
- fullscreen, predefined resolutions, custom width/height, SmartSize, dynamic resolution and multimonitor mode;
- color depth, desktop scale percentage and bitmap cache;
- wallpaper, themes, font smoothing, desktop composition and UI-performance flags;
- clipboard, printers, COM ports, smart cards and drive/custom-drive redirection;
- remote audio, quality, microphone capture and WebAuthn;
- authentication level, CredSSP, credential prompting, load-balance information, redirection server name and Entra/AAD authentication flag;
- RD Gateway host, usage method, credential source and access-token property;
- alternate shell/start program, working directory, RemoteApp program/arguments, signing scope and signature.

Properties that exist only for the embedded ActiveX lifecycle are not serialized, including the selected COM control version, mRemoteNG tab resize behavior, view-only filtering and embedded reconnect events.

### `WindowsCredentialManager`

Uses `CredWriteW` directly to store an mstsc credential under the conventional target:

`TERMSRV/<normalized-host>`

`CredDeleteW` is available through the same wrapper for explicit cleanup/integration with the existing clear-cached-credentials action.

The implementation does not use:

- `cmdkey.exe`;
- BAT/PowerShell helpers;
- shell interpolation;
- a password-bearing process argument;
- a password field in the generated `.rdp` file.

Credentials are not written when:

- `Force.NoCredentials` is active;
- username or password is empty;
- `AlwaysPromptForCredentials` is enabled;
- Restricted Admin is enabled;
- Remote Credential Guard is enabled.

The baseline implementation persists the credential in Windows Credential Manager. This avoids a race where mstsc reads credentials after the bootstrap process exits and avoids relying on an unreliable one-process/one-session lifetime relationship. Prompt-only behavior remains available through `AlwaysPromptForCredentials`.

### `TemporaryRdpFileStore`

Files are created under:

`%LOCALAPPDATA%\mRemoteNG\Temp\Rdp\<guid>.rdp`

Behavior:

- a unique file is generated for every launch;
- the user's global `Documents\Default.rdp` is never modified;
- launch failure triggers immediate best-effort deletion;
- successful launch schedules deletion after a short bootstrap delay;
- files older than one day are removed before a later native launch;
- a crash may temporarily leave a file, but no user maintenance is required because the next native launch performs stale cleanup.

The directory is inside the current user's Local AppData profile. The file contains connection settings and may contain an RD Gateway access token when that property is configured, but never the destination password.

### `NativeRdpLauncher`

Coordinates validation, credential preparation, temporary-file creation, process arguments, launch logging and cleanup.

The executable is resolved as:

`Path.Combine(Environment.SystemDirectory, "mstsc.exe")`

`ProcessStartInfo.ArgumentList` is used instead of manually quoted argument strings.

Native-only switches are added when applicable:

- `/admin`;
- `/restrictedAdmin`;
- `/remoteGuard`;
- `/prompt`.

The generated `.rdp` supplies the remaining connection settings.

## Persistence

### XML files

The optional attribute is written as:

`RdpClientMode="Embedded|NativeMstsc"`

Implemented changes:

- `AbstractConnectionRecord` property and default enum value;
- XML serializer output;
- backward-compatible XML deserialization;
- optional XSD attribute;
- normal clone/copy behavior through the existing connection-property mechanism.

### SQL database mode

SQL persistence uses an explicit `tblCons` DataTable schema, so the new value is mapped explicitly in:

- expected schema;
- row dirty checking;
- row serialization;
- backward-compatible deserialization.

The existing MSSQL and MySQL schema upgraders iterate `DataTableSerializer.GetExpectedSchema()`. They therefore add a missing `RdpClientMode` column automatically; no manual SQL migration is required.

## SSH tunnel behavior

Native mode currently rejects RDP connections that use an mRemoteNG-managed SSH tunnel. The current tunnel lifetime is coupled to the embedded protocol/tab path; launching mstsc without preserving that lifetime would produce a connection that loses its local tunnel.

The user receives an actionable warning and can use `Embedded` mode. Native SSH-tunnel support requires a separate tunnel-lifetime service and is intentionally not simulated with a fragile delay.

## RD Gateway credentials

A destination credential stored as `TERMSRV/<host>` is not assumed to be a separate RD Gateway credential.

Baseline behavior:

- gateway configurations that reuse destination credentials are supported through the generated `.rdp` settings;
- mstsc prompts when separate interactive gateway credentials are required;
- gateway access tokens are serialized only through the corresponding `.rdp` property and are not copied into the generic destination credential entry.

## Lifecycle limitations

mRemoteNG does not own the native mstsc window and cannot reliably map the bootstrap process lifetime to the actual RDP session lifetime. Therefore native mode does not provide:

- an mRemoteNG connection tab;
- embedded reconnect/status events;
- reliable session-close tracking;
- view-only filtering;
- automatic execution of tab-lifecycle actions after mstsc exits.

These are inherent differences between embedded ActiveX and a separate native process, not hidden fallbacks.

## Error handling

Native launch reports actionable errors through `Runtime.MessageCollector`, including:

- missing `mstsc.exe`;
- empty hostname or invalid port;
- temporary-file creation failure;
- Credential Manager Win32 error;
- unsupported native/SSH-tunnel combination;
- process-start failure.

A native-launch failure does not silently open an embedded session. The user can explicitly change the connection mode to `Embedded`.

## Security properties

- no password in `.rdp`;
- no password in command line;
- no shell invocation;
- no modification of global `Default.rdp`;
- unique temporary filenames;
- password buffer zeroed after `CredWriteW`;
- credential injection disabled for prompt, RCG, Restricted Admin and no-credentials modes;
- hostname and username normalized before credential creation.

## Automated validation

Implemented tests cover:

- old connections default to `Embedded`;
- native mode can be stored in the model;
- custom address/port and domain-qualified username serialization;
- password and `password 51` exclusion;
- predefined and custom resolution mapping;
- SmartSize mapping;
- desktop scale percentages and `Auto` omission;
- SQL expected-schema column;
- SQL `NativeMstsc` round trip;
- old SQL schema without the column defaults to `Embedded`.

Repository `PR_Validation` additionally builds tests/specs for x86 and x64, builds the application for x86, x64 and ARM64, and performs the configured application smoke tests.

## Manual Windows validation checklist

- standard hostname and non-default port;
- domain username, UPN username and local account;
- saved credential and prompt-only mode;
- Restricted Admin and Remote Credential Guard;
- fullscreen and mixed-DPI multimonitor behavior;
- custom drives, clipboard, printers, smart cards, microphone and WebAuthn;
- separate RD Gateway authentication;
- RemoteApp/start program;
- parallel native sessions;
- forced mRemoteNG termination followed by stale-file cleanup;
- expected rejection of an mRemoteNG-managed SSH tunnel.
