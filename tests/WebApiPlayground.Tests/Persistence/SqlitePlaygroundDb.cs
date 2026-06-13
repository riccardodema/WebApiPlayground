using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;

namespace WebApiPlayground.Tests.Persistence;

/// <summary>
/// Database relazionale REALE per gli unit test del layer di persistenza: SQLite in-memory
/// (niente Docker, millisecondi). Serve a testare il COMPORTAMENTO di query/paging/sort/outbox —
/// la mutation testing non può osservarlo coi soli mock. Unico adattamento, dichiarato: la colonna
/// <c>rowversion</c> è una feature SQL Server che SQLite non auto-mantiene → il concurrency token
/// viene neutralizzato qui; i percorsi di CONFLITTO di concorrenza restano coperti dagli
/// integration test su SQL Server vero (OptimisticConcurrencyTests).
/// </summary>
public sealed class SqlitePlaygroundDb : IDisposable
{
    private readonly SqliteConnection _connection;

    public PlaygroundDbContext Context { get; }

    private SqlitePlaygroundDb(SqliteConnection connection, PlaygroundDbContext context)
    {
        _connection = connection;
        Context = context;
    }

    public static SqlitePlaygroundDb Create()
    {
        // La connessione tiene in vita il database :memory: per tutta la durata del test.
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<PlaygroundDbContext>()
            .UseSqlite(connection)
            .Options;

        var context = new SqliteTestPlaygroundDbContext(options);
        context.Database.EnsureCreated();
        return new SqlitePlaygroundDb(connection, context);
    }

    /// <summary>Un secondo context sulla STESSA connessione (es. per asserire fuori dal change tracker).</summary>
    public PlaygroundDbContext CreateFreshContext()
    {
        var options = new DbContextOptionsBuilder<PlaygroundDbContext>()
            .UseSqlite(_connection)
            .Options;
        return new SqliteTestPlaygroundDbContext(options);
    }

    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }

    private sealed class SqliteTestPlaygroundDbContext : PlaygroundDbContext
    {
        public SqliteTestPlaygroundDbContext(DbContextOptions<PlaygroundDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // rowversion neutralizzata (vedi doc della classe): nessun token, nessuna generazione DB.
            modelBuilder.Entity<Book>().Property(b => b.RowVersion)
                .IsConcurrencyToken(false)
                .ValueGeneratedNever();
        }
    }
}
