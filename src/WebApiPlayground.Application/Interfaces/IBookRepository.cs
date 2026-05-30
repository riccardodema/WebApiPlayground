using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Application.Interfaces;

public interface IBookRepository
{
    Task<ICollection<Book>> GetAllAsync();
    Task<Book?> GetByIdAsync(int id);
    Task<Book> CreateAsync(Book book);
    Task<bool> DeleteAsync(int id);
}
