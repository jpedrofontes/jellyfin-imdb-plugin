using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    private static Dictionary<string, int>? _cachedRanks;
    private static DateTime _cacheTime = DateTime.MinValue;

    public ImdbPlaylistTask(ILibraryManager libraryManager, IPlaylistManager playlistManager)
    {
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
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

        if (string.IsNullOrWhiteSpace(config.UserId))
            return;

        progress.Report(0);

        var cacheMaxAge = TimeSpan.FromHours(config.ChartCacheHours > 0 ? config.ChartCacheHours : 24);
        var ranks = await GetChartRanksAsync(cacheMaxAge, cancellationToken).ConfigureAwait(false);
        if (ranks.Count == 0)
            return;

        progress.Report(30);

        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
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

        var userId = Guid.Parse(config.UserId);

        // Find or create the playlist
        Playlist? playlist = null;
        if (!string.IsNullOrWhiteSpace(config.PlaylistId) && Guid.TryParse(config.PlaylistId, out var playlistGuid))
            playlist = _libraryManager.GetItemById(playlistGuid) as Playlist;

        if (playlist == null)
        {
            // Search for an existing "IMDb Top 250" playlist
            var playlists = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Playlist },
                Recursive = true,
            });
            playlist = playlists
                .OfType<Playlist>()
                .FirstOrDefault(p => p.Name.Contains("IMDb Top 250", StringComparison.OrdinalIgnoreCase));
        }

        if (playlist == null)
        {
            var result = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = "IMDb Top 250",
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
        var existing = playlist.GetChildren(null, true).ToList();
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
        const string url = "https://web.archive.org/web/2/https://www.imdb.com/chart/top/";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; jellyfin-imdb-plugin/1.0)");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new Dictionary<string, int>();

        var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var match = Regex.Match(html, @"<script id=""__NEXT_DATA__""[^>]*>(.*?)</script>", RegexOptions.Singleline);
        if (!match.Success)
            return new Dictionary<string, int>();

        using var doc = JsonDocument.Parse(match.Groups[1].Value);
        var ranks = new Dictionary<string, int>();
        FindEdges(doc.RootElement, ranks, 0);
        return ranks;
    }

    private static void FindEdges(JsonElement element, Dictionary<string, int> ranks, int depth)
    {
        if (depth > 12) return;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("chartTitles", out var chartTitles) &&
                chartTitles.TryGetProperty("edges", out var edges) &&
                edges.ValueKind == JsonValueKind.Array)
            {
                foreach (var edge in edges.EnumerateArray())
                {
                    if (edge.TryGetProperty("currentRank", out var rankEl) &&
                        edge.TryGetProperty("node", out var node) &&
                        node.TryGetProperty("id", out var idEl))
                    {
                        var id = idEl.GetString();
                        if (!string.IsNullOrEmpty(id) && rankEl.TryGetInt32(out int rank))
                            ranks[id] = rank;
                    }
                }
                return;
            }

            foreach (var prop in element.EnumerateObject())
                FindEdges(prop.Value, ranks, depth + 1);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                FindEdges(item, ranks, depth + 1);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = "IntervalTrigger",
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }
}
