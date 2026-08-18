using System.Collections.Generic;
using System.Text.Json;
using OpenApiContractValidation.Middleware;
using OpenApiContractValidation.Models;
using OpenApiContractValidation.Options;
using Xunit;

namespace OpenApiContractValidation.Tests.Schema;

/// <summary>
/// Regression tests for schema-cache key identity. Both bugs share one root cause: the
/// per-operation cache prefix was derived from <c>operationId</c> (or the concrete request
/// path) instead of a stable per-operation identity. Specs that omit <c>operationId</c>
/// therefore either collided (distinct operations sharing status+mediaType reused the first
/// operation's compiled schema) or grew the cache unboundedly (one entry per concrete path).
/// </summary>
public class SchemaCacheIdentityTests
{
    private static OpenApiContractValidator Create(string contractText) =>
        new(
            Microsoft.Extensions.Options.Options.Create(
                new OpenApiValidationOptions
                {
                    ContractText = contractText,
                    ContractFormat = "json",
                }
            )
        );

    private static ParsedResponse JsonResponse(string bodyJson) =>
        new()
        {
            StatusCode = 200,
            ContentType = "application/json",
            Headers = new Dictionary<string, IReadOnlyList<string>>(),
            Body = JsonDocument.Parse(bodyJson).RootElement.Clone(),
            RawBody = bodyJson,
            HasBody = true,
        };

    /// <summary>
    /// Two operationId-less operations that share status + media type but declare DIFFERENT
    /// schemas must each validate against their own schema. Pre-fix both collapsed to the
    /// cache prefix "op", so the second operation reused the first operation's compiled schema.
    /// </summary>
    private const string TwoOpsNoOperationIdJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/a": {
              "get": {
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "required": ["alpha"],
                          "properties": { "alpha": { "type": "integer" } }
                        }
                      }
                    }
                  }
                }
              }
            },
            "/b": {
              "get": {
                "responses": {
                  "200": {
                    "description": "ok",
                    "content": {
                      "application/json": {
                        "schema": {
                          "type": "object",
                          "required": ["beta"],
                          "properties": { "beta": { "type": "integer" } }
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

    [Fact]
    public void SchemaCache_OperationIdlessSpec_DistinctOperationsUseOwnSchemas()
    {
        var validator = Create(TwoOpsNoOperationIdJson);
        validator.TryResolveOperation("GET", "/a", out var opA, out _, out _);
        validator.TryResolveOperation("GET", "/b", out var opB, out _, out _);
        Assert.NotNull(opA);
        Assert.NotNull(opB);

        // Seed the cache with /a's schema first, so a colliding key would return alpha for /b.
        var aValid = validator.ValidateResponse(opA!, JsonResponse("""{"alpha":1}"""));
        Assert.True(aValid.IsValid, "a body valid for /a must pass on /a");

        // Pre-fix: /b reuses /a's cached alpha schema and wrongly rejects a valid /b body.
        var bValid = validator.ValidateResponse(opB!, JsonResponse("""{"beta":1}"""));
        Assert.True(
            bValid.IsValid,
            "a body valid for /b must pass on /b (no cross-operation schema collision)"
        );

        // No false accepts: a body invalid for its own operation must still be rejected.
        var bInvalid = validator.ValidateResponse(opB!, JsonResponse("""{"alpha":1}"""));
        Assert.False(bInvalid.IsValid, "a /b body missing required 'beta' must be rejected");

        var aInvalid = validator.ValidateResponse(opA!, JsonResponse("""{"beta":1}"""));
        Assert.False(aInvalid.IsValid, "an /a body missing required 'alpha' must be rejected");
    }

    private const string PathParamNoOperationIdJson = """
        {
          "openapi": "3.1.0",
          "info": { "title": "t", "version": "1" },
          "paths": {
            "/items/{id}": {
              "get": {
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

    [Fact]
    public void SchemaCache_PathParamWithoutOperationId_RegistryCountStable()
    {
        var validator = Create(PathParamNoOperationIdJson);

        var countAfterFirst = 0;
        for (var i = 1; i <= 5; i++)
        {
            var path = $"/items/{i}";
            validator.TryResolveOperation(
                "GET",
                path,
                out var operation,
                out var pathParameters,
                out _
            );
            Assert.NotNull(operation);

            var request = new ParsedRequest
            {
                Method = "GET",
                Path = path,
                QueryValues = new Dictionary<string, IReadOnlyList<string>>(),
                Headers = new Dictionary<string, IReadOnlyList<string>>(),
                Cookies = new Dictionary<string, string>(),
            };

            validator.ValidateRequest(operation!, request, pathParameters);

            if (i == 1)
            {
                countAfterFirst = validator.SchemaRegistry.CachedSchemaCount;
            }
        }

        var countAfterFifth = validator.SchemaRegistry.CachedSchemaCount;

        Assert.Equal(countAfterFirst, countAfterFifth);
    }
}
