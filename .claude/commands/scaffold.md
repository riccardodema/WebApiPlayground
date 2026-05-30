# /scaffold — Scaffolding Clean Architecture per una nuova risorsa

Genera tutti i file necessari per esporre una nuova risorsa REST seguendo la Clean Architecture del progetto.

**Uso**: `/scaffold <NomeRisorsa>` (es. `/scaffold Publisher`)

Sostituire `<Name>` con il nome della risorsa in PascalCase (es. `Publisher`) e `<name>` con camelCase (es. `publisher`).

---

## 1. Entità — `src/WebApiPlayground.Domain/Entities/<Name>.cs`

```csharp
using System.ComponentModel.DataAnnotations;

namespace WebApiPlayground.Domain.Entities;

public class <Name>
{
    [Key]
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    // aggiungere altre proprietà
}
```

---

## 2. DTO response — `src/WebApiPlayground.Application/DTOs/<Name>Dto.cs`

```csharp
namespace WebApiPlayground.Application.DTOs;

public record <Name>Dto(int Id, string Name /*, altri campi */);
```

## 3. DTO request — `src/WebApiPlayground.Application/DTOs/Create<Name>Dto.cs`

```csharp
namespace WebApiPlayground.Application.DTOs;

public record Create<Name>Dto(string Name /*, altri campi */);
```

---

## 4. Interfaccia repository — `src/WebApiPlayground.Application/Interfaces/I<Name>Repository.cs`

Restituisce entità di dominio, non DTO.

```csharp
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Application.Interfaces;

public interface I<Name>Repository
{
    Task<ICollection<<Name>>> GetAllAsync();
    Task<<Name>?> GetByIdAsync(int id);
    Task<<Name>> CreateAsync(<Name> entity);
    Task<bool> DeleteAsync(int id);
}
```

---

## 5. Interfaccia service — `src/WebApiPlayground.Application/Interfaces/I<Name>sService.cs`

```csharp
using WebApiPlayground.Application.DTOs;

namespace WebApiPlayground.Application.Interfaces;

public interface I<Name>sService
{
    Task<ICollection<<Name>Dto>> GetAllAsync();
    Task<<Name>Dto?> GetByIdAsync(int id);
    Task<<Name>Dto> CreateAsync(Create<Name>Dto dto);
    Task<bool> DeleteAsync(int id);
}
```

---

## 6. Implementazione service — `src/WebApiPlayground.Application/Services/<Name>sService.cs`

```csharp
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Application.Services;

public class <Name>sService : I<Name>sService
{
    private readonly I<Name>Repository _repository;

    public <Name>sService(I<Name>Repository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    public async Task<ICollection<<Name>Dto>> GetAllAsync()
    {
        var items = await _repository.GetAllAsync();
        return items.Select(MapToDto).ToList();
    }

    public async Task<<Name>Dto?> GetByIdAsync(int id)
    {
        var item = await _repository.GetByIdAsync(id);
        return item is null ? null : MapToDto(item);
    }

    public async Task<<Name>Dto> CreateAsync(Create<Name>Dto dto)
    {
        var entity = new <Name> { Name = dto.Name };
        var created = await _repository.CreateAsync(entity);
        return MapToDto(created);
    }

    public async Task<bool> DeleteAsync(int id) => await _repository.DeleteAsync(id);

    private static <Name>Dto MapToDto(<Name> entity) => new(entity.Id, entity.Name);
}
```

---

## 7. Implementazione repository — `src/WebApiPlayground.Infrastructure/Repositories/<Name>Repository.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Domain.Entities;
using WebApiPlayground.Infrastructure.Persistence;

namespace WebApiPlayground.Infrastructure.Repositories;

public class <Name>Repository : I<Name>Repository
{
    private readonly PlaygroundDbContext _context;

    public <Name>Repository(PlaygroundDbContext context)
    {
        _context = context;
    }

    public async Task<ICollection<<Name>>> GetAllAsync() =>
        await _context.<Name>s.ToListAsync();

    public async Task<<Name>?> GetByIdAsync(int id) =>
        await _context.<Name>s.FindAsync(id);

