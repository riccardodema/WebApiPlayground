# WebApiPlayground — Claude Router

## Quando leggere cosa

| Task | File da leggere |
|------|-----------------|
| Aggiungere risorsa REST | `.claude/context/architecture.md` + `.claude/context/conventions.md` |
| Capire struttura/dipendenze | `.claude/context/architecture.md` |
| Scrivere codice (entity/dto/service/repo/controller) | `.claude/context/conventions.md` |
| Package, URL, comandi dotnet/ef | `.claude/context/stack.md` |
| Prima di usare Swagger/Swashbuckle o configurare OpenAPI | `.claude/lessons.md` |
| Errori ricorrenti o approcci da evitare | `.claude/lessons.md` |

## Quick reference

```
Run:   dotnet run --project src/WebApiPlayground.Api/WebApiPlayground.Api.csproj
Test:  dotnet test tests/WebApiPlayground.Tests/WebApiPlayground.Tests.csproj
UI:    http://localhost:5242/scalar/v1
JSON:  http://localhost:5242/openapi/v1.json
```

## Skills disponibili

- `/scaffold <NomeRisorsa>` — genera tutti i file Clean Architecture per una nuova risorsa
- `/migration <NomeMigration>` — aggiunge e applica una migration EF Core
- `/run` — avvia il progetto con istruzioni per connection string

## Commit convention

`<type>[scope]: <desc>` — tipi: `feat` `fix` `chore` `refactor` `test` `docs` `ci`

Esempio: `feat(books): add pagination to GetAllBooks endpoint`
