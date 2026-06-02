using Microsoft.EntityFrameworkCore;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Infrastructure.Persistence;

public class PlaygroundDbContext : DbContext
{
    public PlaygroundDbContext(DbContextOptions<PlaygroundDbContext> options) : base(options) { }

    public DbSet<Book> Books { get; set; }
    public DbSet<Author> Authors { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // The authoritative schema lives in the SQL project (database/), deployed
        // via DACPAC. The mappings below mirror it 1:1 so the EF model never drifts
        // from the database (column types, lengths, nullability, FK behaviour).
        modelBuilder.Entity<Author>(entity =>
        {
            entity.ToTable("Authors");
            entity.HasKey(a => a.Id);
            entity.Property(a => a.FullName)
                .HasMaxLength(100)
                .IsUnicode(false)   // VARCHAR(100)
                .IsRequired();
        });

        modelBuilder.Entity<Book>(entity =>
        {
            entity.ToTable("Books");
            entity.HasKey(b => b.Id);
            entity.Property(b => b.Title)
                .HasMaxLength(100)  // NVARCHAR(100)
                .IsRequired();

            entity.HasOne(b => b.Author)
                .WithMany(a => a.Books)
                .HasForeignKey(b => b.AuthorId)
                .HasConstraintName("FK_Books_Authors")
                .OnDelete(DeleteBehavior.NoAction);
        });

        base.OnModelCreating(modelBuilder);
    }
}
