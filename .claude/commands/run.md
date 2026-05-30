# /run — Avvio progetto

## Avvio

```bash
dotnet run --project src/WebApiPlayground.Api/WebApiPlayground.Api.csproj
```

Con hot reload:
```bash
dotnet watch --project src/WebApiPlayground.Api/WebApiPlayground.Api.csproj
```

## URL

| Risorsa | URL |
|---------|-----|
| Scalar UI | `http://localhost:5242/scalar/v1` |
| OpenAPI JSON | `http://localhost:5242/openapi/v1.json` |

> Non usare `/swagger` — vedere `.claude/lessons.md` [L01]

## Connection string

Richiesta in `src/WebApiPlayground.Api/appsettings.Development.json`.  
Se assente, l'app si avvia ma le chiamate al DB falliscono a runtime.  
Formato e istruzioni: `.claude/context/stack.md`
