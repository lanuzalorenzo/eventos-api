using System.Net.Http.Headers;
using System.Security.Claims;
using LL.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EventosApi.Tests;

/// <summary>Clientes con token del emisor local que monta el host en Testing.</summary>
internal static class ClienteDePrueba
{
    public static readonly string[] TodosLosPermisos =
    [
        "eventos.publicar",
        "eventos.consumir",
        "eventos.leer",
    ];

    public static LocalJwtIssuer EmisorDePrueba(this WebApplicationFactory<Program> fabrica) =>
        fabrica.Services.GetRequiredService<LocalJwtIssuer>();

    public static HttpClient ConPermisos(
        this WebApplicationFactory<Program> fabrica,
        params string[] permisos)
    {
        var emisor = fabrica.EmisorDePrueba();
        var token = emisor.CreateTokenWithPermissions(
            permisos.Length == 0 ? TodosLosPermisos : permisos,
            claimType: ClaimTypes.Role);

        return fabrica.ConToken(token);
    }

    public static HttpClient ConToken(this WebApplicationFactory<Program> fabrica, string token)
    {
        var cliente = fabrica.CreateClient();
        cliente.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return cliente;
    }
}
