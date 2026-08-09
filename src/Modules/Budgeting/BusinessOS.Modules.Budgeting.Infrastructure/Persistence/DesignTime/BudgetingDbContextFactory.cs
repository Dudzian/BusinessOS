using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BusinessOS.Modules.Budgeting.Infrastructure.Persistence.DesignTime;

public sealed class BudgetingDbContextFactory : IDesignTimeDbContextFactory<BudgetingDbContext>
{
    public BudgetingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BudgetingDbContext>().UseSqlite("Data Source=businessos.db;Pooling=False", x => x.MigrationsHistoryTable("__EFMigrationsHistory_Budgeting")).Options;
        return new(options);
    }
}
