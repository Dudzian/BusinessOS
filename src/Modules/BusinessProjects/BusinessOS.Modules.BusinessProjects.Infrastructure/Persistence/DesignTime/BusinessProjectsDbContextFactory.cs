using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence.DesignTime;

public sealed class BusinessProjectsDbContextFactory : IDesignTimeDbContextFactory<BusinessProjectsDbContext>
{ public BusinessProjectsDbContext CreateDbContext(string[] args) { var o = new DbContextOptionsBuilder<BusinessProjectsDbContext>().UseSqlite("Data Source=businessos-design.db", x => x.MigrationsHistoryTable("__EFMigrationsHistory_BusinessProjects")).Options; return new(o); } }
