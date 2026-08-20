using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.ImdbRatings;

public class ImdbPlaylistTask : IScheduledTask
{
    private static readonly HttpClient _httpClient = new();
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;

    private static Dictionary<string, int>? _cachedRanks;
    private static DateTime _cacheTime = DateTime.MinValue;

    public ImdbPlaylistTask(ILibraryManager libraryManager, IPlaylistManager playlistManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _userManager = userManager;
    }

    public string Name => "Refresh IMDb Top 250 Playlist";
    public string Key => "ImdbPlaylistRefresh";
    public string Description => "Updates the Jellyfin IMDb Top 250 playlist order from the IMDb chart.";
    public string Category => "IMDb";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnablePlaylistTask)
            return;

        var userId = ResolveUserId(config);
        if (userId == Guid.Empty)
            return;

        progress.Report(0);

        var cacheMaxAge = TimeSpan.FromHours(config.ChartCacheHours > 0 ? config.ChartCacheHours : 24);
        var ranks = await GetChartRanksAsync(cacheMaxAge, cancellationToken).ConfigureAwait(false);
        if (ranks.Count == 0)
            return;

        progress.Report(30);

        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            Recursive = true,
        });

        var moviesByImdb = new Dictionary<string, BaseItem>();
        foreach (var movie in movies)
        {
            var imdbId = movie.GetProviderId(MetadataProvider.Imdb);
            if (!string.IsNullOrEmpty(imdbId))
                moviesByImdb[imdbId] = movie;
        }

        var orderedItems = ranks
            .OrderBy(kv => kv.Value)
            .Where(kv => moviesByImdb.ContainsKey(kv.Key))
            .Select(kv => moviesByImdb[kv.Key])
            .ToList();

        if (orderedItems.Count == 0)
            return;

        progress.Report(60);

        // Find or create the playlist
        Playlist? playlist = null;
        if (!string.IsNullOrWhiteSpace(config.PlaylistId) && Guid.TryParse(config.PlaylistId, out var playlistGuid))
            playlist = _libraryManager.GetItemById(playlistGuid) as Playlist;

        if (playlist == null)
        {
            // Search for an existing "IMDb Top 250" playlist
            var playlists = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Playlist },
                Recursive = true,
            });
            playlist = playlists
                .OfType<Playlist>()
                .FirstOrDefault(p => p.Name.Contains("IMDb Top 250 Movies", StringComparison.OrdinalIgnoreCase));
        }

        if (playlist == null)
        {
            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = "IMDb Top 250 Movies",
                UserId = userId,
            }).ConfigureAwait(false);

            config.PlaylistId = result.Id;
            Plugin.Instance!.SaveConfiguration();
            playlist = _libraryManager.GetItemById(Guid.Parse(result.Id)) as Playlist;
            if (playlist == null)
                return;
        }
        else if (config.PlaylistId != playlist.Id.ToString("N"))
        {
            config.PlaylistId = playlist.Id.ToString("N");
            Plugin.Instance!.SaveConfiguration();
        }

        // Remove all existing entries
        var existing = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = playlist.Id,
        }).ToList();
        if (existing.Count > 0)
        {
            var entryIds = existing.Select(e => e.Id.ToString("N")).ToList();
            await _playlistManager.RemoveItemFromPlaylistAsync(
                config.PlaylistId,
                entryIds).ConfigureAwait(false);
        }

        progress.Report(80);

        // Add items in rank order
        var itemIds = orderedItems.Select(i => i.Id).ToArray();
        await _playlistManager.AddItemToPlaylistAsync(
            Guid.Parse(config.PlaylistId),
            itemIds,
            userId).ConfigureAwait(false);

        progress.Report(100);
    }

    private static async Task<Dictionary<string, int>> GetChartRanksAsync(
        TimeSpan maxAge, CancellationToken cancellationToken)
    {
        if (_cachedRanks != null && DateTime.UtcNow - _cacheTime < maxAge)
            return _cachedRanks;

        var ranks = await FetchImdbChartAsync(cancellationToken).ConfigureAwait(false);
        if (ranks.Count > 0)
        {
            _cachedRanks = ranks;
            _cacheTime = DateTime.UtcNow;
        }

        return ranks ?? new Dictionary<string, int>();
    }

    private static async Task<Dictionary<string, int>> FetchImdbChartAsync(CancellationToken cancellationToken)
    {
        // IMDb's official non-commercial dataset
        const string ratingsUrl = "https://datasets.imdbws.com/title.ratings.tsv.gz";
        const string basicsUrl = "https://datasets.imdbws.com/title.basics.tsv.gz";
        const int minVotes = 25_000;

        // Pass 1: stream title.basics.tsv.gz to collect movie tconsts
        var movieIds = new HashSet<string>(StringComparer.Ordinal);
        using (var request = new HttpRequestMessage(HttpMethod.Get, basicsUrl))
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new Dictionary<string, int>();

            await using var gz = new GZipStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false); // skip header
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                // tconst \t titleType \t ...
                var firstTab = line.IndexOf('\t');
                if (firstTab < 0) continue;
                var secondTab = line.IndexOf('\t', firstTab + 1);
                if (secondTab < 0) continue;
                var titleType = line.AsSpan(firstTab + 1, secondTab - firstTab - 1);
                if (titleType.SequenceEqual("movie".AsSpan()))
                    movieIds.Add(line.Substring(0, firstTab));
            }
        }

        // Pass 2: stream title.ratings.tsv.gz to get ratings for movies
        var ratings = new List<(string Id, double Rating, int Votes)>();
        using (var request = new HttpRequestMessage(HttpMethod.Get, ratingsUrl))
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new Dictionary<string, int>();

            await using var gz = new GZipStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), CompressionMode.Decompress);
            using var reader = new StreamReader(gz);
            await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false); // skip header
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                // tconst \t averageRating \t numVotes
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                if (!movieIds.Contains(parts[0])) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rating)) continue;
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var votes)) continue;
                if (votes >= minVotes)
                    ratings.Add((parts[0], rating, votes));
            }
        }

        if (ratings.Count == 0)
            return new Dictionary<string, int>();

        // Bayesian weighted rating: WR = (v/(v+m)) * R + (m/(v+m)) * C
        var meanRating = ratings.Average(r => r.Rating);
        var ranked = ratings
            .Select(r =>
            {
                var wr = (r.Votes / (double)(r.Votes + minVotes)) * r.Rating
                       + (minVotes / (double)(r.Votes + minVotes)) * meanRating;
                return (r.Id, WeightedRating: wr);
            })
            .OrderByDescending(r => r.WeightedRating)
            .Take(250)
            .Select((r, i) => (r.Id, Rank: i + 1))
            .ToDictionary(r => r.Id, r => r.Rank);

        return ranked;
    }

    private Guid ResolveUserId(PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.UserId) && Guid.TryParse(config.UserId, out var explicit_id))
            return explicit_id;

        var firstUser = _userManager.GetFirstUser();
        return firstUser?.Id ?? Guid.Empty;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }
}
