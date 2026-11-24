using EventosApi.Infrastructure.Persistencia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EventosApi.Infrastructure.Salud;

public interface ISondaBase
{
    Task<bool> RespondeAsync(CancellationToken cancelacion);
}

/// <summary>
/// Comprueba la base con una conexión real. El contexto es scoped, así que la
/// sonda abre su propio ámbito y no depende del ciclo de la petición.
/// </summary>
public sealed class SondaBase(IServiceScopeFactory fabrica) : ISondaBase
{
    public async Task<bool> RespondeAsync(CancellationToken cancelacion)
    {
        await using var ambito = fabrica.CreateAsyncScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<ColaDbContext>();
        return await contexto.Database.CanConnectAsync(cancelacion);
    }
}

public sealed class BaseDatosHealthCheck(ISondaBase sonda) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext contexto,
        CancellationToken cancelacion = default)
    {
        try
        {
            return await sonda.RespondeAsync(cancelacion)
                ? HealthCheckResult.Healthy("La base responde.")
                : HealthCheckResult.Unhealthy("La base no responde.");
        }
        catch (Exception excepcion) when (excepcion is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("La base no responde.");
        }
    }
}
