using BusinessOS.Modules.Budgeting.Application;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence;

public interface IBudgetingDatabaseLifecycle { Task InitializeAsync(CancellationToken cancellationToken); Task<IReadOnlyList<string>> PendingAsync(CancellationToken cancellationToken); }
internal sealed class BudgetingDatabaseLifecycle(IDbContextFactory<BudgetingDbContext> factory, string path) : IBudgetingDatabaseLifecycle
{
    public async Task InitializeAsync(CancellationToken ct) { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!); await using var db = await factory.CreateDbContextAsync(ct); await db.Database.MigrateAsync(ct); if (!await db.Database.CanConnectAsync(ct) || (await db.Database.GetPendingMigrationsAsync(ct)).Any()) throw new InvalidOperationException("Budgeting migration verification failed."); }
    public async Task<IReadOnlyList<string>> PendingAsync(CancellationToken ct) { if (!File.Exists(path)) return ["new-database"]; await using var db = await factory.CreateDbContextAsync(ct); return (await db.Database.GetPendingMigrationsAsync(ct)).ToArray(); }
}
public static class BudgetingPersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddBudgetingPersistence(this IServiceCollection services, string databasePath)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        services.AddPooledDbContextFactory<BudgetingDbContext>(o => o.UseSqlite(cs, x => x.MigrationsHistoryTable("__EFMigrationsHistory_Budgeting")));
        services.AddTransient<IBudgetingStore, BudgetingStore>(); services.AddTransient<IActualCostsStore, ActualCostsStore>(); services.AddTransient<IForecastCostsStore, ForecastCostsStore>(); services.AddTransient<IBudgetVarianceReadStore, BudgetVarianceReadStore>(); services.AddSingleton<IBudgetingDatabaseLifecycle>(sp => new BudgetingDatabaseLifecycle(sp.GetRequiredService<IDbContextFactory<BudgetingDbContext>>(), databasePath)); return services;
    }
}
