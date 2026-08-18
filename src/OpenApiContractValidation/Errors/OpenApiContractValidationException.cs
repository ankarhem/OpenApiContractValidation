using System.Text;
using OpenApiContractValidation.Models;

namespace OpenApiContractValidation.Errors;

/// <summary>
/// Thrown when an HTTP request or response violates the configured OpenAPI contract.
/// </summary>
public sealed class OpenApiContractValidationException : Exception
{
    private const string Prefix = "OpenAPI contract violation";

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiContractValidationException"/> class.
    /// </summary>
    public OpenApiContractValidationException(
        ContractPhase phase,
        string? httpMethod,
        string? path,
        IReadOnlyList<ContractViolation> violations
    )
        : this(phase, httpMethod, path, violations, innerException: null) { }

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiContractValidationException"/> class
    /// with an inner exception.
    /// </summary>
    public OpenApiContractValidationException(
        ContractPhase phase,
        string? httpMethod,
        string? path,
        IReadOnlyList<ContractViolation> violations,
        Exception? innerException
    )
        : base(BuildMessage(phase, httpMethod, path, violations), innerException)
    {
        Phase = phase;
        HttpMethod = httpMethod;
        Path = path;
        Violations = violations;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenApiContractValidationException"/> class
    /// without an associated HTTP method or path.
    /// </summary>
    public OpenApiContractValidationException(
        ContractPhase phase,
        IReadOnlyList<ContractViolation> violations
    )
        : this(phase, httpMethod: null, path: null, violations) { }

    /// <summary>The lifecycle phase during which the violation occurred.</summary>
    public ContractPhase Phase { get; }

    /// <summary>The HTTP method of the offending request, when available.</summary>
    public string? HttpMethod { get; }

    /// <summary>The request path, when available.</summary>
    public string? Path { get; }

    /// <summary>The contract violations that were detected.</summary>
    public IReadOnlyList<ContractViolation> Violations { get; }

    private static string BuildMessage(
        ContractPhase phase,
        string? httpMethod,
        string? path,
        IReadOnlyList<ContractViolation> violations
    )
    {
        var builder = new StringBuilder();
        builder
            .Append(Prefix)
            .Append(" during ")
            .Append(phase)
            .Append(" for ")
            .Append(httpMethod)
            .Append(' ')
            .Append(path)
            .Append(": ");

        for (var i = 0; i < violations.Count; i++)
        {
            if (i > 0)
            {
                builder.Append("; ");
            }

            var violation = violations[i];
            builder.Append(violation.Location).Append(": ").Append(violation.Message);
        }

        return builder.ToString();
    }
}
