using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using Json.Schema;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Reader;
using OpenApiContractValidation.Models;
using OpenApiContractValidation.Schema;
using OpenApiContractValidation.Validation;
using Xunit;

namespace OpenApiContractValidation.Tests.Validation;

public class RequestValidatorTests
{
    /// <summary>
    /// Contract exercising query parameters (a required integer, an optional string enum)
    /// and a required JSON request body whose schema declares a required <c>name</c>
    /// and a <c>readOnly</c> integer <c>id</c>.
    /// </summary>
    private const string ContractJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/users": {
              "post": {
                "operationId": "createUser",
                "parameters": [
                  { "name": "userId", "in": "query", "required": true, "schema": { "type": "integer" } },
                  { "name": "status", "in": "query", "schema": { "type": "string", "enum": ["active","inactive"] } }
                ],
                "requestBody": {
                  "required": true,
                  "content": { "application/json": { "schema": { "$ref": "#/components/schemas/User" } } }
                },
                "responses": { "201": { "description": "ok" } }
              }
            }
          },
          "components": {
            "schemas": {
              "User": {
                "type": "object",
                "required": ["name"],
                "properties": {
                  "name": { "type": "string" },
                  "id": { "type": "integer", "readOnly": true }
                }
              }
            }
          }
        }
        """;

    private readonly OpenApiOperation _operation;
    private readonly RequestValidator _validator;
    private readonly IReadOnlyDictionary<string, string> _noPathParameters =
        new Dictionary<string, string>();

    public RequestValidatorTests()
    {
        var read = OpenApiDocument.Parse(ContractJson, "json", new OpenApiReaderSettings());
        var diagnostic =
            read.Diagnostic ?? throw new InvalidOperationException("parser produced no diagnostic");
        Assert.Empty(diagnostic.Errors);
        var doc =
            read.Document ?? throw new InvalidOperationException("parser produced no document");
        _operation = doc.Paths!["/users"].Operations![HttpMethod.Post];
        _validator = new RequestValidator(new ContractSchemaRegistry(doc));
    }

    /// <summary>Builds a POST /users <see cref="ParsedRequest"/> for the given inputs.</summary>
    private static ParsedRequest MakeRequest(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? query = null,
        string? contentType = null,
        string? bodyJson = null
    ) =>
        new()
        {
            Method = "POST",
            Path = "/users",
            ContentType = contentType,
            QueryValues = query ?? new Dictionary<string, IReadOnlyList<string>>(),
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            Cookies = new Dictionary<string, string>(),
            Body = bodyJson is null ? null : JsonDocument.Parse(bodyJson).RootElement,
            RawBody = bodyJson,
        };

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Query(
        string name,
        string value
    ) => new Dictionary<string, IReadOnlyList<string>> { [name] = new List<string> { value } };

    [Fact]
    public void ValidRequest_Passes()
    {
        var request = MakeRequest(
            query: Query("userId", "5"),
            contentType: "application/json",
            bodyJson: """{"name":"x"}"""
        );

        var result = _validator.Validate(_operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void MissingRequiredQueryParam_Violation()
    {
        // userId omitted (it is required); body is valid so the only violation is the query param.
        var request = MakeRequest(contentType: "application/json", bodyJson: """{"name":"x"}""");

        var result = _validator.Validate(_operation, request, _noPathParameters);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "query/userId");
    }

    [Fact]
    public void InvalidEnum_Violation()
    {
        var request = MakeRequest(
            query: new Dictionary<string, IReadOnlyList<string>>
            {
                ["userId"] = new List<string> { "5" },
                ["status"] = new List<string> { "bogus" },
            },
            contentType: "application/json",
            bodyJson: """{"name":"x"}"""
        );

        var result = _validator.Validate(_operation, request, _noPathParameters);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            v => v.Location == "query/status" && v.Keyword == "enum"
        );
    }

    [Fact]
    public void WrongRequestContentType_Violation()
    {
        var request = MakeRequest(
            query: Query("userId", "5"),
            contentType: "text/plain",
            bodyJson: """{"name":"x"}"""
        );

        var result = _validator.Validate(_operation, request, _noPathParameters);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            v =>
                v.Location.Contains("requestBody")
                && v.Location.Contains("contentType", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void RequiredBodyMissing_Violation()
    {
        // No body at all (Body is null) while requestBody.required is true.
        var request = MakeRequest(query: Query("userId", "5"));

        var result = _validator.Validate(_operation, request, _noPathParameters);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            v =>
                v.Location == "requestBody"
                && v.Message.Contains("required", StringComparison.OrdinalIgnoreCase)
        );
    }

    [Fact]
    public void BadBodySchema_Violation()
    {
        // name must be a string; supplying a number must surface a type violation at /name.
        var request = MakeRequest(
            query: Query("userId", "5"),
            contentType: "application/json",
            bodyJson: """{"name":123}"""
        );

        var result = _validator.Validate(_operation, request, _noPathParameters);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            v => v.Location == "requestBody" && v.InstanceLocation == "/name" && v.Keyword == "type"
        );
    }

    [Fact]
    public void ReadOnlyInRequest_Violation()
    {
        // 'id' is readOnly; its presence in a request body must be flagged.
        var request = MakeRequest(
            query: Query("userId", "5"),
            contentType: "application/json",
            bodyJson: """{"name":"x","id":7}"""
        );

        var result = _validator.Validate(_operation, request, _noPathParameters);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Keyword == "readOnly");
    }

    [Fact]
    public void QueryParamTypeMismatch_Violation()
    {
        // userId must be an integer; "abc" cannot be coerced to one.
        var request = MakeRequest(
            query: Query("userId", "abc"),
            contentType: "application/json",
            bodyJson: """{"name":"x"}"""
        );

        var result = _validator.Validate(_operation, request, _noPathParameters);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            v => v.Location == "query/userId" && v.Keyword == "type"
        );
    }

    private const string MultiParamJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/items/{id}": {
              "get": {
                "operationId": "getItem",
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } },
                  { "name": "X-Trace", "in": "header", "required": true, "schema": { "type": "integer" } },
                  { "name": "session", "in": "cookie", "required": true, "schema": { "type": "integer" } },
                  { "name": "price", "in": "query", "schema": { "type": "number" } },
                  { "name": "active", "in": "query", "schema": { "type": "boolean" } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    private const string DeepObjectJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/search": {
              "get": {
                "operationId": "search",
                "parameters": [
                  { "name": "filter", "in": "query", "required": true, "style": "deepObject", "schema": { "type": "object", "properties": { "q": { "type": "string" } } } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    private const string RefSchemaJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/ref/{n}": {
              "get": {
                "operationId": "refTest",
                "parameters": [
                  { "name": "n", "in": "path", "required": true, "schema": { "$ref": "#/components/schemas/RefInt" } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
          },
          "components": { "schemas": { "RefInt": { "type": "integer" } } }
        }
        """;

    private const string ContentParamJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/content": {
              "get": {
                "operationId": "contentParam",
                "parameters": [
                  { "name": "data", "in": "query", "content": { "application/json": { "schema": { "type": "object" } } } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    private static (OpenApiOperation Operation, RequestValidator Validator) ParseOperation(
        string json,
        string path,
        HttpMethod method
    )
    {
        var read = OpenApiDocument.Parse(json, "json", new OpenApiReaderSettings());
        var diagnostic =
            read.Diagnostic ?? throw new InvalidOperationException("parser produced no diagnostic");
        Assert.Empty(diagnostic.Errors);
        var doc =
            read.Document ?? throw new InvalidOperationException("parser produced no document");
        return (
            doc.Paths![path].Operations![method],
            new RequestValidator(new ContractSchemaRegistry(doc))
        );
    }

    private static ParsedRequest MakeGetRequest(
        string path,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? query = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? headers = null,
        IReadOnlyDictionary<string, string>? cookies = null
    ) =>
        new()
        {
            Method = "GET",
            Path = path,
            QueryValues = query ?? new Dictionary<string, IReadOnlyList<string>>(),
            Headers = headers ?? new Dictionary<string, IReadOnlyList<string>>(),
            Cookies = cookies ?? new Dictionary<string, string>(),
        };

    private static Dictionary<string, IReadOnlyList<string>> Multi(
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
    public void NullParameter_InList_IsSkipped()
    {
        _operation.Parameters!.Add(null!);

        var request = MakeRequest(
            query: Query("userId", "5"),
            contentType: "application/json",
            bodyJson: """{"name":"x"}"""
        );

        var result = _validator.Validate(_operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    [Fact]
    public void PathParameter_MissingFromDict_RequiredViolation()
    {
        var (operation, validator) = ParseOperation(MultiParamJson, "/items/{id}", HttpMethod.Get);
        var request = MakeGetRequest(
            "/items/1",
            headers: Multi(("X-Trace", "5")),
            cookies: new Dictionary<string, string> { ["session"] = "123" }
        );

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "path/id");
    }

    [Fact]
    public void PathParameter_Present_Valid()
    {
        var (operation, validator) = ParseOperation(MultiParamJson, "/items/{id}", HttpMethod.Get);
        var request = MakeGetRequest(
            "/items/1",
            headers: Multi(("x-trace", "5")),
            cookies: new Dictionary<string, string> { ["session"] = "123" }
        );
        var pathParams = new Dictionary<string, string> { ["id"] = "1" };

        var result = validator.Validate(operation, request, pathParams);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    [Fact]
    public void HeaderParameter_MissingRequired_Violation()
    {
        var (operation, validator) = ParseOperation(MultiParamJson, "/items/{id}", HttpMethod.Get);
        var request = MakeGetRequest(
            "/items/1",
            cookies: new Dictionary<string, string> { ["session"] = "123" }
        );
        var pathParams = new Dictionary<string, string> { ["id"] = "1" };

        var result = validator.Validate(operation, request, pathParams);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "header/X-Trace");
    }

    [Fact]
    public void HeaderParameter_PresentButInvalidType_Violation()
    {
        var (operation, validator) = ParseOperation(MultiParamJson, "/items/{id}", HttpMethod.Get);
        var request = MakeGetRequest(
            "/items/1",
            headers: Multi(("x-trace", "abc")),
            cookies: new Dictionary<string, string> { ["session"] = "123" }
        );
        var pathParams = new Dictionary<string, string> { ["id"] = "1" };

        var result = validator.Validate(operation, request, pathParams);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            v => v.Location == "header/X-Trace" && v.Keyword == "type"
        );
    }

    [Fact]
    public void CookieParameter_MissingRequired_Violation()
    {
        var (operation, validator) = ParseOperation(MultiParamJson, "/items/{id}", HttpMethod.Get);
        var request = MakeGetRequest("/items/1", headers: Multi(("X-Trace", "5")));
        var pathParams = new Dictionary<string, string> { ["id"] = "1" };

        var result = validator.Validate(operation, request, pathParams);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "cookie/session");
    }

    [Fact]
    public void CookieParameter_PresentButInvalidType_Violation()
    {
        var (operation, validator) = ParseOperation(MultiParamJson, "/items/{id}", HttpMethod.Get);
        var request = MakeGetRequest(
            "/items/1",
            headers: Multi(("X-Trace", "5")),
            cookies: new Dictionary<string, string> { ["session"] = "notanint" }
        );
        var pathParams = new Dictionary<string, string> { ["id"] = "1" };

        var result = validator.Validate(operation, request, pathParams);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            v => v.Location == "cookie/session" && v.Keyword == "type"
        );
    }

    [Fact]
    public void CookieParameter_PresentValid_Passes()
    {
        var (operation, validator) = ParseOperation(MultiParamJson, "/items/{id}", HttpMethod.Get);
        var request = MakeGetRequest(
            "/items/1",
            headers: Multi(("X-Trace", "5")),
            cookies: new Dictionary<string, string> { ["session"] = "123" }
        );
        var pathParams = new Dictionary<string, string> { ["id"] = "1" };

        var result = validator.Validate(operation, request, pathParams);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    [Fact]
    public void NumberAndBoolean_QueryCoercion_Valid()
    {
        var (operation, validator) = ParseOperation(MultiParamJson, "/items/{id}", HttpMethod.Get);
        var request = MakeGetRequest(
            "/items/1",
            query: Multi(("price", "3.14"), ("active", "true")),
            headers: Multi(("X-Trace", "5")),
            cookies: new Dictionary<string, string> { ["session"] = "123" }
        );
        var pathParams = new Dictionary<string, string> { ["id"] = "1" };

        var result = validator.Validate(operation, request, pathParams);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    [Fact]
    public void DeepObject_RequiredAbsent_Violation()
    {
        var (operation, validator) = ParseOperation(DeepObjectJson, "/search", HttpMethod.Get);
        var request = MakeGetRequest("/search");

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "query/filter");
    }

    [Fact]
    public void DeepObject_PresentValid_Passes()
    {
        var (operation, validator) = ParseOperation(DeepObjectJson, "/search", HttpMethod.Get);
        var request = MakeGetRequest(
            "/search",
            query: new Dictionary<string, IReadOnlyList<string>>
            {
                ["filter[q]"] = new List<string> { "hello" },
            }
        );

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    [Fact]
    public void RefSchema_PathParam_Valid()
    {
        var (operation, validator) = ParseOperation(RefSchemaJson, "/ref/{n}", HttpMethod.Get);
        var request = MakeGetRequest("/ref/42");
        var pathParams = new Dictionary<string, string> { ["n"] = "42" };

        var result = validator.Validate(operation, request, pathParams);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    [Fact]
    public void ContentParam_NullJsonNode_Skipped()
    {
        var (operation, validator) = ParseOperation(ContentParamJson, "/content", HttpMethod.Get);
        var request = MakeGetRequest("/content", query: Multi(("data", "null")));

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    [Fact]
    public void ContentParam_EmptyObject_SchemaNull_Skipped()
    {
        var (operation, validator) = ParseOperation(ContentParamJson, "/content", HttpMethod.Get);
        var request = MakeGetRequest("/content", query: Multi(("data", "{}")));

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    // Both a schema (for coercion) and a content map are set on this parameter:
    // content deserializes "42" to a native number, exercising CoerceScalar's
    // non-string early-return.
    [Fact]
    public void ContentParam_WithSchema_NativeScalar_NotReCoerced()
    {
        var parameter = new OpenApiParameter
        {
            Name = "data",
            In = ParameterLocation.Query,
            Schema = new OpenApiSchema { Type = JsonSchemaType.Integer },
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType(),
            },
        };
        var operation = new OpenApiOperation
        {
            OperationId = "dualContentScalar",
            Parameters = new List<IOpenApiParameter> { parameter },
        };
        var doc =
            OpenApiDocument.Parse(ContentParamJson, "json", new OpenApiReaderSettings()).Document
            ?? throw new InvalidOperationException("no doc");
        var validator = new RequestValidator(new ContractSchemaRegistry(doc));

        var request = MakeGetRequest("/content", query: Multi(("data", "42")));

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    // A JSON array containing a null element recurses into CoerceParameter with a
    // null node, exercising its top-level null guard. Schema and content are both set
    // so the content path produces native JSON (with nulls) while the schema path
    // drives coercion.
    [Fact]
    public void ContentParam_WithArraySchema_NullElement_NoThrow()
    {
        var parameter = new OpenApiParameter
        {
            Name = "data",
            In = ParameterLocation.Query,
            Schema = new OpenApiSchema
            {
                Type = JsonSchemaType.Array,
                Items = new OpenApiSchema { Type = JsonSchemaType.Integer },
            },
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["application/json"] = new OpenApiMediaType(),
            },
        };
        var operation = new OpenApiOperation
        {
            OperationId = "dualContentArray",
            Parameters = new List<IOpenApiParameter> { parameter },
        };
        var doc =
            OpenApiDocument.Parse(ContentParamJson, "json", new OpenApiReaderSettings()).Document
            ?? throw new InvalidOperationException("no doc");
        var validator = new RequestValidator(new ContractSchemaRegistry(doc));

        var request = MakeGetRequest("/content", query: Multi(("data", "[1, null, 3]")));

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void ContentParameter_MalformedJson_ProducesViolationNotException()
    {
        var (operation, validator) = ParseOperation(ContentParamJson, "/content", HttpMethod.Get);
        var request = MakeGetRequest("/content", query: Multi(("data", "{bad")));

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Violations,
            v =>
                v.Location == "query/data"
                && v.Message.Contains("not valid JSON", StringComparison.OrdinalIgnoreCase)
        );
    }

    private const string OptionalBodyJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/opt": {
              "post": {
                "operationId": "optBody",
                "requestBody": {
                  "required": false,
                  "content": { "application/json": { "schema": { "type": "string" } } }
                },
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    [Fact]
    public void OptionalBody_Absent_NoViolation()
    {
        var (operation, validator) = ParseOperation(OptionalBodyJson, "/opt", HttpMethod.Post);
        var request = new ParsedRequest
        {
            Method = "POST",
            Path = "/opt",
            QueryValues = new Dictionary<string, IReadOnlyList<string>>(),
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            Cookies = new Dictionary<string, string>(),
        };

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    private const string NoContentBodyJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/nocontent": {
              "post": {
                "operationId": "noContent",
                "requestBody": {
                  "required": false,
                  "content": {}
                },
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    [Fact]
    public void Body_NoContentMap_BodyPresent_NoViolation()
    {
        var (operation, validator) = ParseOperation(
            NoContentBodyJson,
            "/nocontent",
            HttpMethod.Post
        );
        var request = new ParsedRequest
        {
            Method = "POST",
            Path = "/nocontent",
            QueryValues = new Dictionary<string, IReadOnlyList<string>>(),
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            Cookies = new Dictionary<string, string>(),
            ContentType = "application/json",
            Body = JsonDocument.Parse("\"hello\"").RootElement,
            RawBody = "\"hello\"",
        };

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    private const string NoSchemaMediaJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/noschema": {
              "post": {
                "operationId": "noSchema",
                "requestBody": {
                  "required": false,
                  "content": { "application/json": {} }
                },
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    [Fact]
    public void Body_MatchedContentType_NoSchema_NoViolation()
    {
        var (operation, validator) = ParseOperation(
            NoSchemaMediaJson,
            "/noschema",
            HttpMethod.Post
        );
        var request = new ParsedRequest
        {
            Method = "POST",
            Path = "/noschema",
            QueryValues = new Dictionary<string, IReadOnlyList<string>>(),
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            Cookies = new Dictionary<string, string>(),
            ContentType = "application/json",
            Body = JsonDocument.Parse("\"hello\"").RootElement,
            RawBody = "\"hello\"",
        };

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    // A request carrying headers that DON'T match the declared parameter name forces
    // FindHeader to iterate all entries and fall through to its return-null path.
    [Fact]
    public void HeaderParameter_NonMatchingHeadersPresent_RequiredViolation()
    {
        var (operation, validator) = ParseOperation(MultiParamJson, "/items/{id}", HttpMethod.Get);
        var request = MakeGetRequest(
            "/items/1",
            headers: Multi(("X-Other", "5")),
            cookies: new Dictionary<string, string> { ["session"] = "123" }
        );
        var pathParams = new Dictionary<string, string> { ["id"] = "1" };

        var result = validator.Validate(operation, request, pathParams);

        Assert.False(result.IsValid);
        Assert.Contains(result.Violations, v => v.Location == "header/X-Trace");
    }

    // An unresolved $ref leaves RecursiveTarget null, forcing ResolveSchema's
    // cycle guard to break out of the unwrap loop.
    private const string UnresolvedRefJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/broken/{n}": {
              "get": {
                "operationId": "broken",
                "parameters": [
                  { "name": "n", "in": "path", "required": true, "schema": { "$ref": "#/components/schemas/Missing" } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    [Fact]
    public void RefSchema_Unresolved_ResolveSchemaBreaks()
    {
        var read = OpenApiDocument.Parse(UnresolvedRefJson, "json", new OpenApiReaderSettings());
        var doc = read.Document ?? throw new InvalidOperationException("no doc");
        var operation = doc.Paths!["/broken/{n}"].Operations![HttpMethod.Get];
        var validator = new RequestValidator(new ContractSchemaRegistry(doc));
        var request = MakeGetRequest("/broken/1");
        var pathParams = new Dictionary<string, string> { ["n"] = "1" };

        // ResolveSchema's cycle-guard break runs first; the unresolved $ref then
        // surfaces as a RefResolutionException during schema evaluation.
        Assert.Throws<RefResolutionException>(() =>
            validator.Validate(operation, request, pathParams)
        );
    }

    // Null OperationId falls back to request.Path; optional (Required=false) params
    // that are absent produce no violation. An object-schema query param without
    // deepObject style still enters the deepObject extraction path via IsObjectSchema.
    [Fact]
    public void OptionalParams_Absent_AndNullOperationId_NoViolation()
    {
        var operation = new OpenApiOperation
        {
            Parameters = new List<IOpenApiParameter>
            {
                new OpenApiParameter
                {
                    Name = "opt",
                    In = ParameterLocation.Header,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                },
                new OpenApiParameter
                {
                    Name = "opt",
                    In = ParameterLocation.Cookie,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                },
                new OpenApiParameter
                {
                    Name = "obj",
                    In = ParameterLocation.Query,
                    Style = ParameterStyle.Form,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Object },
                },
                new OpenApiParameter { Name = "nullschema", In = ParameterLocation.Query },
                new OpenApiParameter
                {
                    Name = "nulltype",
                    In = ParameterLocation.Query,
                    Schema = new OpenApiSchema(),
                },
            },
        };
        var doc =
            OpenApiDocument.Parse(ContentParamJson, "json", new OpenApiReaderSettings()).Document
            ?? throw new InvalidOperationException("no doc");
        var validator = new RequestValidator(new ContractSchemaRegistry(doc));
        var request = MakeGetRequest("/x");

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }

    // Parameters with null Name exercise the "??" fallback in every location branch.
    [Fact]
    public void NullNameParams_AllLocations_FallbackToQuestionMark()
    {
        var operation = new OpenApiOperation
        {
            OperationId = "nullNames",
            Parameters = new List<IOpenApiParameter>
            {
                new OpenApiParameter { Name = null, In = ParameterLocation.Path },
                new OpenApiParameter
                {
                    Name = null,
                    In = ParameterLocation.Query,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                },
                new OpenApiParameter
                {
                    Name = null,
                    In = ParameterLocation.Query,
                    Style = ParameterStyle.DeepObject,
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Object },
                },
                new OpenApiParameter { Name = null, In = ParameterLocation.Header },
                new OpenApiParameter { Name = null, In = ParameterLocation.Cookie },
            },
        };
        var doc =
            OpenApiDocument.Parse(ContentParamJson, "json", new OpenApiReaderSettings()).Document
            ?? throw new InvalidOperationException("no doc");
        var validator = new RequestValidator(new ContractSchemaRegistry(doc));
        var request = MakeGetRequest("/x");

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.Contains(result.Violations, v => v.Location == "path/?");
    }

    // Malformed bracket keys alongside a valid one exercise every false sub-branch
    // of the deepObject key-detection condition.
    [Fact]
    public void DeepObject_MalformedBracketKeys_AreSkipped()
    {
        var (operation, validator) = ParseOperation(DeepObjectJson, "/search", HttpMethod.Get);
        var request = MakeGetRequest(
            "/search",
            query: new Dictionary<string, IReadOnlyList<string>>
            {
                ["filter[q]"] = new List<string> { "hello" },
                ["other"] = new List<string> { "x" },
                ["filter["] = new List<string> { "x" },
                ["filter[x"] = new List<string> { "x" },
                ["filter[y]"] = new List<string>(),
            }
        );

        var result = validator.Validate(operation, request, _noPathParameters);

        Assert.True(result.IsValid, string.Join("; ", result.Violations.Select(v => v.ToString())));
    }
}
