from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REPORT = ROOT / "docs" / "native-rdp-implementation-report.md"
changes: list[str] = []
notes: list[str] = []
errors: list[str] = []


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def write(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    current = read(path) if path.exists() else None
    normalized = text.replace("\r\n", "\n")
    if current == normalized:
        return
    path.write_text(normalized, encoding="utf-8", newline="\n")
    changes.append(str(path.relative_to(ROOT)))


def replace_once(path: Path, old: str, new: str, label: str) -> None:
    text = read(path)
    if new in text:
        notes.append(f"Already applied: {label}")
        return
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one anchor in {path}, found {count}")
    write(path, text.replace(old, new, 1))


def create_sources() -> None:
    base = ROOT / "mRemoteNG" / "Connection" / "Protocol" / "RDP"

    write(base / "RdpClientMode.cs", r'''using System.ComponentModel;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public enum RdpClientMode
    {
        [Description("Embedded mRemoteNG tab")]
        Embedded = 0,

        [Description("Native Windows Remote Desktop client (mstsc.exe)")]
        NativeMstsc = 1
    }
}
''')

    write(base / "RdpFileSerializer.cs", r'''using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace mRemoteNG.Connection.Protocol.RDP
{
    /// <summary>
    /// Converts the effective mRemoteNG RDP connection settings into an mstsc-compatible file.
    /// Credentials are deliberately excluded and are supplied through Windows Credential Manager.
    /// </summary>
    public sealed class RdpFileSerializer
    {
        public string Serialize(ConnectionInfo connectionInfo)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);

            List<string> lines = new();
            string fullAddress = BuildFullAddress(connectionInfo.Hostname, connectionInfo.Port);

            AddString(lines, "full address", fullAddress);
            if (connectionInfo.Port > 0)
                AddInt(lines, "server port", connectionInfo.Port);

            AddString(lines, "username", BuildUsername(connectionInfo));
            AddString(lines, "domain", connectionInfo.Domain);

            string resolution = connectionInfo.Resolution.ToString();
            AddInt(lines, "screen mode id", resolution.Equals("Fullscreen", StringComparison.OrdinalIgnoreCase) ? 2 : 1);
            if (connectionInfo.ResolutionWidth > 0)
                AddInt(lines, "desktopwidth", connectionInfo.ResolutionWidth);
            if (connectionInfo.ResolutionHeight > 0)
                AddInt(lines, "desktopheight", connectionInfo.ResolutionHeight);
            AddInt(lines, "use multimon", Bool(connectionInfo.RDPUseMultimon));
            AddInt(lines, "session bpp", ColorDepth(connectionInfo.Colors.ToString()));
            AddInt(lines, "desktopscalefactor", Convert.ToInt32(connectionInfo.DesktopScaleFactor, CultureInfo.InvariantCulture));

            AddInt(lines, "bitmapcachepersistenable", Bool(connectionInfo.CacheBitmaps));
            AddInt(lines, "disable wallpaper", Bool(!connectionInfo.DisplayWallpaper));
            AddInt(lines, "disable themes", Bool(!connectionInfo.DisplayThemes));
            AddInt(lines, "allow font smoothing", Bool(connectionInfo.EnableFontSmoothing));
            AddInt(lines, "allow desktop composition", Bool(connectionInfo.EnableDesktopComposition));
            AddInt(lines, "disable full window drag", Bool(connectionInfo.DisableFullWindowDrag));
            AddInt(lines, "disable menu anims", Bool(connectionInfo.DisableMenuAnimations));
            AddInt(lines, "disable cursor setting", Bool(connectionInfo.DisableCursorShadow));
            AddInt(lines, "disable cursor blinking", Bool(connectionInfo.DisableCursorBlinking));

            AddInt(lines, "keyboardhook", connectionInfo.RedirectKeys ? 1 : 0);
            AddInt(lines, "redirectclipboard", Bool(connectionInfo.RedirectClipboard));
            AddInt(lines, "redirectprinters", Bool(connectionInfo.RedirectPrinters));
            AddInt(lines, "redirectcomports", Bool(connectionInfo.RedirectPorts));
            AddInt(lines, "redirectsmartcards", Bool(connectionInfo.RedirectSmartCards));
            AddDriveRedirection(lines, connectionInfo);

            AddInt(lines, "audiomode", AudioMode(connectionInfo.RedirectSound.ToString()));
            AddInt(lines, "audioqualitymode", SoundQuality(connectionInfo.SoundQuality.ToString()));
            AddInt(lines, "audiocapturemode", Bool(connectionInfo.RedirectAudioCapture));
            AddInt(lines, "redirectwebauthn", Bool(connectionInfo.RedirectWebAuthn));

            AddInt(lines, "authentication level", Convert.ToInt32(connectionInfo.RDPAuthenticationLevel, CultureInfo.InvariantCulture));
            AddInt(lines, "enablecredsspsupport", Bool(connectionInfo.UseCredSsp));
            AddInt(lines, "prompt for credentials", Bool(connectionInfo.AlwaysPromptForCredentials));
            AddInt(lines, "use redirection server name", Bool(connectionInfo.UseRedirectionServerName));
            AddInt(lines, "enablerdsaadauth", Bool(connectionInfo.EnableRdsAadAuth));
            AddString(lines, "loadbalanceinfo", connectionInfo.LoadBalanceInfo);

            AddGateway(lines, connectionInfo);

            AddString(lines, "alternate shell", connectionInfo.RDPStartProgram);
            AddString(lines, "shell working directory", connectionInfo.RDPStartProgramWorkDir);
            AddRemoteApp(lines, connectionInfo);
            AddString(lines, "signscope", GetOptionalString(connectionInfo, "RDPSignScope"));
            AddString(lines, "signature", GetOptionalString(connectionInfo, "RDPSignature"));

            return string.Join("\r\n", lines) + "\r\n";
        }

        public static string BuildFullAddress(string hostname, int port)
        {
            string host = (hostname ?? string.Empty).Trim();
            if (host.Contains(':') && !host.StartsWith('['))
                host = $"[{host}]";
            return port > 0 && port != 3389 ? $"{host}:{port}" : host;
        }

        public static string BuildUsername(ConnectionInfo connectionInfo)
        {
            string username = connectionInfo.Username?.Trim() ?? string.Empty;
            string domain = connectionInfo.Domain?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(domain) || username.Contains('\\') || username.Contains('@'))
                return username;
            return $"{domain}\\{username}";
        }

        private static void AddDriveRedirection(ICollection<string> lines, ConnectionInfo connectionInfo)
        {
            string mode = connectionInfo.RedirectDiskDrives.ToString();
            bool enabled = !mode.Equals("None", StringComparison.OrdinalIgnoreCase);
            AddInt(lines, "redirectdrives", Bool(enabled));
            if (!enabled)
            {
                AddString(lines, "drivestoredirect", string.Empty);
                return;
            }

            string custom = connectionInfo.RedirectDiskDrivesCustom?.Trim() ?? string.Empty;
            AddString(lines, "drivestoredirect", string.IsNullOrEmpty(custom) ? "*" : custom);
        }

        private static void AddGateway(ICollection<string> lines, ConnectionInfo connectionInfo)
        {
            string usage = connectionInfo.RDGatewayUsageMethod.ToString();
            int usageValue = usage switch
            {
                "Always" => 1,
                "Detect" => 2,
                _ => 0
            };
            AddInt(lines, "gatewayusagemethod", usageValue);
            AddString(lines, "gatewayhostname", connectionInfo.RDGatewayHostname);

            string credentialSource = connectionInfo.RDGatewayUseConnectionCredentials.ToString();
            int sourceValue = credentialSource switch
            {
                "SmartCard" => 1,
                "Yes" => 2,
                "AccessToken" => 5,
                "ExternalCredentialProvider" => 0,
                _ => 4
            };
            AddInt(lines, "gatewaycredentialssource", sourceValue);
            AddString(lines, "gatewayaccesstoken", connectionInfo.RDGatewayAccessToken);
        }

        private static void AddRemoteApp(ICollection<string> lines, ConnectionInfo connectionInfo)
        {
            string program = GetOptionalString(connectionInfo, "RDPRemoteAppProgram");
            if (string.IsNullOrWhiteSpace(program))
                return;

            AddInt(lines, "remoteapplicationmode", 1);
            AddString(lines, "remoteapplicationprogram", program);
            AddString(lines, "remoteapplicationcmdline", GetOptionalString(connectionInfo, "RDPRemoteAppCmdLine"));
        }

        private static string GetOptionalString(ConnectionInfo connectionInfo, string propertyName)
        {
            PropertyInfo? property = connectionInfo.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            return property?.GetValue(connectionInfo)?.ToString() ?? string.Empty;
        }

        private static int ColorDepth(string value) => value switch
        {
            "Colors256" => 8,
            "Colors15Bit" => 15,
            "Colors16Bit" => 16,
            "Colors24Bit" => 24,
            _ => 32
        };

        private static int AudioMode(string value) => value switch
        {
            "LeaveAtRemoteComputer" => 1,
            "DoNotPlay" => 2,
            _ => 0
        };

        private static int SoundQuality(string value) => value switch
        {
            "Dynamic" => 0,
            "Medium" => 1,
            "High" => 2,
            _ => 0
        };

        private static int Bool(bool value) => value ? 1 : 0;

        private static void AddInt(ICollection<string> lines, string key, int value) =>
            lines.Add($"{key}:i:{value.ToString(CultureInfo.InvariantCulture)}");

        private static void AddString(ICollection<string> lines, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
                lines.Add($"{key}:s:{value}");
        }
    }
}
''')

    write(base / "WindowsCredentialManager.cs", r'''using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public sealed class WindowsCredentialManager
    {
        private const uint CredTypeGeneric = 1;
        private const uint CredPersistLocalMachine = 2;

        public void Write(string targetName, string username, string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
            ArgumentException.ThrowIfNullOrWhiteSpace(username);
            ArgumentNullException.ThrowIfNull(password);

            IntPtr targetPtr = IntPtr.Zero;
            IntPtr usernamePtr = IntPtr.Zero;
            IntPtr passwordPtr = IntPtr.Zero;
            try
            {
                targetPtr = Marshal.StringToCoTaskMemUni(targetName);
                usernamePtr = Marshal.StringToCoTaskMemUni(username);
                passwordPtr = Marshal.StringToCoTaskMemUni(password);

                Credential credential = new()
                {
                    Type = CredTypeGeneric,
                    TargetName = targetPtr,
                    UserName = usernamePtr,
                    CredentialBlob = passwordPtr,
                    CredentialBlobSize = checked((uint)Encoding.Unicode.GetByteCount(password)),
                    Persist = CredPersistLocalMachine
                };

                if (!CredWriteW(ref credential, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Unable to store Windows credential '{targetName}'.");
            }
            finally
            {
                if (passwordPtr != IntPtr.Zero)
                    Marshal.ZeroFreeCoTaskMemUnicode(passwordPtr);
                if (usernamePtr != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(usernamePtr);
                if (targetPtr != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(targetPtr);
            }
        }

        public void Delete(string targetName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
            if (!CredDeleteW(targetName, CredTypeGeneric, 0))
            {
                int error = Marshal.GetLastWin32Error();
                const int ErrorNotFound = 1168;
                if (error != ErrorNotFound)
                    throw new Win32Exception(error, $"Unable to delete Windows credential '{targetName}'.");
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Credential
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWriteW(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDeleteW(string target, uint type, uint flags);
    }
}
''')

    write(base / "TemporaryRdpFileStore.cs", r'''using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public sealed class TemporaryRdpFileStore
    {
        private static readonly TimeSpan StaleAge = TimeSpan.FromDays(1);
        private static readonly TimeSpan DeleteDelay = TimeSpan.FromSeconds(30);

        public string DirectoryPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "mRemoteNG", "Temp", "Rdp");

        public string Create(string content)
        {
            ArgumentNullException.ThrowIfNull(content);
            Directory.CreateDirectory(DirectoryPath);
            CleanupStaleFiles();
            string path = Path.Combine(DirectoryPath, $"{Guid.NewGuid():N}.rdp");
            File.WriteAllText(path, content, Encoding.Unicode);
            return path;
        }

        public void ScheduleDelete(string path)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(DeleteDelay).ConfigureAwait(false);
                TryDelete(path);
            });
        }

        public void TryDelete(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort. The next cleanup pass removes stale files.
            }
        }

        public void CleanupStaleFiles()
        {
            if (!Directory.Exists(DirectoryPath))
                return;

            DateTime threshold = DateTime.UtcNow - StaleAge;
            foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.rdp", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < threshold)
                        File.Delete(file);
                }
                catch
                {
                    // A running mstsc instance may still hold the file.
                }
            }
        }
    }
}
''')

    write(base / "NativeRdpLauncher.cs", r'''using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using mRemoteNG.App;
using mRemoteNG.Messages;

namespace mRemoteNG.Connection.Protocol.RDP
{
    public sealed class NativeRdpLauncher
    {
        private readonly RdpFileSerializer _serializer;
        private readonly TemporaryRdpFileStore _fileStore;
        private readonly WindowsCredentialManager _credentialManager;

        public NativeRdpLauncher()
            : this(new RdpFileSerializer(), new TemporaryRdpFileStore(), new WindowsCredentialManager())
        {
        }

        public NativeRdpLauncher(
            RdpFileSerializer serializer,
            TemporaryRdpFileStore fileStore,
            WindowsCredentialManager credentialManager)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
            _credentialManager = credentialManager ?? throw new ArgumentNullException(nameof(credentialManager));
        }

        public bool Launch(ConnectionInfo connectionInfo, ConnectionInfo.Force force)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);
            string? rdpPath = null;
            try
            {
                Validate(connectionInfo);
                string executable = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
                if (!File.Exists(executable))
                    throw new FileNotFoundException("The native Windows Remote Desktop client was not found.", executable);

                bool prompt = force.HasFlag(ConnectionInfo.Force.NoCredentials) || connectionInfo.AlwaysPromptForCredentials;
                bool credentialless = prompt || connectionInfo.UseRestrictedAdmin || connectionInfo.UseRCG;
                if (!credentialless && !string.IsNullOrWhiteSpace(connectionInfo.Username) && !string.IsNullOrEmpty(connectionInfo.Password))
                {
                    string target = BuildCredentialTarget(connectionInfo.Hostname);
                    string username = RdpFileSerializer.BuildUsername(connectionInfo);
                    _credentialManager.Write(target, username, connectionInfo.Password);
                }

                rdpPath = _fileStore.Create(_serializer.Serialize(connectionInfo));
                ProcessStartInfo startInfo = new(executable)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Environment.SystemDirectory
                };
                startInfo.ArgumentList.Add(rdpPath);
                if (connectionInfo.UseConsoleSession)
                    startInfo.ArgumentList.Add("/admin");
                if (connectionInfo.UseRestrictedAdmin)
                    startInfo.ArgumentList.Add("/restrictedAdmin");
                if (connectionInfo.UseRCG)
                    startInfo.ArgumentList.Add("/remoteGuard");
                if (prompt)
                    startInfo.ArgumentList.Add("/prompt");

                Process? process = Process.Start(startInfo);
                if (process == null)
                    throw new InvalidOperationException("mstsc.exe did not return a process instance.");

                _fileStore.ScheduleDelete(rdpPath);
                Runtime.MessageCollector.AddMessage(
                    MessageClass.InformationMsg,
                    string.Format(CultureInfo.InvariantCulture, "Opened native RDP connection '{0}' using mstsc.exe.", connectionInfo.Name));
                return true;
            }
            catch (Exception ex)
            {
                _fileStore.TryDelete(rdpPath);
                Runtime.MessageCollector.AddExceptionMessage("Unable to open the native Windows RDP client.", ex);
                return false;
            }
        }

        public static string BuildCredentialTarget(string hostname)
        {
            string host = (hostname ?? string.Empty).Trim().TrimStart('[').TrimEnd(']');
            return $"TERMSRV/{host}";
        }

        private static void Validate(ConnectionInfo connectionInfo)
        {
            if (string.IsNullOrWhiteSpace(connectionInfo.Hostname))
                throw new InvalidOperationException("A hostname is required for native RDP launch.");
            if (connectionInfo.Port is < 0 or > 65535)
                throw new InvalidOperationException($"The RDP port '{connectionInfo.Port}' is outside the valid range.");
        }
    }
}
''')

    helper = ROOT / "mRemoteNG" / "Config" / "Serializers" / "ConnectionSerializers" / "Xml" / "RdpClientModeXmlPersistence.cs"
    write(helper, r'''using System;
using System.Xml.Linq;
using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;

namespace mRemoteNG.Config.Serializers.ConnectionSerializers.Xml
{
    internal static class RdpClientModeXmlPersistence
    {
        public static void Apply(ConnectionInfo connectionInfo, XElement element)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);
            ArgumentNullException.ThrowIfNull(element);
            string? value = element.Attribute("RdpClientMode")?.Value;
            connectionInfo.RdpClientMode = Enum.TryParse(value, true, out RdpClientMode mode)
                ? mode
                : RdpClientMode.Embedded;
        }
    }
}
''')

    test = ROOT / "mRemoteNGTests" / "Connection" / "Protocol" / "RDP" / "RdpFileSerializerTests.cs"
    write(test, r'''using mRemoteNG.Connection;
using mRemoteNG.Connection.Protocol.RDP;
using NUnit.Framework;

namespace mRemoteNGTests.Connection.Protocol.RDP
{
    [TestFixture]
    public class RdpFileSerializerTests
    {
        [Test]
        public void ExistingConnectionsDefaultToEmbeddedMode()
        {
            ConnectionInfo connectionInfo = new();
            Assert.That(connectionInfo.RdpClientMode, Is.EqualTo(RdpClientMode.Embedded));
        }

        [Test]
        public void SerializeWritesAddressPortAndUsernameButNeverPassword()
        {
            ConnectionInfo connectionInfo = new()
            {
                Hostname = "rdp.example.test",
                Port = 3391,
                Domain = "CONTOSO",
                Username = "alice",
                Password = "secret-value"
            };

            string result = new RdpFileSerializer().Serialize(connectionInfo);

            Assert.That(result, Does.Contain("full address:s:rdp.example.test:3391"));
            Assert.That(result, Does.Contain("server port:i:3391"));
            Assert.That(result, Does.Contain("username:s:CONTOSO\\alice"));
            Assert.That(result, Does.Not.Contain("secret-value"));
            Assert.That(result, Does.Not.Contain("password 51"));
        }

        [Test]
        public void NativeModeCanBeStoredOnConnection()
        {
            ConnectionInfo connectionInfo = new() { RdpClientMode = RdpClientMode.NativeMstsc };
            Assert.That(connectionInfo.RdpClientMode, Is.EqualTo(RdpClientMode.NativeMstsc));
        }
    }
}
''')


def patch_model() -> None:
    path = ROOT / "mRemoteNG" / "Connection" / "AbstractConnectionRecord.cs"
    replace_once(
        path,
        "        private RdpVersion _rdpProtocolVersion;\n",
        "        private RdpVersion _rdpProtocolVersion;\n        private RdpClientMode _rdpClientMode;\n",
        "RDP client mode backing field")

    old = '''        public virtual RdpVersion RdpVersion
        {
            get => GetPropertyValue(nameof(RdpVersion), _rdpProtocolVersion);
            set => SetField(ref _rdpProtocolVersion, value, nameof(RdpVersion));
        }
'''
    new = old + '''
        [LocalizedAttributes.LocalizedCategory(nameof(Language.Protocol), 3),
         DisplayName("RDP Client"),
         Description("Choose whether RDP opens inside mRemoteNG or in the native Windows mstsc.exe client."),
         TypeConverter(typeof(MiscTools.EnumTypeConverter)),
         AttributeUsedInProtocol(ProtocolType.RDP)]
        public virtual RdpClientMode RdpClientMode
        {
            get => _rdpClientMode;
            set => SetField(ref _rdpClientMode, value, nameof(RdpClientMode));
        }
'''
    replace_once(path, old, new, "RDP client mode property")


def patch_xml_serializer() -> None:
    path = ROOT / "mRemoteNG" / "Config" / "Serializers" / "ConnectionSerializers" / "Xml" / "XmlConnectionNodeSerializer28.cs"
    replace_once(
        path,
        '            element.Add(new XAttribute("RdpVersion", connectionInfo.RdpVersion.ToString().ToLowerInvariant()));\n',
        '            element.Add(new XAttribute("RdpVersion", connectionInfo.RdpVersion.ToString().ToLowerInvariant()));\n            element.Add(new XAttribute("RdpClientMode", connectionInfo.RdpClientMode));\n',
        "XML serialization")


def patch_xsd() -> None:
    candidates = list(ROOT.rglob("mremoteng_confcons_v2_8.xsd"))
    if len(candidates) != 1:
        raise RuntimeError(f"Expected one v2.8 XSD, found {len(candidates)}")
    path = candidates[0]
    replace_once(
        path,
        '    <xs:attribute name="RdpVersion" type="xs:string" use="required" />\n',
        '    <xs:attribute name="RdpVersion" type="xs:string" use="required" />\n    <xs:attribute name="RdpClientMode" type="xs:string" use="optional" />\n',
        "XSD RdpClientMode attribute")


def patch_xml_deserializer() -> None:
    xml_dir = ROOT / "mRemoteNG" / "Config" / "Serializers" / "ConnectionSerializers" / "Xml"
    candidates: list[Path] = []
    for path in xml_dir.glob("*.cs"):
        if path.name in {"XmlConnectionNodeSerializer28.cs", "RdpClientModeXmlPersistence.cs"}:
            continue
        text = read(path)
        if "Deserialize" in text and "ConnectionInfo" in text and ("UseRCG" in text or "RdpVersion" in text):
            candidates.append(path)

    for path in candidates:
        text = read(path)
        if "RdpClientModeXmlPersistence.Apply" in text:
            return

        element_names = re.findall(r"XElement\s+(\w+)", text)
        connection_names = re.findall(r"ConnectionInfo\s+(\w+)\s*=", text)
        if not element_names or not connection_names:
            notes.append(f"Deserializer candidate not patchable automatically: {path.relative_to(ROOT)}")
            continue

        element = element_names[0]
        connection = connection_names[-1]
        return_pattern = re.compile(rf"(?m)^(?P<indent>\s*)return\s+{re.escape(connection)}\s*;")
        match = return_pattern.search(text)
        if match:
            indent = match.group("indent")
            insertion = f"{indent}RdpClientModeXmlPersistence.Apply({connection}, {element});\n"
            write(path, text[:match.start()] + insertion + text[match.start():])
            notes.append(f"Patched XML deserializer: {path.relative_to(ROOT)}")
            return

        marker_lines = [m for m in re.finditer(r"(?m)^.*UseRCG.*$", text)]
        if marker_lines:
            marker = marker_lines[-1]
            line_end = text.find("\n", marker.end()) + 1
            indent = re.match(r"\s*", marker.group(0)).group(0)
            insertion = f"{indent}RdpClientModeXmlPersistence.Apply({connection}, {element});\n"
            write(path, text[:line_end] + insertion + text[line_end:])
            notes.append(f"Patched XML deserializer after UseRCG: {path.relative_to(ROOT)}")
            return

    snippets = []
    for path in candidates:
        text = read(path)
        position = max(text.find("UseRCG"), text.find("RdpVersion"))
        snippets.append(f"### {path.relative_to(ROOT)}\n```csharp\n{text[max(0, position-400):position+600]}\n```")
    raise RuntimeError("Could not patch XML deserializer. Candidates:\n" + "\n".join(snippets))


def patch_connection_initiator() -> None:
    path = ROOT / "mRemoteNG" / "Connection" / "ConnectionInitiator.cs"
    text = read(path)
    if "using mRemoteNG.Connection.Protocol.RDP;" not in text:
        anchor = "using mRemoteNG.Connection.Protocol;\n"
        if anchor not in text:
            raise RuntimeError("ConnectionInitiator using anchor not found")
        text = text.replace(anchor, anchor + "using mRemoteNG.Connection.Protocol.RDP;\n", 1)

    branch = '''                if (connectionInfo.Protocol == ProtocolType.RDP &&
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
    if branch not in text:
        anchor = "                StartPreConnectionExternalApp(connectionInfo);\n\n"
        if text.count(anchor) != 1:
            raise RuntimeError(f"ConnectionInitiator native branch anchor count: {text.count(anchor)}")
        text = text.replace(anchor, anchor + branch, 1)
    write(path, text)


def scan_persistence() -> None:
    hits: list[str] = []
    for folder_name in ("Sql", "Csv"):
        for path in ROOT.rglob("*.cs"):
            if folder_name.lower() not in str(path).lower():
                continue
            text = read(path)
            if "RdpVersion" in text or "UseRCG" in text:
                position = text.find("RdpVersion")
                if position < 0:
                    position = text.find("UseRCG")
                snippet = text[max(0, position - 250):position + 500]
                hits.append(f"### {path.relative_to(ROOT)}\n```csharp\n{snippet}\n```")
    if hits:
        notes.append("SQL/CSV persistence candidates found; verify whether reflection already covers the new property:\n" + "\n".join(hits))
    else:
        notes.append("No explicit SQL/CSV property mappings containing RdpVersion/UseRCG were found; these paths appear to use generic property serialization.")


def update_spec_encoding() -> None:
    path = ROOT / "docs" / "native-rdp-client.md"
    if not path.exists():
        return
    text = read(path)
    text = text.replace("deterministic UTF-8 `.rdp` file", "deterministic UTF-16LE `.rdp` file")
    write(path, text)


def main() -> None:
    try:
        create_sources()
        patch_model()
        patch_xml_serializer()
        patch_xsd()
        patch_xml_deserializer()
        patch_connection_initiator()
        scan_persistence()
        update_spec_encoding()
    except Exception as exc:
        errors.append(str(exc))

    report = ["# Native RDP implementation report", ""]
    report.append("## Changed files")
    report.extend(f"- `{item}`" for item in sorted(set(changes)))
    if not changes:
        report.append("- No source changes were necessary.")
    report.extend(["", "## Notes"])
    report.extend(f"- {item}" for item in notes)
    if not notes:
        report.append("- None.")
    report.extend(["", "## Patcher errors"])
    report.extend(f"- {item}" for item in errors)
    if not errors:
        report.append("- None.")
    report.append("")
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text("\n".join(report), encoding="utf-8")


if __name__ == "__main__":
    main()
