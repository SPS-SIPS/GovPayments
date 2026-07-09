using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Sps.P2G.Gateway.Infrastructure.Callbacks;
using Sps.P2G.Gateway.Infrastructure.Configuration;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.Observability;
using Sps.P2G.Gateway.Infrastructure.RateLimiting;
using Sps.P2G.Gateway.Infrastructure.Security;
using Sps.P2G.Gateway.Infrastructure.SipsConnect;
using Sps.P2G.Gateway.Infrastructure.Time;
using Sps.P2G.Gateway.Modules.Authorities;
using Sps.P2G.Gateway.Modules.Bills;
using Sps.P2G.Gateway.Modules.Configuration;
using Sps.P2G.Gateway.Modules.Dashboard;
using Sps.P2G.Gateway.Modules.Health;
using Sps.P2G.Gateway.Modules.Metrics;
using Sps.P2G.Gateway.Modules.Reconciliation;
using Sps.P2G.Gateway.Modules.Verification;

var builder = WebApplication.CreateBuilder(args);

ProductionConfigurationValidator.Validate(builder.Environment, builder.Configuration);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddDataProtection();
builder.Services.AddOptions<P2GOptions>()
    .Bind(builder.Configuration.GetSection("P2G"))
    .ValidateDataAnnotations();
builder.Services.AddOptions<CallbackDispatchOptions>()
    .Bind(builder.Configuration.GetSection("Callbacks"));
builder.Services.AddOptions<CallbackSecurityOptions>()
    .Bind(builder.Configuration.GetSection("CallbackSecurity"));
builder.Services.AddOptions<DatabaseOptions>()
    .Bind(builder.Configuration.GetSection("Database"));
builder.Services.AddOptions<KeycloakOptions>()
    .Bind(builder.Configuration.GetSection("Keycloak"));
builder.Services.AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection("Auth"));
builder.Services.AddOptions<RequestProtectionOptions>()
    .Bind(builder.Configuration.GetSection("RequestProtection"));
builder.Services.AddOptions<HealthOptions>()
    .Bind(builder.Configuration.GetSection("Health"));
builder.Services.AddOptions<GatewayRateLimitOptions>()
    .Bind(builder.Configuration.GetSection("RateLimits"));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            error = "rate_limit_exceeded",
            message = "too many requests"
        }, cancellationToken);
    };
    options.AddPolicy(RateLimitPolicies.Authority, context =>
    {
        var rateLimitOptions = builder.Configuration.GetSection("RateLimits").Get<GatewayRateLimitOptions>() ?? new GatewayRateLimitOptions();
        return RateLimitPolicies.FixedWindow(context, RateLimitPolicies.Authority, rateLimitOptions.AuthorityPermitLimit, rateLimitOptions);
    });
    options.AddPolicy(RateLimitPolicies.SipsConnect, context =>
    {
        var rateLimitOptions = builder.Configuration.GetSection("RateLimits").Get<GatewayRateLimitOptions>() ?? new GatewayRateLimitOptions();
        return RateLimitPolicies.FixedWindow(context, RateLimitPolicies.SipsConnect, rateLimitOptions.SipsConnectPermitLimit, rateLimitOptions);
    });
    options.AddPolicy(RateLimitPolicies.Dashboard, context =>
    {
        var rateLimitOptions = builder.Configuration.GetSection("RateLimits").Get<GatewayRateLimitOptions>() ?? new GatewayRateLimitOptions();
        return RateLimitPolicies.FixedWindow(context, RateLimitPolicies.Dashboard, rateLimitOptions.DashboardPermitLimit, rateLimitOptions);
    });
});

if (!builder.Environment.IsEnvironment("Testing"))
{
    builder.Services.AddDbContext<GatewayDbContext>(options =>
    {
        var connectionString = builder.Configuration.GetConnectionString("P2G");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:P2G is required unless the DbContext is replaced by tests.");
        }

        options.UseNpgsql(connectionString);
    });
}

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IApiKeyHasher, Sha256ApiKeyHasher>();
builder.Services.AddSingleton<IHashChainService, HashChainService>();
builder.Services.AddSingleton<IDescriptorService, DescriptorService>();
builder.Services.AddSingleton<ICallbackDispatcher, CallbackDispatcher>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<CallbackDispatchWorker>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer();

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<KeycloakOptions>>((options, keycloakOptionsAccessor) =>
    {
        var keycloakOptions = keycloakOptionsAccessor.Value;
        if (!string.IsNullOrWhiteSpace(keycloakOptions.Authority))
        {
            options.Authority = keycloakOptions.Authority;
        }

        if (!string.IsNullOrWhiteSpace(keycloakOptions.MetadataAddress))
        {
            options.MetadataAddress = keycloakOptions.MetadataAddress;
        }

        options.Audience = keycloakOptions.Audience;
        options.RequireHttpsMetadata = keycloakOptions.RequireHttpsMetadata;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = !string.IsNullOrWhiteSpace(keycloakOptions.Audience),
            ValidIssuer = string.IsNullOrWhiteSpace(keycloakOptions.ValidIssuer) ? null : keycloakOptions.ValidIssuer,
            NameClaimType = "preferred_username",
            RoleClaimType = "roles"
        };

        if (!string.IsNullOrWhiteSpace(keycloakOptions.SymmetricSigningKey))
        {
            options.TokenValidationParameters.IssuerSigningKey =
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(keycloakOptions.SymmetricSigningKey));
            options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        }
    });

var keycloakOptions = builder.Configuration.GetSection("Keycloak").Get<KeycloakOptions>() ?? new KeycloakOptions();

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.DashboardUser, policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            AuthClaimReader.HasRealmRole(context.User, keycloakOptions.DashboardRole)));
    options.AddPolicy(AuthPolicies.ConfigurationAdmin, policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            AuthClaimReader.HasRealmRole(context.User, keycloakOptions.ConfigurationAdminRole) ||
            AuthClaimReader.HasClientRole(context.User, keycloakOptions.ConfigurationAdminRole)));
    options.AddPolicy(AuthPolicies.RevenueAuthorityClient, policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
            AuthClaimReader.HasRealmRole(context.User, keycloakOptions.RevenueAuthorityRole) ||
            AuthClaimReader.HasClientRole(context.User, keycloakOptions.RevenueAuthorityRole)));
    options.AddPolicy(AuthPolicies.SipsConnectClient, policy =>
        policy.RequireAuthenticatedUser().RequireAssertion(context =>
        {
            var clientId = AuthClaimReader.GetClientId(context.User);
            return string.Equals(clientId, keycloakOptions.SipsConnectClientId, StringComparison.OrdinalIgnoreCase) &&
                   (AuthClaimReader.HasRealmRole(context.User, keycloakOptions.SipsConnectRole) ||
                    AuthClaimReader.HasClientRole(context.User, keycloakOptions.SipsConnectRole, clientId));
        }));
});

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<RequestProtectionMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { service = "Sps.P2G.Gateway", status = "ok" }));
app.MapOpenApi();
app.MapHealthEndpoints();
app.MapMetricsEndpoints();
app.MapAuthorityEndpoints();
app.MapRevenueConfigurationEndpoints();
app.MapBillEndpoints();
app.MapReconciliationEndpoints();
app.MapSipsConnectEndpoints();
app.MapDashboardEndpoints();

await app.ApplyDatabaseMigrationsAsync();

app.Run();

public partial class Program;
