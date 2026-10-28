using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SwiftRide.MatchingService.Application;
using SwiftRide.MatchingService.Domain;
using SwiftRide.MatchingService.Infrastructure;

namespace SwiftRide.MatchingService.Api;

public static class MatchingApplication
{
    public static WebApplication Build(WebApplicationBuilder builder, Action<IServiceCollection>? configureServices = null)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        var options = builder.Configuration.GetSection("Matching").Get<MatchingOptions>() ?? new();
        options.Validate();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IPricingStrategy, StandardPricingStrategy>();
        builder.Services.AddScoped<UpdateDriverLocation>();
        builder.Services.AddScoped<MatchDriver>();
        builder.Services.AddScoped<CreateQuote>();
        builder.Services.AddInfrastructure(builder.Configuration);
        var issuer = builder.Configuration["Jwt:Issuer"];
        var audience = builder.Configuration["Jwt:Audience"];
        var key = builder.Configuration["Jwt:SigningKey"];
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience) || string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException("JWT issuer, audience and a signing key of at least 32 bytes are required.");
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = true, ValidAudience = audience,
                ValidateLifetime = true, RequireExpirationTime = true, ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256], RoleClaimType = "role", NameClaimType = "sub"
            };
        });
        builder.Services.AddAuthorization();
        if (builder.Configuration.GetValue<bool>("Matching:SeedDemoDrivers"))
        {
            if (!builder.Environment.IsDevelopment()) throw new InvalidOperationException("Demo seed is allowed only in Development.");
            builder.Services.AddHostedService<DemoDriverSeed>();
        }
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health", () => Results.Ok());
        app.MapMatchingEndpoints();
        return app;
    }
}
