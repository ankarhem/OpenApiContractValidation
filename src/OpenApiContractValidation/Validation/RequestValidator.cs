using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Microsoft.OpenApi;
using OpenApiContractValidation.Errors;
using OpenApiContractValidation.Matching;
using OpenApiContractValidation.Models;
using OpenApiContractValidation.Parameters;
using OpenApiContractValidation.Schema;

namespace OpenApiContractValidation.Validation;

/// <summary>
/// Validates an incoming HTTP request against the OpenAPI operation that will handle it.
/// Framework-agnostic and pure: it never touches <c>HttpContext</c> and never throws; every
/// detected contract drift is returned as a <see cref="ContractViolation"/>.
/// </summary>
/// <remarks>
/// <para>
/// The validator is constructed once with a <see cref="ContractSchemaRegistry"/> and may be
/// invoked concurrently for any number of operations. A single <see cref="Validate(Microsoft.OpenApi.OpenApiOperation, OpenApiContractValidation.Models.ParsedRequest, System.Collections.Generic.IReadOnlyDictionary{string, string})"/> call
/// orchestrates the already-built deserialization, schema-evaluation and content-type
/// matching components to check, maximally strictly:
/// <list type="bullet">
/// <item><description>Path parameters (always required; taken from the caller-supplied path
/// template captures).</description></item>
/// <item><description>Query parameters (case-sensitive name match, per OpenAPI).</description></item>
/// <item><description>Header parameters (case-insensitive name match, per RFC 9110).</description></item>
/// <item><description>Cookie parameters (case-sensitive name match).</description></item>
/// <item><description>The request body: required presence, content-type match, schema
/// conformance and <c>readOnly</c> enforcement.</description></item>
/// </list>
/// </para>
/// <para>
/// All violations across every parameter and the body are aggregated; the method never stops
/// at the first violation and never throws. Throwing on drift is the responsibility of the
/// middleware layer (T13).
/// </para>
/// <para>
/// <b>Scalar coercion.</b> OpenAPI transmits path/query/header/cookie values as text, but
/// their schema may declare a scalar JSON type (<c>integer</c>, <c>number</c>,
/// <c>boolean</c>). The <see cref="ParameterDeserializer"/> deliberately emits such scalars as
/// STRING <see cref="JsonValue"/>s so that type drift is not masked. This validator bridges
/// that by coercing a scalar whose declared type is numeric or boolean into its native JSON
/// primitive <em>only when it parses cleanly</em>; a value that cannot be parsed as the
/// declared type is left as a string so the schema evaluator reports a precise
/// <c>type</c> violation. This is the correct OpenAPI parameter semantics (a valid request
/// carries <c>userId=5</c>; an invalid one carries <c>userId=abc</c>) and mirrors the
/// response-header coercion performed by the sibling <c>ResponseValidator</c>.
/// </para>
/// <para>
/// <b>Undocumented body.</b> When the operation declares no <c>requestBody</c> at all, body
/// validation is skipped: OpenAPI does not forbid a client from sending a body the contract
/// is silent about, so strictly only the <em>declared</em> contract is enforced.
/// </para>
/// </remarks>
public sealed class RequestValidator
{
    private readonly ContractSchemaRegistry _schemaRegistry;

    /// <summary>
    /// Initializes a new <see cref="RequestValidator"/> bound to the supplied schema registry,
    /// which is reused to compile and evaluate parameter and request-body schemas.
    /// </summary>
    /// <param name="schemaRegistry">The contract's schema registry. Must not be null.</param>
    public RequestValidator(ContractSchemaRegistry schemaRegistry)
    {
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        _schemaRegistry = schemaRegistry;
    }

