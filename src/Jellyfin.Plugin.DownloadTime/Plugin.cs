using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.DownloadTime;

/// <summary>
/// Detects missing episodes (gaps and newly aired) and missing franchise
/// movies by comparing the library against each item's identifying source.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Download Time";

    public override string Description =>
        "Detects missing episodes and franchise movies: gaps you missed and new releases not yet downloaded.";

    public override Guid Id => Guid.Parse("4d557ba6-d562-4209-9a04-b782775dc2ff");

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.configPage.html"
        };
    }
}
