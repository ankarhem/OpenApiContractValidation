using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.OpenApi;
using OpenApiContractValidation.Errors;
using OpenApiContractValidation.Matching;
using OpenApiContractValidation.Models;
using OpenApiContractValidation.Schema;

namespace OpenApiContractValidation.Validation;

/// <summary>
/// Validates an outgoing HTTP response against the OpenAPI operation that handled it.
/// Pure and framework-agnostic: it never touches <c>HttpContext</c> and never throws;
/// every detected contract drift is returned as a <see cref="ContractViolation"/>.
/// </summary>
/// <remarks>
/// <para>
/// The validator is constructed once with a <see cref="ContractSchemaRegistry"/> and may be
/// invoked concurrently for any number of operations. Each <see cref="Validate(Microsoft.OpenApi.OpenApiOperation, OpenApiContractValidation.Models.ParsedResponse)"/> call:
/// <list type="number">
/// <item><description>Matches the status code against the operation's <c>responses</c> keys
/// (exact &gt; range class &gt; <c>default</c>) via <see cref="StatusMatcher"/>.</description></item>
/// <item><description>For 204/304 responses, forbids a body and skips body validation
/// (headers are still validated).</description></item>
/// <item><description>Validates each <em>declared</em> response header for presence (when
/// <c>required</c>) and schema conformance. Undocumented headers (e.g. <c>Date</c>,
/// <c>Content-Length</c>) are intentionally <em>not</em> flagged: servers legitimately add
/// transport-level headers and the OpenAPI spec does not constrain them.</description></item>
/// <item><description>Validates the response body's content-type against the operation's
/// <c>content</c> map, schema-validates the body via <see cref="ContractSchemaRegistry"/>,
/// and runs <see cref="ReadOnlyWriteOnlyChecker.CheckResponse"/> to enforce that no
/// <c>writeOnly</c> property leaks into a response.</description></item>
/// </list>
/// </para>
/// <para>
/// All violations are aggregated; the method returns <see cref="ValidationResult.Success"/>
/// when none are found, otherwise a <see cref="ValidationResult"/>.Failure carrying every
/// violation. The throwing behaviour (raising an exception on drift) is the responsibility
/// of the middleware layer (T13), not this validator.
/// </para>
/// </remarks>
public sealed class ResponseValidator
{
    private readonly ContractSchemaRegistry _schemaRegistry;

    /// <summary>
    /// Initializes a new <see cref="ResponseValidator"/> bound to the supplied schema
    /// registry, which is reused to compile and evaluate response-body and response-header
    /// schemas.
    /// </summary>
    /// <param name="schemaRegistry">The contract's schema registry. Must not be null.</param>
    public ResponseValidator(ContractSchemaRegistry schemaRegistry)
    {
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        _schemaRegistry = schemaRegistry;
    }

