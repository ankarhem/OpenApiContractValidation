using System.Text.Json.Nodes;

namespace OpenApiContractValidation.Schema;

/// <summary>
/// Rewrites OpenAPI <c>$ref</c> pointers produced by <c>Microsoft.OpenApi</c>'s
/// JSON Schema 2020-12 serializer into forms that JsonSchema.Net can resolve.
/// </summary>
/// <remarks>
/// <para>
/// <c>Microsoft.OpenApi</c> serializes schema references as
/// <c>#/components/schemas/&lt;name&gt;</c> even when emitting JSON Schema 2020-12.
/// JsonSchema.Net, however, expects the contract's named schemas under the standard
/// <c>$defs</c> keyword. This helper performs a <em>structural</em> rewrite: it parses
/// the JSON into a <see cref="JsonNode"/> tree and rewrites only the string value of
/// properties named <c>$ref</c> whose value starts with the
/// <c>#/components/schemas/</c> prefix.
/// </para>
/// <para>
/// Structural rewriting (as opposed to a naive <see cref="string"/> replacement) is
/// important because legitimate schema data - descriptions, examples, enum values -
/// may contain the literal text <c>#/components/schemas/</c> without being a
/// reference, and those occurrences must be left untouched.
/// </para>
/// </remarks>
public static class RefRewriter
{
    /// <summary>
    /// The OpenAPI reference prefix that this rewriter recognises and transforms.
    /// </summary>
    public const string ComponentSchemaPrefix = "#/components/schemas/";

    /// <summary>
    /// The JSON Schema 2020-12 definition prefix used by the local/relative form.
    /// </summary>
    public const string DefsPrefix = "#/$defs/";

    /// <summary>
    /// Rewrites a serialized schema's <c>$ref</c> pointers from the OpenAPI
    /// <c>#/components/schemas/&lt;name&gt;</c> form to the JSON Schema 2020-12
    /// <c>#/$defs/&lt;name&gt;</c> form, mutating the supplied node tree in place.
    /// </summary>
    /// <param name="node">The root schema node. May be <see langword="null"/>.</param>
    /// <returns>The same <paramref name="node"/> reference, with all matching
    /// references rewritten.</returns>
    public static JsonNode? RewriteToLocal(JsonNode? node)
    {
        Walk(node, referenceValue => DefsPrefix + referenceValue[ComponentSchemaPrefix.Length..]);
        return node;
    }

    /// <summary>
    /// Rewrites a serialized schema's <c>$ref</c> pointers from the OpenAPI
    /// <c>#/components/schemas/&lt;name&gt;</c> form to the JSON Schema 2020-12
    /// <c>#/$defs/&lt;name&gt;</c> form.
    /// </summary>
    /// <param name="schemaJson">A serialized JSON schema string.</param>
    /// <returns>The rewritten schema as a JSON string.</returns>
    public static string RewriteToLocal(string schemaJson) =>
        RewriteToLocal(JsonNode.Parse(schemaJson))!.ToJsonString();

    /// <summary>
    /// Rewrites a serialized schema's <c>$ref</c> pointers from the OpenAPI
    /// <c>#/components/schemas/&lt;name&gt;</c> form to an <em>absolute</em> reference
    /// resolved against <paramref name="baseUri"/>, e.g.
    /// <c>openapi://contract/#/$defs/&lt;name&gt;</c>. Mutates the node tree in place.
    /// </summary>
    /// <param name="node">The root schema node. May be <see langword="null"/>.</param>
    /// <param name="baseUri">The absolute base URI the contract is registered under
    /// (without a trailing fragment). For example <c>openapi://contract/</c>.</param>
    /// <returns>The same <paramref name="node"/> reference, with all matching
    /// references rewritten.</returns>
    public static JsonNode? RewriteToAbsolute(JsonNode? node, string baseUri)
    {
        var absolutePrefix = BuildAbsolutePrefix(baseUri);
        Walk(
            node,
            referenceValue => absolutePrefix + referenceValue[ComponentSchemaPrefix.Length..]
        );
        return node;
    }

    /// <summary>
    /// Rewrites a serialized schema's <c>$ref</c> pointers from the OpenAPI
    /// <c>#/components/schemas/&lt;name&gt;</c> form to an <em>absolute</em> reference
    /// resolved against <paramref name="baseUri"/>.
    /// </summary>
    /// <param name="schemaJson">A serialized JSON schema string.</param>
    /// <param name="baseUri">The absolute base URI the contract is registered under
    /// (without a trailing fragment). For example <c>openapi://contract/</c>.</param>
    /// <returns>The rewritten schema as a JSON string.</returns>
    public static string RewriteToAbsolute(string schemaJson, string baseUri) =>
        RewriteToAbsolute(JsonNode.Parse(schemaJson), baseUri)!.ToJsonString();

    private static void Walk(JsonNode? node, Func<string, string> rewrite)
    {
        switch (node)
        {
            case JsonObject obj:
                // Snapshot the key/value pairs so property values can be reassigned
                // safely while iterating.
                foreach (var (key, value) in obj.ToArray())
                {
                    if (
                        key == "$ref"
                        && value is JsonValue jsonValue
                        && jsonValue.TryGetValue<string>(out var referenceValue)
                        && referenceValue.StartsWith(
                            ComponentSchemaPrefix,
                            StringComparison.Ordinal
                        )
                    )
                    {
                        obj["$ref"] = rewrite(referenceValue);
                    }
                    else
                    {
                        Walk(value, rewrite);
                    }
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, rewrite);
                }

                break;
        }
    }

    private static string BuildAbsolutePrefix(string baseUri)
    {
        if (string.IsNullOrEmpty(baseUri))
        {
            return DefsPrefix;
        }

        return baseUri.EndsWith('/') ? baseUri + DefsPrefix : baseUri + "/" + DefsPrefix;
    }
}
