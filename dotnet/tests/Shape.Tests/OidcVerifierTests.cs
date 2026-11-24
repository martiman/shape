using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shape.Auth;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class OidcVerifierTests
{
    [Fact]
    public void JwkToRsa_round_trips_a_real_RSA_public_key()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        var jwk = new Dictionary<string, object?>
        {
            ["kty"] = "RSA",
            ["n"] = Base64UrlEncode(p.Modulus!),
            ["e"] = Base64UrlEncode(p.Exponent!)
        };

        using var imported = OidcVerifier.JwkToRsa(jwk);
        var importedParams = imported.ExportParameters(false);

        Assert.Equal(p.Modulus, importedParams.Modulus);
        Assert.Equal(p.Exponent, importedParams.Exponent);
    }

    [Fact]
    public void JwkToRsa_rejects_non_RSA_key()
    {
        var jwk = new Dictionary<string, object?> { ["kty"] = "EC", ["x"] = "...", ["y"] = "..." };
        var ex = Assert.Throws<ApiException>(() => OidcVerifier.JwkToRsa(jwk));
        Assert.Equal("AUTH_INVALID", ex.ApiCode);
    }

    [Fact]
    public void Base64UrlDecode_handles_unpadded_inputs()
    {
        // "hello" → base64url "aGVsbG8" (unpadded)
        var bytes = OidcVerifier.Base64UrlDecode("aGVsbG8");
        Assert.Equal("hello", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void MapClaims_uses_id_and_email_claims_from_config()
    {
        var verifier = new OidcVerifier(new OidcConfig
        {
            IdClaim = "oid",
            EmailClaim = "preferred_username",
            RoleClaim = "role"
        });

        var payload = new Dictionary<string, object?>
        {
            ["oid"] = "abc-123",
            ["preferred_username"] = "alice@corp.com",
            ["role"] = "admin",
            ["sub"] = "should-be-ignored"
        };

        var ctx = verifier.MapClaims(payload);

        Assert.Equal("abc-123", ctx["id"]);
        Assert.Equal("alice@corp.com", ctx["email"]);
        Assert.Equal("admin", ctx["role"]);
        Assert.Equal(true, ctx["_oidc"]);
    }

    [Fact]
    public void MapClaims_applies_role_mapping_for_string_claim()
    {
        var verifier = new OidcVerifier(new OidcConfig
        {
            RoleMapping = new() { ["BackendAdmin"] = "admin", ["BackendUser"] = "user" }
        });

        var ctx = verifier.MapClaims(new Dictionary<string, object?> { ["role"] = "BackendAdmin" });

        Assert.Equal("admin", ctx["role"]);
    }

    [Fact]
    public void MapClaims_with_role_array_picks_first_mapped_match()
    {
        var verifier = new OidcVerifier(new OidcConfig
        {
            RoleClaim = "roles",
            RoleMapping = new() { ["BackendAdmin"] = "admin" }
        });

        var ctx = verifier.MapClaims(new Dictionary<string, object?>
        {
            ["roles"] = new List<object?> { "OtherRole", "BackendAdmin", "Yet Another" }
        });

        Assert.Equal("admin", ctx["role"]);
    }

    [Fact]
    public void MapClaims_with_role_array_falls_back_to_first_string_when_nothing_maps()
    {
        var verifier = new OidcVerifier(new OidcConfig
        {
            RoleClaim = "roles",
            RoleMapping = new() { ["BackendAdmin"] = "admin" }
        });

        var ctx = verifier.MapClaims(new Dictionary<string, object?>
        {
            ["roles"] = new List<object?> { "Reader", "Writer" }
        });

        Assert.Equal("Reader", ctx["role"]);
    }

    [Fact]
    public void HandlesAlgorithm_default_is_RS256_only()
    {
        var verifier = new OidcVerifier(new OidcConfig());
        Assert.True(verifier.HandlesAlgorithm("RS256"));
        Assert.False(verifier.HandlesAlgorithm("RS384"));
        Assert.False(verifier.HandlesAlgorithm("HS256"));
        Assert.False(verifier.HandlesAlgorithm("none"));
    }

    [Fact]
    public void HandlesAlgorithm_respects_configured_whitelist()
    {
        var verifier = new OidcVerifier(new OidcConfig { Algorithms = new() { "RS256", "RS512" } });
        Assert.True(verifier.HandlesAlgorithm("RS256"));
        Assert.True(verifier.HandlesAlgorithm("RS512"));
        Assert.False(verifier.HandlesAlgorithm("RS384"));
    }

    [Fact]
    public void Verify_round_trips_a_self_signed_RS256_token()
    {
        // Build a JWKS in the cache with a key we control, sign a token, verify.
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        var kid = "test-key-1";

        var cacheDir = Path.Combine(Path.GetTempPath(), "shape_oidc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        try
        {
            var jwks = new Dictionary<string, object?>
            {
                ["keys"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["kty"] = "RSA",
                        ["kid"] = kid,
                        ["n"] = Base64UrlEncode(p.Modulus!),
                        ["e"] = Base64UrlEncode(p.Exponent!)
                    }
                }
            };
            var cacheFile = Path.Combine(cacheDir, Sha256Hex("https://idp.example.com/jwks") + ".json");
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(jwks));

            var config = new OidcConfig
            {
                JwksUri = "https://idp.example.com/jwks",
                Issuer = "https://idp.example.com",
                Audience = "shape-clients",
                JwksCacheDir = cacheDir,
                JwksCacheTtl = 3600
            };
            var verifier = new OidcVerifier(config);

            // Build a JWT
            var header = new Dictionary<string, object?> { ["alg"] = "RS256", ["kid"] = kid, ["typ"] = "JWT" };
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var payload = new Dictionary<string, object?>
            {
                ["sub"] = "user-7",
                ["iss"] = "https://idp.example.com",
                ["aud"] = "shape-clients",
                ["exp"] = now + 600,
                ["nbf"] = now - 60,
                ["email"] = "alice@corp.com",
                ["role"] = "admin"
            };
            var h64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
            var p64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
            var signingInput = Encoding.UTF8.GetBytes(h64 + "." + p64);
            var signature = rsa.SignData(signingInput, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var ctx = verifier.Verify(signingInput, signature, header, payload);

            Assert.Equal("user-7", ctx["id"]);
            Assert.Equal("alice@corp.com", ctx["email"]);
            Assert.Equal("admin", ctx["role"]);
            Assert.Equal(true, ctx["_oidc"]);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { /* cleanup best-effort */ }
        }
    }

    [Fact]
    public void Verify_rejects_bad_signature()
    {
        using var rsa = RSA.Create(2048);
        var p = rsa.ExportParameters(false);
        var kid = "test-key-bad";

        var cacheDir = Path.Combine(Path.GetTempPath(), "shape_oidc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        try
        {
            var jwks = new Dictionary<string, object?>
            {
                ["keys"] = new List<object?>
                {
                    new Dictionary<string, object?>
                    {
                        ["kty"] = "RSA",
                        ["kid"] = kid,
                        ["n"] = Base64UrlEncode(p.Modulus!),
                        ["e"] = Base64UrlEncode(p.Exponent!)
                    }
                }
            };
            var cacheFile = Path.Combine(cacheDir, Sha256Hex("https://idp.example.com/jwks") + ".json");
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(jwks));

            var verifier = new OidcVerifier(new OidcConfig
            {
                JwksUri = "https://idp.example.com/jwks",
                JwksCacheDir = cacheDir,
                JwksCacheTtl = 3600
            });

            var header = new Dictionary<string, object?> { ["alg"] = "RS256", ["kid"] = kid };
            var payload = new Dictionary<string, object?> { ["sub"] = "u" };
            var signingInput = Encoding.UTF8.GetBytes("not.really.signed");
            var fakeSignature = new byte[256]; // wrong bytes

            var ex = Assert.Throws<ApiException>(() => verifier.Verify(signingInput, fakeSignature, header, payload));
            Assert.Equal("AUTH_INVALID", ex.ApiCode);
        }
        finally
        {
            try { Directory.Delete(cacheDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Verify_rejects_disallowed_algorithm()
    {
        var verifier = new OidcVerifier(new OidcConfig { Algorithms = new() { "RS256" } });
        var header = new Dictionary<string, object?> { ["alg"] = "none" };

        var ex = Assert.Throws<ApiException>(() => verifier.Verify(
            Array.Empty<byte>(), Array.Empty<byte>(), header, new Dictionary<string, object?>()));

        Assert.Equal("AUTH_INVALID", ex.ApiCode);
        Assert.Contains("'none'", ex.Message);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string Sha256Hex(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
