# Native Windows RDP client (`mstsc.exe`)

## Goal

Allow an ordinary mRemoteNG RDP connection to open in the native Windows Remote Desktop client while retaining the existing RDP property UI and `ConnectionInfo` model.

The user never creates, edits, validates, selects, or maintains an `.rdp` file. On every native launch, mRemoteNG resolves the effective connection and credential values, generates a fresh temporary file, supplies passwords through Windows Credential Manager, starts `mstsc.exe`, and performs best-effort cleanup.

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

1. Resolve effective connection values, including inherited values, linked credentials, alternate address, runtime hostname resolution, wait-for-host handling, and `Force.NoCredentials`.
2. Run the configured pre-connection external application.
3. For `RDP + NativeMstsc`, validate that the selected options have a safe native equivalent.
4. Resolve destination and RD Gateway credentials.
5. Write eligible passwords to Windows Credential Manager.
6. Generate a unique temporary `.rdp` file from the effective settings.
7. Start `mstsc.exe` with the file and native-only command-line switches.
8. Return without entering the embedded protocol lifecycle.

`NativeRdpLauncher` is deliberately not a `ProtocolBase` implementation. `ProtocolBase` assumes a hosted WinForms control, mRemoteNG tab lifecycle, and embedded connection events that a top-level `mstsc.exe` process cannot reliably provide.

## Components

### `RdpFileSerializer`

Serializes effective `ConnectionInfo` values into a deterministic UTF-16LE `.rdp` document. It never writes a plaintext password or `password 51` value.

Implemented mappings include:

- hostname, custom port, resolved username and domain hint;
- fullscreen, predefined resolutions, custom width/height, SmartSize, dynamic resolution and multimonitor mode;
- color depth, desktop scale percentage and bitmap cache;
- wallpaper, themes, font smoothing, desktop composition and UI-performance flags;
- clipboard, printers, COM ports, smart cards and drive redirection;
- drive modes preserve `None`, `All`, local fixed drives and custom drive-letter lists;
- remote audio, quality, microphone capture and WebAuthn;
- authentication level, CredSSP, credential prompting, load-balance information, redirection server name and Entra/AAD authentication flag;
- RD Gateway host, usage method, credential source, username hint, shared-credential behavior and access-token property;
- alternate shell/start program, working directory and RemoteApp program/arguments.

Properties that exist only for the embedded ActiveX lifecycle are not serialized, including the selected COM control version, mRemoteNG tab resize behavior, idle-timeout callbacks, view-only input filtering and embedded reconnect events.

A stored `RDPSignScope`/`RDPSignature` pair is not copied. A signature covers the original RDP payload and becomes invalid after mRemoteNG regenerates the file. Generated files are therefore intentionally unsigned.

Microsoft's current property reference is:

- <https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties>

### `RdpCredentialResolver`

Resolves the credentials that embedded RDP would otherwise obtain inside `RdpProtocol.SetCredentials()`.

Destination credential sources supported by native mode:

- credentials stored directly on the connection;
- inherited and linked credential records through the normal `ConnectionInfo` getters;
- Windows current-user or configured default credentials when the connection username is empty;
- Delinea Secret Server;
- Clickstudios Passwordstate;
- 1Password CLI;
- Password Safe;
- Vault/OpenBao;
- Microsoft LAPS.

The resolver also:

- splits `DOMAIN\user` into its domain and username components;
- preserves UPN usernames;
- resolves separate RD Gateway credentials and providers;
- reuses destination credentials when `RDGatewayUseConnectionCredentials = Yes`;
- skips password resolution for `Force.NoCredentials`, prompt-only, Restricted Admin and Remote Credential Guard launches.

A provider failure is reported as an actionable native-launch error; the code does not silently fall back to stale or empty credentials.

### `WindowsCredentialManager`

Uses `CredWriteW` directly to store mstsc credentials under the conventional targets:

- destination: `TERMSRV/<normalized-destination-host>`;
- separate gateway credentials: `TERMSRV/<normalized-gateway-host>`.

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
- Remote Credential Guard is enabled;
- smart-card or access-token gateway authentication is selected.

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
- a crash may temporarily leave a file, but no user maintenance is required because a later native launch performs stale cleanup.

The directory is inside the current user's Local AppData profile. The file contains connection settings and may contain an RD Gateway access token when that property is configured, but never a destination or gateway password.

### `NativeRdpLauncher`

Coordinates validation, credential preparation, temporary-file creation, process arguments, launch logging and cleanup.

The executable is resolved as:

`Path.Combine(Environment.SystemDirectory, "mstsc.exe")`

