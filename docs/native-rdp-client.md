# Native Windows RDP client (`mstsc.exe`)

## Goal

Allow an ordinary mRemoteNG RDP connection to open in the native Windows Remote Desktop client while keeping the existing RDP property UI and connection data model.

The user must not create, edit, validate, or keep an `.rdp` file. mRemoteNG generates a temporary file from the effective `ConnectionInfo` on every launch, writes credentials through the Windows Credential Manager API when appropriate, starts `mstsc.exe`, and removes stale temporary files automatically.

## User-facing behavior

Each RDP connection has an **RDP client mode** setting:

- `Embedded` — current ActiveX/MSTSCLib behavior inside an mRemoteNG tab.
- `NativeMstsc` — launch the system `mstsc.exe` as a top-level native window.

Backward compatibility rule: when the property is missing from an existing configuration, the value is `Embedded`.

The native mode setting applies only to RDP connections. It does not change the protocol type to `Ext.App`, so all existing RDP properties remain available.

## Launch architecture

Native launch is selected before mRemoteNG creates an embedded protocol control or tab.

High-level flow:

1. Resolve the effective connection values, including inheritance, linked credentials, alternate address, runtime hostname resolution, and `Force.NoCredentials`.
2. Run the configured pre-connection external application.
3. If protocol is RDP and client mode is `NativeMstsc`, call `NativeRdpLauncher` and return without creating an `RdpProtocol`/ActiveX control.
4. Otherwise continue through the existing embedded connection path.

The native launcher is not a `ProtocolBase` implementation because `ProtocolBase` assumes a hosted WinForms control, tab lifecycle, and embedded connection events.

## Components

### `RdpFileSerializer`

Serializes the effective `ConnectionInfo` into a deterministic UTF-16LE `.rdp` file. The file never contains the plaintext password or a `password 51` blob.

Supported property groups include:

- host and custom port;
- username/domain hint;
- fullscreen, custom size, fit/window sizing, and multimonitor mode;
- color depth and bitmap cache;
- wallpaper, themes, font smoothing, desktop composition, drag/menu/cursor performance flags;
- clipboard, printers, COM ports, smart cards, drives/custom drives;
- remote audio, sound quality, microphone capture, WebAuthn;
- authentication level, CredSSP, load-balance information, redirection server name;
- RD Gateway host, usage method, and credential source;
- alternate shell/start program and working directory;
- RemoteApp fields where available;
- Entra/AAD RDP authentication flag where supported by the native client.

Properties that are specific to the embedded ActiveX lifecycle, such as the selected COM control version, automatic tab resize, view-only behavior, and mRemoteNG reconnect events, are not serialized.

### `WindowsCredentialManager`

Uses Win32 Credential Manager APIs directly:

- `CredReadW`;
- `CredWriteW`;
- `CredDeleteW`;
- `CredFree`.

No `cmdkey.exe`, shell command, BAT file, or password-bearing process argument is used.

Credential target names use the same `TERMSRV/<host>` convention as mstsc. Host normalization is centralized so reading, writing, clearing, and launching use the same target.

Credentials are not written when:

- `Force.NoCredentials` is active;
- username or password is empty;
- Restricted Admin is enabled;
- Remote Credential Guard is enabled;
- the connection explicitly requests a credential prompt.

The first implementation persists the credential in Windows Credential Manager. This avoids a race where mstsc reads credentials after the bootstrap process exits and avoids unreliable tracking when mstsc hands the connection to another process. A prompt-only path remains available through the existing `AlwaysPromptForCredentials` setting.

### `TemporaryRdpFileStore`

Creates files under:

`%LOCALAPPDATA%\mRemoteNG\Temp\Rdp\<guid>.rdp`

Rules:

- create a new file for every launch;
- use restrictive current-user filesystem access where supported;
- delete the file after mstsc has safely started;
- perform best-effort deletion on launch failure;
- remove stale files on application startup;
- never modify the user's `Documents\Default.rdp`.

### `NativeRdpLauncher`

Coordinates serialization, credentials, arguments, process launch, logging, and cleanup.

`ProcessStartInfo.ArgumentList` is used instead of manually quoted argument strings.

Executable path:

`Path.Combine(Environment.SystemDirectory, "mstsc.exe")`

Native-only command-line switches are added where appropriate:

- `/admin`;
- `/restrictedAdmin`;
- `/remoteGuard`;
- `/prompt`.

The generated `.rdp` file supplies the remainder of the settings.

## Persistence

A new optional connection property is stored in the normal mRemoteNG connection configuration:

`RdpClientMode="Embedded|NativeMstsc"`

Requirements:

- XML serializer writes the property;
- XML deserializer treats a missing/invalid value as `Embedded`;
- configuration XSD allows the optional attribute;
- cloning/copying preserves the value;
- database/CSV paths continue to work through the existing connection-property infrastructure or receive an explicit mapping when required.

The setting is intentionally per connection and is not inherited in the first implementation. This avoids silently changing entire folder trees and keeps old configurations behaviorally stable.

## SSH tunnel behavior

A native mstsc process can use an mRemoteNG-managed local SSH tunnel only while that tunnel remains alive. The native path therefore does not bypass the existing connection preparation step.

If the current tunnel implementation cannot provide an independent lifetime handle, native launch fails with a clear message instead of starting a connection that immediately loses its tunnel. Embedded RDP remains available as the fallback mode.

## RD Gateway credentials

The normal server credential target and a separate RD Gateway credential are not assumed to be interchangeable.

Supported baseline:

- gateway using the same credentials as the destination connection;
- gateway prompt handled by mstsc when separate credentials are required.

Gateway access tokens and provider-specific interactive authentication are passed only through documented `.rdp` properties supported by the installed Windows client. They are not copied into generic credential entries.

## Error handling

Native launch reports actionable errors through `Runtime.MessageCollector`, including:

- missing `mstsc.exe`;
- invalid hostname/port;
- temporary file creation failure;
- Credential Manager error code;
- unsupported native/tunnel combination;
- process-start failure.

Failures do not fall through into an embedded launch automatically because that could open an unexpected connection mode. The user can change the connection setting back to `Embedded`.

## Security properties

- no password in `.rdp`;
- no password in command line;
- no shell invocation;
- no modification of global `Default.rdp`;
- no shared fixed temporary filename;
- stale temporary files cleaned up;
- credentials skipped for RCG/Restricted Admin/no-credentials modes;
- credential target and username are normalized before Win32 calls;
- sensitive buffers are zeroed after `CredWriteW`.

## Validation matrix

Automated tests cover:

- deterministic `.rdp` output;
- host and non-default port;
- domain username and UPN username;
- custom resolution/fullscreen/multimonitor;
- device and audio redirection;
- gateway settings;
- Restricted Admin/Remote Guard arguments;
- no password in serialized file or arguments;
- old XML without `RdpClientMode` defaults to embedded;
- XML round trip preserves native mode;
- launch failure removes temporary files;
- credential write is skipped in no-credential modes.

Manual Windows validation covers:

- mixed-DPI multimonitor movement;
- saved credentials and prompt mode;
- separate gateway authentication;
- parallel native sessions;
- application crash followed by stale-file cleanup;
- custom drive selection;
- RemoteApp/start program;
- SSH tunnel lifetime when enabled.
