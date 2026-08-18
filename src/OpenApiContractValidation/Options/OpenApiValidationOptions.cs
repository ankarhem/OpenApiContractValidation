using System.IO;
using OpenApiContractValidation.Errors;

namespace OpenApiContractValidation.Options;

/// <summary>
/// Configures how and where the OpenAPI contract is sourced and which directions are validated.
/// </summary>
public sealed class OpenApiValidationOptions
{
    /// <summary>File system path to the OpenAPI contract, when sourcing from disk.</summary>
    public string? ContractFilePath { get; set; }

    /// <summary>A stream providing the OpenAPI contract, when sourcing from a stream.</summary>
    public Stream? ContractStream { get; set; }

    /// <summary>The raw OpenAPI contract text, when sourcing inline.</summary>
    public string? ContractText { get; set; }

    /// <summary>A hint ("json" or "yaml") describing the contract format, when known.</summary>
    public string? ContractFormat { get; set; }

    /// <summary>
    /// The maximum number of bytes buffered while capturing a response body for validation.
    /// Defaults to 10 MiB.
    /// </summary>
    public long MaxResponseBufferSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// The maximum number of bytes buffered while reading a request body for validation.
    /// Defaults to 10 MiB.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request body larger than the cap is never parsed or validated (distinct from a parse
    /// or schema failure). Under <see cref="ViolationHandling.Throw"/> a catchable
    /// <see cref="OpenApiContractValidationException"/> is raised before the rest of the
    /// pipeline runs, so the request body is not validated and no downstream handler reads it.
    /// Under <see cref="ViolationHandling.Log"/> the violation is logged and the body is
    /// skipped for validation while the request proceeds normally: the read position is
    /// rewound to zero after the bounded read, so downstream handlers still see the buffered
    /// body from the start and any remainder streams through on demand.
    /// </para>
    /// </remarks>
    public long MaxRequestBufferSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Which directions (request and/or response) are validated.</summary>
    public ValidationDirection Validate { get; set; } = ValidationDirection.Both;

    /// <summary>
    /// What to do when a violation is detected: <see cref="ViolationHandling.Throw"/> (default) or
    /// <see cref="ViolationHandling.Log"/>.
    /// </summary>
    public ViolationHandling Handling { get; set; } = ViolationHandling.Throw;

    /// <summary>
    /// An optional observer invoked for every detected violation, regardless of <see cref="Handling"/>,
    /// before the handling policy is applied. Use it to log, emit metrics, or inspect violations. It does
    /// not affect whether the middleware throws.
    /// </summary>
    public Action<OpenApiContractValidationException>? OnViolation { get; set; }
}
