using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace mRemoteNG.Connection.Protocol.RDP
{
    internal static class RdpFileSerializer
    {
        public static string Serialize(
            ConnectionInfo connectionInfo,
            RdpResolvedCredentials destinationCredentials,
            RdpResolvedCredentials gatewayCredentials,
            bool includeGatewayAccessToken = true)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);

            List<string> lines = new();
            AddString(lines, "full address", BuildFullAddress(connectionInfo.Hostname, connectionInfo.Port));
            if (connectionInfo.Port > 0)
                AddInt(lines, "server port", connectionInfo.Port);

            AddString(lines, "username", BuildUsername(destinationCredentials.Username, destinationCredentials.Domain));
            AddString(lines, "domain", destinationCredentials.Domain);

            AddInt(lines, "screen mode id", connectionInfo.Resolution == RDPResolutions.Fullscreen ? 2 : 1);
            AddInt(lines, "smart sizing", Bool(connectionInfo.Resolution == RDPResolutions.SmartSize));
            AddInt(lines, "dynamic resolution", Bool(connectionInfo.AutomaticResize));
            AddInt(lines, "session bpp", ColorDepth(connectionInfo.Colors.ToString()));

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

            AddInt(lines, "authentication level", Convert.ToInt32(connectionInfo.RDPAuthenticationLevel, CultureInfo.InvariantCulture));
            AddInt(lines, "enablecredsspsupport", Bool(connectionInfo.UseCredSsp));
            AddInt(lines, "use redirection server name", Bool(connectionInfo.UseRedirectionServerName));
            AddString(lines, "loadbalanceinfo", connectionInfo.LoadBalanceInfo);

            AddGateway(lines, connectionInfo, gatewayCredentials, includeGatewayAccessToken);
            AddString(lines, "alternate shell", connectionInfo.RDPStartProgram);
            AddString(lines, "shell working directory", connectionInfo.RDPStartProgramWorkDir);

            return string.Join("\r\n", lines) + "\r\n";
        }

        public static string BuildFullAddress(string hostname, int port)
        {
            string host = (hostname ?? string.Empty).Trim();
            if (host.Contains(':') && !host.StartsWith('['))
                host = $"[{host}]";
            return port > 0 && port != 3389 ? $"{host}:{port}" : host;
        }

        public static string BuildUsername(string username, string domain)
        {
            string normalizedUsername = username?.Trim() ?? string.Empty;
            string normalizedDomain = domain?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(normalizedUsername) ||
                string.IsNullOrEmpty(normalizedDomain) ||
                normalizedUsername.Contains('\\') ||
                normalizedUsername.Contains('@'))
            {
                return normalizedUsername;
            }

            return $"{normalizedDomain}\\{normalizedUsername}";
        }

        private static void AddDriveRedirection(ICollection<string> lines, ConnectionInfo connectionInfo)
        {
            switch (connectionInfo.RedirectDiskDrives)
            {
                case RDPDiskDrives.None:
                    AddInt(lines, "redirectdrives", 0);
                    break;
                case RDPDiskDrives.All:
                    AddInt(lines, "redirectdrives", 1);
                    AddString(lines, "drivestoredirect", "*");
                    break;
                case RDPDiskDrives.Custom:
                    string custom = NormalizeCustomDriveList(connectionInfo.RedirectDiskDrivesCustom);
                    AddInt(lines, "redirectdrives", Bool(!string.IsNullOrEmpty(custom)));
                    AddString(lines, "drivestoredirect", custom);
                    break;
                default:
                    string local = string.Join(
                        string.Empty,
                        DriveInfo.GetDrives()
                            .Where(drive => drive.DriveType == DriveType.Fixed)
                            .Select(drive => $"{drive.Name.TrimEnd('\\')}\\;"));
                    AddInt(lines, "redirectdrives", Bool(!string.IsNullOrEmpty(local)));
                    AddString(lines, "drivestoredirect", local);
                    break;
            }
        }

        internal static string NormalizeCustomDriveList(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            IEnumerable<char> letters = value.ToUpperInvariant().Where(char.IsLetter).Distinct();
            return string.Concat(letters.Select(letter => $"{letter}:\\;"));
        }

        private static void AddGateway(
            ICollection<string> lines,
            ConnectionInfo connectionInfo,
            RdpResolvedCredentials gatewayCredentials,
            bool includeGatewayAccessToken)
        {
            int usage = connectionInfo.RDGatewayUsageMethod.ToString() switch
            {
                "Always" => 1,
                "Detect" => 2,
                _ => 0
            };

            AddInt(lines, "gatewayusagemethod", usage);
            AddString(lines, "gatewayhostname", connectionInfo.RDGatewayHostname);
            if (usage != 0 && !string.IsNullOrWhiteSpace(connectionInfo.RDGatewayHostname))
                AddInt(lines, "gatewayprofileusagemethod", 1);

            int credentialSource = connectionInfo.RDGatewayUseConnectionCredentials switch
            {
                RDGatewayUseConnectionCredentials.SmartCard => 1,
                RDGatewayUseConnectionCredentials.AccessToken => 5,
                _ => 0
            };
            AddInt(lines, "gatewaycredentialssource", credentialSource);
            AddInt(lines, "promptcredentialonce", Bool(connectionInfo.RDGatewayUseConnectionCredentials == RDGatewayUseConnectionCredentials.Yes));
            AddString(lines, "gatewayusername", BuildUsername(gatewayCredentials.Username, gatewayCredentials.Domain));

            if (includeGatewayAccessToken &&
                connectionInfo.RDGatewayUseConnectionCredentials == RDGatewayUseConnectionCredentials.AccessToken)
            {
                AddString(lines, "gatewayaccesstoken", connectionInfo.RDGatewayAccessToken);
            }
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
            "Medium" => 1,
            "High" => 2,
            _ => 0
        };

        private static int Bool(bool value) => value ? 1 : 0;

        private static void AddInt(ICollection<string> lines, string key, int value) =>
            lines.Add($"{key}:i:{value.ToString(CultureInfo.InvariantCulture)}");

        private static void AddString(ICollection<string> lines, string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            string safe = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
            lines.Add($"{key}:s:{safe}");
        }
    }
}
