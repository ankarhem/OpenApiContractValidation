using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using OpenApiContractValidation.Errors;
using OpenApiContractValidation.Models;

namespace OpenApiContractValidation.Parameters;

/// <summary>
/// Deserializes the already-URL-decoded raw value(s) of an OpenAPI parameter into a
/// <see cref="JsonNode"/> (<see cref="JsonValue"/>, <see cref="JsonArray"/> or
/// <see cref="JsonObject"/>) whose shape matches the parameter's schema, following the
/// OpenAPI 3.x parameter serialization rules (which are derived from RFC 6570).
/// </summary>
/// <remarks>
/// <para>
/// The resulting node is intended to be handed to a JSON Schema validator downstream.
/// To avoid masking type violations, <b>scalar tokens are emitted as STRING
/// <see cref="JsonValue"/>s</b> -- for example the raw token <c>"3"</c> becomes
/// <c>JsonValue.Create("3")</c> rather than the number <c>3</c>. The single exception is
/// content-based parsing (<see cref="DeserializeContent"/>), where the raw value is a
/// real serialized document of the declared media type and its native JSON types are
/// preserved.
/// </para>
/// <para>
/// <see cref="Deserialize(IOpenApiParameter, IReadOnlyList{String})"/> covers all seven
/// OpenAPI <see cref="ParameterStyle"/> values. A style that cannot be determined (for
/// example a parameter whose <see cref="IOpenApiParameter.Style"/> and
/// <see cref="IOpenApiParameter.In"/> are both <see langword="null"/>) is treated as a
/// contract defect and reported via <see cref="OpenApiContractValidationException"/> at
/// <see cref="ContractPhase.Startup"/> rather than being silently skipped.
/// </para>
/// <para>
/// All members are stateless; the type is safe to call concurrently.
/// </para>
/// </remarks>
public static class ParameterDeserializer
{
    /// <summary>
    /// Deserializes the raw, already-URL-decoded value(s) for an OpenAPI parameter into a
    /// <see cref="JsonNode"/> whose shape matches the parameter schema.
    /// </summary>
    /// <param name="parameter">The OpenAPI parameter describing the expected style,
    /// explode and schema. Must not be <see langword="null"/>.</param>
    /// <param name="rawValues">The decoded value(s) for this parameter exactly as they
    /// appeared in the request. For exploded query/form parameters a single parameter
    /// name may occur multiple times, yielding several entries; for non-exploded
    /// delimited styles this is typically a one-element list containing the delimited
    /// string. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// A <see cref="JsonValue"/> (primitive schema), <see cref="JsonArray"/> (array
    /// schema) or <see cref="JsonObject"/> (object schema) holding the deserialized
    /// values, or <see langword="null"/> when <paramref name="rawValues"/> is empty.
    /// </returns>
    /// <exception cref="OpenApiContractValidationException">Thrown at
    /// <see cref="ContractPhase.Startup"/> when the parameter uses a style this
    /// deserializer cannot resolve.</exception>
    public static JsonNode? Deserialize(
        IOpenApiParameter parameter,
        IReadOnlyList<string> rawValues
    )
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(rawValues);

        // A parameter with a content map is serialized in a media type, not with a style.
        // Delegate to the content path, parsing the (single) raw value as that media type.
        if (parameter.Content is { Count: > 0 })
        {
            return DeserializeContent(parameter, FirstOrEmpty(rawValues));
        }

        if (rawValues.Count == 0)
        {
            return null;
        }

        var style = ResolveStyle(parameter);
        var isObject = IsObject(parameter.Schema);
        var isArray = IsArray(parameter.Schema);

