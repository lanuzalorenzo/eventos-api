using EventosApi.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace EventosApi.Tests;

/// <summary>
/// PostgreSQL real para las pruebas de la cola: el bloqueo de fila, la clave
/// única y el vencimiento del lease no se pueden comprobar en un proveedor
/// en memoria.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer contenedor = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("eventos")
        .WithUsername("eventos")
        .WithPassword("eventos-local")
        .Build();

    public string CadenaConexion => contenedor.GetConnectionString();

    public async Task InitializeAsync()
    {
        await contenedor.StartAsync();

        await using var db = Contexto();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => contenedor.DisposeAsync().AsTask();

    public ColaDbContext Contexto() => new(
        new DbContextOptionsBuilder<ColaDbContext>()
            .UseNpgsql(CadenaConexion)
            .Options);
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
