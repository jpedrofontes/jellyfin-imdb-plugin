using System.Net.Http;
using System.Text.Json;
using System.Threading;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using ItemUpdateType = MediaBrowser.Controller.Library.ItemUpdateType;

namespace Jellyfin.Plugin.ImdbRatings;

public class ImdbRatingsTask : IScheduledTask
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private readonly ILibraryManager _libraryManager;

    public ImdbRatingsTask(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public string Name => "Refresh IMDb Ratings";
    public string Key => "ImdbRatingsRefresh";
    public string Description => "Updates movie community ratings from IMDb via the OMDb API.";
    public string Category => "IMDb";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableRatingsTask)
            return;

        var apiKey = config.OmdbApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            return;

        var movies = _libraryManager.GetItemList(new MediaBrowser.Controller.Entities.InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
            IsVirtualItem = false
        });

        int updated = 0;
        int failed = 0;

        for (int i = 0; i < movies.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var movie = movies[i];
            var imdbId = movie.GetProviderId(MetadataProvider.Imdb);

            if (string.IsNullOrEmpty(imdbId))
            {
                progress.Report((double)(i + 1) / movies.Count * 100);
                continue;
            }

            try
            {
                var url = $"https://www.omdbapi.com/?i={imdbId}&apikey={apiKey}";
                var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.GetProperty("Response").GetString() == "True" &&
                    root.TryGetProperty("imdbRating", out var ratingEl) &&
                    float.TryParse(ratingEl.GetString(),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out float rating) &&
                    rating > 0)
                {
                    movie.CommunityRating = rating;
                    await _libraryManager.UpdateItemAsync(
                        movie,
                        movie.GetParent(),
                        ItemUpdateType.MetadataEdit,
                        cancellationToken).ConfigureAwait(false);
                    updated++;
                }

                // OMDb free tier: 1000 req/day — small delay to be safe
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }

            progress.Report((double)(i + 1) / movies.Count * 100);
        }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Run daily at 3:30am (30 min after Jellyfin's typical library scan)
        yield return new TaskTriggerInfo
        {
            Type = "DailyTrigger",
            TimeOfDayTicks = TimeSpan.FromHours(3.5).Ticks
        };
    }
}
