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
    private readonly ILibraryManager _libraryManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IUserManager _userManager;

    public ImdbPlaylistTask(ILibraryManager libraryManager, IPlaylistManager playlistManager, IUserManager userManager)
    {
        _libraryManager = libraryManager;
        _playlistManager = playlistManager;
        _userManager = userManager;
    }

    public string Name => "Refresh IMDb Top 250 Playlist";
    public string Key => "ImdbPlaylistRefresh";
    public string Description => "Updates the Jellyfin IMDb Top 250 playlist order from IMDb's official datasets.";
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
        var ranks = await ImdbDatasetCache.GetTop250Async(cacheMaxAge, cancellationToken).ConfigureAwait(false);
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
            .Take(250)
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

        // Delete and recreate playlist to ensure clean state
        _libraryManager.DeleteItem(playlist, new DeleteOptions { DeleteFileLocation = false });

        var newPlaylist = await _playlistManager.CreatePlaylist(new PlaylistCreationRequest
        {
            Name = "IMDb Top 250 Movies",
            UserId = userId,
        }).ConfigureAwait(false);

        config.PlaylistId = newPlaylist.Id;
        Plugin.Instance!.SaveConfiguration();

        progress.Report(80);

        // Add items in rank order
        var itemIds = orderedItems.Select(i => i.Id).ToArray();
        await _playlistManager.AddItemToPlaylistAsync(
            Guid.Parse(config.PlaylistId),
            itemIds,
            userId).ConfigureAwait(false);

        progress.Report(100);
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
