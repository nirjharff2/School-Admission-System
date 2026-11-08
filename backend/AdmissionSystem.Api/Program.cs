using System.Text.Json.Serialization;
using AdmissionSystem.Api.Data;
using AdmissionSystem.Api.Endpoints;
using AdmissionSystem.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Port 8080 is the contract. It is only a default: --urls, ASPNETCORE_URLS or ASPNETCORE_HTTP_PORTS take precedence,
// e.g. to run a second instance on another port.
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["http_ports"]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

// Read lazily (the first time a DbContext is built) so settings supplied by a test host are seen, then reused.
string? databaseConnection = null;
builder.Services.AddDbContext<AdmissionDbContext>(options =>
{
    databaseConnection ??= DatabaseConnection.FromConfiguration(builder.Configuration);
    options.UseNpgsql(databaseConnection);
});

builder.Services.AddSingleton(TimeProvider.System);
// Resolved lazily from IConfiguration (env var HOLD_DURATION_SECONDS) and validated at startup below.
builder.Services.AddSingleton(sp => HoldSettings.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => SweeperSettings.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(sp => ClassWriteGate.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddHostedService<ExpirySweeper>();

// JSON: camelCase (web default), enums as strings (HELD, CONFIRMED, ...), timestamps as ISO-8601 UTC with a trailing Z.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.Converters.Add(new UtcSecondsDateTimeConverter());
});

// Bad request bodies must throw in every environment so they reach ApiExceptionHandler and get the uniform error body.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

_ = app.Services.GetRequiredService<HoldSettings>(); // fail fast on an invalid HOLD_DURATION_SECONDS
_ = app.Services.GetRequiredService<SweeperSettings>(); // ... and on an invalid EXPIRY_SWEEP_INTERVAL_SECONDS

await using (var scope = app.Services.CreateAsyncScope())
{
    await DatabaseMigrator.MigrateAsync(scope.ServiceProvider.GetRequiredService<AdmissionDbContext>());
}

app.UseExceptionHandler();
// Unknown routes and wrong methods carry no body by default; give them the uniform one.
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    var body = response.StatusCode switch
    {
        StatusCodes.Status404NotFound => new ErrorBody(ErrorCodes.NotFound, "The requested resource does not exist."),
        StatusCodes.Status405MethodNotAllowed => new ErrorBody(ErrorCodes.MethodNotAllowed, "This method is not allowed on this resource."),
        _ => new ErrorBody(ErrorCodes.InvalidRequest, "The request could not be processed."),
    };
    await response.WriteAsJsonAsync(body);
});
app.UseCors();

app.MapClassEndpoints();
app.MapHoldEndpoints();

app.Run();

public partial class Program;
