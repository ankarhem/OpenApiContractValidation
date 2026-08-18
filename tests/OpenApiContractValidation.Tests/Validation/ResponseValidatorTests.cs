using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using OpenApiContractValidation.Models;
using OpenApiContractValidation.Schema;
using OpenApiContractValidation.Validation;
using Xunit;

namespace OpenApiContractValidation.Tests.Validation;

public class ResponseValidatorTests
{
    /// <summary>
    /// Contract for <c>GET /widget</c> (200 with required <c>X-Rate-Limit</c> header + body schema
    /// carrying a <c>writeOnly</c> <c>secret</c>, 204 no content, and a <c>default</c> error shape)
    /// and <c>GET /strict</c> (only a "200" response, so non-2xx statuses are undocumented).
    /// </summary>
    private const string ContractJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/widget": {
              "get": {
                "operationId": "getWidget",
                "responses": {
                  "200": {
                    "description": "ok",
                    "headers": {
                      "X-Rate-Limit": { "required": true, "schema": { "type": "integer" } }
                    },
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "required": ["id"],
                          "properties": {
                            "id": { "type": "integer" },
                            "secret": { "type": "string", "writeOnly": true }
                          }
                        }
                      }
                    }
                  },
                  "204": { "description": "no content" },
                  "default": {
                    "description": "err",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "properties": { "error": { "type": "string" } }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/strict": {
              "get": {
                "operationId": "getStrict",
                "responses": {
                  "200": {
                    "description": "ok",
                    "headers": {
                      "X-Rate-Limit": { "required": true, "schema": { "type": "integer" } }
                    },
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "required": ["id"],
                          "properties": {
                            "id": { "type": "integer" },
                            "secret": { "type": "string", "writeOnly": true }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private static (ContractSchemaRegistry Registry, ResponseValidator Validator) Create()
    {
        var doc = LoadDocument();
        var registry = new ContractSchemaRegistry(doc);
        return (registry, new ResponseValidator(registry));
    }

    private static (ContractSchemaRegistry Registry, ResponseValidator Validator) CreateFrom(
        string contractJson
    )
    {
        var doc = LoadDocument(contractJson);
        var registry = new ContractSchemaRegistry(doc);
        return (registry, new ResponseValidator(registry));
    }

    private static OpenApiDocument LoadDocument(string json = ContractJson)
    {
        var read = OpenApiDocument.Parse(json, "json", new OpenApiReaderSettings());
        var diagnostic =
            read.Diagnostic ?? throw new InvalidOperationException("parser produced no diagnostic");
        Assert.Empty(diagnostic.Errors);
        return read.Document ?? throw new InvalidOperationException("parser produced no document");
    }

    private static OpenApiOperation GetOperation(string path, string json = ContractJson)
    {
        var paths = LoadDocument(json).Paths ?? throw new InvalidOperationException("no paths");
        var item = paths[path] ?? throw new InvalidOperationException($"path {path} not found");
        var operations = item.Operations ?? throw new InvalidOperationException("no operations");
        return operations[HttpMethod.Get];
    }

    /// <summary>Builds a minimal GET operation carrying a single response definition.</summary>
    private static OpenApiOperation OperationWithResponse(
        string operationId,
        OpenApiResponse response,
        string status = "200"
    )
    {
        return new OpenApiOperation
        {
            OperationId = operationId,
            Responses = new OpenApiResponses { [status] = response },
        };
    }

    private static ParsedResponse Build(
        int status,
        string? contentType = null,
        Dictionary<string, IReadOnlyList<string>>? headers = null,
        string? bodyJson = null
    )
    {
        JsonElement? body = null;
        var hasBody = false;
        if (bodyJson is not null)
        {
            body = JsonDocument.Parse(bodyJson).RootElement.Clone();
            hasBody = true;
        }

        return new ParsedResponse
        {
            StatusCode = status,
            ContentType = contentType,
            Headers = headers ?? new Dictionary<string, IReadOnlyList<string>>(),
            Body = body,
            RawBody = bodyJson,
            HasBody = hasBody,
        };
    }

    private static Dictionary<string, IReadOnlyList<string>> RateLimitHeader(int value) =>
        new() { ["X-Rate-Limit"] = new List<string> { value.ToString() } };

    [Fact]
    public void ValidResponse_Passes()
    {
        var (_, validator) = Create();
        var op = GetOperation("/widget");
        var response = Build(200, "application/json", RateLimitHeader(100), """{"id":1}""");

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid, "status 200 + content-type + header + body all conform");
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void UndocumentedStatus_Violation()
    {
        var (_, validator) = Create();
        var op = GetOperation("/strict");
        var response = Build(500, "application/json");

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "status");
    }

    [Fact]
    public void MissingRequiredResponseHeader_Violation()
    {
        var (_, validator) = Create();
        var op = GetOperation("/widget");
        var response = Build(200, "application/json", headers: new(), bodyJson: """{"id":1}""");

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "responseHeader/X-Rate-Limit");
    }

    [Fact]
    public void BadResponseBodySchema_Violation()
    {
        var (_, validator) = Create();
        var op = GetOperation("/widget");
        var response = Build(200, "application/json", RateLimitHeader(100), """{"id":"notint"}""");

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.InstanceLocation == "/id");
    }

    [Fact]
    public void WriteOnlyInResponse_Violation()
    {
        var (_, validator) = Create();
        var op = GetOperation("/widget");
        var response = Build(
            200,
            "application/json",
            RateLimitHeader(100),
            """{"id":1,"secret":"shh"}"""
        );

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Keyword == "writeOnly");
    }

    [Fact]
    public void Status204_WithBody_Violation()
    {
        var (_, validator) = Create();
        var op = GetOperation("/widget");
        var response = Build(204, bodyJson: """{"id":1}""");

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "responseBody");
    }

    [Fact]
    public void Status204_NoBody_Valid()
    {
        var (_, validator) = Create();
        var op = GetOperation("/widget");
        var response = Build(204);

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void DefaultStatusMatched_Valid()
    {
        var (_, validator) = Create();
        var op = GetOperation("/widget");
        var response = Build(503, "application/json", bodyJson: """{"error":"down"}""");

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void WrongResponseContentType_Violation()
    {
        var (_, validator) = Create();
        var op = GetOperation("/widget");
        var response = Build(200, "text/plain", RateLimitHeader(100), """{"id":1}""");

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "responseBody/contentType");
    }

    private static Dictionary<string, IReadOnlyList<string>> Headers(
        params (string Name, string Value)[] pairs
    )
    {
        var dict = new Dictionary<string, IReadOnlyList<string>>();
        foreach (var (name, value) in pairs)
        {
            dict[name] = new List<string> { value };
        }

        return dict;
    }

    [Fact]
    public void HeaderValueNotParseableAsDeclaredInteger_ReportsSchemaViolation()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-int-fail",
            new OpenApiResponse
            {
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["X-Num"] = new OpenApiHeader
                    {
                        Required = true,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
                    },
                },
            }
        );
        var response = Build(200, headers: Headers(("X-Num", "abc")));

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "responseHeader/X-Num");
    }

    [Fact]
    public void ResponseHeader_DeclaredInteger_ValueAboveIntMax_Passes()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-large-int",
            new OpenApiResponse
            {
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["X-Large-Id"] = new OpenApiHeader
                    {
                        Required = true,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
                    },
                },
            }
        );
        var response = Build(200, headers: Headers(("X-Large-Id", "3000000000")));

        var result = validator.Validate(op, response);

        Assert.True(
            result.IsValid,
            "integer response header above int.MaxValue should coerce to long and pass"
        );
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void ArrayHeaderValue_SplitsOnComma_AndValidates()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-array",
            new OpenApiResponse
            {
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["X-Tags"] = new OpenApiHeader
                    {
                        Required = true,
                        Schema = new OpenApiSchema
                        {
                            Type = JsonSchemaType.Array,
                            Items = new OpenApiSchema { Type = JsonSchemaType.String },
                        },
                    },
                },
            }
        );
        var response = Build(200, headers: Headers(("X-Tags", "a,b,c")));

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void NumberHeaderValue_CoercedAndValidates()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-number",
            new OpenApiResponse
            {
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["X-Score"] = new OpenApiHeader
                    {
                        Required = true,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Number },
                    },
                },
            }
        );
        var response = Build(200, headers: Headers(("X-Score", "3.14")));

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void BooleanHeaderValue_CoercedAndValidates()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-bool",
            new OpenApiResponse
            {
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["X-Flag"] = new OpenApiHeader
                    {
                        Required = true,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean },
                    },
                },
            }
        );
        var response = Build(200, headers: Headers(("X-Flag", "true")));

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void DeclaredHeaderWithoutSchema_Present_IsAccepted()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-noschema",
            new OpenApiResponse
            {
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["X-Bare"] = new OpenApiHeader { Required = true },
                },
            }
        );
        var response = Build(200, headers: Headers(("X-Bare", "anything")));

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void NullHeaderEntry_IsSkipped()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-nullentry",
            new OpenApiResponse
            {
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["Ghost"] = null!,
                    ["X-Real"] = new OpenApiHeader
                    {
                        Required = false,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    },
                },
            }
        );
        var response = Build(200, headers: Headers(("X-Real", "v")));

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void RequiredHeaderMissing_ButOtherHeadersPresent_Violation()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-findmiss",
            new OpenApiResponse
            {
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["X-Rate-Limit"] = new OpenApiHeader
                    {
                        Required = true,
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
                    },
                },
            }
        );
        var response = Build(200, headers: Headers(("Other-Header", "v")));

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "responseHeader/X-Rate-Limit");
    }

    [Fact]
    public void ResponseBody_ContentDeclaredWithoutSchema_IsAccepted()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-noschemabody",
            new OpenApiResponse
            {
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType(),
                },
            }
        );
        var response = Build(200, "application/json", bodyJson: """{"a":1}""");

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    private const string RefHeaderContractJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "components": { "schemas": { "HeaderInt": { "type": "integer" } } },
          "paths": {
            "/ref": {
              "get": {
                "operationId": "getRef",
                "responses": {
                  "200": {
                    "description": "ok",
                    "headers": {
                      "X-Ref": { "required": true, "schema": { "$ref": "#/components/schemas/HeaderInt" } }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    [Fact]
    public void HeaderSchemaReference_ResolvesAndValidates()
    {
        var (_, validator) = CreateFrom(RefHeaderContractJson);
        var op = GetOperation("/ref", RefHeaderContractJson);
        var response = Build(200, headers: Headers(("X-Ref", "42")));

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void NullOperationId_UsesDefaultCachePrefix_AndValidates()
    {
        var (_, validator) = Create();
        var op = new OpenApiOperation
        {
            OperationId = null,
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse
                {
                    Headers = new Dictionary<string, IOpenApiHeader>
                    {
                        ["X-Num"] = new OpenApiHeader
                        {
                            Required = true,
                            Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
                        },
                    },
                },
            },
        };
        var response = Build(200, headers: Headers(("X-Num", "7")));

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void HeaderWithUntypedSchema_AcceptsAnyScalar()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-untyped",
            new OpenApiResponse
            {
                Headers = new Dictionary<string, IOpenApiHeader>
                {
                    ["X-Free"] = new OpenApiHeader
                    {
                        Required = true,
                        Schema = new OpenApiSchema(),
                    },
                },
            }
        );
        var response = Build(200, headers: Headers(("X-Free", "anything")));

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void OperationWithNullResponses_ReportsStatusDrift()
    {
        var (_, validator) = Create();
        var op = new OpenApiOperation { OperationId = "h-noresponses" };
        var response = Build(200, "application/json");

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "status");
    }

    [Fact]
    public void ResponseBodyReturned_ButNoContentDocumented_Violation()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse("h-bodydrift", new OpenApiResponse());
        var response = Build(200, "application/json", bodyJson: """{"a":1}""");

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            v =>
                v.Location == "responseBody"
                && v.Message != null
                && v.Message.Contains("none is documented")
        );
    }

    [Fact]
    public void ResponseBodyReturned_ButContentMapEmpty_Violation()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-emptycontent",
            new OpenApiResponse { Content = new Dictionary<string, OpenApiMediaType>() }
        );
        var response = Build(200, "application/json", bodyJson: """{"a":1}""");

        var result = validator.Validate(op, response);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "responseBody");
    }

    [Fact]
    public void ResponseBody_HasBodyButBodyNull_SkipsSchemaValidation()
    {
        var (_, validator) = Create();
        var op = OperationWithResponse(
            "h-nullbody",
            new OpenApiResponse
            {
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
                    },
                },
            }
        );
        var response = new ParsedResponse
        {
            StatusCode = 200,
            ContentType = "application/json",
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            Body = null,
            RawBody = null,
            HasBody = true,
        };

        var result = validator.Validate(op, response);

        Assert.True(result.IsValid);
        Assert.Empty(result.Violations);
    }
}
