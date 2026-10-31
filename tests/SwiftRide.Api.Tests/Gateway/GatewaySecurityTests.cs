using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace SwiftRide.Api.Tests.Gateway;

public class GatewaySecurityTests
{
    public static TheoryData<string, string> InvalidTokens()
    {
        return new TheoryData<string, string>
        {
            { "no-token", "" },
            { "malformed", "Bearer not-a-valid-token" },
            { "wrong-signature", "wrong-signature" },
            { "wrong-issuer", "wrong-issuer" },
            { "wrong-audience", "wrong-audience" },
            { "expired", "expired" }
        };
    }

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task PublicRoute_RejectsInvalidAuthentication(string scenario, string tokenValue)
    {
        await using var fixture = await GatewayFixture.StartAsync();

        string? headerValue = scenario switch
        {
            "no-token" => null,
            "malformed" => "not-a-valid-token",
            "wrong-signature" => CreateCustomToken(fixture, fixture.Issuer, fixture.Audience,
                new SymmetricSecurityKey(new byte[32]), notBefore: DateTime.UtcNow.AddMinutes(-5), expires: DateTime.UtcNow.AddMinutes(30)),
            "wrong-issuer" => CreateCustomToken(fixture, "wrong-issuer", fixture.Audience,
                new SymmetricSecurityKey(fixture.SigningKey), notBefore: DateTime.UtcNow.AddMinutes(-5), expires: DateTime.UtcNow.AddMinutes(30)),
            "wrong-audience" => CreateCustomToken(fixture, fixture.Issuer, "wrong-audience",
                new SymmetricSecurityKey(fixture.SigningKey), notBefore: DateTime.UtcNow.AddMinutes(-5), expires: DateTime.UtcNow.AddMinutes(30)),
            "expired" => CreateCustomToken(fixture, fixture.Issuer, fixture.Audience,
                new SymmetricSecurityKey(fixture.SigningKey), notBefore: DateTime.UtcNow.AddHours(-2), expires: DateTime.UtcNow.AddHours(-1)),
            _ => tokenValue
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/trips");
        if (headerValue != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", headerValue);
        }

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(fixture.Trip.Requests);
    }

    [Theory]
    [InlineData("rider", "rider-1")]
    [InlineData("driver", "driver-1")]
    public async Task DevToken_IssuesUsableRiderAndDriverTokens(string role, string userId)
    {
        await using var fixture = await GatewayFixture.StartAsync(environment: "Development");

        using var devResponse = await fixture.Client.PostAsync($"/dev/token?userId={userId}&role={role}", null);
        Assert.Equal(HttpStatusCode.OK, devResponse.StatusCode);

        var json = await devResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.TryGetProperty("access_token", out var tokenProp));
        var tokenString = tokenProp.GetString();
        Assert.False(string.IsNullOrWhiteSpace(tokenString));

        var handler = new JwtSecurityTokenHandler();
        var parsedToken = handler.ReadJwtToken(tokenString);
        var expClaim = parsedToken.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Exp);
        Assert.NotNull(expClaim);
        var expSeconds = long.Parse(expClaim.Value);
        var expTime = DateTimeOffset.FromUnixTimeSeconds(expSeconds);
        var validUntil = expTime - DateTimeOffset.UtcNow;
        Assert.InRange(validUntil.TotalMinutes, 25, 31);

        using var tripRequest = new HttpRequestMessage(HttpMethod.Post, "/trips");
        tripRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenString);
        using var tripResponse = await fixture.Client.SendAsync(tripRequest);

        Assert.Equal(HttpStatusCode.NotImplemented, tripResponse.StatusCode);
        Assert.True(tripResponse.Headers.TryGetValues("X-Upstream", out var values));
        Assert.Contains("trip", values);
        var recorded = Assert.Single(fixture.Trip.Requests);
        Assert.Equal("POST", recorded.Method);
        Assert.Equal("/trips", recorded.Path);
    }

    [Theory]
    [InlineData("admin", "user-1")]
    [InlineData("rider", "   ")]
    [InlineData("rider", "")]
    [InlineData("rider", "toolong123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789012345678901234567890123456789")]
    public async Task DevToken_RejectsInvalidIdentityInput(string role, string userId)
    {
        await using var fixture = await GatewayFixture.StartAsync(environment: "Development");

        using var devResponse = await fixture.Client.PostAsync($"/dev/token?userId={Uri.EscapeDataString(userId)}&role={role}", null);
        Assert.Equal(HttpStatusCode.BadRequest, devResponse.StatusCode);
    }

    [Fact]
    public async Task Production_DoesNotExposeDevToken()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "Oidc",
            ["Authentication:Authority"] = "https://identity.swiftride.test",
            ["Authentication:Audience"] = "swiftride-api",
            ["Authentication:MetadataTimeoutSeconds"] = "2"
        };
        await using var fixture = await GatewayFixture.StartAsync(environment: "Production", overrides: overrides);

        using var devResponse = await fixture.Client.PostAsync("/dev/token?userId=rider-1&role=rider", null);
        Assert.Equal(HttpStatusCode.NotFound, devResponse.StatusCode);
    }

    private static string CreateCustomToken(
        GatewayFixture fixture,
        string issuer,
        string audience,
        SecurityKey key,
        DateTime notBefore,
        DateTime expires)
    {
        var token = new JwtSecurityToken(
            issuer,
            audience,
            [new Claim(JwtRegisteredClaimNames.Sub, "rider-1"), new Claim("role", "rider")],
            notBefore: notBefore,
            expires: expires,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
