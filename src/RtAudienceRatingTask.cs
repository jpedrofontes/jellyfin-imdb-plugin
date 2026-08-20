using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using ItemUpdateType = MediaBrowser.Controller.Library.ItemUpdateType;

namespace Jellyfin.Plugin.ImdbRatings;

public class RtAudienceEntry
{
    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("certified")]
    public bool Certified { get; set; }
}

public class RtAudienceRatingTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private const string ScoresFilePath = "/config/data/rt_audience_scores.json";
    private const string ItemScoresFilePath = "/config/data/rt_item_scores.json";

    public RtAudienceRatingTask(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    public string Name => "Refresh RT Audience Data";
    public string Key => "RtAudienceRatingRefresh";
    public string Description => "Generates RT audience/critic certified data files for the web UI (does not overwrite CriticRating).";
    public string Category => "IMDb";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config == null || !config.EnableRtAudienceTask)
            return;

        progress.Report(0);

        if (!File.Exists(ScoresFilePath))
            return;

        Dictionary<string, RtAudienceEntry>? scores;
        try
        {
            var json = await File.ReadAllTextAsync(ScoresFilePath, cancellationToken).ConfigureAwait(false);
            scores = JsonSerializer.Deserialize<Dictionary<string, RtAudienceEntry>>(json);
        }
        catch
        {
            return;
        }

        if (scores == null || scores.Count == 0)
            return;

        progress.Report(20);

        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie },
            IsVirtualItem = false,
        });

        var itemScores = new Dictionary<string, RtAudienceEntry>();

        for (int i = 0; i < movies.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var movie = movies[i];
            var imdbId = movie.GetProviderId(MetadataProvider.Imdb);

            if (!string.IsNullOrEmpty(imdbId) &&
                scores.TryGetValue(imdbId, out var entry))
            {
                itemScores[movie.Id.ToString("N")] = entry;
            }

            progress.Report(20 + (double)(i + 1) / movies.Count * 80);
        }

        // Write item-ID-keyed scores for the web UI JS
        await File.WriteAllTextAsync(
            ItemScoresFilePath,
            JsonSerializer.Serialize(itemScores),
            cancellationToken).ConfigureAwait(false);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(5).Ticks
        };
    }
}
