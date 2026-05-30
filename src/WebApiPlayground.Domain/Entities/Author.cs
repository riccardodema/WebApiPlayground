using System.ComponentModel.DataAnnotations;

namespace WebApiPlayground.Domain.Entities;

public class Author
{
    [Key]
    public int Id { get; set; }
    public string FullName { get; set; } = string.Empty;
    public ICollection<Book> Books { get; set; } = new List<Book>();
}
