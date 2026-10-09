using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Coclico.Services;

public static class ServiceContainer
{
    private static volatile bool _isBuilt = false;

    private static Lazy<IServiceProvider> _lazy =
        new(() => throw new InvalidOperationException(
            "Service container not built. Call Build() at startup."),
            LazyThreadSafetyMode.ExecutionAndPublication);

    public static IServiceProvider Provider => _lazy.Value;

    public static void Build(Action<IServiceCollection> configure)
    {
        _lazy = new Lazy<IServiceProvider>(() =>
        {
            var services = new ServiceCollection();

            // Single logging configuration, shared with LoggingService (Serilog).
            LoggingService.EnsureSerilog();

            _ = services.AddLogging(builder =>
            {
                _ = builder.ClearProviders();
                _ = builder.AddSerilog(Log.Logger, dispose: true);
            });

            _ = services.AddMemoryCache();
            _ = services.AddSingleton<IAuditLog, AuditLogService>();
            _ = services.AddSingleton<ISecurityPolicy, SecurityPolicyService>();
            _ = services.AddSingleton<IProcessExecutionService, ProcessExecutionService>();
            _ = services.AddHttpClient();

            configure?.Invoke(services);

            return services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = false
            });
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _isBuilt = true;
    }

    public static T GetRequired<T>() where T : notnull
    {
        return Provider.GetRequiredService<T>();
    }

    public static T? GetOptional<T>() where T : class
    {
        return _isBuilt ? Provider.GetService<T>() : null;
    }

    public static IServiceScope CreateScope()
    {
        return Provider.CreateScope();
    }

    public static void Shutdown()
    {
        try
        {
            if (_lazy.IsValueCreated && _lazy.Value is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch (Exception exSwallow) { LoggingService.LogException(exSwallow, "SwallowedException"); }
        finally
        {
            Log.CloseAndFlush();
            _lazy = new Lazy<IServiceProvider>(
                () => throw new InvalidOperationException("Service container has been shut down."),
                LazyThreadSafetyMode.ExecutionAndPublication);
            _isBuilt = false;
        }
    }
}