    /// <summary>
    /// Validates <paramref name="request"/> against <paramref name="operation"/> and returns
    /// the aggregate result. Collects every detected violation across all parameters and the
    /// request body; never throws.
    /// </summary>
    /// <param name="operation">The OpenAPI operation that the request was matched to.</param>
    /// <param name="request">The normalized representation of the incoming request.</param>
    /// <param name="pathParameters">
    /// The path-template captures produced by the path matcher (e.g. <c>{"id":"42"}</c>),
    /// keyed by parameter name. Required for path-parameter validation.
    /// </param>
    /// <returns>
    /// <see cref="ValidationResult.Success"/> when the request fully conforms to the
    /// operation's contract; otherwise a failure carrying one violation per detected drift.
    /// </returns>
    public ValidationResult Validate(
        OpenApiOperation operation,
        ParsedRequest request,
        IReadOnlyDictionary<string, string> pathParameters
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        return Validate(operation, request, pathParameters, operation.OperationId ?? request.Path);
    }

    // Overload taking an explicit, stable per-operation cache key. The caller (the middleware
    // layer) supplies a startup-computed "{method} {pathTemplate}" identity so schema-cache
    // entries never collide across operationId-less operations, nor grow per concrete path.
    internal ValidationResult Validate(
        OpenApiOperation operation,
        ParsedRequest request,
        IReadOnlyDictionary<string, string> pathParameters,
        string operationKey
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pathParameters);
        ArgumentNullException.ThrowIfNull(operationKey);

        var violations = new List<ContractViolation>();

        ValidateParameters(operation, request, pathParameters, operationKey, violations);
        ValidateBody(operation, request, operationKey, violations);

