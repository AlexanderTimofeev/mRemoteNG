# Native RDP client (`mstsc.exe`)

## Goal

Allow a normal mRemoteNG RDP connection to open in the native Windows Remote Desktop client while keeping the existing RDP connection editor and `ConnectionInfo` model.

The user does not create, select, validate, update, or maintain an `.rdp` file. For every native launch, mRemoteNG resolves the effective connection, generates a temporary file, supplies eligible password credentials through Windows Credential Manager, starts `mstsc.exe`, and performs best-effort cleanup.

## User-facing setting

Each RDP connection has an **RDP Client** property:

- `Embedded mRemoteNG tab`
- `mstsc (Native Windows Remote Desktop client)`

The protocol remains `RDP`; this is not implemented as an external application connection.

Compatibility rules:

- old XML and SQL connections without `RdpClientMode` load as `Embedded`;
- invalid or missing stored values fall back to `Embedded`;
- the selected mode is copied when a connection is cloned;
- the mode is intentionally per connection and is not folder-inherited in this implementation.

## Launch architecture

Native mode branches in `ConnectionInitiator` before mRemoteNG creates a panel, tab, protocol instance, ActiveX control, or MSTSCLib COM object.

Flow:

1. Resolve the runtime hostname and normal connection values.
2. Run the configured pre-connection external application.
3. Validate settings that do not have a safe native equivalent.
4. Resolve destination and RD Gateway credentials.
5. Write eligible password credentials through `CredWriteW`.
6. Generate a unique UTF-16LE temporary `.rdp` file.
7. Launch `%SystemRoot%\System32\mstsc.exe` using `ProcessStartInfo.ArgumentList`.
8. Schedule temporary-file cleanup and return without entering the embedded protocol lifecycle.

`NativeRdpLauncher` is not a `ProtocolBase`: `ProtocolBase` assumes a hosted WinForms control and an mRemoteNG-managed connection lifecycle, neither of which applies to a separate `mstsc.exe` window.

## Components

### `RdpFileSerializer`

Maps the RDP properties available in this `v1.78.2-dev` upstream to an mstsc-compatible file:

- hostname, IPv6 and custom port;
- username/domain hints;
- fullscreen, SmartSize, dynamic resolution and color depth;
- bitmap cache and visual-performance flags;
- keyboard, clipboard, printers, COM ports and smart cards;
- no drives, all drives, local fixed drives, or selected drive letters;
- remote audio, audio quality and microphone;
- authentication level, CredSSP, load-balance info and redirection-server name;
- explicit RD Gateway usage and credential source;
- gateway access token when Access Token mode is selected;
- alternate shell and working directory.

The source feature had mappings for properties introduced in a different fork lineage. They are not referenced here when the corresponding property does not exist in this upstream model.

Security rules:

- destination and gateway passwords are never written to the `.rdp` file;
- passwords are never passed on the process command line;
- `password 51` is not generated;
- CR, LF and NUL in string settings are replaced so configuration data cannot inject additional RDP directives;
- a gateway token is emitted only for Access Token mode and only when credential injection is allowed.

### `RdpCredentialResolver`

Supports the credential sources present in this upstream:

- direct connection values;
- inherited values returned by normal `ConnectionInfo` getters;
- Windows current-user defaults;
- configured default username, domain and password;
- Delinea Secret Server;
- Clickstudios Passwordstate;
- 1Password CLI;
- Vault/OpenBao;
- separate RD Gateway credentials and providers.

The resolver mirrors embedded RDP fallback behavior, including configured default domain/password when a connection already contains an explicit username.

Credential injection is suppressed for `Force.NoCredentials`, Remote Credential Guard, smart-card gateway authentication, and access-token gateway authentication where a password entry is not applicable.

### `WindowsCredentialManager`

Uses Win32 Credential Manager directly instead of BAT files, PowerShell, or `cmdkey.exe`.

Targets:

- destination: `TERMSRV/<destination-host>`;
- separate gateway: `TERMSRV/<gateway-host>`.

Credentials are stored as current-user `Generic` credentials with local-machine persistence. This avoids races caused by deleting a credential before mstsc has consumed it, but it can update an existing Windows credential for the same target.

The credential cleanup helper removes both:

- native-launch `Generic` credentials;
- mstsc-managed `DomainPassword` credentials.

### `TemporaryRdpFileStore`

Files are generated under:

`%LOCALAPPDATA%\mRemoteNG\Temp\Rdp\<guid>.rdp`

Behavior:

- unique file for every launch;
- never modifies `Documents\Default.rdp`;
- immediate best-effort deletion on launch failure;
- scheduled deletion 30 seconds after process start;
- files older than five minutes are removed before a later native launch.

A temporary file can contain connection settings and an RD Gateway access token. It does not contain destination or gateway passwords.

## Command-line switches

The launcher adds native mstsc switches when applicable:

- `/admin`;
- `/f`;
- `/restrictedAdmin`;
- `/remoteGuard`;
- `/prompt`.

All other supported settings are supplied by the generated `.rdp` file.

## Unsupported combinations

Native launch reports a clear error instead of silently discarding these options:

- mRemoteNG-managed SSH tunnel;
- View Only;
- Hyper-V VM ID;
- Hyper-V Enhanced Session;
- simultaneous Restricted Admin and Remote Credential Guard;
- Remote Credential Guard through RD Gateway;
- Remote Credential Guard through RD Connection Broker settings.

These connections remain usable with `Embedded mRemoteNG tab`.

## Persistence

### XML

The optional attribute is:

`RdpClientMode="Embedded|NativeMstsc"`

Changes include XML serialization, deserialization after normal authentication/decryption, and the optional XSD attribute. Missing values use the enum default `Embedded`.

### SQL

`RdpClientMode` is included in the `tblCons` DataTable schema, dirty checking, serialization and backward-compatible deserialization.

Database schema version is raised from 3.0 to 3.1. `SqlVersion30To31Upgrader` adds a non-null `RdpClientMode` column with default `Embedded` for both MSSQL and MySQL and updates `tblRoot.ConfVersion`.

## Lifecycle limitations

mRemoteNG does not own the native mstsc window and cannot reliably map the bootstrap process to the actual remote session.

Native mode therefore does not provide:

- an mRemoteNG connection tab;
- `OpenConnections`/active-session tracking;
- embedded connected/disconnected/closed events;
- reliable remote-session close detection;
- idle-timeout callbacks;
- embedded reconnect behavior after mstsc starts;
- execution of `PostExtApp` when the external session closes.

The log records that mstsc was launched; it does not claim that remote authentication or session establishment succeeded.

## Validation

Regression tests cover:

- clone preservation of `RdpClientMode`;
- hostname, custom port and IPv6 formatting;
- absence of passwords from generated `.rdp` content;
- directive-injection sanitization;
- explicit RD Gateway password profile;
- native command-line switches;
- Remote Credential Guard validation;
- XML `NativeMstsc` loading and missing-value default;
- SQL schema version 3.1 and mode serialization.

Manual validation from the source implementation confirmed native mstsc launch, automatic temporary-file generation and credential handoff. Because this migration targets a newer and different upstream lineage, the migrated branch must be built and retested on Windows before merge.
