// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.IO.Stores;
using osu.Framework.Logging;
using osu.Game.Configuration;

namespace osu.Game.Online
{
    public sealed class TrustedDomainOnlineStore : OnlineStore
    {
        private readonly Uri[]? fixedAllowedOrigins;
        private readonly OsuConfigManager? config;

        public TrustedDomainOnlineStore(string? privateServerUrl = null, string? privateWebsiteUrl = null, string? privateResourceUrls = null)
        {
            var endpoints = new PrivateServerEndpointConfiguration(privateServerUrl, websiteUrl: privateWebsiteUrl, avatarUrl: privateResourceUrls);
            fixedAllowedOrigins = endpoints.AllowedOrigins.ToArray();
        }

        public TrustedDomainOnlineStore(OsuConfigManager config)
        {
            this.config = config;
        }

        protected override string GetLookupUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !isAllowed(uri))
            {
                Logger.Log($@"Blocking resource lookup from external website: {url}", LoggingTarget.Network, LogLevel.Debug);
                return string.Empty;
            }

            return url;
        }

        private bool isAllowed(Uri uri)
        {
            IReadOnlyList<Uri> allowedOrigins = config == null
                ? fixedAllowedOrigins ?? Array.Empty<Uri>()
                : PrivateServerEndpointConfiguration.FromConfig(config).AllowedOrigins;

            return allowedOrigins.Any(origin => uri.Scheme.Equals(origin.Scheme, StringComparison.OrdinalIgnoreCase)
                                                && uri.IdnHost.Equals(origin.IdnHost, StringComparison.OrdinalIgnoreCase)
                                                && uri.Port == origin.Port);
        }
    }
}
