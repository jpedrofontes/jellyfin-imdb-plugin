using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ImdbRatings;

public class RtAudienceEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("score")]
    public int Score { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("certified")]
    public bool Certified { get; set; }
}

public class RtScoresFetchTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private static readonly HttpClient _httpClient = new();
    private const string ItemScoresPath = "/config/data/rt_item_scores.json";
    private const string CertifiedCriticsPath = "/config/data/rt_certified_critics.json";
    private const string SlugCachePath = "/config/data/rt_slug_cache.json";
    private const double RequestDelay = 1.5;

    public RtScoresFetchTask(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public string Name => "Fetch RT Scores";
    public string Key => "RtScoresFetch";
    public string Description => "Fetches Rotten Tomatoes audience and critic scores for all movies.";
    public string Category => "IMDb";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableRtAudienceTask)
            return;

        progress.Report(0);

        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
            IsVirtualItem = false,
        });

        // Load slug cache
        var slugCache = await LoadJsonAsync<Dictionary<string, string>>(SlugCachePath, cancellationToken)
            ?? new Dictionary<string, string>();

        // Load existing item scores to avoid re-fetching
        var existingScores = await LoadJsonAsync<Dictionary<string, RtAudienceEntry>>(ItemScoresPath, cancellationToken)
            ?? new Dictionary<string, RtAudienceEntry>();

        var itemScores = new Dictionary<string, RtAudienceEntry>();
        var certifiedCritics = new List<string>();
        int fetched = 0;

        for (int i = 0; i < movies.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movie = movies[i];
            var imdbId = movie.GetProviderId(MetadataProvider.Imdb);
            var jfId = movie.Id.ToString("N");

            if (string.IsNullOrEmpty(imdbId))
            {
                progress.Report((double)(i + 1) / movies.Count * 100);
                continue;
            }

            // Use cached result if slug is known and we already have data
            if (existingScores.ContainsKey(jfId) && slugCache.ContainsKey(imdbId))
            {
                itemScores[jfId] = existingScores[jfId];
                // We don't know critic certified from existing data, skip
                progress.Report((double)(i + 1) / movies.Count * 100);
                continue;
            }

            var scorecard = await FetchRtScorecard(movie.Name, movie.ProductionYear, imdbId, slugCache, cancellationToken);
            if (scorecard != null)
            {
                var audScore = scorecard.AudienceScore;
                var audCertified = scorecard.AudienceCertified;
                itemScores[jfId] = new RtAudienceEntry { Score = audScore, Certified = audCertified };
                if (scorecard.CriticCertified)
                    certifiedCritics.Add(jfId);
                fetched++;
            }

            progress.Report((double)(i + 1) / movies.Count * 100);
        }

        // Save results
        await SaveJsonAsync(ItemScoresPath, itemScores, cancellationToken);
        await SaveJsonAsync(CertifiedCriticsPath, certifiedCritics, cancellationToken);
        await SaveJsonAsync(SlugCachePath, slugCache, cancellationToken);
    }

    private async Task<RtScorecard?> FetchRtScorecard(
        string title, int? year, string imdbId,
        Dictionary<string, string> slugCache,
        CancellationToken cancellationToken)
    {
        // Try cached slug first
        if (slugCache.TryGetValue(imdbId, out var cachedSlug))
        {
            var result = await TryFetchSlug(cachedSlug, cancellationToken);
            if (result != null) return result;
            slugCache.Remove(imdbId);
        }

        // Try slug variants
        foreach (var slug in GetSlugVariants(title, year))
        {
            await Task.Delay(TimeSpan.FromSeconds(RequestDelay), cancellationToken);
            var result = await TryFetchSlug(slug, cancellationToken);
            if (result != null)
            {
                slugCache[imdbId] = slug;
                return result;
            }
        }

        return null;
    }

    private async Task<RtScorecard?> TryFetchSlug(string slug, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.rottentomatoes.com/m/{slug}");
            request.Headers.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

            using var response = await _httpClient.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var match = Regex.Match(html,
                @"<script\s+id=""media-scorecard-json""[^>]*>(.*?)</script>",
                RegexOptions.Singleline);
            if (!match.Success) return null;

            using var doc = JsonDocument.Parse(match.Groups[1].Value);
            var root = doc.RootElement;

            if (!root.TryGetProperty("audienceScore", out var aud)) return null;
            if (!aud.TryGetProperty("score", out var audScoreEl)) return null;

            var audScore = int.Parse(audScoreEl.GetString()!, CultureInfo.InvariantCulture);
            var audCertified = aud.TryGetProperty("certified", out var ac) && ac.GetBoolean();

            var criticCertified = false;
            if (root.TryGetProperty("criticsScore", out var crit) &&
                crit.TryGetProperty("certified", out var cc))
                criticCertified = cc.GetBoolean();

            return new RtScorecard
            {
                AudienceScore = audScore,
                AudienceCertified = audCertified,
                CriticCertified = criticCertified,
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<string> GetSlugVariants(string title, int? year)
    {
        var baseSlug = Slugify(title);
        var variants = new List<string> { baseSlug };
        if (baseSlug.StartsWith("the_"))
            variants.Add(baseSlug[4..]);
        if (year.HasValue)
        {
            variants.Add($"{baseSlug}_{year}");
            if (baseSlug.StartsWith("the_"))
                variants.Add($"{baseSlug[4..]}_{year}");
        }
        // Keep hyphens/colons as separators (e.g. "spider-man" → "spider_man")
        var altSlug = SlugifyKeepSeparators(title);
        if (altSlug != baseSlug)
        {
            variants.Add(altSlug);
            if (year.HasValue)
                variants.Add($"{altSlug}_{year}");
        }
        return variants;
    }

    private static string Slugify(string title)
    {
        var normalized = title.Normalize(System.Text.NormalizationForm.FormKD);
        var ascii = new string(normalized.Where(c => c < 128).ToArray());
        ascii = Regex.Replace(ascii.ToLowerInvariant(), @"[^a-z0-9\s]", "");
        return Regex.Replace(ascii.Trim(), @"\s+", "_");
    }

    private static string SlugifyKeepSeparators(string title)
    {
        var normalized = title.Normalize(System.Text.NormalizationForm.FormKD);
        var ascii = new string(normalized.Where(c => c < 128).ToArray());
        // Replace hyphens, colons, dots with spaces (they become underscores)
        ascii = Regex.Replace(ascii, @"[-:.]", " ");
        ascii = Regex.Replace(ascii.ToLowerInvariant(), @"[^a-z0-9\s]", "");
        return Regex.Replace(ascii.Trim(), @"\s+", "_");
    }

    private static async Task<T?> LoadJsonAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch { return null; }
    }

    private static async Task SaveJsonAsync<T>(string path, T data, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(data);
        await File.WriteAllTextAsync(path, json, ct);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4.5).Ticks
        };
    }

    private class RtScorecard
    {
        public int AudienceScore { get; set; }
        public bool AudienceCertified { get; set; }
        public bool CriticCertified { get; set; }
    }
}
