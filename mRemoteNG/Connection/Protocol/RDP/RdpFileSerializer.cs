using System;
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
        public static string Serialize(ConnectionInfo connectionInfo)
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