    public async Task<<Name>> CreateAsync(<Name> entity)
    {
        _context.<Name>s.Add(entity);
        await _context.SaveChangesAsync();
        return entity;
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var entity = await _context.<Name>s.FindAsync(id);
        if (entity is null) return false;
        _context.<Name>s.Remove(entity);
        await _context.SaveChangesAsync();
        return true;
    }
}
```

---

## 8. Aggiornare DbContext — `src/WebApiPlayground.Infrastructure/Persistence/PlaygroundDbContext.cs`

```csharp
// Aggiungere DbSet
public DbSet<<Name>> <Name>s { get; set; }

// In OnModelCreating aggiungere:
modelBuilder.Entity<<Name>>().ToTable("<Name>s");
```

---

## 9. Registrare DI — `src/WebApiPlayground.Infrastructure/DependencyInjection.cs`

```csharp
services.AddScoped<I<Name>Repository, <Name>Repository>();
```

E in `src/WebApiPlayground.Application/DependencyInjection.cs`:
```csharp
services.AddScoped<I<Name>sService, <Name>sService>();
```

---

## 10. Controller — `WebApiPlayground/Controllers/<Name>sController.cs`

```csharp
using Microsoft.AspNetCore.Mvc;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;

namespace WebApiPlayground.Controllers;

[ApiController]
[Route("api/[controller]")]
public class <Name>sController : ControllerBase
{
    private readonly I<Name>sService _service;

    public <Name>sController(I<Name>sService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _service.GetAllAsync());

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById([FromRoute] int id)
    {
        var result = await _service.GetByIdAsync(id);
        return result is not null ? Ok(result) : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] Create<Name>Dto dto)
    {
        var created = await _service.CreateAsync(dto);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete([FromRoute] int id)
    {
        var deleted = await _service.DeleteAsync(id);
        return deleted ? NoContent() : NotFound();
    }
}
```

---

## 11. Unit test — `tests/WebApiPlayground.Tests/Services/<Name>sServiceTests.cs`

```csharp
using Moq;
using WebApiPlayground.Application.DTOs;
using WebApiPlayground.Application.Interfaces;
using WebApiPlayground.Application.Services;
using WebApiPlayground.Domain.Entities;

namespace WebApiPlayground.Tests.Services;

public class <Name>sServiceTests
{
    private readonly Mock<I<Name>Repository> _repositoryMock = new();
    private readonly <Name>sService _sut;

    public <Name>sServiceTests() => _sut = new <Name>sService(_repositoryMock.Object);

    [Fact]
    public async Task GetAllAsync_ReturnsMappedDtos_WhenItemsExist()
    {
        _repositoryMock.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new List<<Name>> { new() { Id = 1, Name = "Test" } });

        var result = await _sut.GetAllAsync();

        Assert.Single(result);
        Assert.Equal("Test", result.First().Name);
    }
    // aggiungere altri test
}
```

---

## 12. Migration

```bash
dotnet ef migrations add Add<Name>Entity \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project WebApiPlayground
dotnet ef database update \
  --project src/WebApiPlayground.Infrastructure \
  --startup-project WebApiPlayground
```

---

## Checklist

- [ ] `Domain/Entities/<Name>.cs`
- [ ] `Application/DTOs/<Name>Dto.cs` + `Create<Name>Dto.cs`
- [ ] `Application/Interfaces/I<Name>Repository.cs` + `I<Name>sService.cs`
- [ ] `Application/Services/<Name>sService.cs`
- [ ] `Application/DependencyInjection.cs` aggiornato
- [ ] `Infrastructure/Repositories/<Name>Repository.cs`
- [ ] `Infrastructure/Persistence/PlaygroundDbContext.cs` aggiornato (DbSet + OnModelCreating)
- [ ] `Infrastructure/DependencyInjection.cs` aggiornato
- [ ] `API/Controllers/<Name>sController.cs`
- [ ] `Tests/Services/<Name>sServiceTests.cs`
- [ ] Migration creata e applicata
