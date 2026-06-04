using System.ComponentModel.DataAnnotations;

namespace WebApiPlayground.Domain.Entities;

public class Book
{
    [Key]
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int AuthorId { get; set; }
    public Author? Author { get; set; }

    /// <summary>
    /// Token di concorrenza ottimistica: una <c>rowversion</c> SQL Server auto-incrementata dal DB a
    /// ogni UPDATE della riga. EF Core la usa come concurrency token (<c>IsRowVersion</c>), così le
    /// scritture diventano condizionali (<c>WHERE Id=@id AND RowVersion=@expected</c>). Esposta in HTTP
    /// come ETag. POCO: il mapping vive in <c>PlaygroundDbContext</c> (Infrastructure), nessun attributo EF.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];
}
