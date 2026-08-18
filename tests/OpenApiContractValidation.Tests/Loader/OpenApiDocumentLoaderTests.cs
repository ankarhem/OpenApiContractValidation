using System.IO;
using System.Text;
using Microsoft.OpenApi;
using OpenApiContractValidation.Errors;
using OpenApiContractValidation.Loader;
using OpenApiContractValidation.Models;
using Xunit;

namespace OpenApiContractValidation.Tests.Loader;

public class OpenApiDocumentLoaderTests
{
    /// <summary>Minimal OpenAPI 3.0.3 document in JSON with one path <c>/pets</c>.</summary>
    private const string Json30 = """
        {
          "openapi": "3.0.3",
          "info": { "title": "Pets API", "version": "1.0.0" },
          "paths": {
            "/pets": {
              "get": {
                "responses": { "200": { "description": "A list of pets." } }
              }
            }
          }
        }
        """;

    /// <summary>Minimal OpenAPI 3.1.0 document in YAML with one path <c>/items</c>.</summary>
    private const string Yaml31 = """
        openapi: 3.1.0
        info:
          title: Items API
          version: 1.0.0
        paths:
          /items:
            get:
              responses:
                '200':
                  description: A list of items.
        """;

    private readonly OpenApiDocumentLoader _loader = new();

    [Fact]
    public void LoadFromText_Json30_ParsesPaths()
    {
        var doc = _loader.LoadFromText(Json30, "json");

        Assert.NotNull(doc.Paths);
        Assert.True(doc.Paths!.ContainsKey("/pets"));
    }

    [Fact]
    public void LoadFromText_Yaml31_ParsesPaths()
    {
        var doc = _loader.LoadFromText(Yaml31, "yaml");

        Assert.NotNull(doc.Paths);
        Assert.True(doc.Paths!.ContainsKey("/items"));
    }

    [Fact]
    public void LoadFromFile_Yaml_Works()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"oapi-loader-{Guid.NewGuid():N}.yaml");
        try
        {
            File.WriteAllText(tempFile, Yaml31, Encoding.UTF8);

            var doc = _loader.LoadFromFile(tempFile);

            Assert.NotNull(doc.Paths);
            Assert.True(doc.Paths!.ContainsKey("/items"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void LoadFromStream_Json_Works()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Json30));

        var doc = _loader.LoadFromStream(stream, "json");

        Assert.NotNull(doc.Paths);
        Assert.True(doc.Paths!.ContainsKey("/pets"));
    }

    [Fact]
    public void Malformed_ThrowsStartup()
    {
        var ex = Assert.Throws<OpenApiContractValidationException>(() =>
            _loader.LoadFromText("{ this is not valid openapi", "json")
        );

        Assert.Equal(ContractPhase.Startup, ex.Phase);
        Assert.NotEmpty(ex.Violations);
    }

    [Fact]
    public void EmptyOrNonOpenApi_ThrowsStartup()
    {
        var ex = Assert.Throws<OpenApiContractValidationException>(() =>
            _loader.LoadFromText("{}", "json")
        );

        Assert.Equal(ContractPhase.Startup, ex.Phase);
        Assert.NotEmpty(ex.Violations);
    }

    [Fact]
    public void LoadFromText_AutoDetectFormat_Parses()
    {
        var doc = _loader.LoadFromText(Json30, format: null);

        Assert.NotNull(doc.Paths);
        Assert.True(doc.Paths!.ContainsKey("/pets"));
    }

    [Fact]
    public void LoadFromStream_Yaml_Works()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Yaml31));

        var doc = _loader.LoadFromStream(stream, "yaml");

        Assert.NotNull(doc.Paths);
        Assert.True(doc.Paths!.ContainsKey("/items"));
    }

    [Fact]
    public void LoadFromFile_YmlExtension_Works()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"oapi-loader-{Guid.NewGuid():N}.yml");
        try
        {
            File.WriteAllText(tempFile, Yaml31, Encoding.UTF8);

            var doc = _loader.LoadFromFile(tempFile);

            Assert.NotNull(doc.Paths);
            Assert.True(doc.Paths!.ContainsKey("/items"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void LoadFromFile_JsonExtension_Works()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"oapi-loader-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(tempFile, Json30, Encoding.UTF8);

            var doc = _loader.LoadFromFile(tempFile);

            Assert.NotNull(doc.Paths);
            Assert.True(doc.Paths!.ContainsKey("/pets"));
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void MalformedFromStream_ThrowsStartup()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{ this is not valid openapi"));

        var ex = Assert.Throws<OpenApiContractValidationException>(() =>
            _loader.LoadFromStream(stream, "json")
        );

        Assert.Equal(ContractPhase.Startup, ex.Phase);
        Assert.NotEmpty(ex.Violations);
    }

    [Fact]
    public void MissingInfo_GuardThrows()
    {
        // The document parses (non-null) but has no Info section, triggering the guard.
        const string noInfo = """{"openapi": "3.0.3", "paths": {}}""";

        var ex = Assert.Throws<OpenApiContractValidationException>(() =>
            _loader.LoadFromText(noInfo, "json")
        );

        Assert.Equal(ContractPhase.Startup, ex.Phase);
        Assert.Contains(ex.Violations, v => v.Keyword == "required");
        Assert.Contains(ex.Violations, v => v.Message != null && v.Message.Contains("info"));
    }

    [Fact]
    public void MissingVersion_GuardThrows()
    {
        const string noVersion = """{"openapi": "3.0.3", "info": {"title": "T"}, "paths": {}}""";

        var ex = Assert.Throws<OpenApiContractValidationException>(() =>
            _loader.LoadFromText(noVersion, "json")
        );

        Assert.Equal(ContractPhase.Startup, ex.Phase);
        Assert.Contains(ex.Violations, v => v.Keyword == "required");
    }

    [Fact]
    public void MalformedJsonArray_NullDocumentWithErrors_ThrowsStartup()
    {
        // Unreachable-code note: lines 115-126 (document is null && violations.Count == 0)
        // cannot be hit through the public API — Microsoft.OpenApi 2.9.0 always records
        // diagnostic errors when it fails to produce a document, and unparseable input
        // throws out of OpenApiDocument.Parse before Validate runs. "[" reaches Validate
        // with a null document and one diagnostic error, exercising the adjacent path.
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("["));

        var ex = Assert.Throws<OpenApiContractValidationException>(() =>
            _loader.LoadFromStream(stream, "json")
        );

        Assert.Equal(ContractPhase.Startup, ex.Phase);
        Assert.NotEmpty(ex.Violations);
        Assert.Contains(ex.Violations, v => v.Location == "document");
    }

    [Fact]
    public void LoadFromStream_AutoDetectFormat_Parses()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Json30));

        var doc = _loader.LoadFromStream(stream, format: null);

        Assert.NotNull(doc.Paths);
        Assert.True(doc.Paths!.ContainsKey("/pets"));
    }

    [Fact]
    public void LoadFromFile_MissingFile_ThrowsContractValidationExceptionWithStartupPhase()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        var ex = Assert.Throws<OpenApiContractValidationException>(() =>
            _loader.LoadFromFile(missingPath)
        );

        Assert.Equal(ContractPhase.Startup, ex.Phase);
        Assert.Contains(missingPath, ex.Message);
        Assert.IsType<FileNotFoundException>(ex.InnerException);
    }
}
