# Lessons Learned — Approcci sbagliati e soluzioni

Ogni entry documenta un approccio che ha causato problemi e la soluzione adottata.
Leggere prima di configurare dipendenze, UI, o infrastruttura.

---

## [L01] Swashbuckle non compatibile con .NET 10

**Approccio errato:** usare `Swashbuckle.AspNetCore` come in .NET 8/9.  
**Errore:** `TypeLoadException` su `GetSwagger` a runtime.  
**Causa:** Swashbuckle 7.x non supporta .NET 10.  
**Soluzione:** sostituire con `Microsoft.AspNetCore.OpenApi` (10.0.0) + `Scalar.AspNetCore` (2.6.0).

```csharp
// Program.cs
builder.Services.AddOpenApi();
app.MapOpenApi();
app.MapScalarApiReference();
```

**URL da usare:** `/scalar/v1` (non `/swagger`).  
**Aggiornare anche:** `launchSettings.json` e `.vscode/launch.json` se referenziano `/swagger`.

---

<!-- Template per nuove entry:
## [L0N] Titolo breve

**Approccio errato:** ...  
**Errore:** ...  
**Causa:** ...  
**Soluzione:** ...
-->
