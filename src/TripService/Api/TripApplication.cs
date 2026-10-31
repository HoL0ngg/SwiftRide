using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.TripService.Api.Observability;
using SwiftRide.TripService.Application;
using SwiftRide.TripService.Application.Abstractions;
using SwiftRide.TripService.Infrastructure;
using SwiftRide.TripService.Infrastructure.Persistence;

namespace SwiftRide.TripService.Api;

public static class TripApplication
{
    public static WebApplication Build(
        WebApplicationBuilder builder,
        Action<IServiceCollection>? configureServices = null)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddTripObservability(builder.Configuration);
        builder.Services.AddSingleton<ITripMetrics, TripMetrics>();

        builder.Services.AddTripApplication();
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

            var issuer = builder.Configuration["Jwt:Issuer"] ?? builder.Configuration["Authentication:Issuer"] ?? "swiftride";
            var audience = builder.Configuration["Jwt:Audience"] ?? builder.Configuration["Authentication:Audience"] ?? "swiftride";
            var signingKey = builder.Configuration["Jwt:SigningKey"] ?? builder.Configuration["Authentication:SigningKey"];

            if (!string.IsNullOrWhiteSpace(signingKey) && Encoding.UTF8.GetByteCount(signingKey) >= 32)
            {
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
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                        RoleClaimType = "role",
                        NameClaimType = "sub"
                    };
                });
            }
            else
            {
                // For testing or environments where signing key is injected later
                builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
            }
        }

        builder.Services.AddAuthorization();

        configureServices?.Invoke(builder.Services);

        var app = builder.Build();

        app.UseExceptionHandler();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/health", () => Results.Ok());
        app.MapTripEndpoints();

        return app;
    }

    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        var shouldMigrate = app.Configuration.GetValue<bool>("Database:MigrateOnStartup", true);
        if (shouldMigrate)
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetService<TripDbContext>();
            if (db is not null && db.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory")
            {
                await db.Database.MigrateAsync();
            }
        }
    }
}
