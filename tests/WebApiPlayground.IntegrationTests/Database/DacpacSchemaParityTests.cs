using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WebApiPlayground.Api.Authorization;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Infrastructure.Persistence;
using Xunit;

namespace WebApiPlayground.IntegrationTests.Database;

/// <summary>
/// <b>Parità DACPAC ↔ modello EF.</b> Il resto della suite crea lo schema con <c>EnsureCreated</c>
/// (dal modello EF), ma in compose/produzione lo schema lo pubblica il DACPAC: se i due divergono,
/// i test restano verdi e l'app vera si rompe. Qui l'app gira CONTRO lo schema deployato dal
/// pacchetto e si verifica: (1) confronto strutturale tabella/colonna/tipo/nullabilità per OGNI
/// entità mappata; (2) ogni entità è leggibile (query reale); (3) i percorsi critici di scrittura
/// (IDENTITY, FK, rowversion/ETag, outbox → snapshot) e la paginazione sul seed reale.
/// </summary>
[Collection("Integration")]
public class DacpacSchemaParityTests : IClassFixture<DacpacDeployedApiFactory>
{
    private readonly DacpacDeployedApiFactory _factory;

    public DacpacSchemaParityTests(DacpacDeployedApiFactory factory)
    {
        _factory = factory;
    }

    // ---- (1) Confronto strutturale -------------------------------------------

    [Fact]
    public async Task Every_mapped_entity_matches_the_deployed_schema_column_by_column()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();

        var differences = new List<string>();

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            var tableName = entityType.GetTableName();
            if (tableName is null)
                continue; // entità senza tabella (keyless/view): fuori scope

            var schema = entityType.GetSchema() ?? "dbo";
            var dbColumns = await LoadColumnsAsync(db, schema, tableName);

            if (dbColumns.Count == 0)
            {
                differences.Add($"tabella mancante nel DACPAC: [{schema}].[{tableName}] (entità {entityType.ClrType.Name})");
                continue;
            }

