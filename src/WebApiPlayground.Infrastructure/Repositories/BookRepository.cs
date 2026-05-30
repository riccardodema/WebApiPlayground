using Microsoft.EntityFrameworkCore;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;

namespace WebApiPlayground.Infrastructure.Repositories;

public class BookRepository : IBookRepository
{
    private readonly PlaygroundDbContext _context;

    public BookRepository(PlaygroundDbContext context)
    {
        _context = context;
    }

    public async Task<ICollection<Book>> GetAllAsync()
    {
        return await _context.Books
            .Include(b => b.Author)
            .ToListAsync();
    }

    public async Task<Book?> GetByIdAsync(int id)
    {
        return await _context.Books
            .Include(b => b.Author)
            .FirstOrDefaultAsync(b => b.Id == id);
    }

    public async Task<Book> CreateAsync(Book book)
    {
        _context.Books.Add(book);
        await _context.SaveChangesAsync();
        return await _context.Books
            .Include(b => b.Author)
            .FirstAsync(b => b.Id == book.Id);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var book = await _context.Books.FindAsync(id);
        if (book is null) return false;

        _context.Books.Remove(book);
        await _context.SaveChangesAsync();
        return true;
    }
}
