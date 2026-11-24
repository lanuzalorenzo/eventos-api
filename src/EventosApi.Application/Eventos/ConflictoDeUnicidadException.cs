namespace EventosApi.Application.Eventos;

/// <summary>
/// Otra sesión insertó el mismo identificador entre la lectura y el alta. La
/// capa de persistencia la traduce desde la violación de clave única y el caso
/// de uso la reintenta como actualización.
/// </summary>
public sealed class ConflictoDeUnicidadException : Exception
{
    public ConflictoDeUnicidadException()
        : base("Otra sesión insertó el mismo identificador.")
    {
    }

    public ConflictoDeUnicidadException(Exception innerException)
        : base("Otra sesión insertó el mismo identificador.", innerException)
    {
    }
}
