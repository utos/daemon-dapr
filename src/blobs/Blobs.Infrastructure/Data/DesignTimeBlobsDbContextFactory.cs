using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Blobs.Infrastructure.Data;

/// <summary>
/// What <c>dotnet ef migrations add</c> builds the context from. No database is contacted to add a
/// migration — only the model is needed — so the connection string is a placeholder, and this
/// project can be the target on its own:
/// <c>dotnet ef migrations add &lt;Name&gt; --project src/blobs/Blobs.Infrastructure</c>.
/// </summary>
public sealed class DesignTimeBlobsDbContextFactory : IDesignTimeDbContextFactory<BlobsDbContext>
{
    public BlobsDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BlobsDbContext>()
            .UseNpgsql("Host=localhost;Database=utos;Username=utos;Password=utos")
            .Options;

        return new BlobsDbContext(options);
    }
}
