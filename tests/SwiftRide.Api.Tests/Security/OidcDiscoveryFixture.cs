using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;

namespace SwiftRide.Api.Tests.Security;

public class OidcDiscoveryFixture : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly RSA _primaryRsa;
    private readonly string _primaryKeyId = Guid.NewGuid().ToString("N");
    private readonly List<(string Kid, RSA Key)> _keys = new();
    private string _authorityUrl = default!;

    public string AuthorityUrl => _authorityUrl;
    public string DefaultAudience => "swiftride-api";

    public OidcDiscoveryFixture()
    {
        _primaryRsa = RSA.Create(2048);
        _keys.Add((_primaryKeyId, _primaryRsa));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();

        _app.MapGet("/.well-known/openid-configuration", (HttpContext ctx) =>
        {
            var config = new
            {
                issuer = _authorityUrl,
                jwks_uri = $"{_authorityUrl}/.well-known/jwks.json",
                response_types_supported = new[] { "code", "token", "id_token" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { SecurityAlgorithms.RsaSha256 }
            };
            return Results.Json(config);
        });

        _app.MapGet("/.well-known/jwks.json", () =>
        {
            var jwkList = new List<object>();
            lock (_keys)
            {
                foreach (var (kid, rsaKey) in _keys)
                {
                    var parameters = rsaKey.ExportParameters(false);
                    jwkList.Add(new
                    {
                        kty = "RSA",
                        use = "sig",
                        alg = SecurityAlgorithms.RsaSha256,
                        kid = kid,
                        n = Base64UrlEncoder.Encode(parameters.Modulus),
                        e = Base64UrlEncoder.Encode(parameters.Exponent)
                    });
                }
            }

            return Results.Json(new { keys = jwkList });
        });
    }

    public async Task InitializeAsync()
    {
        await _app.StartAsync();
        _authorityUrl = _app.Urls.First().TrimEnd('/');
    }

    public string RotateKey()
    {
        var newRsa = RSA.Create(2048);
        var newKid = Guid.NewGuid().ToString("N");
        lock (_keys)
        {
            _keys.Add((newKid, newRsa));
        }
        return newKid;
    }

    public string CreateToken(
        string sub = "user-123",
        string role = "rider",
        string? issuer = null,
        string? audience = null,
        TimeSpan? lifetime = null,
        RSA? customKey = null,
        string? customKid = null)
    {
        RSA keyToUse;
        string kidToUse;

        if (customKey is not null)
        {
            keyToUse = customKey;
            kidToUse = customKid ?? "custom-key";
        }
        else if (customKid is not null)
        {
            lock (_keys)
            {
                var match = _keys.FirstOrDefault(k => k.Kid == customKid);
                if (match.Key is null)
                    throw new InvalidOperationException($"Key with kid {customKid} not found in fixture.");
                keyToUse = match.Key;
                kidToUse = customKid;
            }
        }
        else
        {
            keyToUse = _primaryRsa;
            kidToUse = _primaryKeyId;
        }

        var signingKey = new RsaSecurityKey(keyToUse) { KeyId = kidToUse };
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);

        var now = DateTime.UtcNow;
        var expires = lifetime.HasValue
            ? now.Add(lifetime.Value)
            : now.AddMinutes(30);

        var tokenHandler = new JwtSecurityTokenHandler();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, sub)
        };

        if (!string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim("role", role));
        }

        var notBefore = expires < now ? expires.AddMinutes(-10) : now.AddMinutes(-5);

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? _authorityUrl,
            Audience = audience ?? DefaultAudience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = notBefore,
            Expires = expires,
            SigningCredentials = credentials
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();

        lock (_keys)
        {
            foreach (var (_, key) in _keys)
            {
                key.Dispose();
            }
            _keys.Clear();
        }
    }
}
