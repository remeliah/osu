// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Configuration;

namespace osu.Game.Online
{
    /// <summary>
    /// Endpoint configuration for a private osu!stable-compatible server.
    /// </summary>
    public class PrivateServerEndpointConfiguration : EndpointConfiguration
    {
        public string Domain { get; private set; } = string.Empty;
        public string BanchoUrl { get; private set; } = string.Empty;
        public string? WebsiteRootUrl { get; private set; }
        public string AvatarRootUrl { get; private set; } = string.Empty;
        public IReadOnlyList<Uri> AllowedOrigins { get; private set; } = Array.Empty<Uri>();
        public Exception? ValidationError { get; private set; }

        public PrivateServerEndpointConfiguration(string? serverUrl, string? banchoUrl = null, string? websiteUrl = null, string? avatarUrl = null)
        {
            try
            {
                SetServerUrls(serverUrl, banchoUrl, websiteUrl, avatarUrl);
            }
            catch (ArgumentException ex)
            {
                // Keep the game bootable so the invalid value can be corrected in settings.
                // ValidationError is surfaced by the login provider.
                ValidationError = ex;
            }
        }

        public static PrivateServerEndpointConfiguration FromConfig(OsuConfigManager config)
        {
            bool useAdvancedEndpoints = config.Get<bool>(OsuSetting.PrivateServerAdvancedEndpoints);

            return new PrivateServerEndpointConfiguration(
                config.Get<string>(OsuSetting.PrivateServerUrl),
                useAdvancedEndpoints ? config.Get<string>(OsuSetting.PrivateServerBanchoUrl) : null,
                useAdvancedEndpoints ? config.Get<string>(OsuSetting.PrivateServerWebsiteUrl) : null,
                useAdvancedEndpoints ? config.Get<string>(OsuSetting.PrivateServerAvatarUrl) : null);
        }

        public void SetServerUrl(string serverUrl) => SetServerUrls(serverUrl);

        public void SetServerUrls(string? serverUrl, string? banchoUrl = null, string? websiteUrl = null, string? avatarUrl = null)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
            {
                clear();
                ValidationError = null;
                return;
            }

            string domain;
            try
            {
                domain = PrivateServerEndpointValidator.NormalizeDomain(serverUrl);
            }
            catch (ArgumentException ex)
            {
                ValidationError = ex;
                throw;
            }

            string endpointDomain = PrivateServerEndpointValidator.IsLocalDomain(domain) ? domain : $"c.{domain}";
            string websiteDomain = PrivateServerEndpointValidator.IsLocalDomain(domain) ? domain : $"osu.{domain}";
            string avatarDomain = PrivateServerEndpointValidator.IsLocalDomain(domain) ? domain : $"a.{domain}";

            string resolvedBanchoUrl = resolveEndpointUrl(banchoUrl, $"https://{endpointDomain}");
            string websiteRoot = resolveEndpointUrl(websiteUrl, $"https://{websiteDomain}");
            string resolvedAvatarUrl = resolveEndpointUrl(avatarUrl, $"https://{avatarDomain}");
            Uri[] allowedOrigins = new[] { new Uri(resolvedBanchoUrl), new Uri(websiteRoot), new Uri(resolvedAvatarUrl) }
                                   .DistinctBy(uri => uri.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase)
                                   .ToArray();

            // Do not partially mutate a live configuration. All values above are validated
            // before the active connection and resource stores observe the new endpoints.
            Domain = domain;
            BanchoUrl = resolvedBanchoUrl;
            AvatarRootUrl = resolvedAvatarUrl;
            WebsiteRootUrl = WebsiteUrl = APIUrl = websiteRoot;
            AllowedOrigins = allowedOrigins;

            // These are intentionally private-server relative. No production ppy endpoint is
            // ever used as a fallback when the stable protocol cannot provide a feature.
            SpectatorUrl = $"{websiteRoot}/signalr/spectator";
            MultiplayerUrl = $"{websiteRoot}/signalr/multiplayer";
            MetadataUrl = $"{websiteRoot}/signalr/metadata";
            BeatmapSubmissionServiceUrl = $"{websiteRoot}/beatmap-submission";
            LivenessProbeUrl = null;
            ValidationError = null;
        }

        private static string resolveEndpointUrl(string? configuredUrl, string defaultUrl)
        {
            if (string.IsNullOrWhiteSpace(configuredUrl))
                return defaultUrl;

            string value = configuredUrl.Trim();
            if (!value.Contains("://", StringComparison.Ordinal))
                value = $"https://{value}";

            return PrivateServerEndpointValidator.Validate(value).ToString().TrimEnd('/');
        }

        private void clear()
        {
            Domain = BanchoUrl = WebsiteRootUrl = WebsiteUrl = APIUrl = AvatarRootUrl = string.Empty;
            AllowedOrigins = Array.Empty<Uri>();
            SpectatorUrl = MultiplayerUrl = MetadataUrl = string.Empty;
            BeatmapSubmissionServiceUrl = null;
            LivenessProbeUrl = null;
        }

        public string GetAvatarUrl(int userId) => $"{AvatarRootUrl}/{userId}";
    }

    public static class PrivateServerEndpointValidator
    {
        private static readonly string[] service_prefixes = { "c.", "ce.", "c4.", "osu.", "a." };

        public static string NormalizeDomain(string value)
        {
            string input = value.Trim();
            if (input.Length == 0)
                throw new ArgumentException("Server domain cannot be empty.", nameof(value));

            if (!input.Contains("://", StringComparison.Ordinal))
                input = $"https://{input}";

            Uri uri = Validate(input);
            if (uri.AbsolutePath != "/")
                throw new ArgumentException("Enter only the server base domain, without a path.", nameof(value));

            string host = uri.IdnHost.TrimEnd('.');
            foreach (string prefix in service_prefixes)
            {
                if (host.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    host = host[prefix.Length..];
                    break;
                }
            }

            rejectOfficialDomain(host, nameof(value));
            return uri.IsDefaultPort ? host : $"{host}:{uri.Port}";
        }

        internal static bool IsLocalDomain(string domain)
        {
            string host = domain.Split(':')[0];
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || Uri.CheckHostName(host) == UriHostNameType.IPv4 || Uri.CheckHostName(host) == UriHostNameType.IPv6;
        }

        public static Uri Validate(string serverUrl)
        {
            if (!Uri.TryCreate(serverUrl.Trim(), UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || string.IsNullOrWhiteSpace(uri.Host)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException("Server URL must be an absolute HTTP(S) URL without credentials, query, or fragment.", nameof(serverUrl));
            }

            string host = uri.IdnHost.TrimEnd('.');

            rejectOfficialDomain(host, nameof(serverUrl));

            return uri;
        }

        private static void rejectOfficialDomain(string host, string parameterName)
        {
            if (host.Equals("ppy.sh", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".ppy.sh", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Official ppy.sh endpoints are disabled. Configure a private server.", parameterName);
        }
    }
}
