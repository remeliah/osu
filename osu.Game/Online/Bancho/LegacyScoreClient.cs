// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using osu.Framework.Logging;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Rooms;
using osu.Game.Online.Solo;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens.Play.Leaderboards;
using osu.Game.Users;

namespace osu.Game.Online.Bancho
{
    internal sealed class LegacyScoreClient : IDisposable
    {
        private const string score_version = "20260312";

        private readonly Uri websiteRoot;
        private readonly HttpClient httpClient;

        public LegacyScoreClient(string websiteUrl)
        {
            websiteRoot = PrivateServerEndpointValidator.Validate(websiteUrl);
            httpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            })
            {
                Timeout = TimeSpan.FromSeconds(30),
            };
        }

        public async Task<APIScoresCollection> FetchScoresAsync(GetScoresRequest request, Ruleset ruleset, string username, string passwordHash, CancellationToken cancellationToken)
        {
            if (request.Scope == BeatmapLeaderboardScope.Team)
                throw new NotSupportedException("osu!stable Bancho does not provide a team leaderboard scope.");

            LegacyMods mods = LegacyModConversion.ConvertLeaderboardFilter(ruleset, request.Mods);
            int stableMode = resolveStableLeaderboardMode(request.Ruleset.OnlineID, ref mods);
            // A stable mods leaderboard is selected explicitly by v=2. The mask
            // may legitimately be zero (Selected Mods with NM/Classic), so it
            // cannot be inferred from the converted bitmask alone. If the only
            // selected mods are lazer-only, fall back to the underlying board.
            bool hasOnlyNoModOrClassic = request.Mods.All(mod => mod is ModNoMod or ModClassic);
            bool exactMods = request.FilterByMods && (mods != LegacyMods.None || hasOnlyNoModOrClassic);

            int leaderboardType = exactMods ? 2 : request.Scope switch
            {
                BeatmapLeaderboardScope.Friend => 3,
                BeatmapLeaderboardScope.Country => 4,
                _ => 1,
            };

            string filename = $"{request.BeatmapInfo.Metadata.Artist} - {request.BeatmapInfo.Metadata.Title} ({request.BeatmapInfo.Metadata.Author.Username}) [{request.BeatmapInfo.DifficultyName}].osu";
            var parameters = new Dictionary<string, string>
            {
                ["us"] = username,
                ["ha"] = passwordHash,
                ["s"] = "0",
                ["vv"] = "4",
                ["v"] = leaderboardType.ToString(CultureInfo.InvariantCulture),
                ["c"] = request.BeatmapInfo.MD5Hash,
                ["f"] = filename,
                // The HTTP stable endpoint takes the vanilla mode plus the
                // bitwise mod mask; the server resolves RX/AP/TD from those
                // parameters. (The realtime UserStats packet uses extensions.)
                ["m"] = stableMode.ToString(CultureInfo.InvariantCulture),
                ["i"] = (request.BeatmapInfo.BeatmapSet?.OnlineID ?? -1).ToString(CultureInfo.InvariantCulture),
                ["mods"] = ((int)mods).ToString(CultureInfo.InvariantCulture),
                ["h"] = string.Empty,
                ["a"] = "0",
            };

            int[] modeCandidates = mods.HasFlag(LegacyMods.Relax) && request.Ruleset.OnlineID <= 2
                ? [stableMode, request.Ruleset.OnlineID + 4]
                : [stableMode];

            APIScoresCollection? parsed = null;
            foreach (int candidate in modeCandidates.Distinct())
            {
                try
                {
                    parameters["m"] = candidate.ToString(CultureInfo.InvariantCulture);
                    using var queryContent = new FormUrlEncodedContent(parameters);
                    string query = await queryContent.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    using var message = createRequest(HttpMethod.Get, createEndpoint($"web/osu-osz2-getscores.php?{query}"));
                    using HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
                    string body = await readSuccessfulResponse(response, cancellationToken).ConfigureAwait(false);
                    parsed = ParseScoresResponse(body, request, ruleset);
                    if (parsed.Scores.Count > 0 || !mods.HasFlag(LegacyMods.Relax))
                        return parsed;
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                        throw;

                    Logger.Log($"Stable leaderboard candidate mode {candidate} failed: {ex.Message}", LoggingTarget.Network,
                        parsed == null ? LogLevel.Error : LogLevel.Debug);
                    if (candidate == modeCandidates[^1])
                    {
                        // Some stable-compatible servers only accept one of the two RX
                        // mode encodings. If an earlier encoding returned a valid empty
                        // leaderboard, a failing fallback must not turn that into a
                        // network failure in the client.
                        if (parsed != null)
                            return parsed;

                        throw;
                    }
                }
            }

            return parsed ?? emptyScores();
        }

        public async Task<MultiplayerScore> SubmitScoreAsync(SubmitSoloScoreRequest request, Ruleset ruleset, string username, string passwordHash, string sessionToken, CancellationToken cancellationToken)
        {
            ScoreInfo info = request.ReplayScore.ScoreInfo;
            if (!LegacyModConversion.TryConvertForSubmission(ruleset, info.Mods, out LegacyMods mods, out string error))
                throw new InvalidOperationException(error);

            if (info.LegacyTotalScore is not long classicScore)
                throw new InvalidOperationException("Exact osu!stable ScoreV1 total is unavailable; refusing to submit an approximated score.");

            int stableScore = checked((int)Math.Clamp(classicScore, 0, int.MaxValue));
            int n300 = info.GetCount300() ?? 0;
            int n100 = info.GetCount100() ?? 0;
            int n50 = info.GetCount50() ?? 0;
            int ngeki = info.GetCountGeki() ?? 0;
            int nkatu = info.GetCountKatu() ?? 0;
            int nmiss = info.GetCountMiss() ?? 0;
            bool perfect = ruleset.RulesetInfo.OnlineID == 3
                ? nmiss + n50 + n100 == 0 && (n300 + ngeki + nkatu + n100 + n50 + nmiss == 0
                    || (double)nkatu / (n300 + ngeki + nkatu + n100 + n50 + nmiss) < 0.1)
                : info.MaxCombo == info.GetMaximumAchievableCombo();
            string clientTime = info.Date.ToUniversalTime().ToString("yyMMddHHmmss", CultureInfo.InvariantCulture);
            string grade = info.Rank.ToString();
            string clientHash = BanchoClientIdentity.ScoreClientHash;
            string checksum = ComputeOnlineChecksum(n300, n100, n50, ngeki, nkatu, nmiss, info.BeatmapInfo!.MD5Hash, info.MaxCombo, perfect,
                username, stableScore, grade, mods, info.Passed, info.RulesetID, clientTime, clientHash);

            string[] scoreData =
            [
                info.BeatmapInfo.MD5Hash,
                username,
                checksum,
                n300.ToString(CultureInfo.InvariantCulture),
                n100.ToString(CultureInfo.InvariantCulture),
                n50.ToString(CultureInfo.InvariantCulture),
                ngeki.ToString(CultureInfo.InvariantCulture),
                nkatu.ToString(CultureInfo.InvariantCulture),
                nmiss.ToString(CultureInfo.InvariantCulture),
                stableScore.ToString(CultureInfo.InvariantCulture),
                info.MaxCombo.ToString(CultureInfo.InvariantCulture),
                perfect.ToString(),
                grade,
                ((int)mods).ToString(CultureInfo.InvariantCulture),
                info.Passed.ToString(),
                info.RulesetID.ToString(CultureInfo.InvariantCulture),
                clientTime,
                score_version,
            ];

            byte[] iv = RandomNumberGenerator.GetBytes(32);
            byte[] key = Encoding.UTF8.GetBytes($"osu!-scoreburgr---------{score_version}");
            string encryptedScore = Convert.ToBase64String(EncryptRijndael256(Encoding.UTF8.GetBytes(string.Join(':', scoreData)), key, iv));
            string encryptedClientHash = Convert.ToBase64String(EncryptRijndael256(Encoding.UTF8.GetBytes(clientHash), key, iv));
            string encryptedVisualSettings = Convert.ToBase64String(EncryptRijndael256(Encoding.UTF8.GetBytes("0:False:False:False:True"), key, iv));

            var replayScore = request.ReplayScore.DeepClone();
            replayScore.ScoreInfo.TotalScore = stableScore;
            replayScore.ScoreInfo.LegacyTotalScore = stableScore;
            byte[] replay = new LegacyScoreEncoder(replayScore, request.Beatmap).EncodeReplayData();

            using var content = new MultipartFormDataContent();
            addField(content, "x", "0");
            addField(content, "ft", info.Passed ? "0" : ((int)info.BeatmapInfo.Length).ToString(CultureInfo.InvariantCulture));
            addField(content, "fs", encryptedVisualSettings);
            addField(content, "bmk", info.BeatmapInfo.MD5Hash);
            addField(content, "iv", Convert.ToBase64String(iv));
            addField(content, "c1", $"{BanchoClientIdentity.UninstallId}|{BanchoClientIdentity.DiskId}");
            addField(content, "st", ((int)info.BeatmapInfo.Length).ToString(CultureInfo.InvariantCulture));
            addField(content, "pass", passwordHash);
            addField(content, "osuver", score_version);
            addField(content, "s", encryptedClientHash);
            addField(content, "score", encryptedScore);
            content.Add(new ByteArrayContent(replay), "score", "score.osr");

            using var message = createRequest(HttpMethod.Post, createEndpoint("web/osu-submit-modular-selector.php"));
            message.Headers.TryAddWithoutValidation("token", sessionToken);
            message.Content = content;
            using HttpResponseMessage response = await httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            string body = await readSuccessfulResponse(response, cancellationToken).ConfigureAwait(false);

            if (body.StartsWith("error:", StringComparison.OrdinalIgnoreCase) && !(body.StartsWith("error: no", StringComparison.OrdinalIgnoreCase) && !info.Passed))
                throw new InvalidDataException($"Stable score submission failed: {body.Trim()}");

            long scoreId = parseChartValue(body, "onlineScoreId") ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            int? position = checked((int?)parseChartValue(body, "rankAfter"));
            (UserStatistics? statisticsBefore, UserStatistics? statisticsAfter) = ParseSubmissionStatistics(body, info.User);
            return new MultiplayerScore
            {
                ID = scoreId,
                User = info.User,
                Rank = info.Rank,
                TotalScore = info.TotalScore,
                LegacyTotalScore = stableScore,
                Accuracy = info.Accuracy,
                MaxCombo = info.MaxCombo,
                Mods = info.APIMods,
                Statistics = new Dictionary<HitResult, int>(info.Statistics),
                MaximumStatistics = new Dictionary<HitResult, int>(info.MaximumStatistics),
                Passed = info.Passed,
                EndedAt = info.Date,
                Position = position,
                PP = info.PP,
                HasReplay = info.Passed,
                Ranked = true,
                RulesetId = info.RulesetID,
                BeatmapId = request.BeatmapId,
                StatisticsBefore = statisticsBefore,
                StatisticsAfter = statisticsAfter,
            };
        }

        internal static (UserStatistics? Before, UserStatistics? After) ParseSubmissionStatistics(string response, APIUser user)
        {
            string? overallChart = response.Replace("\r", string.Empty, StringComparison.Ordinal)
                                           .Split('\n')
                                           .FirstOrDefault(line => line.Contains("chartId:overall", StringComparison.OrdinalIgnoreCase));

            if (overallChart == null)
                return (null, null);

            Dictionary<string, string> values = overallChart.Split('|', StringSplitOptions.RemoveEmptyEntries)
                                                             .Select(segment => segment.Split(':', 2))
                                                             .Where(parts => parts.Length == 2)
                                                             .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);

            var before = new UserStatistics { User = user };
            var after = new UserStatistics { User = user };

            before.GlobalRank = parseNullableInt(values, "rankBefore");
            after.GlobalRank = parseNullableInt(values, "rankAfter");
            before.RankedScore = parseLong(values, "rankedScoreBefore");
            after.RankedScore = parseLong(values, "rankedScoreAfter");
            before.TotalScore = parseLong(values, "totalScoreBefore");
            after.TotalScore = parseLong(values, "totalScoreAfter");
            before.MaxCombo = parseInt(values, "maxComboBefore");
            after.MaxCombo = parseInt(values, "maxComboAfter");
            before.Accuracy = parseDouble(values, "accuracyBefore");
            after.Accuracy = parseDouble(values, "accuracyAfter");
            before.PP = parseNullableDecimal(values, "ppBefore");
            after.PP = parseNullableDecimal(values, "ppAfter");

            return (before, after);
        }

        internal static APIScoresCollection ParseScoresResponse(string response, GetScoresRequest request, Ruleset ruleset)
        {
            string[] lines = response.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
            if (lines.Length == 0)
                return emptyScores();

            string[] header = lines[0].Split('|');
            if (int.TryParse(header[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int stableStatus))
                request.LegacyBeatmapStatus = convertStableRankedStatus(stableStatus);

            if (header.Length > 2 && int.TryParse(header[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int headerBeatmapId) && headerBeatmapId > 0)
                request.LegacyBeatmapId = headerBeatmapId;
            if (header.Length > 3 && int.TryParse(header[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int headerBeatmapSetId) && headerBeatmapSetId > 0)
                request.LegacyBeatmapSetId = headerBeatmapSetId;

            // Stable servers return a short status-only header for unranked maps and maps with no leaderboard.
            if (header.Length < 5)
                return emptyScores();

            if (lines.Length < 5)
                throw new InvalidDataException("Private server returned a malformed stable leaderboard response.");

            int scoreCount = int.TryParse(header[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) ? count : 0;
            int beatmapId = request.LegacyBeatmapId is int responseBeatmapId
                ? responseBeatmapId
                : request.BeatmapInfo.OnlineID;
            var topScores = lines.Skip(5).Where(line => !string.IsNullOrWhiteSpace(line)).Select(line =>
            {
                try
                {
                    return parseScoreLine(line, request, ruleset, beatmapId);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Ignoring malformed stable leaderboard row: {ex.Message}; row={line}", LoggingTarget.Network, LogLevel.Error);
                    return null;
                }
            }).Where(score => score != null).Cast<SoloScoreInfo>().ToList();
            APIScoreWithPosition? userScore = null;
            if (!string.IsNullOrWhiteSpace(lines[4]))
            {
                try
                {
                    SoloScoreInfo personal = parseScoreLine(lines[4], request, ruleset, beatmapId);
                    int position = parsePosition(lines[4]);
                    userScore = new APIScoreWithPosition { Position = position, Score = personal };
                }
                catch (Exception ex)
                {
                    Logger.Log($"Ignoring malformed personal stable leaderboard row: {ex.Message}; row={lines[4]}", LoggingTarget.Network, LogLevel.Error);
                }
            }

            return new APIScoresCollection { ScoresCount = scoreCount, Scores = topScores, UserScore = userScore };
        }

        private static int resolveStableLeaderboardMode(int mode, ref LegacyMods mods)
        {
            if (mods.HasFlag(LegacyMods.Relax))
            {
                if (mode <= 2)
                    return mode;

                // Relax is not a valid stable extension for mania.
                mods &= ~LegacyMods.Relax;
            }

            return mode;
        }

        private static BeatmapOnlineStatus convertStableRankedStatus(int status) => status switch
        {
            -1 => BeatmapOnlineStatus.WIP, // not submitted
            0 => BeatmapOnlineStatus.Pending,
            // Stable's legacy client displays all submitted/ranked lifecycle
            // states as a ranked leaderboard. Lazer's finer statuses otherwise
            // show a misleading "unknown" badge for stable maps.
            1 or 2 or 3 or 4 or 5 => BeatmapOnlineStatus.Ranked,
            _ => BeatmapOnlineStatus.None,
        };

        private static APIScoresCollection emptyScores() => new()
        {
            ScoresCount = 0,
            Scores = [],
        };

        private static SoloScoreInfo parseScoreLine(string line, GetScoresRequest request, Ruleset ruleset, int beatmapId)
        {
            string[] values = line.Split('|');
            if (values.Length < 16)
                throw new InvalidDataException("Server returned a malformed stable score row.");

            long id = parseLong(values[0]);
            int score = checked((int)Math.Clamp(parseLong(values[2]), 0, int.MaxValue));
            int maxCombo = parseInt(values[3]);
            int n50 = parseInt(values[4]);
            int n100 = parseInt(values[5]);
            int n300 = parseInt(values[6]);
            int nmiss = parseInt(values[7]);
            int nkatu = parseInt(values[8]);
            int ngeki = parseInt(values[9]);
            LegacyMods legacyMods = (LegacyMods)parseInt(values[11]);
            int userId = parseInt(values[12]);
            int objectCount = getObjectCount(request.Ruleset.OnlineID, n300, n100, n50, ngeki, nkatu, nmiss);
            var statistics = createStatistics(request.Ruleset.OnlineID, n300, n100, n50, ngeki, nkatu, nmiss);

            var mods = ruleset.ConvertFromLegacyMods(legacyMods).ToList();
            ModClassic? classic = ruleset.CreateMod<ModClassic>();
            if (classic != null)
                mods.Add(classic);

            return new SoloScoreInfo
            {
                ID = checked((ulong)id),
                LegacyScoreId = checked((ulong)id),
                BeatmapID = beatmapId,
                RulesetID = request.Ruleset.OnlineID,
                Passed = true,
                TotalScore = ConvertClassicToStandardised(request.Ruleset.OnlineID, score, objectCount),
                LegacyTotalScore = score,
                Accuracy = calculateAccuracy(request.Ruleset.OnlineID, n300, n100, n50, ngeki, nkatu, nmiss),
                UserID = userId,
                User = new APIUser { Id = userId, Username = values[1], WasRecentlyOnline = true },
                MaxCombo = maxCombo,
                Rank = calculateRank(request.Ruleset.OnlineID, n300, n100, n50, ngeki, nkatu, nmiss, legacyMods),
                EndedAt = parseDate(values[14]),
                Mods = mods.Select(mod => new APIMod(mod)).ToArray(),
                Statistics = statistics,
                MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = objectCount },
                HasReplay = values[15] == "1",
                Ranked = true,
            };
        }

        private static Dictionary<HitResult, int> createStatistics(int mode, int n300, int n100, int n50, int ngeki, int nkatu, int nmiss)
        {
            var result = new Dictionary<HitResult, int> { [HitResult.Great] = n300, [HitResult.Miss] = nmiss };
            switch (mode)
            {
                case 0:
                    result[HitResult.Ok] = n100;
                    result[HitResult.Meh] = n50;
                    break;

                case 1:
                    result[HitResult.Ok] = n100;
                    result[HitResult.LargeBonus] = ngeki + nkatu;
                    break;

                case 2:
                    result[HitResult.LargeTickHit] = n100;
                    result[HitResult.SmallTickHit] = n50;
                    result[HitResult.SmallTickMiss] = nkatu;
                    break;

                case 3:
                    result[HitResult.Perfect] = ngeki;
                    result[HitResult.Good] = nkatu;
                    result[HitResult.Ok] = n100;
                    result[HitResult.Meh] = n50;
                    break;
            }
            return result;
        }

        private static int getObjectCount(int mode, int n300, int n100, int n50, int ngeki, int nkatu, int nmiss) => mode switch
        {
            2 => n300 + n100 + n50 + nkatu + nmiss,
            3 => n300 + n100 + n50 + ngeki + nkatu + nmiss,
            _ => n300 + n100 + n50 + nmiss,
        };

        private static double calculateAccuracy(int mode, int n300, int n100, int n50, int ngeki, int nkatu, int nmiss)
        {
            int count = getObjectCount(mode, n300, n100, n50, ngeki, nkatu, nmiss);
            if (count == 0)
                return 0;

            return mode switch
            {
                1 => (n300 + n100 * 0.5) / count,
                2 => (double)(n300 + n100 + n50) / count,
                3 => (n300 * 300d + ngeki * 300d + nkatu * 200d + n100 * 100d + n50 * 50d) / (count * 300d),
                _ => (n300 * 300d + n100 * 100d + n50 * 50d) / (count * 300d),
            };
        }

        private static ScoreRank calculateRank(int mode, int n300, int n100, int n50, int ngeki, int nkatu, int nmiss, LegacyMods mods)
        {
            ScoreRank rank = LegacyScoreRankCalculator.Calculate(mode, true, n300, n100, n50, ngeki, nkatu, nmiss);

            bool silver = mods.HasFlag(LegacyMods.Hidden) || mods.HasFlag(LegacyMods.Flashlight)
                                                        || mode == 3 && mods.HasFlag(LegacyMods.FadeIn);
            return silver ? rank switch { ScoreRank.X => ScoreRank.XH, ScoreRank.S => ScoreRank.SH, _ => rank } : rank;
        }

        internal static long ConvertClassicToStandardised(int mode, long classicScore, int objectCount)
        {
            if (objectCount <= 0 || mode == 3)
                return classicScore;

            double standardised = mode switch
            {
                0 => classicScore * ScoreProcessor.MAX_SCORE / (Math.Pow(objectCount, 2) * 32.57 + 100000),
                1 => classicScore * ScoreProcessor.MAX_SCORE / (objectCount * 1109d + 100000),
                2 => solveCatchStandardisedScore(classicScore, objectCount),
                _ => classicScore,
            };
            return (long)Math.Clamp(Math.Round(standardised), 0, ScoreProcessor.MAX_SCORE);
        }

        private static double solveCatchStandardisedScore(long classicScore, int objectCount)
        {
            double quadratic = objectCount * objectCount * 21.62;
            double proportion = (-100000 + Math.Sqrt(100000d * 100000 + 4 * quadratic * classicScore)) / (2 * quadratic);
            return proportion * ScoreProcessor.MAX_SCORE;
        }

        internal static string ComputeOnlineChecksum(int n300, int n100, int n50, int ngeki, int nkatu, int nmiss, string beatmapHash, int maxCombo, bool perfect,
                                                     string username, int score, string grade, LegacyMods mods, bool passed, int mode, string clientTime, string clientHash)
        {
            string source = $"chickenmcnuggets{n100 + n300}o15{n50}{ngeki}smustard{nkatu}{nmiss}uu{beatmapHash}{maxCombo}{perfect}{username}{score}{grade}{(int)mods}Q{passed}{mode}{score_version}{clientTime}{clientHash}";
            return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
        }

        internal static byte[] EncryptRijndael256(byte[] input, byte[] key, byte[] iv)
        {
            var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new RijndaelEngine(256)), new Pkcs7Padding());
            cipher.Init(true, new ParametersWithIV(new KeyParameter(key), iv));
            byte[] output = new byte[cipher.GetOutputSize(input.Length)];
            int length = cipher.ProcessBytes(input, output, 0);
            length += cipher.DoFinal(output, length);
            return output[..length];
        }

        private static void addField(MultipartFormDataContent content, string name, string value) => content.Add(new StringContent(value), name);

        private HttpRequestMessage createRequest(HttpMethod method, Uri uri)
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "osu!");
            return request;
        }

        private Uri createEndpoint(string relativePath)
            => ResolveRequestEndpoint(websiteRoot, relativePath);

        internal static Uri ResolveRequestEndpoint(Uri websiteRoot, string relativePath)
        {
            var root = new Uri(websiteRoot.AbsoluteUri.TrimEnd('/') + "/", UriKind.Absolute);
            var endpoint = new Uri(root, relativePath);

            // The endpoint validator deliberately rejects query strings in persisted server configuration.
            // Validate the resolved request path, then retain the request's encoded query parameters.
            PrivateServerEndpointValidator.Validate(endpoint.GetLeftPart(UriPartial.Path));
            return endpoint;
        }

        private static async Task<string> readSuccessfulResponse(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new HttpRequestException("Private stable web endpoint returned a redirect; redirects are disabled.");

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Private stable web endpoint returned HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}", null, response.StatusCode);
            return body;
        }

        private static long? parseChartValue(string response, string key)
        {
            foreach (string segment in response.Split('|'))
            {
                if (segment.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(segment[(key.Length + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                    return value;
            }
            return null;
        }

        private static int? parseNullableInt(IReadOnlyDictionary<string, string> values, string key)
            => values.TryGetValue(key, out string? value)
               && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : null;

        private static long parseLong(IReadOnlyDictionary<string, string> values, string key)
            => values.TryGetValue(key, out string? value)
               && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
                ? result
                : 0;

        private static int parseInt(IReadOnlyDictionary<string, string> values, string key)
            => values.TryGetValue(key, out string? value)
               && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : 0;

        private static double parseDouble(IReadOnlyDictionary<string, string> values, string key)
            => values.TryGetValue(key, out string? value)
               && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
                ? result
                : 0;

        private static decimal? parseNullableDecimal(IReadOnlyDictionary<string, string> values, string key)
            => values.TryGetValue(key, out string? value)
               && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal result)
                ? result
                : null;

        private static int parsePosition(string line)
        {
            string[] values = line.Split('|');
            return values.Length > 13 ? parseInt(values[13]) : 0;
        }

        private static int parseInt(string value) => int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        private static long parseLong(string value) => long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);

        private static DateTimeOffset parseDate(string value)
        {
            if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long timestamp))
            {
                // Different stable-compatible servers serialize play_time as
                // either Unix seconds or milliseconds.
                return timestamp > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                    : DateTimeOffset.FromUnixTimeSeconds(timestamp);
            }

            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset result)
                ? result
                : DateTimeOffset.UnixEpoch;
        }

        public void Dispose() => httpClient.Dispose();
    }
}
