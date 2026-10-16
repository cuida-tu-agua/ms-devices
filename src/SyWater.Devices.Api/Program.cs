using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using SyWater.Devices.Api.Composition;
using SyWater.Devices.Api.Errors;
using SyWater.Devices.Api.Messaging;
using SyWater.Devices.Api.Security;
using SyWater.Devices.Infrastructure.Persistence;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ── Inbound adapter: HTTP ────────────────────────────────────────────────
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(
        new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper))); // NeverReported <-> "NEVER_REPORTED"

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
// The OpenAPI document says the API lives under the gateway prefix (e.g. "/places"),
// so "Try it out" in Swagger UI calls https://api.<domain>/places/...
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, context, cancellationToken) =>
{
    var serverUrl = builder.Configuration["OpenApi:ServerUrl"];
    if (!string.IsNullOrWhiteSpace(serverUrl))
        document.Servers = [new OpenApiServer { Url = serverUrl }];
    return Task.CompletedTask;
}));

// ── Hexagon + adapters ──────────────────────────────────────────────────
builder.Services.AddDevicesApplication(builder.Configuration);
builder.Services.AddDevicesInfrastructure(builder.Configuration);
builder.Services.AddDevicesBackgroundWork(builder.Configuration);   // MQTT + sweeper

// ── Security: every endpoint requires a valid ms-iam token unless marked AllowAnonymous ──
builder.Services.AddIamJwtAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(AuthorizationPolicies.Admin, policy => policy.RequireClaim("roles", AuthorizationPolicies.AdminRole));
builder.Services.AddPairingRateLimiter(builder.Configuration);   // HU-012: attempts per user

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins(builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("Retry-After")));   // lets the web version read how long to wait after a 429

builder.Services.AddHealthChecks()
    .AddDbContextCheck<DevicesDbContext>("database")
    .AddCheck<MqttHealthCheck>("mqtt");

var app = builder.Build();

app.UseExceptionHandler();

// Development: always. Other environments: only when OpenApi:Enabled=true (set by infra).
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi().AllowAnonymous(); // GET /openapi/v1.json
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();   // after authentication: the limit is per user ("sub")

app.MapControllers();
app.MapHealthChecks("/health").AllowAnonymous();

app.Run();
