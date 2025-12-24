using TextOps.Contracts.Execution;
using TextOps.Contracts.Orchestration;
using TextOps.Orchestrator.Orchestration;
using TextOps.Persistence;
using TextOps.Persistence.Queue;
using TextOps.Persistence.Repositories;
using TextOps.Worker;
using TextOps.Worker.Stub;

var builder = Host.CreateApplicationBuilder(args);

// Configure worker options
builder.Services.Configure<WorkerOptions>(
    builder.Configuration.GetSection("Worker"));

// Configure persistence (same as DevApi)
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

// Register database queue
builder.Services.AddScoped<IExecutionQueue, DatabaseExecutionQueue>();

// Register orchestrator (for execution callbacks)
builder.Services.AddScoped<IRunOrchestrator, PersistentRunOrchestrator>();

// Register stub executor (replace with real executor in production)
builder.Services.AddScoped<IWorkerExecutor, StubWorkerExecutor>();

// Register worker hosted service
builder.Services.AddHostedService<WorkerHostedService>();

var host = builder.Build();

// Ensure database exists (tables must be created by DevApi first or via migrations)
await host.Services.EnsureDatabaseCreatedAsync();

host.Run();

