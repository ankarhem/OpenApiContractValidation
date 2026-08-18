using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using OpenApiContractValidation.Options;

namespace OpenApiContractValidation.Middleware;

/// <summary>
/// Extension methods on <see cref="IServiceCollection"/> that register the OpenAPI contract validation
/// services.
/// </summary>
public static class OpenApiValidationServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="OpenApiValidationOptions"/> (configured by <paramref name="configure"/>) and
    /// the singleton <see cref="OpenApiContractValidator"/> that loads the contract once at startup.
    /// Invalid options (for example a non-positive <see cref="OpenApiValidationOptions.MaxResponseBufferSizeBytes"/>)
    /// fail fast: the host throws an <see cref="OptionsValidationException"/> at start.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <param name="configure">A delegate that configures the <see cref="OpenApiValidationOptions"/>.</param>
    /// <returns>The <paramref name="services"/> collection, for chaining.</returns>
    public static IServiceCollection AddOpenApiValidation(
        this IServiceCollection services,
        Action<OpenApiValidationOptions> configure
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        AddValidation(services);
        return services;
    }

    /// <summary>
    /// Registers the singleton <see cref="OpenApiContractValidator"/> for cases where
    /// <see cref="OpenApiValidationOptions"/> is configured elsewhere (for example via
    /// <c>IConfiguration</c> binding). Use <see cref="AddOpenApiValidation(IServiceCollection, Action{OpenApiValidationOptions})"/>
    /// when you want to configure options inline. Invalid options fail fast: the host throws an
    /// <see cref="OptionsValidationException"/> at start.
    /// </summary>
    /// <param name="services">The service collection to add to.</param>
    /// <returns>The <paramref name="services"/> collection, for chaining.</returns>
    public static IServiceCollection AddOpenApiValidation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        AddValidation(services);
        return services;
    }

    private static void AddValidation(IServiceCollection services)
    {
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IValidateOptions<OpenApiValidationOptions>,
                OpenApiValidationOptionsValidator
            >()
        );
        services.AddOptions<OpenApiValidationOptions>().ValidateOnStart();
        services.AddSingleton<OpenApiContractValidator>();
    }
}