`ProcessStartInfo.ArgumentList` is used instead of manually quoted argument strings.

Native-only switches are added when applicable:

- `/admin`;
- `/f` for a forced fullscreen launch;
- `/restrictedAdmin`;
- `/remoteGuard`;
- `/prompt`.

The generated `.rdp` supplies the remaining connection settings. The documented mstsc command-line reference is:

- <https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/mstsc>

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

## Explicitly unsupported native combinations

Native launch fails with a clear message rather than ignoring these settings:

- mRemoteNG-managed SSH tunnel — its lifetime is currently coupled to the embedded tab/protocol path;
- `Force.ViewOnly` — view-only is implemented by filtering input messages in the embedded control;
- Hyper-V `UseVmId` or Enhanced Session mode — these depend on the embedded RDP/Hyper-V control path;
- simultaneous Restricted Admin and Remote Credential Guard.

These connections remain usable by selecting `Embedded`.

## RD Gateway behavior

A destination credential stored as `TERMSRV/<host>` is not assumed to be a separate RD Gateway credential.

Implemented behavior:

- shared destination/gateway credentials set `promptcredentialonce:i:1`;
- separate gateway credentials are resolved and stored under the gateway target;
- smart-card and access-token modes do not create password credentials;
- an access token is passed only through its RDP property;
- if destination and gateway resolve to the same target but use different credentials, the destination credential is preserved and mstsc is allowed to prompt for the gateway credential.

## Lifecycle limitations

mRemoteNG does not own the native mstsc window and cannot reliably map the bootstrap process lifetime to the actual RDP session lifetime. Therefore native mode does not provide:

- an mRemoteNG connection tab;
- embedded reconnect/status events;
- reliable session-close tracking;
- `RDPMinutesToIdleTimeout`/`RDPAlertIdleTimeout` callbacks;
- `RetryOnFirstConnect` polling after mstsc has started;
- execution of `PostExtApp` when the native session exits;
- view-only filtering.

These are inherent differences between embedded ActiveX and a separate native process, not hidden fallbacks.

## Unsigned RDP policy compatibility

The generated file cannot safely reuse an existing signature. Enterprise Windows policy can require signed `.rdp` files or warn/block files from unknown publishers. On such managed machines, native launch may be blocked by the local Remote Desktop Client policy even though the generated file is valid.

Supporting these environments requires a future certificate-selection/signing feature; silently copying an unrelated stored signature would not solve the policy requirement.

## Error handling

Native launch reports actionable errors through `Runtime.MessageCollector`, including:

- missing `mstsc.exe`;
- empty hostname or invalid port;
- temporary-file creation failure;
- Credential Manager Win32 error;
- external credential-provider failure;
- unsupported SSH tunnel, view-only or Hyper-V combination;
- mutually exclusive security modes;
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
- hostname and username normalized before credential creation;
- invalid stored RDP signatures are not propagated.

## Automated validation

Implemented tests cover:

- old connections default to `Embedded`;
- native mode can be stored in the model;
- custom address/port and domain-qualified username serialization;
- resolved credential hints override stored placeholders;
- destination and gateway passwords never enter the `.rdp` payload;
- shared gateway credentials and `promptcredentialonce`;
- stored signature omission;
- predefined and custom resolution mapping;
- SmartSize mapping;
- desktop scale percentages and `Auto` omission;
- all, custom and disabled drive redirection modes;
- domain-prefix credential resolution;
- destination credentials reused for the gateway when configured;
- `Force.NoCredentials` resolver behavior;
- native command-line force flags and security switches;
- SQL expected-schema column;
- SQL `NativeMstsc` round trip;
- old SQL schema without the column defaults to `Embedded`.

Repository `PR_Validation` additionally builds tests/specs for x86 and x64, builds the application for x86, x64 and ARM64, and performs the configured application smoke tests.

## Manual Windows validation checklist

- standard hostname and non-default port;
- domain username, UPN username and local account;
- direct, linked, inherited and external-provider credentials;
- saved credential and prompt-only mode;
- Restricted Admin and Remote Credential Guard;
- fullscreen and mixed-DPI multimonitor behavior;
- local fixed, all and custom drives;
- clipboard, printers, smart cards, microphone and WebAuthn;
- shared and separate RD Gateway authentication;
- RemoteApp/start program;
- parallel native sessions;
- forced mRemoteNG termination followed by stale-file cleanup;
- expected rejection of SSH-tunnel, view-only and Hyper-V enhanced combinations;
- behavior on a machine with a policy that blocks unsigned `.rdp` files.
