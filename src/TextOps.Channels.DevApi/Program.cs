using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using TextOps.Contracts.Execution;
using TextOps.Contracts.Orchestration;
using TextOps.Contracts.Parsing;
using TextOps.Execution;
using TextOps.Orchestrator.Orchestration;
using TextOps.Orchestrator.Parsing;
using TextOps.Persistence;
using TextOps.Persistence.Queue;
using TextOps.Persistence.Repositories;
using TextOps.Worker.Stub;

var builder = WebApplication.CreateBuilder(args);

ConfigureControllers(builder.Services);
var dbConnStr = ConfigurePersistence(builder.Services, builder.Configuration);
ConfigureOrchestrator(builder.Services);
var queueProvider = ConfigureExecutionQueue(builder.Services, builder.Configuration);

var app = builder.Build();

await ConfigureApplication(app, dbConnStr, queueProvider);

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
                        .SelectMany(x => x.Value?.Errors ?? Enumerable.Empty<Microsoft.AspNetCore.Mvc.ModelBinding.ModelError>())
                        .Select(x => x.ErrorMessage))
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
        var dbConnStr = connectionStrings.GetValue<string>("Postgres")
            ?? throw new InvalidOperationException("PostgreSQL connection string not configured");
        services.AddTextOpsPostgres(dbConnStr);
        return dbConnStr;
    }
    else
    {
        var dbConnStr = connectionStrings.GetValue<string>("Sqlite") ?? "Data Source=textops.db";
        services.AddTextOpsSqlite(dbConnStr);
        return dbConnStr;
    }
}

static void ConfigureOrchestrator(IServiceCollection services)
{
    services.AddScoped<IRunOrchestrator, PersistentRunOrchestrator>();
    services.AddSingleton<IIntentParser, DeterministicIntentParser>();
}

static string ConfigureExecutionQueue(IServiceCollection services, IConfiguration configuration)
{
    var queueProvider = configuration.GetValue<string>("Queue:Provider") ?? "InMemory";
    
    if (queueProvider.Equals("Database", StringComparison.OrdinalIgnoreCase))
    {
        services.AddScoped<IExecutionQueue, DatabaseExecutionQueue>();
        services.AddScoped<IExecutionDispatcher>(sp => sp.GetRequiredService<IExecutionQueue>());
    }
    else
    {
        services.AddSingleton<InMemoryExecutionQueue>();
        services.AddSingleton<IExecutionQueue>(sp => sp.GetRequiredService<InMemoryExecutionQueue>());
        services.AddSingleton<IExecutionDispatcher>(sp => sp.GetRequiredService<InMemoryExecutionQueue>());
        
        services.AddScoped<IWorkerExecutor>(sp =>
        {
            var orchestrator = sp.GetRequiredService<IRunOrchestrator>();
            return new StubWorkerExecutor(orchestrator);
        });
        
        services.AddHostedService<ExecutionHostedService>();
    }
    
    return queueProvider;
}

static async Task ConfigureApplication(WebApplication app, string dbConnStr, string queueProvider)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DevApi");
    logger.LogInformation("DevApi starting with Database={Database}, Queue={Queue}", dbConnStr, queueProvider);
    
    await app.Services.EnsureDatabaseCreatedAsync();
    
    app.UseHttpsRedirection();
    app.MapControllers();
}
