using Jellyfin.Plugin.DownloadTime.Model;
using Jellyfin.Plugin.DownloadTime.Services;
using Jellyfin.Plugin.DownloadTime.Services.Lanes;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.DownloadTime;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new CatalogCache(Path.Combine(paths.CachePath, "downloadtime"), sp.GetRequiredService<IClock>());
        });
        services.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            return new ReportStore(Path.Combine(paths.DataPath, "downloadtime"));
        });
        static PluginConfiguration Config() => Plugin.Instance?.Configuration ?? new PluginConfiguration();
        services.AddSingleton<ITvdbSource>(sp => new TvdbScrapeFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DownloadTime"),
            () => Config().RequestDelayMs));
        services.AddSingleton<ITvmazeSource>(sp => new TvmazeFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DownloadTime")));
        services.AddSingleton<IAniDbSource>(sp => new AniDbFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DownloadTime"),
            sp.GetRequiredService<IClock>(),
            ts => Task.Delay(ts),
            () => Config().RequestDelayMs,
            () => (Config().AniDbClientName, Config().AniDbClientVersion)));
        services.AddSingleton<ITmdbSource>(sp => new TmdbFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("DownloadTime"),
            () => Config().TmdbApiKey,
            ts => Task.Delay(ts)));
        services.AddSingleton<ILibraryReader, JellyfinLibraryReader>();
        services.AddSingleton<VirtualEpisodeWriter>();
        services.AddSingleton<ScanService>();
        services.AddSingleton(sp => new ScanRunner(
            sp.GetRequiredService<ScanService>(),
            sp.GetRequiredService<ReportStore>(),
            Config));
    }
}
