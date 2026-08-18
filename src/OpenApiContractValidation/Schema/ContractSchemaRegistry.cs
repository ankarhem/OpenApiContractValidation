using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.OpenApi;
using OpenApiContractValidation.Errors;
using OpenApiContractValidation.Models;

namespace OpenApiContractValidation.Schema;

/// <summary>
/// Bridges an <see cref="OpenApiDocument"/> parsed by <c>Microsoft.OpenApi</c> with the
/// <c>JsonSchema.Net</c> evaluator, turning the document's
/// <c>components/schemas</c> table into a self-contained JSON Schema 2020-12 schema
/// document whose named definitions are resolved through a dedicated, instance-local
/// <see cref="SchemaRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Construction is one-time and (after it completes) read-only: the
/// <c>components/schemas</c> entries are serialized via <c>SerializeAsV31</c> (which
/// yields JSON Schema 2020-12 and converts OpenAPI 3.0 idioms such as
/// <c>nullable</c> into type unions), their internal
/// <c>#/components/schemas/&lt;name&gt;</c> references are rewritten to
/// <c>#/$defs/&lt;name&gt;</c>, and the resulting <c>{ "$defs": { ... } }</c> document
/// is compiled into a single <see cref="JsonSchema"/> registered under the base URI
/// <see cref="BaseUriString"/>.
/// </para>
/// <para>
/// Per-target schemas produced by <see cref="GetTargetSchema"/> rewrite their
/// references to <em>absolute</em> form (<c>openapi://contract/#/$defs/&lt;name&gt;</c>)
/// and are built against the same local registry so that cross-schema and recursive
/// references resolve correctly. Compiled target schemas are cached by caller-supplied
/// key and never recompiled on a cache hit.
/// </para>
/// <para>
/// A fresh, instance-local <see cref="SchemaRegistry"/> is used (never
/// <see cref="SchemaRegistry.Global"/>) so that several contracts loaded in the same
/// process never collide.
/// </para>
/// <para>
/// Instances are safe for concurrent reads after construction.
/// </para>
/// </remarks>
public sealed class ContractSchemaRegistry
{
    /// <summary>
    /// The base URI every contract schema is registered under, and that absolute
    /// target-schema references are resolved against.
    /// </summary>
    public const string BaseUriString = "openapi://contract/";

    private static readonly Uri BaseUri = new(BaseUriString);

    // Shared across all Validate calls: JsonSchema.Net's evaluation only reads these options
    // (the library itself reuses the static EvaluationOptions.Default for every parameterless
    // Evaluate, including concurrent ones), so a single instance is safe to reuse.
    private static readonly EvaluationOptions SharedEvaluationOptions = new()
    {
        OutputFormat = OutputFormat.Hierarchical,
        RequireFormatValidation = true,
    };

    private readonly SchemaRegistry _registry;
    private readonly BuildOptions _buildOptions;
    private readonly ConcurrentDictionary<string, JsonSchema> _targetCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ContractSchemaRegistry"/> class,
    /// compiling the supplied document's <c>components/schemas</c> into a JSON Schema
    /// 2020-12 document backed by an instance-local registry.
    /// </summary>
    /// <param name="document">The OpenAPI document whose schemas should be made
    /// available for validation. May contain a <see langword="null"/>
    /// <c>components.schemas</c> table, in which case the registry simply has no
    /// definitions.</param>
    public ContractSchemaRegistry(OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        _registry = new SchemaRegistry();

        // Draft 2020-12 permits unknown keywords, so OpenAPI annotation keywords emitted
        // by SerializeAsV31 (xml, example, discriminator, ...) are ignored, not rejected.
        _buildOptions = new BuildOptions
        {
            SchemaRegistry = _registry,
            Dialect = Dialect.Draft202012,
        };

        RootSchema = BuildRootSchema(document);
        _registry.Register(BaseUri, RootSchema);
    }

    /// <summary>
    /// The compiled root JSON Schema (the <c>$defs</c> document) registered under
    /// <see cref="BaseUriString"/>. Target-schema references resolve into this
    /// document's <c>$defs</c>.
    /// </summary>
    public JsonSchema RootSchema { get; }

    // Number of compiled target schemas currently cached. Exposed for tests asserting that
    // a stable per-operation cache key keeps the cache bounded regardless of concrete path.
    internal int CachedSchemaCount => _targetCache.Count;