    /// <summary>
    /// Validates <paramref name="response"/> against <paramref name="operation"/> and returns
    /// the aggregate result. Collects every detected violation; never throws.
    /// </summary>
    /// <param name="operation">The OpenAPI operation that produced the response.</param>
    /// <param name="response">The normalized representation of the outgoing response.</param>
    /// <returns>
    /// <see cref="ValidationResult.Success"/> when the response fully conforms to the
    /// operation's contract; otherwise a failure carrying one violation per detected drift.
    /// </returns>
    public ValidationResult Validate(OpenApiOperation operation, ParsedResponse response)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Validate(
            operation,
            response,
            operation.OperationId is null ? "op" : operation.OperationId
        );
    }

    // Overload taking an explicit, stable per-operation cache key. The caller (the middleware
    // layer) supplies a startup-computed "{method} {pathTemplate}" identity so schema-cache
    // entries never collide across operationId-less operations that share status + media type.
    internal ValidationResult Validate(
        OpenApiOperation operation,
        ParsedResponse response,
        string operationKey
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(operationKey);

        var violations = new List<ContractViolation>();

        var responseKeys =
            (operation.Responses?.Keys as IReadOnlyCollection<string>)
            ?? operation.Responses?.Keys.ToList()
            ?? (IReadOnlyCollection<string>)Array.Empty<string>();

        // 1. Status code matching.
        if (!StatusMatcher.TryMatch(response.StatusCode, responseKeys, out var matchedStatusKey))
        {
            // Without a matched response definition we cannot meaningfully validate the
            // body or headers, so report the status drift and stop.
            var allowed = string.Join(", ", responseKeys);
            violations.Add(
                new ContractViolation(
                    Location: "status",
                    InstanceLocation: null,
                    Keyword: null,
                    Expected: allowed,
                    Actual: response.StatusCode.ToString(),
                    Message: $"status {response.StatusCode} is not documented; allowed: {allowed}"
                )
            );
            return ValidationResult.Failure(violations);
        }

        var statusKey = matchedStatusKey!;
        var resp = operation.Responses![statusKey];

        // 2. No-body status semantics: 204 and 304 must not carry a body.
        var isNoBodyStatus = response.StatusCode == 204 || response.StatusCode == 304;
        if (isNoBodyStatus && response.HasBody)
        {
            violations.Add(
                new ContractViolation(
                    Location: "responseBody",
                    InstanceLocation: null,
                    Keyword: null,
                    Expected: null,
                    Actual: null,
                    Message: $"{response.StatusCode} responses must not contain a body"
                )
            );
        }

        // 3. Response headers: validate only the headers the contract declares.
        ValidateHeaders(resp, response, operationKey, violations);

        // 4. Response body. Gate on HasBody (not parsed Body) so the content-type check
        // also runs for non-JSON bodies.
        if (!isNoBodyStatus && response.HasBody)
        {
            ValidateBody(resp, response, operationKey, statusKey, violations);
        }

        return violations.Count == 0
            ? ValidationResult.Success
            : ValidationResult.Failure(violations);
    }

    private void ValidateHeaders(
        IOpenApiResponse resp,
        ParsedResponse response,
        string cachePrefix,
        List<ContractViolation> violations
    )
    {
        var declaredHeaders = resp.Headers;
        if (declaredHeaders is null || declaredHeaders.Count == 0)
        {
            return;
        }

        // NOTE: Undocumented response headers (Date, Content-Length, Server, ...) are
        // intentionally NOT flagged. OpenAPI documents the headers an operation SHOULD
        // return; it does not forbid servers from adding transport-level headers, and
        // doing so here would produce constant false positives. Only declared headers
        // are presence- and schema-checked.
        foreach (var (name, headerSpec) in declaredHeaders)
        {
            if (headerSpec is null)
            {
                continue;
            }

            var actualValues = OpenApiSchemaResolution.FindHeader(response.Headers, name);

            if (actualValues is null || actualValues.Count == 0)
            {
                if (headerSpec.Required)
                {
                    violations.Add(
                        new ContractViolation(
                            Location: $"responseHeader/{name}",
                            InstanceLocation: null,
                            Keyword: "required",
                            Expected: null,
                            Actual: null,
                            Message: $"required response header '{name}' is missing"
                        )
                    );
                }

                continue;
            }

            var schema = headerSpec.Schema;
            if (schema is null)
            {
                continue;
            }

            var headerNode = CoerceHeaderValue(schema, actualValues);
            if (headerNode is null)
            {
                continue;
            }

            // JsonNode -> JsonElement for the schema evaluator.
            var headerElement = JsonSerializer.SerializeToElement(headerNode);
            var location = $"responseHeader/{name}";
            var compiled = _schemaRegistry.GetTargetSchema(
                $"{cachePrefix}|responseHeader|{name}",
                schema
            );
            var result = _schemaRegistry.Validate(compiled, headerElement, location);
            if (!result.IsValid)
            {
                violations.AddRange(result.Violations);
            }
        }
    }

    private void ValidateBody(
        IOpenApiResponse resp,
        ParsedResponse response,
        string cachePrefix,
        string statusKey,
        List<ContractViolation> violations
    )
    {
        var content = resp.Content;
        if (content is null || content.Count == 0)
        {
            // Strict-but-spec-accurate: the contract declares no body for this status,
            // yet one was returned. That is contract drift.
            violations.Add(
                new ContractViolation(
                    Location: "responseBody",
                    InstanceLocation: null,
                    Keyword: null,
                    Expected: null,
                    Actual: null,
                    Message: "response body returned but none is documented for this status"
                )
            );
            return;
        }

        if (
            !ContentTypeMatcher.TryMatch(
                response.ContentType,
                content.Keys,
                out var matchedMediaType
            )
        )
        {
            var allowed = string.Join(", ", content.Keys);
            violations.Add(
                new ContractViolation(
                    Location: "responseBody/contentType",
                    InstanceLocation: null,
                    Keyword: null,
                    Expected: allowed,
                    Actual: response.ContentType,
                    Message: $"response content-type '{response.ContentType}' is not documented; allowed: {allowed}"
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
            response.Body is null
            && IsJsonMediaType(matchedMediaType!)
            && IsJsonMediaType(response.ContentType)
            && !string.IsNullOrEmpty(response.RawBody)
        )
        {
            violations.Add(
                new ContractViolation(
                    Location: "responseBody",
                    InstanceLocation: null,
                    Keyword: null,
                    Expected: null,
                    Actual: null,
                    Message: "response body is not valid JSON"
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

        // Schema validation requires a parsed JSON body; non-JSON bodies pass the
        // content-type check above but have no JSON instance to evaluate.
        if (response.Body is null)
        {
            return;
        }

        var body = response.Body.Value;

        var compiled = _schemaRegistry.GetTargetSchema(
            $"{cachePrefix}|response|{statusKey}|{matchedMediaType}",
            schema
        );
        var result = _schemaRegistry.Validate(compiled, body, "responseBody");
        if (!result.IsValid)
        {
            violations.AddRange(result.Violations);
        }

        // writeOnly enforcement (JSON Schema evaluators do not check readOnly/writeOnly).
        var bodyNode = JsonNode.Parse(body.GetRawText());
        var writeOnlyViolations = ReadOnlyWriteOnlyChecker.CheckResponse(
            schema,
            bodyNode,
            "responseBody"
        );
        if (writeOnlyViolations.Count > 0)
        {
            violations.AddRange(writeOnlyViolations);
        }
    }

    /// <summary>
    /// Determines whether a media-type string denotes JSON (loose case-insensitive "json"
    /// substring, mirroring the middleware's content-type check), covering the actual
    /// <c>Content-Type</c> header as well as contract keys such as <c>application/json</c>,
    /// <c>text/json</c> and structured suffixes like <c>application/*+json</c>. Returns
    /// <see langword="false"/> when the value is null (e.g. a missing content type).
    /// </summary>
    private static bool IsJsonMediaType(string? mediaType) =>
        mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Converts a raw header string value into a JSON node whose primitive shape matches the
    /// declared schema, so the JSON Schema evaluator can type-check it. Header values are
    /// always transmitted as text, so a scalar declared as <c>integer</c>/<c>number</c>/
    /// <c>boolean</c> is parsed into the corresponding JSON primitive; arrays are split on
    /// commas (OpenAPI simple-style). Anything that cannot be parsed as the declared scalar
    /// type is left as a string, which lets the evaluator emit a clean type violation.
    /// </summary>
    private static JsonNode? CoerceHeaderValue(IOpenApiSchema? schema, IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        var resolved = OpenApiSchemaResolution.Resolve(schema);
        var type = resolved?.Type ?? (JsonSchemaType)0;

        if ((type & JsonSchemaType.Array) != 0)
        {
            var array = new JsonArray();
            foreach (var value in values)
            {
                foreach (var part in value.Split(','))
                {
                    array.Add(part.Trim());
                }
            }

            return array;
        }

        var first = values[0];

        if (
            (type & JsonSchemaType.Integer) != 0
            && long.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
        )
        {
            return (JsonNode)i;
        }

        if (
            (type & JsonSchemaType.Number) != 0
            && decimal.TryParse(first, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
        )
        {
            return (JsonNode)d;
        }

        if ((type & JsonSchemaType.Boolean) != 0 && bool.TryParse(first, out var b))
        {
            return (JsonNode)b;
        }

        return (JsonNode)first;
    }
}
