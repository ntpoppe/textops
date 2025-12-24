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

// Add services to the container
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
    {
        // Use ProblemDetails for validation errors
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

// Configure persistence based on settings
var persistenceProvider = builder.Configuration.GetValue<string>("Persistence:Provider") ?? "Sqlite";
var connectionStrings = builder.Configuration.GetSection("Persistence:ConnectionStrings");

if (persistenceProvider.Equals("Postgres", StringComparison.OrdinalIgnoreCase))
{
    var connStr = connectionStrings.GetValue<string>("Postgres")
        ?? throw new InvalidOperationException("PostgreSQL connection string not configured");
    builder.Services.AddTextOpsPostgres(connStr);
}
else
{
    var connStr = connectionStrings.GetValue<string>("Sqlite") ?? "Data Source=textops.db";
    builder.Services.AddTextOpsSqlite(connStr);
}

// Register orchestrator and parser
builder.Services.AddScoped<IRunOrchestrator, PersistentRunOrchestrator>();
builder.Services.AddSingleton<IIntentParser, DeterministicIntentParser>();

// Configure execution queue based on settings
var queueProvider = builder.Configuration.GetValue<string>("Queue:Provider") ?? "InMemory";
if (queueProvider.Equals("Database", StringComparison.OrdinalIgnoreCase))
{
    // Database queue - dispatches are stored in DB, processed by standalone worker
    builder.Services.AddScoped<IExecutionQueue, DatabaseExecutionQueue>();
    builder.Services.AddScoped<IExecutionDispatcher>(sp => sp.GetRequiredService<IExecutionQueue>());
    
    // No local worker - standalone TextOps.Worker polls the database
}
else
{
    // In-memory queue - dispatches processed in-process (development mode)
    builder.Services.AddSingleton<InMemoryExecutionQueue>();
    builder.Services.AddSingleton<IExecutionQueue>(sp => sp.GetRequiredService<InMemoryExecutionQueue>());
    builder.Services.AddSingleton<IExecutionDispatcher>(sp => sp.GetRequiredService<InMemoryExecutionQueue>());
    
    // Register stub worker executor for in-process execution
    builder.Services.AddScoped<IWorkerExecutor>(sp =>
    {
        var orchestrator = sp.GetRequiredService<IRunOrchestrator>();
        return new StubWorkerExecutor(orchestrator);
    });
    
    // Register execution hosted service for in-process queue processing
    builder.Services.AddHostedService<ExecutionHostedService>();
}

var app = builder.Build();

// Ensure database is created on startup
await app.Services.EnsureDatabaseCreatedAsync();

// Configure the HTTP request pipeline
app.UseHttpsRedirection();
app.MapControllers();

app.Run();
