using System.Net.Http;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.ImdbRatings;

public class ImdbImageProvider : IRemoteImageProvider
{
    private static readonly HttpClient _httpClient = new HttpClient();

    public string Name => "IMDb (OMDb)";

    public bool Supports(BaseItem item) => item is Movie;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        yield return ImageType.Primary;
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var imdbId = item.GetProviderId(MetadataProvider.Imdb);
        if (string.IsNullOrEmpty(imdbId))
            return Enumerable.Empty<RemoteImageInfo>();

        var apiKey = Plugin.Instance?.Configuration.OmdbApiKey ?? string.Empty;

        try
        {
            var url = $"https://www.omdbapi.com/?i={imdbId}&apikey={apiKey}";
            var json = await _httpClient.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.GetProperty("Response").GetString() != "True")
                return Enumerable.Empty<RemoteImageInfo>();

            if (!root.TryGetProperty("Poster", out var posterEl))
                return Enumerable.Empty<RemoteImageInfo>();

            var posterUrl = posterEl.GetString();
            if (string.IsNullOrEmpty(posterUrl) || posterUrl == "N/A")
                return Enumerable.Empty<RemoteImageInfo>();

            return new[]
            {
                new RemoteImageInfo
                {
                    ProviderName = Name,
                    Url = posterUrl,
                    Type = ImageType.Primary
                }
            };
        }
        catch
        {
            return Enumerable.Empty<RemoteImageInfo>();
        }
    }

    public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        return await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
    }
}