        return style switch
        {
            ParameterStyle.Simple => DeserializeSimple(parameter, rawValues, isObject, isArray),
            ParameterStyle.Form => DeserializeForm(parameter, rawValues, isObject, isArray),
            ParameterStyle.SpaceDelimited => DeserializeDelimited(
                parameter,
                rawValues,
                ' ',
                isObject,
                isArray
            ),
            ParameterStyle.PipeDelimited => DeserializeDelimited(
                parameter,
                rawValues,
                '|',
                isObject,
                isArray
            ),
            ParameterStyle.Matrix => DeserializeMatrix(parameter, rawValues, isObject, isArray),
            ParameterStyle.Label => DeserializeLabel(parameter, rawValues, isObject, isArray),
            ParameterStyle.DeepObject => DeserializeDeepFromValues(parameter, rawValues, isObject),
            _ => throw UnsupportedStyle(parameter, style),
        };
    }

    /// <summary>
    /// Deserializes the bracketed key/value pairs of a <c>deepObject</c>-style query
    /// parameter into a <see cref="JsonObject"/>.
    /// </summary>
    /// <param name="parameter">The OpenAPI parameter with
    /// <see cref="IOpenApiParameter.Style"/> equal to
    /// <see cref="ParameterStyle.DeepObject"/>. Used only to validate intent; the keys
    /// come from <paramref name="bracketedPairs"/>. Must not be <see langword="null"/>.</param>
    /// <param name="bracketedPairs">A mapping from the bracket key (for example
    /// <c>"R"</c> extracted from the query key <c>color[R]</c>) to its decoded string
    /// value. Must not be <see langword="null"/>.</param>
    /// <returns>
    /// A <see cref="JsonObject"/> whose property values are STRING
    /// <see cref="JsonValue"/>s, or <see langword="null"/> when
    /// <paramref name="bracketedPairs"/> is empty.
    /// </returns>
    public static JsonNode? DeserializeDeepObject(
        IOpenApiParameter parameter,
        IReadOnlyDictionary<string, string> bracketedPairs
    )
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(bracketedPairs);

        if (bracketedPairs.Count == 0)
        {
            return null;
        }

        var obj = new JsonObject();
        foreach (var pair in bracketedPairs)
        {
            // Skip malformed entries without an actionable key.
            if (pair.Key is null)
            {
                continue;
            }

            obj[pair.Key] = ToStringValue(pair.Value);
        }

        return obj;
    }

    /// <summary>
    /// Deserializes the raw value of a content-based parameter (one that declares a
    /// <see cref="IOpenApiParameter.Content"/> map instead of a schema) into a
    /// <see cref="JsonNode"/>.
    /// </summary>
    /// <param name="parameter">The OpenAPI parameter whose <see cref="IOpenApiParameter.Content"/>
    /// map identifies the media type. Must not be <see langword="null"/>.</param>
    /// <param name="rawValue">The decoded parameter value as a serialized document of
    /// the declared media type.</param>
    /// <returns>
    /// For <c>application/json</c> (and <c>*+json</c>) media types, the value parsed as
    /// JSON with its native types preserved. For any other media type the raw value is
    /// wrapped as a STRING <see cref="JsonValue"/> since no general parser is available;
    /// callers requiring richer handling should plug in their own media-type reader.
    /// </returns>
    public static JsonNode? DeserializeContent(IOpenApiParameter parameter, string rawValue)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        if (HasJsonMediaType(parameter))
        {
            // Preserve native JSON types; content params are not subject to the
            // "string tokens only" rule applied to style-based deserialization.
            return JsonNode.Parse(rawValue);
        }

        return ToStringValue(rawValue);
    }

    private static JsonNode? DeserializeSimple(
        IOpenApiParameter parameter,
        IReadOnlyList<string> rawValues,
        bool isObject,
        bool isArray
    )
    {
        var value = FirstOrEmpty(rawValues);

        if (isArray)
        {
            // style=simple always uses a comma delimiter for arrays, regardless of explode.
            return ToStringArray(value.Split(','));
        }

        if (isObject)
        {
            return parameter.Explode
                ? ToObjectFromKeyedSegments(value.Split(','))
                : ToObjectFromPairs(value.Split(','));
        }

        return ToStringValue(value);
    }

    private static JsonNode? DeserializeForm(
        IOpenApiParameter parameter,
        IReadOnlyList<string> rawValues,
        bool isObject,
        bool isArray
    )
    {
        if (isArray)
        {
            return parameter.Explode
                ? ToStringArray(rawValues)
                : ToStringArray(FirstOrEmpty(rawValues).Split(','));
        }

        if (isObject)
        {
            return parameter.Explode
                ? ToObjectFromKeyedSegments(rawValues)
                : ToObjectFromPairs(FirstOrEmpty(rawValues).Split(','));
        }

        return ToStringValue(FirstOrEmpty(rawValues));
    }

    private static JsonNode? DeserializeDelimited(
        IOpenApiParameter parameter,
        IReadOnlyList<string> rawValues,
        char delimiter,
        bool isObject,
        bool isArray
    )
    {
        if (isArray)
        {
            return parameter.Explode
                ? ToStringArray(rawValues)
                : ToStringArray(FirstOrEmpty(rawValues).Split(delimiter));
        }

        if (isObject)
        {
            return parameter.Explode
                ? ToObjectFromKeyedSegments(rawValues)
                : ToObjectFromPairs(FirstOrEmpty(rawValues).Split(delimiter));
        }

        return ToStringValue(FirstOrEmpty(rawValues));
    }

    private static JsonNode? DeserializeMatrix(
        IOpenApiParameter parameter,
        IReadOnlyList<string> rawValues,
        bool isObject,
        bool isArray
    )
    {
        // Matrix serialization prefixes the value with ";name=". Repeated occurrences
        // (";id=3;id=4;id=5") indicate exploded arrays; a single segment may itself be
        // comma-delimited (";id=3,4,5") for non-exploded arrays.
        var segments = MatrixValueSegments(FirstOrEmpty(rawValues), parameter.Name);

        if (isArray)
        {
            return segments.Count > 1
                ? ToStringArray(segments)
                : ToStringArray(segments[0].Split(','));
        }

        if (isObject)
        {
            return parameter.Explode && segments.Count > 1
                ? ToObjectFromKeyedSegments(segments)
                : ToObjectFromPairs(segments[0].Split(','));
        }

        return ToStringValue(segments[0]);
    }

    private static JsonNode? DeserializeLabel(
        IOpenApiParameter parameter,
        IReadOnlyList<string> rawValues,
        bool isObject,
        bool isArray
    )
    {
        // Label serialization prefixes the value with "."; explode then separates
        // elements with further dots, while non-explode keeps commas.
        var value = FirstOrEmpty(rawValues);
        if (value.Length > 0 && value[0] == '.')
        {
            value = value[1..];
        }

        if (isArray)
        {
            var delimiter = parameter.Explode ? '.' : ',';
            return ToStringArray(value.Split(delimiter));
        }

        if (isObject)
        {
            return parameter.Explode
                ? ToObjectFromKeyedSegments(value.Split('.'))
                : ToObjectFromPairs(value.Split(','));
        }

        return ToStringValue(value);
    }

    private static JsonNode? DeserializeDeepFromValues(
        IOpenApiParameter parameter,
        IReadOnlyList<string> rawValues,
        bool isObject
    )
    {
        // Graceful handling of deepObject values that arrive as "R=100" style pairs via
        // the standard entrypoint. The primary path is DeserializeDeepObject, which
        // receives already-extracted bracketed keys.
        _ = parameter;

        if (!isObject)
        {
            return ToStringValue(FirstOrEmpty(rawValues));
        }

        return ToObjectFromKeyedSegments(rawValues);
    }

    /// <summary>
    /// Resolves the effective style of a parameter, applying the OpenAPI defaults by
    /// location (query/cookie -&gt; form; path/header -&gt; simple) when
    /// <see cref="IOpenApiParameter.Style"/> is not set.
    /// </summary>
    private static ParameterStyle ResolveStyle(IOpenApiParameter parameter)
    {
        if (parameter.Style.HasValue)
        {
            return parameter.Style.Value;
        }

        return parameter.In switch
        {
            ParameterLocation.Query or ParameterLocation.Cookie => ParameterStyle.Form,
            ParameterLocation.Path or ParameterLocation.Header => ParameterStyle.Simple,
            _ => throw UnsupportedStyle(parameter, style: null),
        };
    }

    private static bool IsArray(IOpenApiSchema? schema) =>
        schema is not null
        && schema.Type.HasValue
        && schema.Type.Value.HasFlag(JsonSchemaType.Array);

    private static bool IsObject(IOpenApiSchema? schema) =>
        schema is not null
        && schema.Type.HasValue
        && schema.Type.Value.HasFlag(JsonSchemaType.Object);

    private static bool HasJsonMediaType(IOpenApiParameter parameter)
    {
        var content = parameter.Content;
        if (content is null || content.Count == 0)
        {
            return false;
        }

        foreach (var key in content.Keys)
        {
            if (key is null)
            {
                continue;
            }

            if (
                key.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a matrix raw value into its value segments. For ";id=3,4,5" the result is
    /// <c>["3,4,5"]</c>; for ";id=3;id=4;id=5" the result is <c>["3","4","5"]</c>.
    /// </summary>
    private static List<string> MatrixValueSegments(string raw, string? name)
    {
        var segments = new List<string>();
        var prefix = string.IsNullOrEmpty(name) ? null : name + "=";

        foreach (var segment in raw.Split(';'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            var current = segment;
            if (prefix is not null && current.StartsWith(prefix, StringComparison.Ordinal))
            {
                current = current[prefix.Length..];
            }
            else
            {
                var equalsIndex = current.IndexOf('=');
                if (equalsIndex >= 0)
                {
                    current = current[(equalsIndex + 1)..];
                }
            }

            segments.Add(current);
        }

        return segments.Count > 0 ? segments : new List<string> { string.Empty };
    }

    private static JsonValue ToStringValue(string value) => JsonValue.Create(value)!;

    private static JsonArray ToStringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(ToStringValue(value));
        }

        return array;
    }

    /// <summary>
    /// Builds an object from "key=value" segments (explode=true object form).
    /// </summary>
    private static JsonObject ToObjectFromKeyedSegments(IEnumerable<string> segments)
    {
        var obj = new JsonObject();
        foreach (var segment in segments)
        {
            var equalsIndex = segment.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            var key = segment[..equalsIndex];
            var val = segment[(equalsIndex + 1)..];
            obj[key] = ToStringValue(val);
        }

        return obj;
    }

    /// <summary>
    /// Builds an object from alternating key,value segments (explode=false object form).
    /// </summary>
    private static JsonObject ToObjectFromPairs(IReadOnlyList<string> tokens)
    {
        var obj = new JsonObject();
        for (var i = 0; i + 1 < tokens.Count; i += 2)
        {
            obj[tokens[i]] = ToStringValue(tokens[i + 1]);
        }

        return obj;
    }

    private static string FirstOrEmpty(IReadOnlyList<string> rawValues) =>
        rawValues.Count > 0 ? rawValues[0] : string.Empty;

    private static OpenApiContractValidationException UnsupportedStyle(
        IOpenApiParameter parameter,
        ParameterStyle? style
    ) =>
        new(
            ContractPhase.Startup,
            new[]
            {
                new ContractViolation(
                    Location: $"parameter/{parameter.Name ?? "?"}",
                    InstanceLocation: null,
                    Keyword: "style",
                    Expected: "one of: matrix, label, form, simple, spaceDelimited, pipeDelimited, deepObject",
                    Actual: style?.ToString() ?? "<null>",
                    Message: $"Parameter '{parameter.Name ?? "?"}' "
                        + $"uses style '{style?.ToString() ?? "<null>"}' which is not supported "
                        + "for deserialization."
                ),
            }
        );
}
