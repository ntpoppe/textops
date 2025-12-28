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
var databaseConnectionString = ConfigurePersistence(builder.Services, builder.Configuration);
ConfigureServices(builder.Services);

var host = builder.Build();

await ConfigureApplication(host, databaseConnectionString);

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

static void ConfigureServices(IServiceCollection services)
{
    services.AddScoped<IExecutionQueue, DatabaseExecutionQueue>();
    services.AddScoped<IRunOrchestrator, PersistentRunOrchestrator>();
    services.AddScoped<IWorkerExecutor, StubWorkerExecutor>();
    services.AddHostedService<WorkerHostedService>();
}

static async Task ConfigureApplication(IHost host, string databaseConnectionString)
{
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Worker");
    logger.LogInformation("Worker starting with Database={Database}", databaseConnectionString);
    
    await host.Services.EnsureDatabaseCreatedAsync();
}