        return violations.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(violations);
    }

    private void ValidateParameters(
        OpenApiOperation operation,
        ParsedRequest request,
        IReadOnlyDictionary<string, string> pathParameters,
        string cachePrefix,
        List<ContractViolation> violations
    )
    {
        var parameters = operation.Parameters;
        if (parameters is null || parameters.Count == 0)
        {
            return;
        }

        foreach (var parameter in parameters)
        {
            if (parameter is null)
            {
                continue;
            }

            switch (parameter.In)
            {
                case ParameterLocation.Path:
                    ValidatePathParameter(parameter, pathParameters, cachePrefix, violations);
                    break;
                case ParameterLocation.Query:
                    ValidateQueryParameter(parameter, request, cachePrefix, violations);
                    break;
                case ParameterLocation.Header:
                    ValidateHeaderParameter(parameter, request, cachePrefix, violations);
                    break;
                case ParameterLocation.Cookie:
                    ValidateCookieParameter(parameter, request, cachePrefix, violations);
                    break;
            }
        }
    }

    /// <summary>
    /// Path parameters are always required (per OpenAPI) and supplied by the path matcher as
    /// single decoded string values.
    /// </summary>
    private void ValidatePathParameter(
        IOpenApiParameter parameter,
        IReadOnlyDictionary<string, string> pathParameters,
        string cachePrefix,
        List<ContractViolation> violations
    )
    {
        var name = parameter.Name ?? "?";
        var location = $"path/{name}";

        if (!pathParameters.TryGetValue(name, out var rawValue))
        {
            violations.Add(
                MissingParameterViolation(location, $"path parameter '{name}' is required")
            );
            return;
        }

        ValidateStyleParameter(
            parameter,
            new[] { rawValue },
            cachePrefix,
            "path",
            name,
            location,
            violations
        );
    }

    /// <summary>Query parameter names are matched case-sensitively (OpenAPI).</summary>
    private void ValidateQueryParameter(
        IOpenApiParameter parameter,
        ParsedRequest request,
        string cachePrefix,
        List<ContractViolation> violations
    )
    {
        var name = parameter.Name ?? "?";
        var location = $"query/{name}";

        if (parameter.Style is ParameterStyle.DeepObject || IsObjectSchema(parameter.Schema))
        {
            ValidateDeepObjectQueryParameter(parameter, request, cachePrefix, violations);
            return;
        }

        if (
            !request.QueryValues.TryGetValue(name, out var rawValues)
            || rawValues is null
            || rawValues.Count == 0
        )
        {
            if (parameter.Required)
            {
                violations.Add(
                    MissingParameterViolation(location, $"query parameter '{name}' is required")
                );
            }

            return;
        }

        ValidateStyleParameter(
            parameter,
            rawValues,
            cachePrefix,
            "query",
            name,
            location,
            violations
        );
    }

    /// <summary>
    /// A <c>deepObject</c> query parameter arrives as a set of <c>name[key]=value</c> pairs
    /// which the path matcher flattens into the query dictionary; collect the bracketed
    /// suffixes and hand them to <see cref="ParameterDeserializer.DeserializeDeepObject"/>.
    /// </summary>
    private void ValidateDeepObjectQueryParameter(
        IOpenApiParameter parameter,
        ParsedRequest request,
        string cachePrefix,
        List<ContractViolation> violations
    )
    {
        var name = parameter.Name ?? "?";
        var location = $"query/{name}";
        var prefix = name + "[";
        var bracketed = new Dictionary<string, string>();

        foreach (var (key, values) in request.QueryValues)
        {
            if (
                key is not null
                && key.StartsWith(prefix, StringComparison.Ordinal)
                && key.Length > prefix.Length
                && key[^1] == ']'
                && values is { Count: > 0 }
            )
            {
                bracketed[key[prefix.Length..^1]] = values[0];
            }
        }

        if (bracketed.Count == 0)
        {
            if (parameter.Required)
            {
                violations.Add(
                    MissingParameterViolation(location, $"query parameter '{name}' is required")
                );
            }

            return;
        }

        var node = ParameterDeserializer.DeserializeDeepObject(parameter, bracketed);
        ValidateDeserializedParameter(
            parameter,
            node,
            cachePrefix,
            "query",
            name,
            location,
            violations
        );
    }

    /// <summary>Header names are matched case-insensitively (RFC 9110).</summary>
    private void ValidateHeaderParameter(
        IOpenApiParameter parameter,
        ParsedRequest request,
        string cachePrefix,
        List<ContractViolation> violations
    )
    {
        var name = parameter.Name ?? "?";
        var location = $"header/{name}";
        var rawValues = OpenApiSchemaResolution.FindHeader(request.Headers, name);

        if (rawValues is null || rawValues.Count == 0)
        {
            if (parameter.Required)
            {
                violations.Add(
                    MissingParameterViolation(location, $"header parameter '{name}' is required")
                );
            }

            return;
        }

        ValidateStyleParameter(
            parameter,
            rawValues,
            cachePrefix,
            "header",
            name,
            location,
            violations
        );
    }

    /// <summary>Cookie names are matched case-sensitively.</summary>
    private void ValidateCookieParameter(
        IOpenApiParameter parameter,
        ParsedRequest request,
        string cachePrefix,
        List<ContractViolation> violations
    )
    {
        var name = parameter.Name ?? "?";
        var location = $"cookie/{name}";

        if (!request.Cookies.TryGetValue(name, out var rawValue))
        {
            if (parameter.Required)
            {
                violations.Add(
                    MissingParameterViolation(location, $"cookie parameter '{name}' is required")
                );
            }

            return;
        }

        ValidateStyleParameter(
            parameter,
            new[] { rawValue },
            cachePrefix,
            "cookie",
            name,
            location,
            violations
        );
    }

    /// <summary>
    /// Deserializes a style/content parameter from its raw values and schema-validates the
    /// result, coercing scalar leaves to native JSON primitives per the declared schema type.
    /// </summary>
    private void ValidateStyleParameter(
        IOpenApiParameter parameter,
        IReadOnlyList<string> rawValues,
        string cachePrefix,
        string bucket,
        string name,
        string location,
        List<ContractViolation> violations
    )
    {
        JsonNode? node;

        if (parameter.Content is { Count: > 0 })
        {
            // Content-based parameter: parse the single raw value as its declared media type.
            var first = rawValues.Count > 0 ? rawValues[0] : string.Empty;
            try
            {
                node = ParameterDeserializer.DeserializeContent(parameter, first);
            }
            catch (JsonException)
            {
                violations.Add(
                    new ContractViolation(
                        Location: location,
                        InstanceLocation: null,
                        Keyword: null,
                        Expected: null,
                        Actual: first,
                        Message: "parameter value is not valid JSON"
                    )
                );
                return;
            }
        }
        else
        {
            node = ParameterDeserializer.Deserialize(parameter, rawValues);
        }

        ValidateDeserializedParameter(
            parameter,
            node,
            cachePrefix,
            bucket,
            name,
            location,
            violations
        );
    }

    /// <summary>
    /// Coerces and schema-validates an already-deserialized parameter node, appending any
    /// violations under the supplied <paramref name="location"/>.
    /// </summary>
    private void ValidateDeserializedParameter(
        IOpenApiParameter parameter,
        JsonNode? node,
        string cachePrefix,
        string bucket,
        string name,
        string location,
        List<ContractViolation> violations
    )
    {
        // The deserializer yields null only when the raw value(s) were absent; the required
        // check has already been handled by the caller, so an absent optional value is valid.
        if (node is null)
        {
            return;
        }

        var schema = parameter.Schema;
        if (schema is null)
        {
            return;
        }

        var coerced = CoerceParameter(schema, node);
        var instance = JsonSerializer.SerializeToElement(coerced);
        var compiled = _schemaRegistry.GetTargetSchema(
            $"{cachePrefix}|param|{bucket}|{name}",
            schema
        );
        var result = _schemaRegistry.Validate(compiled, instance, location);
        if (!result.IsValid)
        {
            violations.AddRange(result.Violations);
        }
    }

    private void ValidateBody(
        OpenApiOperation operation,
        ParsedRequest request,
        string cachePrefix,
        List<ContractViolation> violations
    )
    {
        var requestBody = operation.RequestBody;

        // Strict-but-spec-accurate: OpenAPI does not forbid a body the contract is silent
        // about, so when no requestBody is declared we do not validate (or flag) any body.
        if (requestBody is null)
        {
            return;
        }

        // Body presence is byte presence (RawBody), not JSON-parse success, so a non-JSON
        // body still reaches the content-type check instead of looking absent.
        var hasBody = !string.IsNullOrEmpty(request.RawBody) || request.Body is not null;

        if (requestBody.Required && !hasBody)
        {
            violations.Add(
                new ContractViolation(
                    Location: "requestBody",
                    InstanceLocation: null,
                    Keyword: "required",
                    Expected: null,
                    Actual: null,
                    Message: "request body is required"
                )
            );
            return;
        }

        if (!hasBody)
        {
            return;
        }

        var content = requestBody.Content;
        if (content is null || content.Count == 0)
        {
            return;
        }

        if (
            !ContentTypeMatcher.TryMatch(
                request.ContentType,
                content.Keys,
                out var matchedMediaType
            )
        )
        {
            var allowed = string.Join(", ", content.Keys);
            violations.Add(
                new ContractViolation(
                    Location: "requestBody/contentType",
                    InstanceLocation: null,
                    Keyword: null,
                    Expected: allowed,
                    Actual: request.ContentType,
                    Message: $"request content-type '{request.ContentType}' is not documented; allowed: {allowed}"
                )
            );
            return;
        }

        // A matched JSON media type with a non-empty raw body but no parsed instance means the
        // body is malformed JSON: contract drift, not "no instance to evaluate". The actual
        // content type must be JSON-ish too: the matcher is lenient on a missing content type
        // (it can match a JSON key without one), but no parse was attempted then, so a null
        // Body is not a parse failure.
        if (
            request.Body is null
            && IsJsonMediaType(matchedMediaType!)
            && IsJsonMediaType(request.ContentType)
            && !string.IsNullOrEmpty(request.RawBody)
        )
        {
            violations.Add(
                new ContractViolation(
                    Location: "requestBody",
                    InstanceLocation: null,
                    Keyword: null,
                    Expected: null,
                    Actual: null,
                    Message: "request body is not valid JSON"
                )
            );
            return;
        }

        var mediaType = content[matchedMediaType!];
        var schema = mediaType.Schema;
        if (schema is null)
        {
            return;
        }

        // A non-JSON body (e.g. application/octet-stream) passes the content-type check above
        // but has no JSON instance to evaluate.
        if (request.Body is null)
        {
            return;
        }

        var body = request.Body.Value;

        // Schema validation of the parsed body (native JSON types; no coercion needed).
        var compiled = _schemaRegistry.GetTargetSchema(
            $"{cachePrefix}|requestBody|{matchedMediaType}",
            schema
        );
        var result = _schemaRegistry.Validate(compiled, body, "requestBody");
        if (!result.IsValid)
        {
            violations.AddRange(result.Violations);
        }

        // readOnly enforcement: JSON Schema evaluators deliberately ignore readOnly/writeOnly.
        var bodyNode = JsonNode.Parse(body.GetRawText());
        var readOnlyViolations = ReadOnlyWriteOnlyChecker.CheckRequest(
            schema,
            bodyNode,
            "requestBody"
        );
        if (readOnlyViolations.Count > 0)
        {
            violations.AddRange(readOnlyViolations);
        }
    }

    private static ContractViolation MissingParameterViolation(string location, string message) =>
        new(
            Location: location,
            InstanceLocation: null,
            Keyword: "required",
            Expected: null,
            Actual: null,
            Message: message
        );

    /// <summary>
    /// Determines whether a media-type string denotes JSON (loose case-insensitive "json"
    /// substring, mirroring the middleware's content-type check), covering the actual
    /// <c>Content-Type</c> header as well as contract keys such as <c>application/json</c>,
    /// <c>text/json</c> and structured suffixes like <c>application/*+json</c>. Returns
    /// <see langword="false"/> when the value is null (e.g. a missing content type).
    /// </summary>
    private static bool IsJsonMediaType(string? mediaType) =>
        mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private static bool IsObjectSchema(IOpenApiSchema? schema) =>
        schema is not null
        && schema.Type.HasValue
        && schema.Type.Value.HasFlag(JsonSchemaType.Object);

    /// <summary>
    /// Recursively coerces scalar leaves of a deserialized parameter node into native JSON
    /// primitives according to the declared schema type. URL-transported values arrive as
    /// STRING tokens; a numeric/boolean schema parses cleanly-formatted values into JSON
    /// numbers/booleans so a valid request validates, while a value that does not parse is
    /// left as a string so the evaluator emits a precise <c>type</c> violation. Object and
    /// array nodes are walked element/property-wise against their subschemas.
    /// </summary>
    private static JsonNode? CoerceParameter(IOpenApiSchema? schema, JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        var resolved = OpenApiSchemaResolution.Resolve(schema);
        var type = resolved?.Type ?? (JsonSchemaType)0;

        switch (node)
        {
            case JsonArray array:
            {
                var itemsSchema = resolved?.Items;
                var result = new JsonArray();
                foreach (var element in array)
                {
                    // Detach: a returned un-coerced node is still parented to the source.
                    result.Add(Detach(CoerceParameter(itemsSchema, element)));
                }

                return result;
            }
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    IOpenApiSchema? propertySchema = null;
                    resolved?.Properties?.TryGetValue(key, out propertySchema);
                    result[key] = Detach(CoerceParameter(propertySchema, value));
                }

                return result;
            }
            default:
                return CoerceScalar(type, node);
        }
    }

    private static JsonNode? Detach(JsonNode? node) =>
        node is null || node.Parent is null ? node : node.DeepClone();

    /// <summary>
    /// Coerces a single scalar STRING token into a native JSON primitive when the declared
    /// schema type demands it and the value parses cleanly; otherwise returns the node
    /// unchanged so the schema evaluator reports the type drift.
    /// </summary>
    private static JsonNode CoerceScalar(JsonSchemaType type, JsonNode node)
    {
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
        {
            // Already a native (non-string) primitive, e.g. from content deserialization.
            return node;
        }

        if (
            (type & JsonSchemaType.Integer) != 0
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
        )
        {
            return (JsonNode)i;
        }

        if (
            (type & JsonSchemaType.Number) != 0
            && decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
        )
        {
            return (JsonNode)d;
        }

        if ((type & JsonSchemaType.Boolean) != 0 && bool.TryParse(text, out var b))
        {
            return (JsonNode)b;
        }

        return node;
    }
}
