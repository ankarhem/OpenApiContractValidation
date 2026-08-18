using System.IO;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using Microsoft.OpenApi.YamlReader;
using OpenApiContractValidation.Errors;
using OpenApiContractValidation.Models;

namespace OpenApiContractValidation.Loader;

/// <summary>
/// Loads OpenAPI documents (3.0.x and 3.1.x) from text, files or streams, supporting
/// both JSON and YAML. Any parse or structural diagnostic error is surfaced as an
/// <see cref="OpenApiContractValidationException"/> in the
/// <see cref="ContractPhase.Startup"/> phase.
/// </summary>
public sealed class OpenApiDocumentLoader
{
    /// <summary>
    /// Parses an OpenAPI document from a string.
    /// </summary>
    /// <param name="text">The JSON or YAML text of the OpenAPI document.</param>
    /// <param name="format">
    /// The format of the document ("json" or "yaml"). When <see langword="null"/>, the
    /// reader auto-detects the format.
    /// </param>
    /// <returns>The parsed <see cref="OpenApiDocument"/>.</returns>
    /// <exception cref="OpenApiContractValidationException">
    /// Thrown when the document cannot be parsed, reports diagnostic errors, or is
    /// missing required OpenAPI structure (version or <c>info</c>).
    /// </exception>
    public OpenApiDocument LoadFromText(string text, string? format = null)
    {
        var readResult = OpenApiDocument.Parse(text, format, CreateSettings());
        return Validate(readResult.Document, readResult.Diagnostic);
    }

    /// <summary>
    /// Loads an OpenAPI document from a file path. The format is inferred from the file
    /// extension (<c>.yaml</c>/<c>.yml</c> -&gt; YAML, otherwise JSON).
    /// </summary>
    /// <param name="path">The absolute path to the OpenAPI document file.</param>
    /// <returns>The parsed <see cref="OpenApiDocument"/>.</returns>
    /// <exception cref="OpenApiContractValidationException">
    /// Thrown when the file cannot be read or the document is invalid.
    /// </exception>
    public OpenApiDocument LoadFromFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
            when (ex is FileNotFoundException
                || ex is DirectoryNotFoundException
                || ex is UnauthorizedAccessException
                || ex.GetType() == typeof(IOException)
            )
        {
            throw new OpenApiContractValidationException(
                ContractPhase.Startup,
                httpMethod: null,
                path,
                new List<ContractViolation>
                {
                    new ContractViolation(
                        Location: "contractFile",
                        InstanceLocation: null,
                        Keyword: null,
                        Expected: null,
                        Actual: null,
                        Message: "the contract file could not be read."
                    ),
                },
                ex
            );
        }

        return LoadFromText(text, InferFormatFromExtension(path));
    }

    /// <summary>
    /// Loads an OpenAPI document from a stream.
    /// </summary>
    /// <param name="stream">A stream containing the JSON or YAML document.</param>
    /// <param name="format">
    /// The format of the document ("json" or "yaml"). When <see langword="null"/>, the
    /// reader auto-detects the format.
    /// </param>
    /// <returns>The parsed <see cref="OpenApiDocument"/>.</returns>
    /// <exception cref="OpenApiContractValidationException">
    /// Thrown when the document cannot be parsed or is invalid.
    /// </exception>
    public OpenApiDocument LoadFromStream(Stream stream, string? format = null)
    {
        // leaveOpen: the stream is owned by the caller (options.ContractStream); the loader
        // must not dispose it.
        using var reader = new StreamReader(stream, leaveOpen: true);
        var text = reader.ReadToEnd();
        return LoadFromText(text, format);
    }

    private static OpenApiReaderSettings CreateSettings()
    {
        var settings = new OpenApiReaderSettings();
        settings.AddYamlReader();
        return settings;
    }

    private static string? InferFormatFromExtension(string path)
    {
        var extension = Path.GetExtension(path);
        return
            extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
            ? OpenApiConstants.Yaml
            : OpenApiConstants.Json;
    }

    private static OpenApiDocument Validate(
        OpenApiDocument? document,
        OpenApiDiagnostic? diagnostic
    )
    {
        var violations = new List<ContractViolation>();

        if (diagnostic is not null && diagnostic.Errors.Count > 0)
        {
            foreach (var error in diagnostic.Errors)
            {
                violations.Add(
                    new ContractViolation(
                        Location: "document",
                        InstanceLocation: error.Pointer,
                        Keyword: null,
                        Expected: null,
                        Actual: null,
                        Message: error.Message
                    )
                );
            }
        }

        if (document is null)
        {
            if (violations.Count == 0)
            {
                violations.Add(
                    new ContractViolation(
                        Location: "document",
                        InstanceLocation: null,
                        Keyword: null,
                        Expected: null,
                        Actual: null,
                        Message: "the OpenAPI document could not be parsed."
                    )
                );
            }

            throw new OpenApiContractValidationException(ContractPhase.Startup, violations);
        }

        // Guard against documents that parse without explicit errors but are not valid
        // OpenAPI documents (e.g. an empty JSON object "{}").
        if (document.Info is null || string.IsNullOrWhiteSpace(document.Info.Version))
        {
            violations.Add(
                new ContractViolation(
                    Location: "document",
                    InstanceLocation: null,
                    Keyword: "required",
                    Expected: "info with a non-empty version",
                    Actual: null,
                    Message: "the OpenAPI document is missing the required 'info' section or version."
                )
            );
        }

        if (violations.Count > 0)
        {
            throw new OpenApiContractValidationException(ContractPhase.Startup, violations);
        }

        return document;
    }
}
