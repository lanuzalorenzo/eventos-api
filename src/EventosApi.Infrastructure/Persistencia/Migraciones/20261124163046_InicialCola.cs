using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventosApi.Infrastructure.Persistencia.Migraciones
{
    /// <inheritdoc />
    public partial class InicialCola : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "cola");

            migrationBuilder.CreateTable(
                name: "evento",
                schema: "cola",
                columns: table => new
                {
                    id = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    tipo = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    carga = table.Column<string>(type: "jsonb", nullable: false),
                    publicado_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    estado = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    intentos = table.Column<int>(type: "integer", nullable: false),
                    lease_hasta = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    disponible_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    entrega_abandonada = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    resultado = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evento", x => x.id);
                    table.CheckConstraint("ck_evento_estado", "estado in ('pendiente', 'reclamado', 'aplicado', 'fallido', 'muerto')");
                    table.CheckConstraint("ck_evento_id", "id ~ '^evt_[0-9A-HJKMNP-TV-Z]{26}$'");
                    table.CheckConstraint("ck_evento_intentos", "intentos between 0 and 5");
                    table.CheckConstraint("ck_evento_lease", "(estado = 'reclamado') = (lease_hasta is not null)");
                });

            migrationBuilder.CreateIndex(
                name: "ix_evento_estado_disponible",
                schema: "cola",
                table: "evento",
                columns: new[] { "estado", "disponible_en" });

            migrationBuilder.CreateIndex(
                name: "ix_evento_lease",
                schema: "cola",
                table: "evento",
                column: "lease_hasta");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "evento",
                schema: "cola");

            migrationBuilder.DropSchema(name: "cola");
        }
    }
}
