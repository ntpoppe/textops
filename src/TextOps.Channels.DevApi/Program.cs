using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TextOps.Contracts.Execution;
using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Parsing;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;
using TextOps.Execution;
using TextOps.Persistence;

var builder = WebApplication.CreateBuilder(args);

ConfigureControllers(builder.Services);
var databaseConnectionString = ConfigurePersistence(builder.Services, builder.Configuration);
ConfigureOrchestrator(builder.Services);
var queueProvider = ConfigureExecutionQueue(builder.Services, builder.Configuration);

var app = builder.Build();

await ConfigureApplication(app, databaseConnectionString, queueProvider);

app.Run();

static void ConfigureControllers(IServiceCollection services)
{
    services.AddControllers()
        .ConfigureApiBehaviorOptions(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var problemDetails = new ProblemDetails
                {
                    Title = "Validation Error",
                    Status = StatusCodes.Status400BadRequest,
                    Detail = string.Join(" ", context.ModelState
                        .SelectMany(modelStateEntry => modelStateEntry.Value?.Errors ?? Enumerable.Empty<Microsoft.AspNetCore.Mvc.ModelBinding.ModelError>())
                        .Select(modelError => modelError.ErrorMessage))
                };
                return new BadRequestObjectResult(problemDetails);
            };
        })
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });
}

static string ConfigurePersistence(IServiceCollection services, IConfiguration configuration)
{
    var persistenceProvider = configuration.GetValue<string>("Persistence:Provider") ?? "Sqlite";
    var connectionStrings = configuration.GetSection("Persistence:ConnectionStrings");
    
    if (persistenceProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
    {
        var databaseConnectionString = connectionStrings.GetValue<string>("Postgres")
            ?? throw new InvalidOperationException("PostgreSQL connection string not configured");
        services.AddTextOpsPostgres(databaseConnectionString);
        return databaseConnectionString;
    }
    else
    {
        var databaseConnectionString = connectionStrings.GetValue<string>("Sqlite") ?? "Data Source=textops.db";
        services.AddTextOpsSqlite(databaseConnectionString);
        return databaseConnectionString;
    }
}

static void ConfigureOrchestrator(IServiceCollection services)
{
    services.AddScoped<IRunOrchestrator, PersistentRunOrchestrator>();
    services.AddSingleton<IIntentParser, DeterministicIntentParser>();
}

static string ConfigureExecutionQueue(IServiceCollection services, IConfiguration configuration)
{
    services.AddScoped<IExecutionQueue, DatabaseExecutionQueue>();
    return "Database";
}

static async Task ConfigureApplication(WebApplication app, string databaseConnectionString, string queueProvider)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DevApi");
    logger.LogInformation("DevApi starting with Database={Database}, Queue={Queue}", databaseConnectionString, queueProvider);
    
    await app.Services.EnsureDatabaseCreatedAsync();
    
    // Only enable HTTPS redirection in non-development environments or when HTTPS is explicitly configured
    var environment = app.Environment;
    if (!environment.IsDevelopment() || app.Configuration["ASPNETCORE_HTTPS_PORT"] != null)
    {
        app.UseHttpsRedirection();
    }
    
    app.MapControllers();
}
