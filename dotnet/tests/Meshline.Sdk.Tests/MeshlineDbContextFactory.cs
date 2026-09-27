using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Meshline.Tests;

sealed class MeshlineDbContextFactory : IDesignTimeDbContextFactory<MeshlineDbContext>
{
    public MeshlineDbContextFactory()
    {
    }

    public MeshlineDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<MeshlineDbContext>().UseSqlite("Data Source=:memory:").Options);
}
