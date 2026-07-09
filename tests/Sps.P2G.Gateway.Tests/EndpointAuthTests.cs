using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sps.P2G.Gateway.Domain.Authorities;
using Sps.P2G.Gateway.Infrastructure.Database;
using Sps.P2G.Gateway.Infrastructure.Security;
using Sps.P2G.Gateway.Modules.Shared;

namespace Sps.P2G.Gateway.Tests;

public sealed class EndpointAuthTests
{
    [Fact]
    public async Task ResolveAuthorityFromToken_MapsKeycloakClientIdToAuthorityCode()
    {
        var services = new ServiceCollection();
        services.AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase($"auth-{Guid.NewGuid()}"));
        services.AddSingleton<IOptions<KeycloakOptions>>(Options.Create(new KeycloakOptions
        {
            Enabled = true,
            RevenueAuthorityRole = "p2g-authority"
        }));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var authority = new RevenueAuthority
        {
            Id = Guid.NewGuid(),
            Code = "SRA",
            Name = "Somali Revenue Authority",
            Status = "Active",
            ApiKeyHash = "unused",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.RevenueAuthorities.Add(authority);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("azp", "SRA"),
                new Claim("realm_access", """{"roles":["p2g-authority"]}""")
            ], "test"))
        };

        var resolved = await EndpointAuth.ResolveAuthorityFromTokenAsync(httpContext, authority.Id, db, CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(authority.Id, resolved.Id);
    }

    [Fact]
    public async Task ResolveAuthorityFromToken_RejectsClientWithoutAuthorityRole()
    {
        var services = new ServiceCollection();
        services.AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase($"auth-{Guid.NewGuid()}"));
        services.AddSingleton<IOptions<KeycloakOptions>>(Options.Create(new KeycloakOptions
        {
            Enabled = true,
            RevenueAuthorityRole = "p2g-authority"
        }));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var authority = new RevenueAuthority
        {
            Id = Guid.NewGuid(),
            Code = "SRA",
            Name = "Somali Revenue Authority",
            Status = "Active",
            ApiKeyHash = "unused",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.RevenueAuthorities.Add(authority);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("azp", "SRA"),
                new Claim("realm_access", """{"roles":["p2g-dashboard"]}""")
            ], "test"))
        };

        var resolved = await EndpointAuth.ResolveAuthorityFromTokenAsync(httpContext, authority.Id, db, CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveAuthorityFromToken_RejectsClientRoleFromAnotherClient()
    {
        var services = new ServiceCollection();
        services.AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase($"auth-{Guid.NewGuid()}"));
        services.AddSingleton<IOptions<KeycloakOptions>>(Options.Create(new KeycloakOptions
        {
            Enabled = true,
            RevenueAuthorityRole = "p2g-authority"
        }));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var authority = new RevenueAuthority
        {
            Id = Guid.NewGuid(),
            Code = "SRA",
            Name = "Somali Revenue Authority",
            Status = "Active",
            ApiKeyHash = "unused",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.RevenueAuthorities.Add(authority);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("azp", "SRA"),
                new Claim("resource_access", """{"other-client":{"roles":["p2g-authority"]}}""")
            ], "test"))
        };

        var resolved = await EndpointAuth.ResolveAuthorityFromTokenAsync(httpContext, authority.Id, db, CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task RequireAuthorityAsync_RejectsApiKeyWhenFallbackDisabled()
    {
        var hasher = new Sha256ApiKeyHasher();
        var apiKey = "p2g_test_key";
        var services = new ServiceCollection();
        services.AddDbContext<GatewayDbContext>(options => options.UseInMemoryDatabase($"auth-{Guid.NewGuid()}"));
        services.AddSingleton<IOptions<KeycloakOptions>>(Options.Create(new KeycloakOptions()));
        services.AddSingleton<IOptions<AuthOptions>>(Options.Create(new AuthOptions
        {
            AllowAuthorityApiKeyFallback = false
        }));

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var authority = new RevenueAuthority
        {
            Id = Guid.NewGuid(),
            Code = "SRA",
            Name = "Somali Revenue Authority",
            Status = "Active",
            ApiKeyHash = hasher.Hash(apiKey),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.RevenueAuthorities.Add(authority);
        await db.SaveChangesAsync();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };
        httpContext.Request.Headers["X-Authority-Api-Key"] = apiKey;

        var resolved = await EndpointAuth.RequireAuthorityAsync(httpContext, authority.Id, db, hasher, CancellationToken.None);

        Assert.Null(resolved);
    }

    [Fact]
    public void HasInternalAccess_RejectsInternalKeyWhenFallbackDisabled()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<KeycloakOptions>>(Options.Create(new KeycloakOptions()));
        services.AddSingleton<IOptions<AuthOptions>>(Options.Create(new AuthOptions
        {
            AllowSipsConnectInternalKeyFallback = false
        }));

        using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider
        };
        httpContext.Request.Headers["X-Internal-Api-Key"] = "internal";

        var hasAccess = EndpointAuth.HasInternalAccess(
            httpContext,
            Options.Create(new P2GOptions { InternalApiKey = "internal" }));

        Assert.False(hasAccess);
    }

    [Fact]
    public void HasInternalAccess_AcceptsSipsConnectKeycloakClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<KeycloakOptions>>(Options.Create(new KeycloakOptions
        {
            SipsConnectClientId = "sips-connect-p2g",
            SipsConnectRole = "p2g-sips-connect"
        }));
        services.AddSingleton<IOptions<AuthOptions>>(Options.Create(new AuthOptions
        {
            AllowSipsConnectInternalKeyFallback = false
        }));

        using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("azp", "sips-connect-p2g"),
                new Claim("realm_access", """{"roles":["p2g-sips-connect"]}""")
            ], "test"))
        };

        var hasAccess = EndpointAuth.HasInternalAccess(
            httpContext,
            Options.Create(new P2GOptions { InternalApiKey = "internal" }));

        Assert.True(hasAccess);
    }

    [Fact]
    public void HasInternalAccess_RejectsSipsRoleFromUnexpectedClient()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<KeycloakOptions>>(Options.Create(new KeycloakOptions
        {
            SipsConnectClientId = "sips-connect-p2g",
            SipsConnectRole = "p2g-sips-connect"
        }));
        services.AddSingleton<IOptions<AuthOptions>>(Options.Create(new AuthOptions
        {
            AllowSipsConnectInternalKeyFallback = false
        }));

        using var provider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = provider,
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("azp", "rogue-client"),
                new Claim("realm_access", """{"roles":["p2g-sips-connect"]}""")
            ], "test"))
        };

        var hasAccess = EndpointAuth.HasInternalAccess(
            httpContext,
            Options.Create(new P2GOptions { InternalApiKey = "internal" }));

        Assert.False(hasAccess);
    }
}
