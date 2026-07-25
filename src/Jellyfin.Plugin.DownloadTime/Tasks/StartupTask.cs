using System.Reflection;
using System.Runtime.Loader;
using Jellyfin.Plugin.DownloadTime.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.DownloadTime.Tasks;

/// <summary>Registers the badge injection with the FileTransformation plugin at startup.</summary>
public class StartupTask : IScheduledTask
{
    private readonly ILogger<StartupTask> _logger;

    public StartupTask(ILogger<StartupTask> logger) => _logger = logger;

    public string Name => "Register badge injection";
    public string Key => "DownloadTimeStartup";
    public string Description => "Registers Download Time's web badge injection with the FileTransformation plugin.";
    public string Category => "Download Time";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
#if JELLYFIN_10_10
        yield return new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerStartup };
#else
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
#endif
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var payload = new JObject
        {
            ["id"] = "7be0d6d4-6a4e-4a02-a5f0-c6c66b825b39",
            ["fileNamePattern"] = "index.html",
            ["callbackAssembly"] = GetType().Assembly.FullName,
            ["callbackClass"] = typeof(TransformationPatch).FullName,
            ["callbackMethod"] = nameof(TransformationPatch.InjectIntoIndexHtml),
        };
        var ftAssembly = AssemblyLoadContext.All.SelectMany(x => x.Assemblies)
            .FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.OrdinalIgnoreCase) ?? false);
        if (ftAssembly is null)
        {
            _logger.LogWarning("Download Time: FileTransformation plugin not found; badges disabled.");
            return Task.CompletedTask;
        }
        var iface = ftAssembly.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        iface?.GetMethod("RegisterTransformation")?.Invoke(null, new object?[] { payload });
        _logger.LogInformation("Download Time: badge injection registered.");
        return Task.CompletedTask;
    }
}
