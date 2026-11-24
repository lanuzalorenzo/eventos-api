using EventosApi.Domain;
using Microsoft.EntityFrameworkCore;

namespace EventosApi.Infrastructure.Persistencia;

/// <summary>
/// Esquema <c>cola</c>: el servicio no conoce el dominio del consumidor.
/// Una sola tabla de eventos y las reglas del contrato como restricciones.
/// </summary>
public sealed class ColaDbContext(DbContextOptions<ColaDbContext> opciones) : DbContext(opciones)
{
    public DbSet<Evento> Eventos => Set<Evento>();

    protected override void OnModelCreating(ModelBuilder modelo)
    {
        modelo.HasDefaultSchema("cola");
        modelo.ApplyConfiguration(new EventoConfiguration());
    }
}
