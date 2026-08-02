from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
changed: list[str] = []


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def write(path: Path, text: str) -> None:
    normalized = text.replace("\r\n", "\n")
    if read(path) == normalized:
        return
    path.write_text(normalized, encoding="utf-8", newline="\n")
    changed.append(str(path.relative_to(ROOT)))


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = read(path)
    if new in text:
        return
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one anchor in {path}, found {count}")
    write(path, text.replace(old, new, 1))


# XML: missing/invalid values remain Embedded, preserving all existing connection files.
deserializer = ROOT / "mRemoteNG" / "Config" / "Serializers" / "ConnectionSerializers" / "Xml" / "XmlConnectionsDeserializer.cs"
replace_once(
    deserializer,
    '                    connectionInfo.AlwaysPromptForCredentials = a.GetAttrBool("AlwaysPromptForCredentials");\n',
    '                    connectionInfo.AlwaysPromptForCredentials = a.GetAttrBool("AlwaysPromptForCredentials");\n'
    '                    connectionInfo.RdpClientMode = a.GetAttrEnum("RdpClientMode", RdpClientMode.Embedded);\n',
    "XML RdpClientMode deserialization")

# The native path must run before any panel/tab/ActiveX control is created.
initiator = ROOT / "mRemoteNG" / "Connection" / "ConnectionInitiator.cs"
text = read(initiator)
if "using mRemoteNG.Connection.Protocol.RDP;" not in text:
    anchor = "using mRemoteNG.Connection.Protocol;\n"
    if text.count(anchor) != 1:
        raise RuntimeError("ConnectionInitiator RDP using anchor not found")
    text = text.replace(anchor, anchor + "using mRemoteNG.Connection.Protocol.RDP;\n", 1)

native_branch = '''                if (connectionInfo.Protocol == ProtocolType.RDP &&
                    connectionInfo.RdpClientMode == RdpClientMode.NativeMstsc)
                {
                    if (!string.IsNullOrEmpty(connectionInfoOriginal.SSHTunnelConnectionName))
                    {
                        Runtime.MessageCollector.AddMessage(
                            MessageClass.WarningMsg,
                            "Native mstsc launch is not available for RDP connections using an mRemoteNG-managed SSH tunnel. Use Embedded mode for this connection.");
                        return;
                    }

                    NativeRdpLauncher launcher = new();
                    launcher.Launch(connectionInfo, force);
                    return;
                }

'''
if native_branch not in text:
    anchor = "                StartPreConnectionExternalApp(connectionInfo);\n\n"
    if text.count(anchor) != 1:
        raise RuntimeError(f"ConnectionInitiator native branch anchor count: {text.count(anchor)}")
    text = text.replace(anchor, anchor + native_branch, 1)
write(initiator, text)

# The helper produced by the first pass targeted XElement, while the actual loader uses XmlNode + an attribute dictionary.
obsolete_helper = ROOT / "mRemoteNG" / "Config" / "Serializers" / "ConnectionSerializers" / "Xml" / "RdpClientModeXmlPersistence.cs"
if obsolete_helper.exists():
    obsolete_helper.unlink()
    changed.append(str(obsolete_helper.relative_to(ROOT)) + " (removed)")

spec = ROOT / "docs" / "native-rdp-client.md"
if spec.exists():
    text = read(spec).replace("deterministic UTF-8 `.rdp` file", "deterministic UTF-16LE `.rdp` file")
    write(spec, text)

report = ROOT / "docs" / "native-rdp-implementation-report.md"
lines = [
    "# Native RDP implementation report",
    "",
    "## Finalizer changes",
    *[f"- `{item}`" for item in changed],
    "",
    "## Persistence behavior",
    "- XML serializer writes optional `RdpClientMode`.",
    "- XML deserializer uses `Embedded` when the attribute is absent or invalid.",
    "- The setting is intentionally not inherited in the first implementation.",
    "",
    "## Native launch behavior",
    "- Native mode is selected immediately after effective connection preparation and the pre-connection external app.",
    "- No panel, tab, protocol instance, or ActiveX control is created for native mode.",
    "- mRemoteNG-managed SSH tunnels are rejected with an actionable warning until their lifetime is decoupled from embedded tabs.",
    "",
    "## Validation",
    "- Build and test results are provided by the repository PR_Validation workflow.",
    "",
]
report.write_text("\n".join(lines), encoding="utf-8", newline="\n")
