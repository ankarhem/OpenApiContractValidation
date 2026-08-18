using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using OpenApiContractValidation.Errors;
using OpenApiContractValidation.Loader;
using OpenApiContractValidation.Matching;
using OpenApiContractValidation.Models;
using OpenApiContractValidation.Options;
using OpenApiContractValidation.Schema;
using OpenApiContractValidation.Validation;

namespace OpenApiContractValidation.Middleware;

/// <summary>
/// A singleton holder that loads the OpenAPI contract exactly once at startup, then builds and caches
/// every framework-agnostic validation component (<see cref="ContractSchemaRegistry"/>,
/// <see cref="RequestValidator"/>, <see cref="ResponseValidator"/> and a
/// <see cref="PathTemplateMatcher"/>) so that the per-request middleware only performs cheap lookups
/// and validation.
/// </summary>
/// <remarks>
/// <para>
/// The contract is sourced from exactly one of <see cref="OpenApiValidationOptions.ContractFilePath"/>,
/// <see cref="OpenApiValidationOptions.ContractStream"/> or
/// <see cref="OpenApiValidationOptions.ContractText"/>; if none (or more than one) is set the
/// constructor throws an <see cref="OpenApiContractValidationException"/> in the
/// <see cref="ContractPhase.Startup"/> phase.
/// </para>
/// <para>
/// Hot-reload of the contract is intentionally out of scope, so <see cref="IOptions{TOptions}"/> is used
/// rather than <c>IOptionsMonitor&lt;T&gt;</c>: the contract is loaded once, at construction time.
/// </para>
/// </remarks>
public sealed class OpenApiContractValidator
{
    private const string StreamingMediaType = "text/event-stream";

    private readonly OpenApiValidationOptions _options;
    private readonly OpenApiDocument _document;
    private readonly ContractSchemaRegistry _schemaRegistry;
    private readonly RequestValidator _requestValidator;
    private readonly ResponseValidator _responseValidator;
    private readonly PathTemplateMatcher _pathMatcher;
    private readonly HashSet<OpenApiOperation> _streamingOperations;
    private readonly Dictionary<OpenApiOperation, string> _operationKeys;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiContractValidator"/> class, loading and
    /// compiling the configured OpenAPI contract. All heavy work happens here, exactly once.
    /// </summary>
    /// <param name="options">
    /// The bound <see cref="OpenApiValidationOptions"/> describing where the contract is sourced and
    /// which directions are validated.
    /// </param>
    /// <exception cref="OpenApiContractValidationException">
    /// Thrown in the <see cref="ContractPhase.Startup"/> phase when no contract source is configured,
    /// or the contract cannot be parsed. Operations declaring a streaming content type are recorded and
    /// skipped at request time rather than rejected here (see <see cref="IsStreamingOperation"/>).
    /// </exception>
    public OpenApiContractValidator(IOptions<OpenApiValidationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        _document = LoadDocument(_options);

        // Guard against a document with no paths: there is nothing to match against.
        if (_document.Paths is null || _document.Paths.Count == 0)
        {
            throw new OpenApiContractValidationException(
                ContractPhase.Startup,
                SingleViolation("document", "The OpenAPI document declares no paths.")
            );
        }

        _streamingOperations = CollectStreamingOperations(_document);

        _schemaRegistry = new ContractSchemaRegistry(_document);
        _requestValidator = new RequestValidator(_schemaRegistry);
        _responseValidator = new ResponseValidator(_schemaRegistry);
        _pathMatcher = new PathTemplateMatcher(_document.Paths.Keys);
        _operationKeys = BuildOperationKeys(_document);
    }

    /// <summary>The bound options that configured this validator.</summary>
    public OpenApiValidationOptions Options => _options;

    // The compiled schema registry backing the validators. Exposed for tests asserting the
    // target-schema cache stays bounded across distinct concrete paths.
    internal ContractSchemaRegistry SchemaRegistry => _schemaRegistry;

    /// <summary>
    /// Indicates whether the given operation declares a streaming media type
    /// (<c>text/event-stream</c>) for its request or response content. Such operations cannot be
    /// buffered and schema-validated, so the middleware skips them rather than failing.
    /// </summary>
    /// <param name="operation">The matched operation.</param>
    /// <returns><see langword="true"/> if the operation is streaming and validation must be skipped.</returns>
    public bool IsStreamingOperation(OpenApiOperation operation) =>
        _streamingOperations.Contains(operation);

    /// <summary>
    /// Resolves the <see cref="OpenApiOperation"/> (if any) that handles
    /// <paramref name="method"/> on <paramref name="path"/>, using the contract's path templates as the
    /// sole source of truth for matching.
    /// </summary>
    /// <param name="method">The HTTP method (e.g. "GET").</param>
    /// <param name="path">The URL-decoded request path.</param>
    /// <param name="operation">
    /// When this method returns, the matched operation, or <see langword="null"/> when the path exists
    /// but the method is not documented, or when no path matches at all.
    /// </param>
    /// <param name="pathParameters">
    /// When the path matches, the captured path parameters keyed by their OpenAPI name; otherwise an
    /// empty dictionary.
    /// </param>
    /// <param name="pathExists">
    /// <see langword="true"/> when the path matched a template (regardless of whether the method is
    /// documented); <see langword="false"/> when no template matched.
    /// </param>
    /// <returns>Always <see langword="true"/>; callers should inspect the out parameters.</returns>
    public bool TryResolveOperation(
        string method,
        string path,
        out OpenApiOperation? operation,
        out IReadOnlyDictionary<string, string> pathParameters,
        out bool pathExists
    )
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(path);