    /// <summary>
    /// Returns a compiled <see cref="JsonSchema"/> for validating instances against the
    /// supplied OpenAPI schema, rewriting its references to absolute form and compiling
    /// it against this registry's local schema registry.
    /// </summary>
    /// <remarks>
    /// Compiled schemas are cached by <paramref name="cacheKey"/>. A cache hit returns
    /// the previously compiled instance (reference-identical) and performs no
    /// serialization or compilation.
    /// </remarks>
    /// <param name="cacheKey">A caller-supplied key that uniquely identifies this
    /// schema within the caller's context (e.g. <c>"GET /users/{id} response body"</c>).
    /// Repeated calls with the same key return the same compiled instance.</param>
    /// <param name="schema">The OpenAPI schema to compile. Typically a value obtained
    /// from <c>document.Components.Schemas[...]</c> or an operation's media-type
    /// schema.</param>
    /// <returns>The compiled, cached <see cref="JsonSchema"/>.</returns>
    public JsonSchema GetTargetSchema(string cacheKey, IOpenApiSchema schema)
    {
        if (_targetCache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var built = BuildTargetSchema(schema);
        return _targetCache.GetOrAdd(cacheKey, built);
    }

    /// <summary>
    /// Evaluates <paramref name="instance"/> against <paramref name="schema"/> and
    /// flattens the result into a <see cref="ValidationResult"/>.
    /// </summary>
    /// <param name="schema">A schema produced by <see cref="GetTargetSchema"/>
    /// (or otherwise built against this registry's schema registry).</param>
    /// <param name="instance">The JSON instance to validate, as a
    /// <see cref="JsonElement"/> (e.g. parsed from an HTTP body).</param>
    /// <param name="locationLabel">A human-readable label identifying where in the
    /// request/response the instance came from (e.g. <c>"responseBody"</c>,
    /// <c>"query/id"</c>). Echoed on every emitted violation.</param>
    /// <returns>
    /// <see cref="ValidationResult.Success"/> when the instance is valid; otherwise a
    /// <see cref="ValidationResult"/> carrying one <see cref="ContractViolation"/> per
    /// evaluation error, each annotated with <paramref name="locationLabel"/>, the
    /// offending JSON pointer and the failing keyword.
    /// </returns>
    public ValidationResult Validate(JsonSchema schema, JsonElement instance, string locationLabel)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(locationLabel);

        var results = schema.Evaluate(instance, SharedEvaluationOptions);
        if (results.IsValid)
        {
            return ValidationResult.Success;
        }

        var violations = new List<ContractViolation>();
        foreach (var node in Flatten(results))
        {
            if (node.Errors is null || node.Errors.Count == 0)
            {
                continue;
            }

            var instanceLocation = node.InstanceLocation.ToString();
            foreach (var error in node.Errors)
            {
                violations.Add(
                    new ContractViolation(
                        Location: locationLabel,
                        InstanceLocation: instanceLocation,
                        Keyword: error.Key,
                        Expected: null,
                        Actual: null,
                        Message: error.Value
                    )
                );
            }
        }

        return ValidationResult.Failure(violations);
    }

    private JsonSchema BuildRootSchema(OpenApiDocument document)
    {
        var defs = new JsonObject();

        var schemas = document.Components?.Schemas;
        if (schemas is not null)
        {
            foreach (var pair in schemas)
            {
                if (pair.Value is null)
                {
                    continue;
                }

                var serialized = SerializeSchemaV31(pair.Value);
                var rewritten = RefRewriter.RewriteToLocal(serialized);
                defs[pair.Key] = JsonNode.Parse(rewritten);
            }
        }

        var rootJson = new JsonObject { ["$defs"] = defs }.ToJsonString();
        return JsonSchema.FromText(rootJson, _buildOptions, BaseUri);
    }

    private JsonSchema BuildTargetSchema(IOpenApiSchema schema)
    {
        var serialized = SerializeSchemaV31(schema);
        var rewritten = RefRewriter.RewriteToAbsolute(serialized, BaseUriString);
        return JsonSchema.FromText(rewritten, _buildOptions);
    }

    private static string SerializeSchemaV31(IOpenApiSchema schema)
    {
        // SerializeAsV31 writes through to the underlying TextWriter synchronously,
        // so no explicit flush is required.
        var stringWriter = new StringWriter();
        var jsonWriter = new OpenApiJsonWriter(stringWriter);
        schema.SerializeAsV31(jsonWriter);
        return stringWriter.ToString();
    }

    private static IEnumerable<EvaluationResults> Flatten(EvaluationResults node)
    {
        yield return node;

        if (node.Details is null)
        {
            yield break;
        }

        foreach (var child in node.Details)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }
}
