using System;
using mRemoteNG.App;
using mRemoteNG.Properties;
using mRemoteNG.Security.SymmetricEncryption;

namespace mRemoteNG.Connection.Protocol.RDP
{
    internal readonly record struct RdpResolvedCredentials(string Username, string Password, string Domain)
    {
        public static RdpResolvedCredentials Empty => new(string.Empty, string.Empty, string.Empty);
        public bool HasPassword => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrEmpty(Password);
    }

    internal static class RdpCredentialResolver
    {
        public static RdpResolvedCredentials ResolveDestination(ConnectionInfo connectionInfo, ConnectionInfo.Force force)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);
            if (force.HasFlag(ConnectionInfo.Force.NoCredentials))
                return RdpResolvedCredentials.Empty;

            string username = connectionInfo.Username ?? string.Empty;
            string password = connectionInfo.Password ?? string.Empty;
            string domain = connectionInfo.Domain ?? string.Empty;

            ResolveExternalProvider(
                connectionInfo.ExternalCredentialProvider,
                connectionInfo.UserViaAPI ?? string.Empty,
                connectionInfo,
                connectionInfo.Hostname ?? string.Empty,
                connectionInfo.Username ?? string.Empty,
                ref username,
                ref password,
                ref domain);

            ParseDomainPrefix(ref username, ref domain);
            ApplyDefaults(connectionInfo, ref username, ref password, ref domain);
            return new RdpResolvedCredentials(username, password, domain);
        }

        public static RdpResolvedCredentials ResolveGateway(
            ConnectionInfo connectionInfo,
            RdpResolvedCredentials destinationCredentials,
            ConnectionInfo.Force force)
        {
            ArgumentNullException.ThrowIfNull(connectionInfo);
            if (force.HasFlag(ConnectionInfo.Force.NoCredentials) ||
                connectionInfo.RDGatewayUsageMethod == RDGatewayUsageMethod.Never ||
                string.IsNullOrWhiteSpace(connectionInfo.RDGatewayHostname))
            {
                return RdpResolvedCredentials.Empty;
            }

            switch (connectionInfo.RDGatewayUseConnectionCredentials)
            {
                case RDGatewayUseConnectionCredentials.Yes:
                    return destinationCredentials;
                case RDGatewayUseConnectionCredentials.SmartCard:
                case RDGatewayUseConnectionCredentials.AccessToken:
                    return RdpResolvedCredentials.Empty;
            }

            string username = connectionInfo.RDGatewayUsername ?? string.Empty;
            string password = connectionInfo.RDGatewayPassword ?? string.Empty;
            string domain = connectionInfo.RDGatewayDomain ?? string.Empty;

            ResolveExternalProvider(
                connectionInfo.RDGatewayExternalCredentialProvider,
                connectionInfo.RDGatewayUserViaAPI ?? string.Empty,
                connectionInfo,
                connectionInfo.RDGatewayHostname ?? string.Empty,
                connectionInfo.RDGatewayUsername ?? string.Empty,
                ref username,
                ref password,
                ref domain);

            ParseDomainPrefix(ref username, ref domain);
            return new RdpResolvedCredentials(username, password, domain);
        }

        internal static void ParseDomainPrefix(ref string username, ref string domain)
        {
            if (!string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(username)) return;
            int slash = username.IndexOf('\\');
            if (slash <= 0) return;
            domain = username.Substring(0, slash);
            username = username.Substring(slash + 1);
        }

        private static void ApplyDefaults(
            ConnectionInfo connectionInfo,
            ref string username,
            ref string password,
            ref string domain)
        {
            string mode = OptionsCredentialsPage.Default.EmptyCredentials;

            if (string.IsNullOrEmpty(username))
            {
                if (mode == "windows")
                {
                    username = Environment.UserName;
                }
                else if (mode == "custom")
                {
                    username = OptionsCredentialsPage.Default.DefaultUsername ?? string.Empty;
                    if (string.IsNullOrEmpty(username) &&
                        OptionsCredentialsPage.Default.ExternalCredentialProviderDefault != ExternalCredentialProvider.None)
                    {
                        ResolveExternalProvider(
                            OptionsCredentialsPage.Default.ExternalCredentialProviderDefault,
                            OptionsCredentialsPage.Default.UserViaAPIDefault ?? string.Empty,
                            connectionInfo,
                            connectionInfo.Hostname ?? string.Empty,
                            connectionInfo.Username ?? string.Empty,
                            ref username,
                            ref password,
                            ref domain);
                    }
                }
            }

            if (string.IsNullOrEmpty(domain))
            {
                domain = mode switch
                {
                    "windows" => Environment.UserDomainName,
                    "custom" => OptionsCredentialsPage.Default.DefaultDomain ?? string.Empty,
                    _ => domain
                };
            }

            if (string.IsNullOrEmpty(password) &&
                mode == "custom" &&
                !string.IsNullOrEmpty(OptionsCredentialsPage.Default.DefaultPassword))
            {
                LegacyRijndaelCryptographyProvider provider = new();
                password = provider.Decrypt(OptionsCredentialsPage.Default.DefaultPassword, Runtime.EncryptionKey);
            }
        }

        private static void ResolveExternalProvider(
            ExternalCredentialProvider provider,
            string apiReference,
            ConnectionInfo connectionInfo,
            string targetHostname,
            string vaultFallbackUsername,
            ref string username,
            ref string password,
            ref string domain)
        {
            if (provider == ExternalCredentialProvider.None) return;

            string privateKey = string.Empty;
            try
            {
                switch (provider)
                {
                    case ExternalCredentialProvider.DelineaSecretServer:
                        ExternalConnectors.DSS.SecretServerInterface.FetchSecretFromServer(
                            apiReference, out username, out password, out domain, out privateKey);
                        break;
                    case ExternalCredentialProvider.ClickstudiosPasswordState:
                        ExternalConnectors.CPS.PasswordstateInterface.FetchSecretFromServer(
                            apiReference, out username, out password, out domain, out privateKey);
                        break;
                    case ExternalCredentialProvider.OnePassword:
                        ExternalConnectors.OP.OnePasswordCli.ReadPassword(
                            apiReference, out username, out password, out domain, out privateKey);
                        break;
                    case ExternalCredentialProvider.VaultOpenbao:
                        if (connectionInfo.VaultOpenbaoSecretEngine == VaultOpenbaoSecretEngine.Kv &&
                            string.IsNullOrEmpty(username))
                        {
                            username = vaultFallbackUsername;
                        }
                        ExternalConnectors.VO.VaultOpenbao.ReadPasswordRDP(
                            (int)connectionInfo.VaultOpenbaoSecretEngine,
                            connectionInfo.VaultOpenbaoMount ?? string.Empty,
                            connectionInfo.VaultOpenbaoRole ?? string.Empty,
                            ref username,
                            out password);
                        break;
                    default:
                        throw new NotSupportedException($"External credential provider '{provider}' is not supported for native RDP launch.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Unable to resolve credentials using external provider '{provider}' for '{targetHostname}'.",
                    ex);
            }
        }
    }
}
