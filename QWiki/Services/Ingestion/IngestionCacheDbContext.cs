using Microsoft.EntityFrameworkCore;

namespace QWiki.Services.Ingestion;

// A DbContext that keeps track of which documents have been ingested.
// This makes it possible to avoid re-ingesting documents that have not changed,
// and to delete documents that have been removed from the underlying source.
public class IngestionCacheDbContext : DbContext
{
    public IngestionCacheDbContext(DbContextOptions<IngestionCacheDbContext> options) : base(options)
    {
    }

    public DbSet<IngestedDocument> Documents { get; set; } = default!;
    public DbSet<IngestedRecord> Records { get; set; } = default!;

    public static void Initialize(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<IngestionCacheDbContext>();
        db.Database.EnsureCreated();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Create composite key on (SourceId, Id) to allow same document IDs from different sources
        modelBuilder.Entity<IngestedDocument>()
            .HasKey(d => new { d.SourceId, d.Id });
        
        modelBuilder.Entity<IngestedDocument>()
            .HasMany(d => d.Records)
            .WithOne()
            .HasForeignKey(r => new { r.DocumentSourceId, r.DocumentId })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class IngestedDocument
{
    public required string Id { get; set; }
    public required string SourceId { get; set; }
    public required string Version { get; set; }
    public List<IngestedRecord> Records { get; set; } = [];
}

public class IngestedRecord
{
    public required string Id { get; set; }
    public required string DocumentSourceId { get; set; }
    public required string DocumentId { get; set; }
}
