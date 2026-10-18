using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PulseRAM.Core;

namespace PulseRAM
{
    public sealed class UpdateInfo
    {
        public string Version { get; init; } = "";
        public string PageUrl { get; init; } = "";
        public string Title { get; init; } = "";
    }

    /// <summary>
    /// Asks GitHub for the latest PulseRAM release, at most once a day. Sends nothing but a plain HTTPS GET
    /// (no identifiers, no telemetry). The result only ever opens the release page in the browser: PulseRAM never
    /// downloads or runs anything by itself.
    /// </summary>
    public static class UpdateChecker
    {
        public const string Repo = "Hiirocchi18/PulseRAM";
        private const string LatestApi = "https://api.github.com/repos/" + Repo + "/releases/latest";
        private const string ReleasePagePrefix = "https://github.com/" + Repo + "/releases/";

        public static Version CurrentVersion
        {
            get
            {
                var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string CurrentVersionText => $"{CurrentVersion.Major}.{CurrentVersion.Minor}.{CurrentVersion.Build}";

        /// <summary>True if a scheduled check is due (enabled and not checked in the last 24 h).</summary>
        public static bool IsDue(EngineConfig cfg, DateTime utcNow) =>
            cfg.CheckForUpdates && (cfg.LastUpdateCheckUtc is not DateTime last || utcNow - last >= TimeSpan.FromHours(24) || last > utcNow);

        /// <summary>
        /// Returns the newer release, or null when up to date, offline, rate-limited or no release exists.
        /// Never throws. <paramref name="ignoreSkipped"/> is for a manual "Check now".
        /// </summary>
        public static async Task<UpdateInfo?> CheckAsync(EngineConfig cfg, bool ignoreSkipped, CancellationToken ct = default)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PulseRAM", CurrentVersionText));
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

                using var resp = await http.GetAsync(LatestApi, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    Log.Info($"Update check: GitHub answered {(int)resp.StatusCode} (no published release yet, or rate-limited)");
                    return null;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
                var root = doc.RootElement;
                string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
                string name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                bool draft = root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True;
                bool pre = root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True;

                if (draft || pre || !VersionInfo.IsNewer(tag, CurrentVersion)) return null;
                if (!VersionInfo.TryParseTag(tag, out var latest)) return null;
                string latestText = $"{latest.Major}.{latest.Minor}.{latest.Build}";
                if (!ignoreSkipped && string.Equals(cfg.SkippedUpdateVersion, latestText, StringComparison.OrdinalIgnoreCase)) return null;

                // Only ever open this repository's own release page.
                if (!url.StartsWith(ReleasePagePrefix, StringComparison.OrdinalIgnoreCase))
                    url = ReleasePagePrefix + "latest";

                Log.Info($"Update available: {latestText} (running {CurrentVersionText})");
                return new UpdateInfo { Version = latestText, PageUrl = url, Title = string.IsNullOrWhiteSpace(name) ? $"PulseRAM {latestText}" : name };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException)
            {
                Log.Info($"Update check skipped: {ex.GetType().Name}");
                return null;
            }
            catch (Exception ex)
            {
                Log.Warn("Update check failed", ex);
                return null;
            }
        }
    }
}
