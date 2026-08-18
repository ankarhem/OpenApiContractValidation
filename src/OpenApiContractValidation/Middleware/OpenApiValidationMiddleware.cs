using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.OpenApi;
using OpenApiContractValidation.Errors;
using OpenApiContractValidation.Models;
using OpenApiContractValidation.Options;

namespace OpenApiContractValidation.Middleware;

/// <summary>
/// The ASP.NET Core middleware that validates every incoming request and outgoing response against the
/// configured OpenAPI contract, throwing <see cref="OpenApiContractValidationException"/> on any
/// violation.
/// </summary>
/// <remarks>
/// <para>
/// It is the adapter that turns the framework-agnostic core (loader, schema registry, request/response
/// validators and path matcher) into a usable ASP.NET Core middleware. All heavy contract work is done
/// once by the injected <see cref="OpenApiContractValidator"/> singleton; per-request work is limited to
/// path resolution, body parsing and validation.
/// </para>
/// <para>
/// <b>Response buffering.</b> When response validation is enabled the middleware installs a
/// <see cref="HoldBackResponseBodyFeature"/> as the active
/// <see cref="IHttpResponseBodyFeature"/> before invoking the rest of the pipeline. Every byte written by
/// downstream application code is therefore captured in memory and <em>nothing reaches the client</em>
/// until validation passes. If validation fails the exception is thrown before the buffered body is
/// replayed, so an invalid response is never delivered. On success
/// <see cref="HoldBackResponseBodyFeature.CommitAsync"/> flushes the buffer to the real transport.
/// </para>
/// <para>
/// <b>Note on <c>Response.Body</c>.</b> The middleware installs the feature only and deliberately does
/// <em>not</em> assign <c>HttpContext.Response.Body</c>: in ASP.NET Core the <c>Body</c> setter, when the
/// active feature is not a <c>StreamResponseBodyFeature</c>, replaces the feature with a new
/// <c>StreamResponseBodyFeature</c> and would un-install the hold-back. Setting the feature alone is
/// sufficient because <c>HttpResponse.Body</c>/<c>BodyWriter</c> read through the active feature.
/// </para>
/// </remarks>
public sealed class OpenApiValidationMiddleware
{
    private const string JsonToken = "json";

