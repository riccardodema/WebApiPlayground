# /scaffold — Scaffolding Clean Architecture

**Uso:** `/scaffold <NomeRisorsa>` (PascalCase, es. `Publisher`)

Genera tutti i file per una nuova risorsa REST seguendo le convenzioni in `.claude/context/conventions.md`.

---

## Ordine di generazione

1. `Domain/Entities/<Name>.cs` — POCO entity
2. `Application/DTOs/<Name>Dto.cs` — response record
3. `Application/DTOs/Create<Name>Dto.cs` — request record
4. `Application/Interfaces/I<Name>Repository.cs` — interfaccia repository
5. `Application/Interfaces/I<Name>sService.cs` — interfaccia service
6. `Application/Services/<Name>sService.cs` — implementazione service
7. `Infrastructure/Repositories/<Name>Repository.cs` — implementazione repository
8. `Infrastructure/Persistence/PlaygroundDbContext.cs` — aggiungere `DbSet<<Name>> <Name>s`
9. `Infrastructure/DependencyInjection.cs` — `AddScoped<I<Name>Repository, <Name>Repository>()`
10. `Application/DependencyInjection.cs` — `AddScoped<I<Name>sService, <Name>sService>()`
11. `API/Controllers/<Name>sController.cs` — controller con GET/GET{id}/POST/DELETE
12. `Tests/Services/<Name>sServiceTests.cs` — unit test con Moq

---

## Note critiche

- Repository restituisce entità di dominio, **mai DTO** — il mapping è nel service
- Controller dipende da `I{Name}sService`, **mai dal repository**
- `ArgumentNullException.ThrowIfNull()` nel costruttore di service e controller
- DI scope: `AddScoped` per entrambi

## Dopo lo scaffold

Creare la migration: `/migration Add<Name>Entity`
