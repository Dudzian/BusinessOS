using BusinessOS.Modules.Companies.Domain;
using Microsoft.EntityFrameworkCore;

namespace BusinessOS.Modules.Companies.Infrastructure.Persistence;

public sealed class CompaniesDbContext(DbContextOptions<CompaniesDbContext> options) : DbContext(options)
{
    public DbSet<Company> Companies => Set<Company>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new Configurations.CompanyConfiguration());
    }
}
