<img src="assets/iconos/eventos-api.svg" width="64" alt="Icono de Eventos API">

# EventosApi

Este servicio es la caja de encargos que comparten dos procesos: uno deja un trabajo cerrado y otro lo recoge, lo hace y avisa de que está hecho. Funciona como una taquilla de consigna: el servicio guarda el paquete, la llave y el plazo, pero no abre el paquete ni entiende lo que lleva dentro.

Es un servicio de eventos del cierre en .NET 10. En v1 guarda el cierre de una sesión y lo entrega a un consumidor con un lease, sin conocer el dominio. El productor publica un sobre opaco; el consumidor lo reclama, lo aplica y confirma con el hash del commit, o lo rechaza con un motivo. El servicio solo custodia el sobre, sus estados y sus intentos.

La carga es opaca: su esquema lo fija el consumidor. El servicio no interpreta el contenido ni aprende el dominio. Esa frontera es la que permite reutilizarlo para otros trabajos sin tocar la cola.

## Contrato

La fuente es [`contracts/eventos.v1.openapi.json`](contracts/eventos.v1.openapi.json) para la superficie HTTP y [`contracts/evento.v1.schema.json`](contracts/evento.v1.schema.json) para el sobre. La versión va en la ruta (`/v1`) para que una v2 pueda convivir. Las reglas y su motivo están en [`docs/adr/0001`](docs/adr/0001-contrato-del-evento.md).

| Método | Ruta | Qué hace | Permiso |
|---|---|---|---|
| `POST` | `/v1/eventos` | Publica un evento. Idempotente por `id`: crea con 201, reemplaza si sigue pendiente o fallido con 200, y responde 409 si está reclamado, aplicado o muerto, o si perdió una entrega sin cerrarla. | `eventos.publicar` |
| `GET` | `/v1/eventos` | Lista por estado con paginación por cursor. | `eventos.leer` |
| `POST` | `/v1/eventos/siguiente` | Reclama el evento disponible más antiguo del `tipo` pedido y abre un lease de 300 s. Sin trabajo, responde 204. | `eventos.consumir` |
| `GET` | `/v1/eventos/{id}` | Consulta un evento por identificador. | `eventos.leer` |
| `POST` | `/v1/eventos/{id}/ack` | Marca el evento como aplicado con el hash del commit y el número de entrega (`intentos`) de la entrega vigente. | `eventos.consumir` |
| `POST` | `/v1/eventos/{id}/nack` | Marca la entrega como fallida con un motivo y el número de entrega (`intentos`), y programa el reintento. | `eventos.consumir` |
| `GET` | `/health` | Comprueba proceso y base de datos; es anónimo. | — |

El sobre lleva `id` (ULID con prefijo `evt_`), `tipo`, `carga` (hasta 64 KiB en UTF-8), `publicado_en`, `estado`, `intentos`, `lease_hasta` y `resultado`. Los estados son `pendiente`, `reclamado`, `aplicado`, `fallido` y `muerto`; solo cambian por las transiciones del contrato. Un evento admite cinco entregas: el quinto nack lo deja fallido; al cumplirse la espera, el siguiente reclamo lo pasa a muerto. La espera crece entre intentos, con un máximo de cinco minutos.

La autenticación con Entra ID y los permisos por operación están declarados en el contrato y se implementan en la fase de acceso; hoy la API local no exige token.

## Estructura

- `src/EventosApi.Domain`: el evento, sus estados y sus invariantes. Sin dependencias.
- `src/EventosApi.Application`: casos de uso y puertos. Solo depende de `Domain`.
- `src/EventosApi.Infrastructure`: PostgreSQL, migraciones y sonda de base. Depende de `Application` y `Domain`.
- `src/EventosApi.Api`: host mínimo; compone los servicios y expone los endpoints.
- `contracts/`: JSON Schema y OpenAPI versionados del sobre y de la superficie HTTP.
- `docs/adr/`: decisiones estructurales con su motivo.
- `tests/EventosApi.Tests`: contrato, dominio, persistencia, integración contra PostgreSQL y salud.

