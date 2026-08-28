using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Transforms;

var builder = WebApplication.CreateBuilder(args);

var mcp = builder.Configuration.GetSection("Mcp").Get<McpOptions>()
          ?? throw new InvalidOperationException("Configuration section 'Mcp' is missing.");
mcp.Validate();

var upstream = builder.Configuration.GetSection("Upstream").Get<UpstreamOptions>()
               ?? throw new InvalidOperationException("Configuration section 'Upstream' is missing.");
upstream.Validate();

// Traefik terminates TLS and is the only hop in front of this gateway.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Discovery: {Issuer}/.well-known/openid-configuration
        options.Authority = mcp.Issuer;
        options.RequireHttpsMetadata = true;
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = mcp.Issuer,
            // Keycloak has no RFC 8707 'resource' support yet, so the audience is
            // stamped by a client-scope audience mapper. It must equal Mcp:Resource.
            ValidateAudience = true,
            ValidAudience = mcp.Resource,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "preferred_username",
            RoleClaimType = "roles"
        };

        options.Events = new JwtBearerEvents
        {
            // An MCP client discovers the authorization server from this challenge.
            // The 401 status is required; Claude ignores WWW-Authenticate on a 200.
            OnChallenge = context =>
            {
                context.HandleResponse();
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate =
                    mcp.BuildChallenge(error: "invalid_token", description: context.ErrorDescription);
                return Task.CompletedTask;
            },
            OnForbidden = context =>
            {
                context.Response.Headers.WWWAuthenticate =
                    mcp.BuildChallenge(error: "insufficient_scope");
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(McpOptions.PolicyName, policy =>
    {
        policy.RequireAuthenticatedUser();

        if (!string.IsNullOrWhiteSpace(mcp.RequiredScope))
        {
            policy.RequireAssertion(context =>
                (context.User.FindFirst("scope")?.Value ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(mcp.RequiredScope, StringComparer.Ordinal));
        }
    });

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
    .AddTransforms(context =>
    {
        context.AddRequestTransform(transform =>
        {
            var request = transform.ProxyRequest;

            // The Keycloak token authorises the caller against this gateway and must
            // never reach the upstream, whatever credential that upstream expects
            // instead. Stripping it unconditionally also stops a caller smuggling
            // one through when no Authorization header is configured.
            request.Headers.Remove("Authorization");

            foreach (var header in upstream.Headers)
            {
                request.Headers.Remove(header.Key);
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            return ValueTask.CompletedTask;
        });
    });

var app = builder.Build();

// Names only. The values are credentials.
app.Logger.LogInformation("Injecting upstream headers: {Headers}", upstream.Describe());

app.UseForwardedHeaders();
app.UseAuthentication();
app.UseAuthorization();

// RFC 9728 protected resource metadata. Claude reads this to find Keycloak.
// 'resource' must match the URL typed into Claude character for character.
var resourceMetadata = mcp.BuildResourceMetadata();

app.MapGet("/.well-known/oauth-protected-resource", () => Results.Json(resourceMetadata))
   .AllowAnonymous();

// Claude probes the path-suffixed form first when the 401 pointer is absent.
app.MapGet("/.well-known/oauth-protected-resource/{**path}", () => Results.Json(resourceMetadata))
   .AllowAnonymous();

app.MapGet("/healthz", () => Results.Json(new { status = "ok" }))
   .AllowAnonymous();

app.MapReverseProxy().RequireAuthorization(McpOptions.PolicyName);

app.Run();

internal sealed class McpOptions
{
    public const string PolicyName = "mcp";

    /// <summary>Public URL of the MCP endpoint, exactly as entered in Claude.</summary>
    public string Resource { get; set; } = string.Empty;

    /// <summary>Keycloak realm issuer, e.g. https://keycloak.example.com/realms/home.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Absolute URL of this gateway's protected resource metadata document.</summary>
    public string ResourceMetadataUrl { get; set; } = string.Empty;

    public string[] ScopesSupported { get; set; } = [];

    /// <summary>Optional. When set, the token must carry this scope.</summary>
    public string? RequiredScope { get; set; }

    public void Validate()
    {
        Require(Resource, nameof(Resource));
        Require(Issuer, nameof(Issuer));
        Require(ResourceMetadataUrl, nameof(ResourceMetadataUrl));

        if (Issuer.EndsWith('/'))
        {
            // Keycloak's 'iss' claim never carries a trailing slash; a mismatch here
            // fails every token with a confusing "issuer invalid" error.
            throw new InvalidOperationException("Mcp:Issuer must not end with '/'.");
        }

        static void Require(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"Mcp:{name} is required.");
            }
        }
    }

    public IReadOnlyDictionary<string, object?> BuildResourceMetadata() =>
        new Dictionary<string, object?>
        {
            ["resource"] = Resource,
            ["authorization_servers"] = new[] { Issuer },
            ["scopes_supported"] = ScopesSupported,
            ["bearer_methods_supported"] = new[] { "header" }
        };

    public string BuildChallenge(string? error = null, string? description = null)
    {
        var challenge = new StringBuilder("Bearer resource_metadata=\"")
            .Append(ResourceMetadataUrl)
            .Append('"');

        if (!string.IsNullOrWhiteSpace(error))
        {
            challenge.Append(", error=\"").Append(Quote(error)).Append('"');
        }

        if (!string.IsNullOrWhiteSpace(description))
        {
            challenge.Append(", error_description=\"").Append(Quote(description)).Append('"');
        }

        if (ScopesSupported.Length > 0)
        {
            challenge.Append(", scope=\"").Append(string.Join(' ', ScopesSupported)).Append('"');
        }

        return challenge.ToString();

        // A stray quote or newline would split the header and break the handshake.
        static string Quote(string value) =>
            value.Replace("\\", string.Empty)
                 .Replace("\"", string.Empty)
                 .Replace('\r', ' ')
                 .Replace('\n', ' ');
    }
}

internal sealed class UpstreamOptions
{
    /// <summary>
    /// Headers sent to the upstream MCP server, replacing whatever the caller sent.
    /// This is where the upstream's own credential belongs: a Firefly III personal
    /// access token, a Home Assistant long-lived access token, and so on. Keeping it
    /// a plain dictionary is what lets one image front more than one MCP server.
    /// </summary>
    public Dictionary<string, string> Headers { get; set; } = [];

    public void Validate()
    {
        if (Headers.Count == 0)
        {
            throw new InvalidOperationException(
                "Upstream:Headers is empty. Declare at least the upstream's credential, " +
                "e.g. Upstream__Headers__Authorization=\"Bearer <token>\".");
        }

        foreach (var header in Headers)
        {
            if (string.IsNullOrWhiteSpace(header.Value))
            {
                throw new InvalidOperationException(
                    $"Upstream:Headers:{header.Key} has no value. Set it in the environment, " +
                    "not in appsettings.json.");
            }
        }
    }

    public string Describe() => string.Join(", ", Headers.Keys);
}
