using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenApiContractValidation.Middleware;
using OpenApiContractValidation.Options;
using Xunit;

namespace OpenApiContractValidation.Tests.Middleware;

/// <summary>
/// Fail-fast tests proving that a host built with invalid <see cref="OpenApiValidationOptions"/>
/// throws <see cref="OptionsValidationException"/> at start (not per-request), for both
/// <see cref="OpenApiValidationServiceCollectionExtensions"/> overloads. Options validation runs
/// eagerly via <c>ValidateOnStart</c>, so bad configuration aborts host startup.
/// </summary>
public class AddOpenApiValidationFailFastTests
{
    private const string MinimalContract =
        """{"openapi":"3.0.0","info":{"title":"t","version":"1"},"paths":{"/a":{"get":{"responses":{"200":{"description":"ok"}}}}}}""";

    [Fact]
    public async Task AddOpenApiValidation_MaxResponseBufferSizeBytesZero_HostStartFails()
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartHostAsync(services =>
                services.AddOpenApiValidation(o =>
                {
                    o.ContractText = MinimalContract;
                    o.MaxResponseBufferSizeBytes = 0;
                })
            )
        );

        Assert.Contains("MaxResponseBufferSizeBytes", exception.Message);
    }

    [Fact]
    public async Task AddOpenApiValidation_MaxRequestBufferSizeBytesZero_HostStartFails()
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartHostAsync(services =>
                services.AddOpenApiValidation(o =>
                {
                    o.ContractText = MinimalContract;
                    o.MaxRequestBufferSizeBytes = 0;
                })
            )
        );

        Assert.Contains("MaxRequestBufferSizeBytes", exception.Message);
    }

    [Fact]
    public async Task AddOpenApiValidation_InvalidContractFormat_HostStartFails()
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartHostAsync(services =>
                services.AddOpenApiValidation(o =>
                {
                    o.ContractText = MinimalContract;
                    o.ContractFormat = "xml";
                })
            )
        );

        Assert.Contains("ContractFormat", exception.Message);
    }

    [Fact]
    public async Task AddOpenApiValidation_ValidOptions_HostStarts()
    {
        using var host = BuildHost(services =>
            services.AddOpenApiValidation(o =>
            {
                o.ContractText = MinimalContract;
                o.ContractFormat = "json";
            })
        );
        await host.StartAsync();

        // Force the validator singleton to construct too, proving the whole startup path is healthy.
        _ = host.Services.GetRequiredService<OpenApiContractValidator>();
    }

    [Fact]
    public async Task AddOpenApiValidation_NoDelegateOverload_AlsoValidates()
    {
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() =>
            StartHostAsync(services =>
            {
                services.AddOpenApiValidation(); // parameterless overload: options configured elsewhere
                services.Configure<OpenApiValidationOptions>(o =>
                {
                    o.ContractText = MinimalContract;
                    o.MaxResponseBufferSizeBytes = -1;
                });
            })
        );

        Assert.Contains("MaxResponseBufferSizeBytes", exception.Message);
    }

    private static async Task StartHostAsync(Action<IServiceCollection> configureServices)
    {
        using var host = BuildHost(configureServices);
        await host.StartAsync();
    }

    private static IHost BuildHost(Action<IServiceCollection> configureServices)
    {
        return new HostBuilder()
            .ConfigureWebHost(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(configureServices);
                webHost.Configure(app =>
                {
                    app.UseOpenApiValidation();
                    app.Run(_ => Task.CompletedTask);
                });
            })
            .Build();
    }
}
