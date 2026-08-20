using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ImdbRatings;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "Movie Ratings (IMDb + RT)";

    public override Guid Id => new Guid("8db72461-cd14-4a3c-8093-5891cf02b8d0");

    public override string Description => "Syncs IMDb ratings, maintains a Top 250 playlist, and provides Rotten Tomatoes audience/critic data for the web UI. Information courtesy of IMDb (https://www.imdb.com). Used with permission.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html"
        };
    }
}
