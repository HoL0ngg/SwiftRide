using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Domain;
using SwiftRide.MatchingService.Infrastructure;
using SwiftRide.MatchingService.Api.Observability;

namespace SwiftRide.MatchingService.Api;

public static class MatchingApplication
{
    public static WebApplication Build(WebApplicationBuilder builder, Action<IServiceCollection>? configureServices = null)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddMatchingObservability(builder.Configuration);
        var options = builder.Configuration.GetSection("Matching").Get<MatchingOptions>() ?? new();
        options.Validate();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IPricingStrategy, StandardPricingStrategy>();
        builder.Services.AddScoped<UpdateDriverLocation>();
        builder.Services.AddScoped<MatchDriver>();
        builder.Services.AddScoped<CreateQuote>();
        builder.Services.AddInfrastructure(builder.Configuration);

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

            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
            {
                o.Authority = authority;
                o.Audience = audience;
                o.RequireHttpsMetadata = requireHttps;
                o.BackchannelTimeout = TimeSpan.FromSeconds(timeoutSec);
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
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

            var issuer = builder.Configuration["Jwt:Issuer"] ?? builder.Configuration["Authentication:Issuer"];
            var audience = builder.Configuration["Jwt:Audience"] ?? builder.Configuration["Authentication:Audience"];
            var key = builder.Configuration["Jwt:SigningKey"] ?? builder.Configuration["Authentication:SigningKey"];
            if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
                throw new InvalidOperationException("JWT issuer, audience and a signing key of at least 32 bytes are required.");
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                    RoleClaimType = "role",
                    NameClaimType = "sub"
                };
            });
        }

        builder.Services.AddAuthorization();
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health", () => Results.Ok());
        app.MapMatchingEndpoints();
        return app;
    }
}
