using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var issuer = builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException("Jwt:Issuer is required.");
var audience = builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException("Jwt:Audience is required.");
var signingKey = builder.Configuration["Jwt:SigningKey"]
    ?? throw new InvalidOperationException("Jwt:SigningKey is required.");
if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience) || Encoding.UTF8.GetByteCount(signingKey) < 32)
    throw new InvalidOperationException("JWT issuer, audience and a signing key of at least 32 bytes are required.");
var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = key,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        RoleClaimType = "role"
    };
});
builder.Services.AddAuthorization(options =>
    options.AddPolicy("public", policy => policy.RequireAuthenticatedUser()));

var permitLimit = builder.Configuration.GetValue<int>("RateLimit:PermitLimit");
var windowSeconds = builder.Configuration.GetValue<int>("RateLimit:WindowSeconds");
var queueLimit = builder.Configuration.GetValue<int>("RateLimit:QueueLimit");
if (permitLimit <= 0 || windowSeconds <= 0 || queueLimit < 0)
    throw new InvalidOperationException("RateLimit requires positive PermitLimit/WindowSeconds and nonnegative QueueLimit.");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("gateway", limiter =>
    {
        // ponytail: one shared window; partition by client if independent quotas are needed.
        limiter.PermitLimit = permitLimit;
        limiter.Window = TimeSpan.FromSeconds(windowSeconds);
        limiter.QueueLimit = queueLimit;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

foreach (var cluster in new[] { "trip-service", "matching-service" })
{
    var address = builder.Configuration[$"ReverseProxy:Clusters:{cluster}:Destinations:primary:Address"];
    if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
        throw new InvalidOperationException($"A valid configured address is required for {cluster}.");
}
// /match, /quote and /payments are internal service calls and must not have gateway routes.
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.MapGet("/health", () => Results.Ok());
app.MapReverseProxy().RequireRateLimiting("gateway");

if (app.Environment.IsDevelopment())
{
    app.MapPost("/dev/token", (string userId, string role) =>
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 128 || role is not ("rider" or "driver"))
            return Results.BadRequest("A userId (1-128 characters) and role rider or driver are required.");
        var token = new JwtSecurityToken(issuer, audience,
            [new Claim(JwtRegisteredClaimNames.Sub, userId), new Claim("role", role)],
            notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return Results.Ok(new { access_token = new JwtSecurityTokenHandler().WriteToken(token) });
    }).RequireRateLimiting("gateway");
}

app.Run();
