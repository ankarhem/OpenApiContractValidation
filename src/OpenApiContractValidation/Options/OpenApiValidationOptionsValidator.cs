using Microsoft.Extensions.Options;

namespace OpenApiContractValidation.Options;

/// <summary>
/// Fail-fast validation for <see cref="OpenApiValidationOptions"/>: invalid values surface as an
/// <see cref="OptionsValidationException"/> at host start (via <c>ValidateOnStart</c>) instead of
/// as per-request failures deep in the pipeline.
/// </summary>
internal sealed class OpenApiValidationOptionsValidator : IValidateOptions<OpenApiValidationOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, OpenApiValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.MaxResponseBufferSizeBytes <= 0)
        {
            failures.Add(
                $"{nameof(OpenApiValidationOptions.MaxResponseBufferSizeBytes)} must be greater than zero, "
                    + $"but was {options.MaxResponseBufferSizeBytes}."
            );
        }

        if (options.MaxRequestBufferSizeBytes <= 0)
        {
            failures.Add(
                $"{nameof(OpenApiValidationOptions.MaxRequestBufferSizeBytes)} must be greater than zero, "
                    + $"but was {options.MaxRequestBufferSizeBytes}."
            );
        }

        if (
            options.ContractFormat is not null
            && !string.Equals(options.ContractFormat, "json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(options.ContractFormat, "yaml", StringComparison.OrdinalIgnoreCase)
        )
        {
            failures.Add(
                $"{nameof(OpenApiValidationOptions.ContractFormat)} must be \"json\" or \"yaml\" "
                    + $"(case-insensitive) or null, but was \"{options.ContractFormat}\"."
            );
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
