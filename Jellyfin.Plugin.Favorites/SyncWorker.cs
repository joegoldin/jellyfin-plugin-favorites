using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Favorites;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IFavoritesStore, JellyfinStore>();
        serviceCollection.AddSingleton<SyncEngine>();
        serviceCollection.AddHostedService<SyncWorker>();
    }
}

public sealed class SyncWorker(SyncEngine engine, IUserManager users, ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give Jellyfin time to finish initializing the library and users.
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try
            {
                foreach (var user in users.GetUsers())
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try
                    {
                        await engine.Sync(user.Id, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Favorites sync failed for user {UserId}; will retry", user.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Favorites sync could not enumerate users; will retry");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
