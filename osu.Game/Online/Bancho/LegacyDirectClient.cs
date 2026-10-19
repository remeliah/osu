// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using osu.Game.Beatmaps;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Overlays.BeatmapListing;

namespace osu.Game.Online.Bancho
{
    /// <summary>
    /// Adapts lazer's beatmap listing to the stable osu!direct endpoints exposed by
    /// the configured server. The server remains responsible for choosing
    /// its beatmap mirror.
    /// </summary>
    internal sealed class LegacyDirectClient : IDisposable
    {
        private const int page_size = 100;
        private const int maximum_redirects = 5;

        private static readonly Regex difficulty_regex = new(
            @"(?:^|,)\[(?<stars>[0-9]+(?:\.[0-9]+)?)[^\]]*\]\s*(?<name>.*?)\s*\{cs:\s*(?<cs>[0-9]+(?:\.[0-9]+)?)\s*/\s*od:\s*(?<od>[0-9]+(?:\.[0-9]+)?)\s*/\s*ar:\s*(?<ar>[0-9]+(?:\.[0-9]+)?)\s*/\s*hp:\s*(?<hp>[0-9]+(?:\.[0-9]+)?)\s*\}@(?<mode>-?[0-9]+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly Uri websiteRoot;
        private readonly HttpClient client;

        public LegacyDirectClient(string websiteRoot)
        {
            this.websiteRoot = new Uri(websiteRoot, UriKind.Absolute);
            client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(60),
            };
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "osu!");
        }

        public async Task<SearchBeatmapSetsResponse> SearchAsync(SearchBeatmapSetsRequest request, string username, string passwordHash, CancellationToken cancellationToken)
        {
            var query = new Dictionary<string, string>
            {
                ["u"] = username,
                ["h"] = passwordHash,
                ["r"] = getDirectRankedStatus(request.SearchCategory).ToString(CultureInfo.InvariantCulture),
                ["q"] = getDirectQuery(request),
                ["m"] = (request.RulesetId is >= 0 and <= 3 ? request.RulesetId : -1).ToString(CultureInfo.InvariantCulture),
                ["p"] = request.Page.ToString(CultureInfo.InvariantCulture),
            };

            Uri endpoint = appendQuery(resolveEndpoint("web/osu-search.php"), query);
            using var response = await client.GetAsync(endpoint, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"osu!direct search returned HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}", null, response.StatusCode);

            return ParseSearchResponse(body, request.Page);
        }

        public async Task<string> DownloadAsync(DownloadBeatmapSetRequest request, CancellationToken cancellationToken)
        {
            if (request.Model.OnlineID <= 0)
                throw new InvalidOperationException("Cannot download a beatmap set without a stable online ID.");

            Uri current = resolveEndpoint($"d/{request.Model.OnlineID}{(request.NoVideo ? "n" : string.Empty)}");

            for (int redirect = 0; redirect <= maximum_redirects; redirect++)
            {
                validateDownloadUri(current);
                using var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                if (isRedirect(response.StatusCode))
                {
                    if (redirect == maximum_redirects)
                        throw new HttpRequestException("beatmap mirror returned too many redirects.");

                    Uri? location = response.Headers.Location;
                    if (location == null)
                        throw new HttpRequestException("beatmap mirror returned a redirect without a destination.");

                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    throw new HttpRequestException($"beatmap mirror returned HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}", null, response.StatusCode);
                }

                string filename = Path.Combine(Path.GetTempPath(), $"osu-direct-{Guid.NewGuid():N}.osz");

                try
                {
                    using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    using var destination = new FileStream(filename, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    return filename;
                }
                catch
                {
                    File.Delete(filename);
                    throw;
                }
            }

            throw new HttpRequestException("beatmap mirror redirect handling failed.");
        }

        internal static SearchBeatmapSetsResponse ParseSearchResponse(string body, int page)
        {
            string[] lines = body.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || !int.TryParse(lines[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int declaredCount))
                throw new InvalidDataException("osu!direct returned an invalid result count.");

            if (declaredCount < 0)
                throw new InvalidDataException(lines.Length > 1 ? lines[1] : "osu!direct search failed.");

            var sets = new List<APIBeatmapSet>();

            foreach (string line in lines.Skip(1))
            {
                string[] fields = line.Split('|', 14);
                if (fields.Length != 14
                    || !int.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out int setId))
                    continue;

                BeatmapOnlineStatus status = parseApiStatus(fields[4]);
                DateTimeOffset.TryParse(fields[6], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset lastUpdated);

                var set = new APIBeatmapSet
                {
                    OnlineID = setId,
                    Artist = fields[1],
                    ArtistUnicode = fields[1],
                    Title = fields[2],
                    TitleUnicode = fields[2],
                    AuthorString = fields[3],
                    Status = status,
                    LastUpdated = lastUpdated == default ? null : lastUpdated,
                    HasVideo = fields[9] is "1" or "true" or "True",
                };

                set.Beatmaps = parseDifficulties(fields[13], set, status, lastUpdated).ToArray();
                sets.Add(set);
            }

            bool hasMore = declaredCount > page_size || sets.Count >= page_size;
            return new SearchBeatmapSetsResponse
            {
                BeatmapSets = sets,
                Total = page * page_size + sets.Count + (hasMore ? 1 : 0),
                Cursor = hasMore
                    ? new Cursor { Properties = new Dictionary<string, JToken> { ["page"] = JToken.FromObject(page + 1) } }
                    : null,
            };
        }