        if (!_pathMatcher.TryMatch(path, out var template, out pathParameters))
        {
            operation = null;
            pathExists = false;
            return true;
        }

        pathExists = true;
        operation = null;

        if (template is null)
        {
            return true;
        }

        if (!_document.Paths.TryGetValue(template.Template, out var pathItem) || pathItem is null)
        {
            return true;
        }

        var operations = pathItem.Operations;
        if (operations is null || operations.Count == 0)
        {
            return true;
        }

        foreach (var pair in operations)
        {
            if (
                pair.Value is not null
                && string.Equals(pair.Key.Method, method, StringComparison.OrdinalIgnoreCase)
            )
            {
                operation = pair.Value;
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates <paramref name="request"/> against <paramref name="operation"/> using the cached
    /// <see cref="RequestValidator"/>.
    /// </summary>
    public ValidationResult ValidateRequest(
        OpenApiOperation operation,
        ParsedRequest request,
        IReadOnlyDictionary<string, string> pathParameters
    ) => _requestValidator.Validate(operation, request, pathParameters, GetOperationKey(operation));

    /// <summary>
    /// Validates <paramref name="response"/> against <paramref name="operation"/> using the cached
    /// <see cref="ResponseValidator"/>.
    /// </summary>
    public ValidationResult ValidateResponse(OpenApiOperation operation, ParsedResponse response) =>
        _responseValidator.Validate(operation, response, GetOperationKey(operation));

    // Stable per-operation cache-key identity ("{method} {pathTemplate}") resolved by reference.
    // Operations always originate from TryResolveOperation (i.e. this document), so the lookup
    // hits; the fallback only guards a caller passing a foreign operation instance.
    private string GetOperationKey(OpenApiOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return _operationKeys.TryGetValue(operation, out var key)
            ? key
            : operation.OperationId ?? "op";
    }

    private static Dictionary<OpenApiOperation, string> BuildOperationKeys(OpenApiDocument document)
    {
        var keys = new Dictionary<OpenApiOperation, string>(ReferenceEqualityComparer.Instance);

        foreach (var pathPair in document.Paths)
        {
            var operations = pathPair.Value?.Operations;
            if (operations is null)
            {
                continue;
            }

            foreach (var operationPair in operations)
            {
                if (operationPair.Value is null)
                {
                    continue;
                }

                keys[operationPair.Value] = $"{operationPair.Key.Method} {pathPair.Key}";
            }
        }

        return keys;
    }

    private static OpenApiDocument LoadDocument(OpenApiValidationOptions options)
    {
        var configured =
            (options.ContractFilePath is not null ? 1 : 0)
            + (options.ContractStream is not null ? 1 : 0)
            + (options.ContractText is not null ? 1 : 0);

        if (configured == 0)
        {
            throw new OpenApiContractValidationException(
                ContractPhase.Startup,
                SingleViolation(
                    "document",
                    "No OpenAPI contract source is configured. Set ContractFilePath, ContractStream or ContractText."
                )
            );
        }

        if (configured > 1)
        {
            throw new OpenApiContractValidationException(
                ContractPhase.Startup,
                SingleViolation(
                    "document",
                    "Multiple OpenAPI contract sources are configured. Set exactly one of ContractFilePath, ContractStream or ContractText."
                )
            );
        }

        var loader = new OpenApiDocumentLoader();

        if (options.ContractFilePath is not null)
        {
            return loader.LoadFromFile(options.ContractFilePath);
        }

        if (options.ContractStream is not null)
        {
            return loader.LoadFromStream(options.ContractStream, options.ContractFormat);
        }

        return loader.LoadFromText(options.ContractText!, options.ContractFormat);
    }

    /// <summary>
    /// Collects the operations whose declared request or response content uses the streaming media type
    /// <c>text/event-stream</c>. Streaming bodies cannot be buffered whole and therefore cannot be
    /// schema-validated, so the middleware skips these operations rather than failing the request.
    /// </summary>
    private static HashSet<OpenApiOperation> CollectStreamingOperations(OpenApiDocument document)
    {
        var streaming = new HashSet<OpenApiOperation>();

        foreach (var pathPair in document.Paths)
        {
            if (pathPair.Value?.Operations is null)
            {
                continue;
            }

            foreach (var operationPair in pathPair.Value.Operations)
            {
                var operation = operationPair.Value;
                if (operation is null)
                {
                    continue;
                }

                if (HasStreamingContent(operation.RequestBody?.Content?.Keys))
                {
                    streaming.Add(operation);
                    continue;
                }

                if (operation.Responses is null)
                {
                    continue;
                }

                foreach (var responsePair in operation.Responses)
                {
                    if (HasStreamingContent(responsePair.Value?.Content?.Keys))
                    {
                        streaming.Add(operation);
                        break;
                    }
                }
            }
        }

        return streaming;
    }

    private static bool HasStreamingContent(IEnumerable<string?>? mediaTypeKeys)
    {
        if (mediaTypeKeys is null)
        {
            return false;
        }

        foreach (var key in mediaTypeKeys)
        {
            if (
                key is not null
                && key.StartsWith(StreamingMediaType, StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ContractViolation> SingleViolation(
        string location,
        string message
    ) =>
        new[]
        {
            new ContractViolation(
                Location: location,
                InstanceLocation: null,
                Keyword: null,
                Expected: null,
                Actual: null,
                Message: message
            ),
        };
}
