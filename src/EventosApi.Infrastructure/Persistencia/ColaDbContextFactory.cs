using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EventosApi.Infrastructure.Persistencia;

/// <summary>
/// Contexto de diseño para <c>dotnet ef</c>. La cadena es local y no se usa en
/// ejecución: el host la toma de <c>ConnectionStrings:Cola</c>.
/// </summary>
public sealed class ColaDbContextFactory : IDesignTimeDbContextFactory<ColaDbContext>
{
    public ColaDbContext CreateDbContext(string[] args)
    {
        var cadena = Environment.GetEnvironmentVariable("ConnectionStrings__Cola")
            ?? "Host=localhost;Port=5432;Database=eventos;Username=eventos;Password=eventos-local";

        var opciones = new DbContextOptionsBuilder<ColaDbContext>()
            .UseNpgsql(cadena)
            .Options;

        return new ColaDbContext(opciones);
    }
}
