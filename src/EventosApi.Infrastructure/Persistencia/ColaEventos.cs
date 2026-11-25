using EventosApi.Application.Eventos;
using EventosApi.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EventosApi.Infrastructure.Persistencia;

/// <summary>
/// Implementa la cola sobre PostgreSQL. El bloqueo de fila y el salto de filas
/// bloqueadas son lo que hace que dos reclamos simultáneos no entreguen el
/// mismo evento dos veces.
/// </summary>
public sealed class ColaEventos(ColaDbContext db) : IColaEventos, IColaEnTransaccion
{
    public async Task<T> EnTransaccionAsync<T>(
        Func<IColaEnTransaccion, CancellationToken, Task<T>> trabajo,
        CancellationToken cancelacion)
    {
        await using var transaccion = await db.Database.BeginTransactionAsync(cancelacion);
        var resultado = await trabajo(this, cancelacion);
        await transaccion.CommitAsync(cancelacion);
        return resultado;
    }

    public async Task<Evento?> BuscarConBloqueoAsync(string id, CancellationToken cancelacion) =>
        await db.Eventos
            .FromSqlInterpolated($"SELECT * FROM cola.evento WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancelacion);

    public async Task<Evento?> BuscarAsync(string id, CancellationToken cancelacion) =>
        await db.Eventos
            .AsNoTracking()
            .SingleOrDefaultAsync(evento => evento.Id == id, cancelacion);

    public async Task<IReadOnlyList<Evento>> ListarAsync(
        EstadoEvento? estado,
        (DateTimeOffset PublicadoEn, string Id)? desde,
        int limite,
        CancellationToken cancelacion)
    {
        var consulta = db.Eventos.AsNoTracking();

        if (estado is { } filtro)
        {
            consulta = consulta.Where(evento => evento.Estado == filtro);
        }

        if (desde is var (publicadoEn, id))
        {
            // La clave de orden es (publicado_en, id); el cursor continúa
            // estrictamente después de la última fila entregada.
            consulta = consulta.Where(evento =>
                evento.PublicadoEn > publicadoEn
                || (evento.PublicadoEn == publicadoEn && evento.Id.CompareTo(id) > 0));
        }

        return await consulta
            .OrderBy(evento => evento.PublicadoEn)
            .ThenBy(evento => evento.Id)
            .Take(limite)
            .ToListAsync(cancelacion);
    }

    public void Agregar(Evento evento) => db.Eventos.Add(evento);

    public async Task<Evento?> SiguienteCandidatoAsync(
        string tipo,
        DateTimeOffset ahora,
        CancellationToken cancelacion) =>
        await db.Eventos
            .FromSqlInterpolated($"""
                SELECT * FROM cola.evento
                WHERE tipo = {tipo}
                  AND ((estado = 'pendiente'
                        AND (disponible_en IS NULL OR disponible_en <= {ahora}))
                       OR (estado = 'fallido' AND disponible_en <= {ahora})
                       OR (estado = 'reclamado' AND lease_hasta <= {ahora}))
                ORDER BY publicado_en, id
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """)
            .SingleOrDefaultAsync(cancelacion);

    public async Task GuardarAsync(CancellationToken cancelacion)
    {
        try
        {
            await db.SaveChangesAsync(cancelacion);
        }
        catch (DbUpdateException excepcion)
            when (excepcion.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // La entidad añadida no debe sobrevivir al reintento: la siguiente
            // transacción vuelve a leer la fila que ganó el alta.
            db.ChangeTracker.Clear();
            throw new ConflictoDeUnicidadException(excepcion);
        }
    }
}
