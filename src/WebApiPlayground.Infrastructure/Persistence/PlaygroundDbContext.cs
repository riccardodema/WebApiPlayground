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
        modelBuilder.Entity<Author>()
            .ToTable("Authors");

        modelBuilder.Entity<Book>()
            .ToTable("Books")
            .HasOne(b => b.Author)
            .WithMany(a => a.Books)
            .HasForeignKey(b => b.AuthorId);

        base.OnModelCreating(modelBuilder);
    }
}
