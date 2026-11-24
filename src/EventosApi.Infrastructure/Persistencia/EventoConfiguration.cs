using System.Text.Json;
using System.Text.Json.Serialization;
using EventosApi.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace EventosApi.Infrastructure.Persistencia;

public sealed class EventoConfiguration : IEntityTypeConfiguration<Evento>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly ValueConverter<JsonElement, string> CargaAJson = new(
        carga => carga.GetRawText(),
        texto => JsonSerializer.Deserialize<JsonElement>(texto));

    private static readonly ValueConverter<ResultadoEvento?, string?> ResultadoAJson = new(
        resultado => resultado == null ? null : JsonSerializer.Serialize(resultado, Json),
        texto => string.IsNullOrEmpty(texto) ? null : JsonSerializer.Deserialize<ResultadoEvento>(texto, Json));

    private static readonly ValueConverter<EstadoEvento, string> EstadoATexto = new(
        estado => estado.ToString().ToLowerInvariant(),
        texto => Enum.Parse<EstadoEvento>(texto, ignoreCase: true));

    public void Configure(EntityTypeBuilder<Evento> constructor)
    {
        constructor.ToTable("evento", "cola", tabla =>
        {
            tabla.HasCheckConstraint("ck_evento_id", "id ~ '^evt_[0-9A-HJKMNP-TV-Z]{26}$'");
            tabla.HasCheckConstraint(
                "ck_evento_estado",
                "estado in ('pendiente', 'reclamado', 'aplicado', 'fallido', 'muerto')");
            tabla.HasCheckConstraint("ck_evento_intentos", "intentos between 0 and 5");
            tabla.HasCheckConstraint(
                "ck_evento_lease",
                "(estado = 'reclamado') = (lease_hasta is not null)");
        });

        constructor.HasKey(evento => evento.Id);
        constructor.Property(evento => evento.Id).HasColumnName("id").HasMaxLength(30);
        constructor.Property(evento => evento.Tipo).HasColumnName("tipo").HasMaxLength(100).IsRequired();
        constructor.Property(evento => evento.Carga)
            .HasColumnName("carga")
            .HasColumnType("jsonb")
            .HasConversion(CargaAJson)
            .IsRequired();
        constructor.Property(evento => evento.PublicadoEn).HasColumnName("publicado_en").IsRequired();
        constructor.Property(evento => evento.Estado)
            .HasColumnName("estado")
            .HasConversion(EstadoATexto)
            .HasMaxLength(20)
            .IsRequired();
        constructor.Property(evento => evento.Intentos).HasColumnName("intentos").IsRequired();
        constructor.Property(evento => evento.LeaseHasta).HasColumnName("lease_hasta");
        constructor.Property(evento => evento.DisponibleEn).HasColumnName("disponible_en");
        constructor.Property(evento => evento.EntregaAbandonada)
            .HasColumnName("entrega_abandonada")
            .HasDefaultValue(false)
            .IsRequired();
        constructor.Property(evento => evento.Resultado)
            .HasColumnName("resultado")
            .HasColumnType("jsonb")
            .HasConversion(ResultadoAJson);

        constructor.HasIndex(evento => new { evento.Estado, evento.DisponibleEn })
            .HasDatabaseName("ix_evento_estado_disponible");
        constructor.HasIndex(evento => evento.LeaseHasta).HasDatabaseName("ix_evento_lease");
    }
}
