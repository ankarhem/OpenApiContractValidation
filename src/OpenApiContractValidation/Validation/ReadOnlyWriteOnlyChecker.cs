using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using OpenApiContractValidation.Errors;
using OpenApiContractValidation.Schema;

namespace OpenApiContractValidation.Validation;

/// <summary>
/// Enforces OpenAPI <c>readOnly</c> / <c>writeOnly</c> property semantics, which
/// JSON Schema validators deliberately do not check. The checker walks a parsed
/// JSON instance against an OpenAPI schema (instance-driven, so it naturally
/// terminates even for recursive schemas) and reports any property whose presence
/// violates the contract for the given direction.
/// </summary>
public static class ReadOnlyWriteOnlyChecker
{
    /// <summary>
    /// Validates a request-body instance: any property whose schema is marked
    /// <c>readOnly: true</c> MUST NOT appear in a request.
    /// </summary>
    /// <param name="schema">The OpenAPI schema describing the request body.</param>
    /// <param name="instance">The parsed JSON instance of the request body, or <see langword="null"/>.</param>
    /// <param name="locationLabel">Human-readable label of the location (e.g. "requestBody").</param>
    /// <returns>The list of <c>readOnly</c> violations found; never <see langword="null"/>.</returns>
    public static IReadOnlyList<ContractViolation> CheckRequest(
        IOpenApiSchema schema,
        JsonNode? instance,
        string locationLabel
    ) => Check(schema, instance, locationLabel, Direction.Request);

    /// <summary>
    /// Validates a response-body instance: any property whose schema is marked
    /// <c>writeOnly: true</c> MUST NOT appear in a response.
    /// </summary>
    /// <param name="schema">The OpenAPI schema describing the response body.</param>
    /// <param name="instance">The parsed JSON instance of the response body, or <see langword="null"/>.</param>
    /// <param name="locationLabel">Human-readable label of the location (e.g. "responseBody").</param>
    /// <returns>The list of <c>writeOnly</c> violations found; never <see langword="null"/>.</returns>
    public static IReadOnlyList<ContractViolation> CheckResponse(
        IOpenApiSchema schema,
        JsonNode? instance,
        string locationLabel
    ) => Check(schema, instance, locationLabel, Direction.Response);

    private enum Direction
    {
        Request,
        Response,
    }

    private static IReadOnlyList<ContractViolation> Check(
        IOpenApiSchema? schema,
        JsonNode? instance,
        string locationLabel,
        Direction direction
    )
    {
        var violations = new List<(string InstanceLocation, string Keyword, string Message)>();
        Walk(schema, instance, "", locationLabel, direction, violations);

        var seen = new HashSet<(string, string)>();
        var results = new List<ContractViolation>();
        foreach (var (path, keyword, message) in violations)
        {
            if (!seen.Add((path, keyword)))
            {
                continue;
            }
            results.Add(
                new ContractViolation(
                    Location: locationLabel,
                    InstanceLocation: path,
                    Keyword: keyword,
                    Expected: null,
                    Actual: null,
                    Message: message
                )
            );
        }
        return results;
    }

    private static void Walk(
        IOpenApiSchema? schema,
        JsonNode? node,
        string instancePath,
        string locationLabel,
        Direction direction,
        List<(string, string, string)> violations
    )
    {
        var resolved = OpenApiSchemaResolution.Resolve(schema);
        if (resolved is null)
        {
            return;
        }

        if (resolved.AllOf is { Count: > 0 })
        {
            foreach (var sub in resolved.AllOf)
            {
                Walk(sub, node, instancePath, locationLabel, direction, violations);
            }
        }
        if (resolved.OneOf is { Count: > 0 })
        {
            foreach (var sub in resolved.OneOf)
            {
                Walk(sub, node, instancePath, locationLabel, direction, violations);
            }
        }
        if (resolved.AnyOf is { Count: > 0 })
        {
            foreach (var sub in resolved.AnyOf)
            {
                Walk(sub, node, instancePath, locationLabel, direction, violations);
            }
        }

        if (node is JsonObject obj && resolved.Properties is { Count: > 0 })
        {
            foreach (var (name, propertySchema) in resolved.Properties)
            {
                if (!obj.TryGetPropertyValue(name, out var child))
                {
                    continue;
                }

                var childPath = instancePath + "/" + EscapeToken(name);
                var flagged =
                    direction == Direction.Request
                        ? IsReadOnly(propertySchema)
                        : IsWriteOnly(propertySchema);

                if (flagged)
                {
                    var (keyword, where) =
                        direction == Direction.Request
                            ? ("readOnly", "the request")
                            : ("writeOnly", "the response");
                    violations.Add(
                        (
                            childPath,
                            keyword,
                            $"property '{name}' is {keyword} and must not be present in {where}"
                        )
                    );
                }

                if (child is not null)
                {
                    Walk(propertySchema, child, childPath, locationLabel, direction, violations);
                }
            }
        }

        if (node is JsonArray array && resolved.Items is not null)
        {
            for (var i = 0; i < array.Count; i++)
            {
                var element = array[i];
                if (element is null)
                {
                    continue;
                }
                Walk(
                    resolved.Items,
                    element,
                    instancePath + "/" + i,
                    locationLabel,
                    direction,
                    violations
                );
            }
        }
    }

    /// <summary>
    /// A property is considered <c>readOnly</c> for enforcement if the property's
    /// own schema (after resolving references) or any applicable compositional
    /// subschema (<c>allOf</c>/<c>oneOf</c>/<c>anyOf</c>) marks it so.
    /// </summary>
    private static bool IsReadOnly(IOpenApiSchema? schema) =>
        HasFlag(schema, static s => s.ReadOnly, new HashSet<IOpenApiSchema>());

    /// <summary>A property is considered <c>writeOnly</c> under the same rules as <see cref="IsReadOnly"/>.</summary>
    private static bool IsWriteOnly(IOpenApiSchema? schema) =>
        HasFlag(schema, static s => s.WriteOnly, new HashSet<IOpenApiSchema>());

    private static bool HasFlag(
        IOpenApiSchema? schema,
        Func<IOpenApiSchema, bool> read,
        HashSet<IOpenApiSchema> visited
    )
    {
        var resolved = OpenApiSchemaResolution.Resolve(schema);
        if (resolved is null || !visited.Add(resolved))
        {
            return false;
        }

        if (read(resolved))
        {
            return true;
        }

        if (resolved.AllOf is { Count: > 0 })
        {
            foreach (var sub in resolved.AllOf)
            {
                if (HasFlag(sub, read, visited))
                {
                    return true;
                }
            }
        }
        if (resolved.OneOf is { Count: > 0 })
        {
            foreach (var sub in resolved.OneOf)
            {
                if (HasFlag(sub, read, visited))
                {
                    return true;
                }
            }
        }
        if (resolved.AnyOf is { Count: > 0 })
        {
            foreach (var sub in resolved.AnyOf)
            {
                if (HasFlag(sub, read, visited))
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Escapes a JSON Pointer reference token (RFC 6901: '~' then '/').</summary>
    private static string EscapeToken(string token) => token.Replace("~", "~0").Replace("/", "~1");
}
