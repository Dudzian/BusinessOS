using BusinessOS.Modules.BusinessProjects.Domain;
using Microsoft.EntityFrameworkCore;
namespace BusinessOS.Modules.BusinessProjects.Infrastructure.Persistence;

public sealed class BusinessProjectsDbContext(DbContextOptions<BusinessProjectsDbContext> options) : DbContext(options)
{
    public DbSet<BusinessProject> BusinessProjects => Set<BusinessProject>();
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.ApplyConfiguration(new Configurations.BusinessProjectConfiguration());
}
