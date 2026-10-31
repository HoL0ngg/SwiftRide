using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.ApiGateway.Observability;

namespace SwiftRide.ApiGateway;

public static class GatewayApplication
{
    public static WebApplication Build(WebApplicationBuilder builder)
    {
        builder.Services.AddGatewayObservability(builder.Configuration);
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

        var explicitAuthMode = builder.Configuration["Authentication:Mode"];
        string authMode;
        if (!string.IsNullOrWhiteSpace(explicitAuthMode))
        {
            if (!string.Equals(explicitAuthMode, "Oidc", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(explicitAuthMode, "DevelopmentJwt", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Invalid Authentication:Mode '{explicitAuthMode}'. Supported modes: 'Oidc', 'DevelopmentJwt'.");
            }
            authMode = explicitAuthMode;
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(builder.Configuration["Authentication:Authority"]))
            {
                authMode = "Oidc";
            }
            else if (!string.IsNullOrWhiteSpace(builder.Configuration["Jwt:SigningKey"]) ||
                     !string.IsNullOrWhiteSpace(builder.Configuration["Authentication:SigningKey"]))
            {
                authMode = "DevelopmentJwt";
            }
            else
            {
                authMode = builder.Environment.IsProduction() ? "Oidc" : "DevelopmentJwt";
            }
        }

        // Validate resolved mode: Production MUST use Oidc and NEVER DevelopmentJwt
        if (builder.Environment.IsProduction() && !string.Equals(authMode, "Oidc", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Production environment requires Authentication:Mode='Oidc'.");
        }

        SymmetricSecurityKey? devKey = null;
        string? devIssuer = null;
        string? devAudience = null;

        if (string.Equals(authMode, "Oidc", StringComparison.OrdinalIgnoreCase))
        {
            var authority = builder.Configuration["Authentication:Authority"]
                ?? throw new InvalidOperationException("Authentication:Authority is required when Authentication:Mode is 'Oidc'.");

            var audience = builder.Configuration["Authentication:Audience"]
                ?? builder.Configuration["Jwt:Audience"]
                ?? throw new InvalidOperationException("Authentication:Audience is required when Authentication:Mode is 'Oidc'.");

            var requireHttps = builder.Configuration.GetValue<bool?>("Authentication:RequireHttpsMetadata") ?? true;
            if (builder.Environment.IsProduction())
            {
                if (!authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("OIDC Authority must use HTTPS in Production.");
                requireHttps = true;
            }

            var roleClaim = builder.Configuration["Authentication:RoleClaimType"] ?? "role";
            var nameClaim = builder.Configuration["Authentication:NameClaimType"] ?? "sub";
            var timeoutSec = builder.Configuration.GetValue<int?>("Authentication:MetadataTimeoutSeconds") ?? 5;

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.Authority = authority;
                options.Audience = audience;
                options.RequireHttpsMetadata = requireHttps;
                options.BackchannelTimeout = TimeSpan.FromSeconds(timeoutSec);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = authority,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    RoleClaimType = roleClaim,
                    NameClaimType = nameClaim
                };
            });
        }
        else // DevelopmentJwt
        {
            if (builder.Environment.IsProduction() && string.Equals(explicitAuthMode, "DevelopmentJwt", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Production environment requires Authentication:Mode='Oidc'.");

            var issuer = builder.Configuration["Jwt:Issuer"] ?? builder.Configuration["Authentication:Issuer"]
                ?? throw new InvalidOperationException("Jwt:Issuer is required.");
            var audience = builder.Configuration["Jwt:Audience"] ?? builder.Configuration["Authentication:Audience"]
                ?? throw new InvalidOperationException("Jwt:Audience is required.");
            var signingKey = builder.Configuration["Jwt:SigningKey"] ?? builder.Configuration["Authentication:SigningKey"]
                ?? throw new InvalidOperationException("Jwt:SigningKey is required.");

            if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32)
                throw new InvalidOperationException("JWT issuer, audience and a signing key of at least 32 bytes are required.");

            devIssuer = issuer;
            devAudience = audience;
            devKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey));

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = devKey,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RoleClaimType = "role",
                    NameClaimType = "sub"
                };
            });
        }

        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("public", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy("rider", policy => policy.RequireAuthenticatedUser().RequireRole("rider"));
            options.AddPolicy("driver", policy => policy.RequireAuthenticatedUser().RequireRole("driver"));
        });

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
        app.UseExceptionHandler();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapGet("/health", () => Results.Ok());
        app.MapReverseProxy().RequireRateLimiting("gateway");

        if (app.Environment.IsDevelopment() && string.Equals(authMode, "DevelopmentJwt", StringComparison.OrdinalIgnoreCase) && devKey is not null)
        {
            app.MapPost("/dev/token", (string userId, string role) =>
            {
                if (string.IsNullOrWhiteSpace(userId) || userId.Length > 128 || role is not ("rider" or "driver"))
                    return Results.BadRequest("A userId (1-128 characters) and role rider or driver are required.");
                var token = new JwtSecurityToken(devIssuer, devAudience,
                    [new Claim(JwtRegisteredClaimNames.Sub, userId), new Claim("role", role)],
                    notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddMinutes(30),
                    signingCredentials: new SigningCredentials(devKey, SecurityAlgorithms.HmacSha256));
                return Results.Ok(new { access_token = new JwtSecurityTokenHandler().WriteToken(token) });
            }).RequireRateLimiting("gateway");
        }

        return app;
    }
}
