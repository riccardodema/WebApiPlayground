using System.ComponentModel.DataAnnotations;

namespace WebApiPlayground.Domain.Entities;

public class Book
{
    [Key]
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public int AuthorId { get; set; }
    public Author? Author { get; set; }
}