La regla de dependencias es `Domain ← Application ← Infrastructure ← Api`. No la debilites.

## Puesta en marcha

Con Docker, API y PostgreSQL 18 en un solo paso. El build necesita un PAT de solo lectura del feed de GitHub Packages, que exige token incluso para leer paquetes públicos:

```bash
export GH_PACKAGES_TOKEN="<pat-de-solo-lectura>"
docker compose up -d --build
curl http://localhost:8080/health
```

`compose.yaml` fija credenciales locales que no sirven fuera de ese entorno y aplica la migración al arrancar (`Database:MigrarAlArrancar`). La API escucha en `127.0.0.1:8080`.

Para ejecutar el host contra una base ya levantada:

```bash
export GH_PACKAGES_TOKEN="<pat-de-solo-lectura>"
dotnet run --project src/EventosApi.Api
```

En Development, `appsettings.Development.json` ya trae la cadena local contra el PostgreSQL del compose. Fuera de Development no hay cadena por defecto: hay que exportar `ConnectionStrings__Cola` o el host no arranca. La documentación OpenAPI solo se publica en Development. `/api/saludo` es un eco de arranque heredado de la plantilla; no forma parte del contrato.

## Base de datos

El esquema es `cola` y la tabla, `evento`; la decisión está en [`docs/adr/0002`](docs/adr/0002-persistencia-postgres-esquema-cola.md). La unicidad del alta y el bloqueo de fila del reclamo viven en PostgreSQL; el fencing por entrega, en el dominio.

```bash
dotnet ef database update --project src/EventosApi.Infrastructure --startup-project src/EventosApi.Api
dotnet ef database update 0 --project src/EventosApi.Infrastructure --startup-project src/EventosApi.Api
```

## Controles

Los paquetes `LL.Security` y `LL.Hosting` aportan CORS con denegación por defecto, cabeceras de seguridad con la `Server` de Kestrel apagada, límite por IP, ProblemDetails, HTTPS/HSTS y salud anónima. La configuración entra por `Program.cs` y `appsettings.json`; los orígenes permitidos van en `Cors:AllowedOrigins`. La sonda `/health` queda fuera del cupo del limitador.

## Verificación

```bash
dotnet restore EventosApi.slnx
dotnet build EventosApi.slnx -c Release --no-restore
dotnet test EventosApi.slnx -c Release --no-build --no-restore
dotnet format EventosApi.slnx --verify-no-changes --no-restore
gitleaks detect --source . --redact --verbose --exit-code 2
```

El CI usa las acciones de [`core`](https://github.com/lanuzalorenzo/core) para compilar, escanear secretos, analizar el código y escanear dependencias e imagen; la construcción del contenedor, la comprobación de usuario no privilegiado y la revisión de dependencias van en el mismo workflow. Restaura los paquetes de [`shared-dotnet`](https://github.com/lanuzalorenzo/shared-dotnet). La versión de `LL.*` está fijada en `Directory.Packages.props`; se sube a mano cuando `shared-dotnet` publica una versión nueva.

Las pruebas de integración necesitan Docker: `PostgresFixture` levanta PostgreSQL 18 en un contenedor y sin él `dotnet test` falla.

## Docker

```bash
docker build --secret id=github_token,env=GH_PACKAGES_TOKEN -t eventos-api .
docker run --rm -p 127.0.0.1:8080:8080 \
  -e ConnectionStrings__Cola="Host=<host>;Port=5432;Database=eventos;Username=eventos;Password=<password>" \
  eventos-api
```

La imagen no trae cadena de base por defecto: sin `ConnectionStrings__Cola` el host no arranca. Para el compose completo, usa `compose.yaml`.

El contenedor ejecuta con usuario no privilegiado y escucha HTTP en el 8080. La redirección a HTTPS se aplica en cualquier entorno salvo `Testing` cuando hay un puerto HTTPS configurado; HSTS se añade fuera de Development. En el contenedor no hay puerto HTTPS: la terminación TLS corresponde al host o a la plataforma, así que la imagen no redirige por sí sola.

Licencia: MIT.