        private static IEnumerable<APIBeatmap> parseDifficulties(string value, APIBeatmapSet set, BeatmapOnlineStatus status, DateTimeOffset lastUpdated)
        {
            foreach (Match match in difficulty_regex.Matches(value))
            {
                if (!double.TryParse(match.Groups["stars"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double stars)
                    || !float.TryParse(match.Groups["cs"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float cs)
                    || !float.TryParse(match.Groups["od"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float od)
                    || !float.TryParse(match.Groups["ar"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ar)
                    || !float.TryParse(match.Groups["hp"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float hp)
                    || !int.TryParse(match.Groups["mode"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int mode))
                    continue;

                yield return new APIBeatmap
                {
                    OnlineBeatmapSetID = set.OnlineID,
                    BeatmapSet = set,
                    Status = status,
                    RulesetID = mode,
                    StarRating = stars,
                    DifficultyName = match.Groups["name"].Value,
                    CircleSize = cs,
                    OverallDifficulty = od,
                    ApproachRate = ar,
                    DrainRate = hp,
                    LastUpdated = lastUpdated,
                };
            }
        }

        private static BeatmapOnlineStatus parseApiStatus(string value)
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int status))
                return BeatmapOnlineStatus.None;

            return status switch
            {
                -2 => BeatmapOnlineStatus.Graveyard,
                -1 => BeatmapOnlineStatus.WIP,
                0 => BeatmapOnlineStatus.Pending,
                1 => BeatmapOnlineStatus.Ranked,
                2 => BeatmapOnlineStatus.Approved,
                3 => BeatmapOnlineStatus.Qualified,
                4 => BeatmapOnlineStatus.Loved,
                _ => BeatmapOnlineStatus.None,
            };
        }

        private static int getDirectRankedStatus(SearchCategory category) => category switch
        {
            SearchCategory.Ranked => 0,
            SearchCategory.Pending or SearchCategory.Wip => 2,
            SearchCategory.Qualified => 3,
            SearchCategory.Graveyard => 5,
            SearchCategory.Loved => 8,
            _ => 4,
        };

        private static string getDirectQuery(SearchBeatmapSetsRequest request)
        {
            if (!string.IsNullOrWhiteSpace(request.Query))
                return request.Query;

            return request.SortCriteria switch
            {
                SortCriteria.Updated => "Newest",
                SortCriteria.Rating => "Top+Rated",
                SortCriteria.Plays => "Most+Played",
                _ => string.Empty,
            };
        }

        private Uri resolveEndpoint(string relativePath) => LegacyScoreClient.ResolveRequestEndpoint(websiteRoot, relativePath);

        private static Uri appendQuery(Uri endpoint, IReadOnlyDictionary<string, string> values)
        {
            var builder = new UriBuilder(endpoint)
            {
                Query = string.Join('&', values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
            };
            return builder.Uri;
        }

        private static bool isRedirect(HttpStatusCode statusCode) => statusCode is
            HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

        private static void validateDownloadUri(Uri uri)
        {
            if (!uri.IsAbsoluteUri || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                throw new HttpRequestException("beatmap mirror returned an invalid URL.");

            // Validation deliberately receives the path-only form because mirror URLs may
            // contain download query parameters. It also rejects osu.ppy.sh and subdomains.
            PrivateServerEndpointValidator.Validate(uri.GetLeftPart(UriPartial.Path));
        }

        public void Dispose() => client.Dispose();
    }
}
