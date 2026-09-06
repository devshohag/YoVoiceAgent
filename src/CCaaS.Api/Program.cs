using System.Text;
using System.Threading.RateLimiting;
using CCaaS.Infrastructure;
using CCaaS.Infrastructure.BackgroundJobs;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
// NOTE: MapPrometheusScrapingEndpoint ships in the OpenTelemetry.Exporter.Prometheus.AspNetCore
// package. Its namespace has moved between preview releases of that package - if the compiler
// can't find it after `dotnet restore`, check that package's current docs (it has lived in both
// `OpenTelemetry.Exporter` and `Microsoft.AspNetCore.Builder`) and adjust this using accordingly.
using OpenTelemetry.Exporter;
using Serilog;

// Section 17 - Observability: "Structured logs include TenantId, CorrelationId, TraceId,
// CallId/ConversationId, AgentId when available." Serilog is bootstrapped first so startup
// failures themselves get logged, not swallowed.
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    builder.Services.AddControllers();
    builder.Services.AddDataProtection().PersistKeysToFileSystem(
        new DirectoryInfo(builder.Configuration["DataProtection:KeyPath"] ?? "/var/lib/ccaas/keys"));
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst("tenant_id")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 20 }));
    });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Description = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"",
            Name = "Authorization",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.ApiKey,
            Scheme = "Bearer"
        });
    });

    // Section 14 - Authentication: "ASP.NET Core Identity, JWT access token, rotating
    // refresh token; MFA-ready." Tenant identity itself is read from the "tenant_id" claim
    // by HttpCurrentTenant (Infrastructure) - never trust a tenant id from the request body.
    var jwtSection = builder.Configuration.GetSection("Jwt");
    if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(jwtSection["SigningKey"]))
        throw new InvalidOperationException("Jwt:SigningKey must be supplied by a production secret provider.");
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwtSection["Issuer"],
                ValidateAudience = true,
                ValidAudience = jwtSection["Audience"],
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtSection["SigningKey"]!)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

            // Allow SignalR clients to authenticate via access_token query string (browser
            // WebSocket connections cannot set an Authorization header) - Section 5:
            // "SignalR carries real-time UI state, not voice media."
            options.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    var accessToken = context.Request.Query["access_token"];
                    var path = context.HttpContext.Request.Path;
                    if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                        context.Token = accessToken;
                    return Task.CompletedTask;
                }
            };
        });
    builder.Services.AddAuthorization();

    builder.Services.AddSignalR();

    // `ng serve` (the Angular dev server) runs on http://localhost:4200 and calls this API
    // directly on http://localhost:5000 (see frontend/src/environments/environment.development.ts)
    // - that's a cross-origin request, and without CORS the browser blocks it before it ever
    // reaches a controller. This is exactly why Swagger UI (served BY this API, so same-origin)
    // can log in fine while the Angular app's login silently fails with no useful server-side
    // error to show. Dev-only policy - the production build talks to the API through Nginx on
    // the same origin (see environment.ts's relative "/api" apiBaseUrl), so it needs no CORS at
    // all; a real deployment should scope this to your actual frontend origin(s) instead of
    // hardcoding localhost.
    const string DevCorsPolicy = "DevCors";
    builder.Services.AddCors(options =>
    {
        options.AddPolicy(DevCorsPolicy, policy => policy
            .WithOrigins("http://localhost:4200")
            .AllowAnyHeader()
            .AllowAnyMethod());
    });

    builder.Services.AddCcaasInfrastructure(builder.Configuration);

    var app = builder.Build();

    // Fresh-server bootstrap is opt-in outside Development. This keeps normal production
    // startup non-mutating while allowing the documented first deployment to create the
    // schema and the temporary demo tenant before the flag is turned off.
    if (builder.Configuration.GetValue("Bootstrap:MigrateDatabase", false))
    {
        using var migrationScope = app.Services.CreateScope();
        var migrationDb = migrationScope.ServiceProvider
            .GetRequiredService<CCaaS.Infrastructure.Persistence.CcaasDbContext>();
        await migrationDb.Database.MigrateAsync();
    }

    if (app.Environment.IsDevelopment()
        || builder.Configuration.GetValue("Bootstrap:SeedDemoData", false))
    {
        app.UseSwagger();
        app.UseSwaggerUI();

        // Dev-only bootstrap: on a brand new database there is otherwise no way to log in at
        // all (register needs an existing TenantId; the only endpoint that creates a tenant
        // requires a PlatformAdmin role nobody can hold yet). See DevDataSeeder for the full
        // explanation. Safe to run every startup - it no-ops once a tenant exists.
        using var seedScope = app.Services.CreateScope();
        var db = seedScope.ServiceProvider.GetRequiredService<CCaaS.Infrastructure.Persistence.CcaasDbContext>();
        var passwordHasher = seedScope.ServiceProvider.GetRequiredService<CCaaS.Application.Identity.IPasswordHasher>();
        var seedLogger = seedScope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        await CCaaS.Infrastructure.Persistence.DevDataSeeder.SeedAsync(db, passwordHasher, seedLogger);
        await CCaaS.Infrastructure.Persistence.DevelopmentAppointmentSeeder.SeedAsync(db, seedLogger);
        await CCaaS.Infrastructure.Persistence.DevelopmentMasterDataSeeder.SeedAsync(db, seedLogger);
    }

    app.UseSerilogRequestLogging();
    app.UseHttpsRedirection();

    if (app.Environment.IsDevelopment())
        app.UseCors(DevCorsPolicy);

    app.UseAuthentication();
    app.UseRateLimiter();
    app.UseAuthorization();

    app.MapControllers();
    app.MapHub<CCaaS.Api.Hubs.AgentHub>("/hubs/agent");

    // Section 17 - "Health endpoints: /health/live and /health/ready with checks for SQL,
    // Redis, RabbitMQ, object storage and telephony connectivity."
    app.MapHealthChecks("/health/live");
    app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        Predicate = check => check.Tags.Contains("ready")
    });

    app.MapPrometheusScrapingEndpoint("/metrics");

    if (app.Environment.IsDevelopment()) app.UseHangfireDashboard("/hangfire");
    var recurringJobManager =
    app.Services.GetRequiredService<IRecurringJobManager>();

    HangfireJobs.ConfigureRecurringJobs(
        recurringJobManager,
        builder.Configuration.GetValue(
            "Hangfire:EnableIncompleteJobs",
            false));

    Log.Information("CCaaS API starting up");
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "CCaaS API terminated unexpectedly during startup");
}
finally
{
    Log.CloseAndFlush();
}
