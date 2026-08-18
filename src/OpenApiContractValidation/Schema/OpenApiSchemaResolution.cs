using Microsoft.OpenApi;

namespace OpenApiContractValidation.Schema;

/// <summary>
/// Shared schema- and header-resolution helpers used by the request and response
/// validators (single source of truth for reference unwrapping and case-insensitive
/// header lookup).
/// </summary>
internal static class OpenApiSchemaResolution
{
    /// <summary>
    /// Unwraps an <see cref="OpenApiSchemaReference"/> to its fully-resolved target.
    /// Bounded to guarantee termination on pathological reference chains.
    /// </summary>
    internal static IOpenApiSchema? Resolve(IOpenApiSchema? schema)
    {
        var guard = 0;
        while (schema is OpenApiSchemaReference reference)
        {
            var target = reference.RecursiveTarget;
            if (target is null || ReferenceEquals(target, schema) || ++guard > 64)
            {
                break;
            }

            schema = target;
        }

        return schema;
    }

    /// <summary>
    /// Case-insensitively locates <paramref name="name"/> among the actual headers.
    /// HTTP header names are case-insensitive per RFC 9110, so the lookup must be too.
    /// </summary>
    internal static IReadOnlyList<string>? FindHeader(
        IReadOnlyDictionary<string, IReadOnlyList<string>> actual,
        string name
    )
    {
        foreach (var (key, values) in actual)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return values;
            }
        }

        return null;
    }
}
