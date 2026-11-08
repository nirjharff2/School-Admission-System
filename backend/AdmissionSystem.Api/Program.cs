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

builder.Services.AddDbContext<AdmissionDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Default");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        throw new InvalidOperationException(
            "No database connection string. Set the ConnectionStrings__Default environment variable.");
    }
    options.UseNpgsql(connectionString);
});

builder.Services.AddSingleton(TimeProvider.System);

// JSON: camelCase (web default) and enums as strings (HELD, CONFIRMED, ...).
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Bad request bodies must throw in every environment so they reach ApiExceptionHandler and get the uniform error body.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AdmissionDbContext>().Database.Migrate();
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

app.Run();

public partial class Program;
