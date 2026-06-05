using Microsoft.EntityFrameworkCore;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Infrastructure.Persistence;

public class PlaygroundDbContext : DbContext
{
    public PlaygroundDbContext(DbContextOptions<PlaygroundDbContext> options) : base(options) { }

    public DbSet<Book> Books { get; set; }
    public DbSet<Author> Authors { get; set; }
    public DbSet<BookPopularitySnapshot> BookPopularitySnapshots { get; set; }

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

            // Optimistic concurrency: ROWVERSION auto-mantenuta da SQL Server. IsRowVersion la marca
            // come concurrency token store-generated → l'UPDATE/DELETE diventa condizionale
            // (WHERE Id=@id AND RowVersion=@original); 0 righe → DbUpdateConcurrencyException.
            entity.Property(b => b.RowVersion)
                .IsRowVersion();

            entity.HasOne(b => b.Author)
                .WithMany(a => a.Books)
                .HasForeignKey(b => b.AuthorId)
                .HasConstraintName("FK_Books_Authors")
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<BookPopularitySnapshot>(entity =>
        {
            entity.ToTable("BookPopularitySnapshots");

            // PK = FK verso Books (relazione 1:1). La chiave NON è store-generated: la fornisce il worker.
            entity.HasKey(s => s.BookId);
            entity.Property(s => s.BookId).ValueGeneratedNever();

            entity.Property(s => s.Source)
                .HasMaxLength(50)
                .IsUnicode(false)   // VARCHAR(50)
                .IsRequired();

            // Cascade: cancellando il libro sparisce anche il suo snapshot (nessuna riga orfana).
            entity.HasOne<Book>()
                .WithOne()
                .HasForeignKey<BookPopularitySnapshot>(s => s.BookId)
                .HasConstraintName("FK_BookPopularitySnapshots_Books")
                .OnDelete(DeleteBehavior.Cascade);
        });

        base.OnModelCreating(modelBuilder);
    }
}
