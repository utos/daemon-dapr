using Blobs.Domain;
using Blobs.Infrastructure.Data;
using Blobs.Infrastructure.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Blobs.Api.Extensions;

/// <summary>
/// The blobs module's composition point. Holds no gRPC service yet — <c>BlobService</c> follows —
/// but the module is composed from here rather than from <c>Blobs.Infrastructure</c> because
/// <c>API.Main</c> composes <c>*.Api</c> projects only, and a shortcut now would be an edge to
/// unpick later.
/// </summary>
public static class BlobsExtensions
{
    public static IHostApplicationBuilder AddBlobsModule(
        this IHostApplicationBuilder builder, string databaseName)
    {
        builder.AddNpgsqlDbContext<BlobsDbContext>(databaseName);

        builder.Services.AddScoped<IBlobRepository, EfBlobRepository>();

        return builder;
    }

    /// <summary>
    /// Applies the module's migrations, as the execution module does with its own: the database is
    /// ephemeral in development and the tables have to exist before anything writes a blob.
    /// </summary>
    public static WebApplication UseBlobsModule(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BlobsDbContext>().Database.Migrate();

        return app;
    }
}