    private readonly RequestDelegate _next;
    private readonly OpenApiContractValidator _validator;
    private readonly ILogger<OpenApiValidationMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiValidationMiddleware"/> class. The middleware
    /// is constructed once (singleton); the <see cref="OpenApiContractValidator"/> is injected from DI.
    /// </summary>
    /// <param name="next">The next delegate in the pipeline.</param>
    /// <param name="validator">The singleton that holds the loaded contract and validators.</param>
    /// <param name="logger">The logger for diagnostic messages.</param>
    public OpenApiValidationMiddleware(
        RequestDelegate next,
        OpenApiContractValidator validator,
        ILogger<OpenApiValidationMiddleware> logger
    )
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _validator = validator;
        _logger = logger;
    }

    /// <summary>
    /// Validates the request (and, when enabled, the buffered response) against the OpenAPI contract,
    /// throwing <see cref="OpenApiContractValidationException"/> on any violation.
    /// </summary>
    /// <param name="context">The current HTTP context.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var options = _validator.Options;
        var direction = options.Validate;

        if (direction == ValidationDirection.None)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var method = context.Request.Method;
        var path = context.Request.Path.Value ?? "/";

        // Always resolve the operation: even response-only validation needs it.
        _validator.TryResolveOperation(
            method,
            path,
            out var operation,
            out var pathParameters,
            out var pathExists
        );

        if (!pathExists)
        {
            HandleViolation(
                ContractPhase.Request,
                method,
                path,
                new[]
                {
                    new ContractViolation(
                        Location: "path",
                        InstanceLocation: null,
                        Keyword: null,
                        Expected: null,
                        Actual: path,
                        Message: $"path '{path}' is not documented in the OpenAPI contract."
                    ),
                }
            );

            // Reached only under the log policy: no operation to validate against, so pass through.
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (operation is null)
        {
            HandleViolation(
                ContractPhase.Request,
                method,
                path,
                new[]
                {
                    new ContractViolation(
                        Location: "path",
                        InstanceLocation: null,
                        Keyword: null,
                        Expected: null,
                        Actual: method,
                        Message: $"HTTP method '{method}' is not documented for path '{path}'."
                    ),
                }
            );

            await _next(context).ConfigureAwait(false);
            return;
        }

        // Streaming operations cannot be buffered/validated; pass them through untouched.
        if (_validator.IsStreamingOperation(operation))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if ((direction & ValidationDirection.Request) != 0)
        {
            var parsedRequest = await BuildParsedRequestAsync(
                    context,
                    method,
                    path,
                    options.MaxRequestBufferSizeBytes,
                    context.RequestAborted
                )
                .ConfigureAwait(false);

            if (parsedRequest is null)
            {
                // The body exceeded the buffer cap, so it was never parsed: this is distinct from
                // a parse or schema failure, and the body is not validated at all. Throw policy:
                // throws here, before _next, so no downstream handler reads the body. Log policy:
                // HandleViolation logs and the pipeline continues, with the rewound stream still
                // readable downstream (mirroring the response-cap pass-through semantics).
                var limit =
                    options.MaxRequestBufferSizeBytes.ToString(CultureInfo.InvariantCulture)
                    + " bytes";
                var totalBytes = Math.Max(
                    context.Request.ContentLength ?? 0,
                    options.MaxRequestBufferSizeBytes + 1
                );
                HandleViolation(
                    ContractPhase.Request,
                    method,
                    path,
                    new[]
                    {
                        new ContractViolation(
                            Location: "requestBody",
                            InstanceLocation: null,
                            Keyword: null,
                            Expected: limit,
                            Actual: totalBytes.ToString(CultureInfo.InvariantCulture) + " bytes",
                            Message: $"the request body exceeds the maximum buffer size ({limit}) and cannot be validated."
                        ),
                    }
                );
            }
            else
            {
                var requestResult = _validator.ValidateRequest(
                    operation,
                    parsedRequest,
                    pathParameters
                );
                if (!requestResult.IsValid)
                {
                    // Throw policy: throws. Log policy: logs and falls through to run the request anyway.
                    HandleViolation(ContractPhase.Request, method, path, requestResult.Violations);
                }
            }
        }

        if ((direction & ValidationDirection.Response) != 0)
        {
            await InvokeWithResponseCaptureAsync(context, operation, method, path)
                .ConfigureAwait(false);
        }
        else
        {
            await _next(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Installs the hold-back response body feature, invokes the rest of the pipeline, then validates the
    /// captured response. On validation failure the exception is thrown before the body is replayed; on
    /// success the buffer is committed to the real transport. The original feature is always restored in
    /// the <see langword="finally"/> so the pipeline is left clean even on throw.
    /// </summary>
    private async Task InvokeWithResponseCaptureAsync(
        HttpContext context,
        OpenApiOperation operation,
        string method,
        string path
    )
    {
        var options = _validator.Options;
        var originalFeature = context.Features.Get<IHttpResponseBodyFeature>();

        if (originalFeature is null)
        {
            // No response body feature means the host cannot capture a response; validate nothing and
            // fall through to keep the application functional.
            _logger.LogWarning(
                "No IHttpResponseBodyFeature is present on the context; response validation is skipped."
            );
            await _next(context).ConfigureAwait(false);
            return;
        }

        var holdback = new HoldBackResponseBodyFeature(
            originalFeature,
            options.MaxResponseBufferSizeBytes,
            throwOnCapExceeded: options.Handling == ViolationHandling.Throw
        );

        try
        {
            context.Features.Set<IHttpResponseBodyFeature>(holdback);

            await _next(context).ConfigureAwait(false);

            // The response streamed (DisableBuffering) or exceeded the buffer cap under the log policy:
            // it has already been written through to the client and cannot be validated. Skip.
            if (holdback.BufferingDisabled || holdback.CapExceeded)
            {
                _logger.LogDebug(
                    "Response for {Method} {Path} could not be buffered (streaming or over the size cap); validation skipped.",
                    method,
                    path
                );
                return;
            }

            var parsedResponse = BuildParsedResponse(context, holdback, method);
            var responseResult = _validator.ValidateResponse(operation, parsedResponse);
            if (!responseResult.IsValid)
            {
                // Throw policy: throws BEFORE committing, so the offending body never reaches the
                // client. Log policy: logs and falls through to commit the (invalid) response.
                HandleViolation(ContractPhase.Response, method, path, responseResult.Violations);
            }

            // Valid (or log policy): replay the buffered response to the real transport.
            await holdback.CommitAsync(context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            context.Features.Set(originalFeature);

            // The holdback (buffer + PipeWriter) is always disposed, even on the throw path.
            // Disposal is idempotent after CommitAsync; a disposal failure is logged rather
            // than thrown so it can never mask an in-flight contract violation exception.
            try
            {
                await holdback.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Disposing the response hold-back feature for {Method} {Path} failed.",
                    method,
                    path
                );
            }
        }
    }

    /// <summary>
    /// Applies the configured <see cref="ViolationHandling"/> policy to a detected violation: it always
    /// invokes <see cref="OpenApiValidationOptions.OnViolation"/> (when set), then either throws
    /// <see cref="OpenApiContractValidationException"/> (<see cref="ViolationHandling.Throw"/>) or logs
    /// and returns (<see cref="ViolationHandling.Log"/>), letting the caller continue.
    /// </summary>
    private void HandleViolation(
        ContractPhase phase,
        string method,
        string path,
        IReadOnlyList<ContractViolation> violations
    )
    {
        var exception = new OpenApiContractValidationException(phase, method, path, violations);

        // An observer exception must never replace the contract violation exception (Throw) or
        // break the request (Log): it is isolated and logged.
        try
        {
            _validator.Options.OnViolation?.Invoke(exception);
        }
        catch (Exception observerException)
        {
            _logger.LogWarning(
                observerException,
                "The configured OnViolation observer threw an exception for {Phase} {Method} {Path}; violation handling continues.",
                phase,
                method,
                path
            );
        }

        if (_validator.Options.Handling == ViolationHandling.Throw)
        {
            throw exception;
        }

        _logger.LogWarning(
            "OpenAPI contract violation during {Phase} for {Method} {Path}: {Message}",
            phase,
            method,
            path,
            exception.Message
        );
    }

    /// <summary>
    /// Reads and parses the incoming request body (when present and JSON), building a
    /// <see cref="ParsedRequest"/> suitable for the framework-agnostic validator. The body is
    /// read with a bounded, cancellation-aware read of at most <paramref name="maxBufferSizeBytes"/>
    /// bytes and the request stream is rewound so downstream handlers see an unread body.
    /// Returns <see langword="null"/> when the body exceeds the cap: the caller reports the
    /// violation and skips validation (the body itself is never parsed).
    /// </summary>
    private static async Task<ParsedRequest?> BuildParsedRequestAsync(
        HttpContext context,
        string method,
        string path,
        long maxBufferSizeBytes,
        CancellationToken cancellationToken
    )
    {
        var request = context.Request;

        string? rawBody = null;
        JsonElement? body = null;

        if (MayHaveBody(request))
        {
            request.EnableBuffering();

            rawBody = await ReadBodyBoundedAsync(
                    request.Body,
                    maxBufferSizeBytes,
                    cancellationToken
                )
                .ConfigureAwait(false);

            // Rewind in every case (under and over cap): under Throw nobody downstream runs, and
            // under Log downstream handlers must still be able to read the buffered body.
            request.Body.Position = 0;

            if (rawBody is null)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(rawBody) && IsJsonContentType(request.ContentType))
            {
                body = TryParseJson(rawBody);
            }
        }

        return new ParsedRequest
        {
            Method = method,
            Path = path,
            ContentType = request.ContentType,
            QueryValues = ToQueryValues(request.Query),
            Headers = ToHeaders(request.Headers),
            Cookies = ToCookies(request.Cookies),
            Body = body,
            RawBody = rawBody,
        };
    }

    /// <summary>
    /// Reads the body up to <paramref name="maxBufferSizeBytes"/> bytes and returns the decoded
    /// UTF-8 text. Returns <see langword="null"/> when the body exceeds the cap: the read stops
    /// as soon as one byte beyond the cap is observed, so a huge body is never buffered.
    /// </summary>
    private static async Task<string?> ReadBodyBoundedAsync(
        Stream body,
        long maxBufferSizeBytes,
        CancellationToken cancellationToken
    )
    {
        var buffer = new byte[81920];
        using var buffered = new MemoryStream();

        while (true)
        {
            var read = await body.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return Encoding.UTF8.GetString(buffered.ToArray());
            }

            buffered.Write(buffer, 0, read);
            if (buffered.Length > maxBufferSizeBytes)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Builds a <see cref="ParsedResponse"/> from the response status, headers and the bytes captured by
    /// the hold-back feature. HEAD responses never carry a body regardless of what was buffered.
    /// </summary>
    private static ParsedResponse BuildParsedResponse(
        HttpContext context,
        HoldBackResponseBodyFeature holdback,
        string method
    )
    {
        var response = context.Response;
        var isHead = HttpMethods.IsHead(method);
        var bufferedBytes = holdback.GetBufferedSpan();
        var hasBody = !isHead && bufferedBytes.Length > 0;

        string? rawBody = hasBody ? Encoding.UTF8.GetString(bufferedBytes) : null;
        JsonElement? body = null;

        if (hasBody && IsJsonContentType(response.ContentType))
        {
            body = TryParseJson(rawBody!);
        }

        return new ParsedResponse
        {
            StatusCode = response.StatusCode,
            ContentType = response.ContentType,
            Headers = ToHeaders(response.Headers),
            Body = body,
            RawBody = rawBody,
            HasBody = hasBody,
        };
    }

    private static bool MayHaveBody(HttpRequest request) =>
        request.ContentLength is > 0 || !string.IsNullOrEmpty(request.ContentType);

    private static bool IsJsonContentType(string? contentType) =>
        contentType is not null
        && contentType.Contains(JsonToken, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses JSON without throwing: a malformed body yields <see langword="null"/>, which the
    /// validators interpret — together with a matched JSON media type and a non-empty raw body —
    /// as a "body is not valid JSON" contract violation.
    /// </summary>
    private static JsonElement? TryParseJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            // Clone so the JsonElement outlives the disposed JsonDocument.
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies query parameters preserving each key's original casing; query names are matched
    /// case-sensitively per OpenAPI, so the dictionary uses the default ordinal comparer.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToQueryValues(
        IQueryCollection query
    )
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(query.Count);
        foreach (var (key, values) in query)
        {
            result[key] = ToStringList(values);
        }

        return result;
    }

    /// <summary>
    /// Copies headers keyed case-insensitively (RFC 9110). Header name matching in the validators is
    /// already case-insensitive, so this is the conventional choice.
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToHeaders(
        IHeaderDictionary headers
    )
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(
            headers.Count,
            StringComparer.OrdinalIgnoreCase
        );
        foreach (var (key, values) in headers)
        {
            result[key] = ToStringList(values);
        }

        return result;
    }

    /// <summary>
    /// Copies cookies keyed with the default ordinal comparer; cookie names are matched case-sensitively
    /// per OpenAPI.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ToCookies(IRequestCookieCollection cookies)
    {
        var result = new Dictionary<string, string>(cookies.Count);
        foreach (var (key, value) in cookies)
        {
            result[key] = value;
        }

        return result;
    }

    private static IReadOnlyList<string> ToStringList(StringValues values)
    {
        if (values.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (values.Count == 1)
        {
            return new[] { values.ToString() };
        }

        var list = new List<string>(values.Count);
        foreach (var value in values)
        {
            list.Add(value ?? string.Empty);
        }

        return list;
    }
}