            var storeObject = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(tableName, schema);
            foreach (var property in entityType.GetProperties())
            {
                var columnName = property.GetColumnName(storeObject);
                if (columnName is null)
                    continue;

                if (!dbColumns.TryGetValue(columnName, out var column))
                {
                    differences.Add($"colonna mancante: [{tableName}].[{columnName}] (proprietà {entityType.ClrType.Name}.{property.Name})");
                    continue;
                }

                var expectedType = NormalizeType(property.GetColumnType());
                if (!string.Equals(expectedType, column.StoreType, StringComparison.OrdinalIgnoreCase))
                    differences.Add(
                        $"tipo divergente su [{tableName}].[{columnName}]: EF '{expectedType}' vs DACPAC '{column.StoreType}'");

                if (property.IsNullable != column.IsNullable)
                    differences.Add(
                        $"nullabilità divergente su [{tableName}].[{columnName}]: EF {(property.IsNullable ? "NULL" : "NOT NULL")} vs DACPAC {(column.IsNullable ? "NULL" : "NOT NULL")}");
            }
        }

        Assert.True(differences.Count == 0,
            "Drift di schema tra modello EF e DACPAC:\n  - " + string.Join("\n  - ", differences));
    }

    // ---- (2) Ogni entità è leggibile sullo schema vero ------------------------

    [Fact]
    public async Task Every_mapped_entity_can_be_queried_against_the_deployed_schema()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();

        // SELECT reale (TOP 5, tutte le colonne mappate) per ogni entità: una colonna mancante o di
        // tipo incompatibile fa esplodere la query — copre anche ciò che il confronto strutturale
        // non vede (es. conversioni di lettura).
        var helper = typeof(DacpacSchemaParityTests)
            .GetMethod(nameof(QueryTopFiveAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (var entityType in db.Model.GetEntityTypes())
        {
            if (entityType.GetTableName() is null)
                continue;

            var task = (Task)helper.MakeGenericMethod(entityType.ClrType).Invoke(null, [db])!;
            await task; // un'eccezione qui nomina l'entità via stack — nessun catch di comodo
        }
    }

    private static async Task QueryTopFiveAsync<TEntity>(PlaygroundDbContext db) where TEntity : class =>
        await db.Set<TEntity>().AsNoTracking().Take(5).ToListAsync();

    // ---- (3) Percorsi critici sullo schema vero -------------------------------

    [Fact]
    public async Task Seeded_catalog_is_pageable_and_sortable_through_the_api()
    {
        var client = _factory.CreateClientWithScope(BooksPermissions.ScopeRead);

        var response = await client.GetAsync("/api/v1/books?pageNumber=2&pageSize=10&sortBy=title&sortDir=asc");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // Il post-deployment seed del DACPAC (100 libri) è arrivato davvero nel database.
        Assert.True(json.RootElement.GetProperty("totalCount").GetInt32() >= 100);
        Assert.Equal(10, json.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task Full_write_path_works_against_the_deployed_schema()
    {
        var writeClient = _factory.CreateClientWithScope(BooksPermissions.ScopeReadWrite);
        var readClient = _factory.CreateClientWithScope(BooksPermissions.ScopeRead);

        // CREATE: IDENTITY + FK verso un autore del seed + riga outbox nella stessa transazione.
        var authorId = await FirstSeededAuthorIdAsync();
        var title = $"Parity Probe {Guid.NewGuid():N}";
        var created = await writeClient.PostAsJsonAsync("/api/v1/books", new CreateBookDto(title, authorId));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var book = await created.Content.ReadFromJsonAsync<BookDto>();

        // UPDATE con If-Match: esercita la colonna rowversion (concurrency token) dello schema DACPAC.
        var current = await readClient.GetAsync($"/api/v1/books/{book!.Id}");
        var update = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/books/{book.Id}")
        {
            Content = JsonContent.Create(new UpdateBookDto($"{title} (rev)", authorId)),
        };
        update.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
        var updated = await writeClient.SendAsync(update);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        // OUTBOX → SNAPSHOT: drain deterministico; l'upsert scrive BookPopularitySnapshots sullo schema vero.
        await _factory.DrainOutboxAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        Assert.True(await db.OutboxMessages.AsNoTracking().AnyAsync(m => m.ProcessedAt != null));
        Assert.NotNull(await db.BookPopularitySnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.BookId == book.Id));
    }

    private async Task<int> FirstSeededAuthorIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PlaygroundDbContext>();
        return await db.Authors.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Id).FirstAsync();
    }

    // ---- Lettura dello schema deployato ---------------------------------------

    private sealed record DbColumn(string StoreType, bool IsNullable);

    private static async Task<Dictionary<string, DbColumn>> LoadColumnsAsync(
        PlaygroundDbContext db, string schema, string table)
    {
        var columns = new Dictionary<string, DbColumn>(StringComparer.OrdinalIgnoreCase);

        var connection = (SqlConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.name, t.name AS type_name, c.max_length, c.precision, c.scale, c.is_nullable
            FROM sys.columns c
            JOIN sys.types t ON t.user_type_id = c.user_type_id
            JOIN sys.tables tb ON tb.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = tb.schema_id
            WHERE s.name = @schema AND tb.name = @table
            """;
        command.Parameters.Add(new SqlParameter("@schema", schema));
        command.Parameters.Add(new SqlParameter("@table", table));

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            var storeType = RenderStoreType(
                reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4));
            columns[name] = new DbColumn(NormalizeType(storeType), reader.GetBoolean(5));
        }

        return columns;
    }

    /// <summary>Ricostruisce lo store type come lo scrive EF ("nvarchar(100)", "decimal(4,2)", …).</summary>
    private static string RenderStoreType(string typeName, short maxLength, byte precision, byte scale) =>
        typeName switch
        {
            "nvarchar" or "nchar" => maxLength == -1 ? $"{typeName}(max)" : $"{typeName}({maxLength / 2})",
            "varchar" or "char" or "varbinary" or "binary" =>
                maxLength == -1 ? $"{typeName}(max)" : $"{typeName}({maxLength})",
            "decimal" or "numeric" => $"{typeName}({precision},{scale})",
            "datetime2" or "datetimeoffset" or "time" => $"{typeName}({scale})",
            _ => typeName,
        };

    /// <summary>
    /// Normalizzazione per il confronto: sinonimi (timestamp = rowversion) e precisioni di default
    /// che EF omette ("datetimeoffset" ≡ "datetimeoffset(7)").
    /// </summary>
    private static string NormalizeType(string storeType)
    {
        var type = storeType.ToLowerInvariant();
        if (type == "timestamp")
            return "rowversion";
        foreach (var prefix in new[] { "datetime2", "datetimeoffset", "time" })
        {
            if (type == $"{prefix}(7)")
                return prefix;
        }

        return type;
    }
}
