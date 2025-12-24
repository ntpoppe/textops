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

// Register orchestrator
builder.Services.AddScoped<IRunOrchestrator, PersistentRunOrchestrator>();
builder.Services.AddSingleton<IIntentParser, DeterministicIntentParser>();

// Register execution queue, dispatcher, and reader
builder.Services.AddSingleton<InMemoryExecutionQueue>();
builder.Services.AddSingleton<IExecutionDispatcher>(sp => sp.GetRequiredService<InMemoryExecutionQueue>());
builder.Services.AddSingleton<IExecutionQueueReader>(sp => sp.GetRequiredService<InMemoryExecutionQueue>());

// Register worker executor
builder.Services.AddScoped<IWorkerExecutor>(sp =>
{
    var orchestrator = sp.GetRequiredService<IRunOrchestrator>();
    return new StubWorkerExecutor(orchestrator);
});

// Register execution hosted service
builder.Services.AddHostedService<ExecutionHostedService>();

var app = builder.Build();

// Ensure database is created on startup
await app.Services.EnsureDatabaseCreatedAsync();

// Configure the HTTP request pipeline
app.UseHttpsRedirection();
app.MapControllers();

app.Run();
