using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ImdbRatings;

public class PluginConfiguration : BasePluginConfiguration
{
    public string OmdbApiKey { get; set; } = string.Empty;

    /// <summary>Auto-populated when the plugin creates the playlist.</summary>
    public string PlaylistId { get; set; } = string.Empty;

    public string UserId { get; set; } = string.Empty;

    public bool EnableRatingsTask { get; set; } = true;

    public bool EnablePlaylistTask { get; set; } = true;

    public int ChartCacheHours { get; set; } = 24;
}
