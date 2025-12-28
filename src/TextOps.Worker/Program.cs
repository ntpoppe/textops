using Microsoft.Extensions.Logging;
using TextOps.Contracts.Execution;
using TextOps.Contracts.Orchestration;
using TextOps.Orchestrator.Orchestration;
using TextOps.Persistence;
using TextOps.Persistence.Queue;
using TextOps.Persistence.Repositories;
using TextOps.Worker;
using TextOps.Worker.Stub;

var builder = Host.CreateApplicationBuilder(args);

ConfigureWorkerOptions(builder.Services, builder.Configuration);
var dbConnStr = ConfigurePersistence(builder.Services, builder.Configuration);
ConfigureServices(builder.Services);

var host = builder.Build();

await ConfigureApplication(host, dbConnStr);

host.Run();

static void ConfigureWorkerOptions(IServiceCollection services, IConfiguration configuration)
{
    services.Configure<WorkerOptions>(configuration.GetSection("Worker"));
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

static void ConfigureServices(IServiceCollection services)
{
    services.AddScoped<IExecutionQueue, DatabaseExecutionQueue>();
    services.AddScoped<IRunOrchestrator, PersistentRunOrchestrator>();
    services.AddScoped<IWorkerExecutor, StubWorkerExecutor>();
    services.AddHostedService<WorkerHostedService>();
}

static async Task ConfigureApplication(IHost host, string dbConnStr)
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Worker");
    logger.LogInformation("Worker starting with Database={Database}", dbConnStr);
    
    await host.Services.EnsureDatabaseCreatedAsync();
}

