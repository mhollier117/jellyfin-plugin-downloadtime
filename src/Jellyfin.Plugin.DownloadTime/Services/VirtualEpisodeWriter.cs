using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DownloadTime.Model;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DownloadTime.Services;

/// <summary>
/// Applies placeholder plans to the library. Every item we create carries
/// ProviderIds[MarkerProviderKey]; we never touch virtual items without it.
/// The server's own SeriesMetadataService.RemoveObsoleteEpisodes removes our
/// placeholders automatically when the physical episode arrives (12.0 verified).
/// </summary>
public class VirtualEpisodeWriter
{
    public const string MarkerProviderKey = "DownloadTime";

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<VirtualEpisodeWriter> _logger;

    public VirtualEpisodeWriter(ILibraryManager libraryManager, ILogger<VirtualEpisodeWriter> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public IReadOnlyList<ExistingPlaceholder> GetExisting(Guid seriesId)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series series)
        {
            return Array.Empty<ExistingPlaceholder>();
        }
        return series.GetRecursiveChildren().OfType<Episode>()
            .Where(e => e.IsVirtualItem && e.ProviderIds.TryGetValue(MarkerProviderKey, out var m) && !string.IsNullOrEmpty(m))
            .Select(e => new ExistingPlaceholder(e.Id, e.ParentIndexNumber, e.IndexNumber, e.ProviderIds[MarkerProviderKey]))
            .ToList();
    }

    public int Apply(Guid seriesId, PlaceholderPlan plan)
    {
        if (_libraryManager.GetItemById(seriesId) is not Series series)
        {
            return 0;
        }
        var ops = 0;

        foreach (var id in plan.Deletes)
        {
            if (_libraryManager.GetItemById(id) is Episode ep
                && ep.IsVirtualItem
                && ep.ProviderIds.ContainsKey(MarkerProviderKey))
            {
                _libraryManager.DeleteItem(ep, new DeleteOptions { DeleteFileLocation = false }, false);
                ops++;
            }
        }

        foreach (var create in plan.Creates)
        {
            var season = series.Children.OfType<Season>()
                .FirstOrDefault(s => s.IndexNumber == create.Season);
            if (season is null)
            {
                _logger.LogInformation("Download Time: no local season {Season} for {Series}; skipping placeholder S{Season}E{Number}",
                    create.Season, series.Name, create.Season, create.Number);
                continue; // v1: placeholders only inside existing seasons (virtual season creation is out of scope)
            }
            var name = create.Title ?? $"Episode {create.Number}";
            var episode = new Episode
            {
                Name = name,
                IndexNumber = create.Number,
                ParentIndexNumber = create.Season,
                Id = _libraryManager.GetNewItemId(
                    series.Id + create.Season.ToString(System.Globalization.CultureInfo.InvariantCulture) + create.Marker,
                    typeof(Episode)),
                IsVirtualItem = true,
                SeasonId = season.Id,
                SeriesId = series.Id,
                SeriesName = series.Name,
            };
            if (create.AiredAt.HasValue)
            {
                episode.PremiereDate = create.AiredAt.Value.UtcDateTime;
            }
            episode.ProviderIds[MarkerProviderKey] = create.Marker;
            season.AddChild(episode);
            ops++;
        }
        return ops;
    }

    public int DeleteAllPlaceholders()
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsVirtualItem = true,
            Recursive = true,
        });
        var count = 0;
        foreach (var item in items)
        {
            if (item.ProviderIds.TryGetValue(MarkerProviderKey, out var m) && !string.IsNullOrEmpty(m))
            {
                _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, false);
                count++;
            }
        }
        return count;
    }
}
