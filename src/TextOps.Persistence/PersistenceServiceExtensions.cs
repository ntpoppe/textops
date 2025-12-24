using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TextOps.Persistence.Repositories;

namespace TextOps.Persistence;

/// <summary>
/// Extension methods for configuring persistence services.
/// </summary>
public static class PersistenceServiceExtensions
{
    /// <summary>
    /// Adds TextOps persistence with SQLite (for development).
    /// </summary>
    public static IServiceCollection AddTextOpsSqlite(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<TextOpsDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped<IRunRepository, EfRunRepository>();

        return services;
    }

    /// <summary>
    /// Adds TextOps persistence with PostgreSQL (for production).
    /// </summary>
    public static IServiceCollection AddTextOpsPostgres(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<TextOpsDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IRunRepository, EfRunRepository>();

        return services;
    }

    /// <summary>
    /// Ensures the database is created and applies any pending migrations.
    /// Call this at application startup.
    /// </summary>
    public static async Task EnsureDatabaseCreatedAsync(this IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TextOpsDbContext>();
        await db.Database.EnsureCreatedAsync();
    }
}

