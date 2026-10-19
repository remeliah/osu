// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using osu.Framework.Logging;

namespace osu.Game.Online.Bancho
{
    internal sealed class BanchoConnection : IDisposable
    {
        // Keep this aligned with the stable client snapshot under reference/.
        // Private servers may validate the build date and expose it in session logs.
        internal const string STABLE_VERSION = "b20260711";

        private readonly HttpClient httpClient;
        private readonly ConcurrentQueue<byte[]> outgoingPackets = new();
        private readonly Uri endpoint;

        private string? token;

        public BanchoConnection(string endpointUrl)
        {
            endpoint = PrivateServerEndpointValidator.Validate(endpointUrl);

            httpClient = new HttpClient(new HttpClientHandler
            {
                // A private endpoint must not be able to bounce the client to an official host.
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
        }

        public async Task<IReadOnlyList<BanchoPacket>> LoginAsync(string username, string passwordHash, CancellationToken cancellationToken)
        {
            int utcOffset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.Now).TotalHours;
            string loginBody = $"{username}\n{passwordHash}\n{STABLE_VERSION}|{utcOffset.ToString(CultureInfo.InvariantCulture)}|0|{BanchoClientIdentity.LoginHashes}|0\n";

            using var request = createRequest(Encoding.UTF8.GetBytes(loginBody), includeToken: false);
            request.Headers.TryAddWithoutValidation("osu-version", STABLE_VERSION);
            Logger.Log($"Sending Bancho login POST to {endpoint.GetLeftPart(UriPartial.Path)}", LoggingTarget.Network);
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

            ensurePrivateRedirectWasNotReturned(response);
            await ensureSuccess(response, cancellationToken).ConfigureAwait(false);

            if (!response.Headers.TryGetValues("cho-token", out IEnumerable<string>? values)
                || string.IsNullOrWhiteSpace(token = values.FirstOrDefault()))
                throw new InvalidDataException("Server did not return a cho-token header.");

            return BanchoPacketCodec.Decode(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        }

        public void Enqueue(ClientPacket type, Action<BinaryWriter>? writePayload = null) => outgoingPackets.Enqueue(BanchoPacketCodec.Encode(type, writePayload));

        public string? SessionToken => token;

        public async Task<IReadOnlyList<BanchoPacket>> PollAsync(CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            while (outgoingPackets.TryDequeue(out byte[]? packet))
                body.Write(packet);

            if (body.Length == 0)
                body.Write(BanchoPacketCodec.Encode(ClientPacket.Pong));

            using var request = createRequest(body.ToArray(), includeToken: true);
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

            ensurePrivateRedirectWasNotReturned(response);
            await ensureSuccess(response, cancellationToken).ConfigureAwait(false);
            return BanchoPacketCodec.Decode(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        }

        public async Task LogoutAsync(CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(token))
                return;

            using var request = createRequest(CreateLogoutPacket(), includeToken: true);
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

            ensurePrivateRedirectWasNotReturned(response);
            await ensureSuccess(response, cancellationToken).ConfigureAwait(false);
        }

        internal static byte[] CreateLogoutPacket() =>
            BanchoPacketCodec.Encode(ClientPacket.Logout, writer => writer.Write(0));

        private static async Task ensureSuccess(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.IsSuccessStatusCode)
                return;

            string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (responseBody.Length > 300)
                responseBody = responseBody[..300];

            Logger.Log($"Bancho endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}): {responseBody}", LoggingTarget.Network, LogLevel.Important);
            throw new HttpRequestException($"Bancho endpoint returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}). {responseBody}", null, response.StatusCode);
        }

        private HttpRequestMessage createRequest(byte[] body, bool includeToken)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new ByteArrayContent(body),
            };
            // Stable sends this exact value (without a product version). Some Bancho
            // implementations reject unknown clients before reading the login body.
            request.Headers.TryAddWithoutValidation("User-Agent", "osu!");

            // pWebRequest.AddRaw() does not assign a content type. Keep this unset to match
            // stable and avoid reverse proxies routing the request as a generic binary upload.

            if (includeToken)
                request.Headers.TryAddWithoutValidation("osu-token", token);

            return request;
        }

        private static void ensurePrivateRedirectWasNotReturned(HttpResponseMessage response)
        {
            if ((int)response.StatusCode is < 300 or >= 400)
                return;

            Uri? redirect = response.Headers.Location;
            if (redirect != null && redirect.IsAbsoluteUri)
                PrivateServerEndpointValidator.Validate(redirect.AbsoluteUri);

            throw new HttpRequestException("Bancho endpoint returned a redirect; redirects are disabled.");
        }

        public void Dispose() => httpClient.Dispose();
    }
}
