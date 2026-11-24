# ADR-0002 — Persistencia en PostgreSQL y esquema `cola`

> **Estado:** Aceptada · 2026-11-24
> **Ámbito:** `src/EventosApi.Infrastructure/Persistencia`, la migración `InicialCola`, `compose.yaml` y la sonda de `/health`.
> **Evidencia:** migración aplicada, revertida y vuelta a aplicar contra PostgreSQL 18 en compose; `/health` responde 200 con la base y 503 al pararla; el contenedor aplica la migración al arrancar y la deja en `__EFMigrationsHistory`.
> **Relacionada con:** [ADR-0001](0001-contrato-del-evento.md), que fija el sobre y los estados.

## 1. Contexto

El contrato del evento ya existe, pero nada lo guarda. Hace falta una base que sobreviva al proceso, permita reclamar un evento con lease y sostenga las restricciones del contrato sin confiar en que el código las recuerde. El servicio no conoce el dominio del consumidor: solo guarda el sobre y su estado.

La base tiene que poder levantarse con el resto del servicio en local, aceptar `jsonb` para la carga opaca y dar una sonda real: que `/health` no diga «sano» cuando la base no responde.

## 2. Decisión

1. **PostgreSQL 18 con EF Core 10 y Npgsql.** Es la base que fija la estrategia del frente y la que soporta el bloqueo y la concurrencia que pedirán los siguientes ítems. La versión de la imagen va fijada en `compose.yaml`.
2. **Un esquema propio, `cola`, y una sola tabla, `evento`.** El servicio no comparte esquema con el dominio del consumidor, que llegará con sus propias tablas y su propio rol.
3. **Las reglas del contrato como restricciones de la tabla**, no solo en el dominio:
   - `estado in ('pendiente', 'reclamado', 'aplicado', 'fallido', 'muerto')`, en minúsculas como el contrato; el conversor traduce desde el enum.
   - `intentos between 0 and 5`.
   - `id ~ '^evt_[0-9A-HJKMNP-TV-Z]{26}$'`.
   - `(estado = 'reclamado') = (lease_hasta is not null)`.
4. **`carga` y `resultado` como `jsonb`.** La carga es opaca y no se normaliza; el resultado guarda `{ "hash": … }` o `{ "motivo": … }` sin columnas nuevas.
5. **`entrega_abandonada` se persiste.** Es la bandera interna que congela la carga cuando una entrega venció sin ack ni nack. Sin columna, al recargar el evento se perdería y volvería el caso que cierra el ADR-0001 §2.5.
6. **Índices por la consulta que existe:** `(estado, disponible_en)` para la cola y `lease_hasta` para localizar vencidos. No se añaden índices «por si acaso».
7. **La migración se aplica al arrancar solo si `Database:MigrarAlArrancar` está activo.** Compose lo activa para que `docker compose up` deje el servicio listo. En local se deja apagado y se usa `dotnet ef database update`; en producción se aplicará como paso previo, no desde cada réplica, cuando exista el despliegue.
8. **La sonda consulta la base.** `ISondaBase` abre un ámbito propio y ejecuta `CanConnectAsync`; el health check la traduce a `Healthy` o `Unhealthy`. Las pruebas sustituyen la sonda por una falsa para no depender de una base en el CI.

## 3. Alternativas descartadas

- **SQLite.** El servicio nace para varios consumidores y leases concurrentes; el bloqueo de escritura de SQLite y su modelo de archivo no encajan con el despliegue en contenedor. Además, el `jsonb` y las restricciones con expresiones regulares son de PostgreSQL.
- **Guardar el estado en ficheros JSON.** Es el problema que este frente sustituye: sin transacción, sin historial y sin restricciones.
- **Una tabla por estado.** Las transiciones obligarían a mover filas entre tablas y el historial se reparte; una columna de estado y restricciones es una sola verdad.
- **Un tipo `enum` nativo de PostgreSQL.** Cada valor nuevo exige migración de tipo y complica la reversión; el `check` sobre texto da la misma garantía y viaja mejor entre versiones.
- **Aplicar migraciones desde la imagen con `dotnet ef`.** La imagen final no lleva el SDK ni la herramienta; el arranque condicionado y el comando local cubren el caso sin meter tooling en producción.
- **Sonda que solo mira el proceso.** El contrato dice que `/health` comprueba proceso y base; un 200 con la base caída es una evidencia falsa.

## 4. Consecuencias

- `ConnectionStrings:Cola` es obligatoria: el host falla al arrancar si falta, en vez de arrancar sin persistencia. Compose la inyecta; `appsettings.Development.json` trae la local.
- La migración de ida y vuelta se prueba con `dotnet ef database update` y `dotnet ef database update 0`. El `Down` elimina la tabla y el esquema `cola`.
- La aplicación de migraciones al arrancar sirve para una instancia; con varias réplicas hará falta un paso de despliegue que la ejecute una vez, antes de levantar el servicio. Si la base no responde al arrancar, el proceso no llega a levantar `/health`; en compose, `depends_on: service_healthy` espera a que la base esté lista.
- La carga sigue siendo opaca: no se indexa ni se consulta por su contenido. Si el consumidor necesita consultas sobre ella, se resuelve en su esquema, no aquí.
- El rol de base de la API todavía no está separado del rol del consumidor. La separación de permisos llega con el dominio del cierre.
