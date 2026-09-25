using Common.Blobs;
using Common.ExecutionLifecycle;
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

        builder.Services.Configure<BlobOptions>(
            builder.Configuration.GetSection(BlobOptions.SectionName));

        // What the execution module consumes. Registered here because this module owns the
        // implementation, and consumed through the contract in `common/` because the two modules do
        // not reference each other.
        builder.Services.AddScoped<IBlobAccess, BlobAccess>();
        builder.Services.AddScoped<IBlobReclamation, BlobReclaimer>();
        builder.Services.AddScoped<IBlobAdmission, BlobAdmitter>();

        // How this module learns a run tree finished, which is what makes the sweep accurate rather
        // than merely eventual. Registered as the shared contract, so the execution module announces
        // the ending without knowing anyone is listening.
        builder.Services.AddScoped<IExecutionLifecycleListener, RunTreeEndingListener>();

        builder.Services.AddHostedService<BlobSweepService>();

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
