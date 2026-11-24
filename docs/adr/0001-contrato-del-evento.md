# ADR-0001 — El sobre del evento, sus estados y su caducidad

> **Estado:** Aceptada · 2026-11-24
> **Ámbito:** `contracts/evento.v1.schema.json`, `contracts/eventos.v1.openapi.json` y `src/EventosApi.Domain/Evento.cs`.
> **Evidencia:** contrato JSON y OpenAPI versionados, con 35 pruebas correctas en la solución (33 de contrato y 2 de salud): transiciones válidas y prohibidas, fencing por entrega, espera creciente y paridad entre los dos contratos y el dominio; los cinco comandos de `## Verificación` del repositorio en verde.
> **Relacionada con:** [contrato JSON](../../contracts/evento.v1.schema.json) y [contrato OpenAPI](../../contracts/eventos.v1.openapi.json).

## 1. Contexto

El servicio guarda trabajo que produce otro proceso: en la primera versión, el cierre de una sesión. No conoce el dominio y no debe aprenderlo. Un contrato implícito, repartido entre la documentación del proyecto y lo que cada consumidor suponga, produce dos fallos: un evento que se entrega dos veces sin que nadie lo note, o uno que se pierde porque el estado cambió por una ruta que nadie había previsto.

Antes de escribir la primera tabla hay que fijar qué viaja, qué estados existen, quién puede moverlos y cuándo caduca una entrega. Ese es el objeto de este ADR. La persistencia y los endpoints llegan después; aquí solo se decide el contrato y se deja probado el autómata de estados.

## 2. Decisión

### 2.1. Un sobre único y una ruta versionada

Todo tipo de trabajo viaja en el mismo sobre. La carga es opaca para el servicio: solo se comprueba que es un objeto JSON de hasta 64 KiB en UTF-8. El consumidor filtra por `tipo` y define el esquema de `carga`.

| Campo | Tipo | Regla |
|---|---|---|
| `id` | ULID con prefijo `evt_` | Único; publicar dos veces el mismo identificador no duplica |
| `tipo` | texto | `cierre.sesion` en v1; minúsculas separadas por puntos, hasta 100 caracteres |
| `carga` | objeto JSON | Opaca; máximo 64 KiB |
| `publicado_en` | instante UTC | Lo pone el servicio en la primera publicación |
| `estado` | `pendiente`, `reclamado`, `aplicado`, `fallido`, `muerto` | Solo por las transiciones de abajo |
| `intentos` | entero 0–5 | Sube en cada entrega |
| `lease_hasta` | instante o nulo | 300 s desde el reclamo; al vencer, el evento vuelve a `pendiente` |
| `resultado` | objeto o nulo | En ack, `{ "hash": "<commit>" }`; en nack o muerto, `{ "motivo": "<texto>" }` |

**Publicar dos veces.** Si el identificador no existe, el evento nace en `pendiente`. Si existe, está `pendiente` o `fallido` y no perdió ninguna entrega, se reemplazan `tipo` y `carga` y se conservan `publicado_en`, `intentos`, la espera y el resultado. Si está `reclamado`, `aplicado` o `muerto`, o si una entrega venció sin ack ni nack, la publicación se rechaza con `409`: cambiar la carga de un evento entregado o ya aplicado rompería el hash que prueba el cierre, cambiar la de uno muerto lo resucitaría, y un evento que perdió el lease pudo dejar un consumidor vivo con la carga anterior. La actualización no es un control de propiedad; los portadores de `eventos.publicar` son procesos propios y el identificador es un ULID, no un secreto.

El sobre se versiona en `contracts/evento.v1.schema.json` (JSON Schema 2020-12) y la superficie HTTP en `contracts/eventos.v1.openapi.json` (OpenAPI 3.1). La versión va en la ruta, `/v1`: una v2 puede convivir con la v1 en el mismo host. Las pruebas comprueban que los cinco estados del dominio y los dos contratos dicen lo mismo, que las seis rutas y sus siete operaciones siguen declaradas con su permiso y que el patrón de `tipo` no se ha separado entre esquema y OpenAPI.

### 2.2. Estados y transiciones

Transiciones válidas:

| Desde | Hacia | Cuándo |
|---|---|---|
| `pendiente` | `reclamado` | Un consumidor pide el siguiente evento del tipo; se fija el lease |
| `reclamado` | `aplicado` | Ack con el hash del commit, el lease vigente y la entrega vigente |
| `reclamado` | `fallido` | Nack con el motivo, el lease vigente y la entrega vigente |
| `reclamado` | `pendiente` | El lease vence sin ack ni nack |
| `fallido` | `pendiente` | Se cumple la espera y quedan entregas |
| `fallido` | `muerto` | Se cumple la espera y ya se hicieron las cinco entregas |
| `pendiente` | `muerto` | Un sexto reclamo encuentra un evento que ya agotó las cinco entregas |

Prohibidas: cualquier otra, en particular todo lo que salga de `aplicado` o de `muerto`.

### 2.3. Cinco entregas y muertos

`intentos` cuenta entregas, no fallos: se suma al reclamar. El tope es cinco. El quinto nack deja el evento en `fallido`; al disponerlo, con la espera cumplida, pasa a `muerto` porque ya no quedan entregas. Un evento que pierde cinco leases sin ack ni nack también se aparta: el sexto reclamo no entrega, lo pasa a `muerto` con el motivo `cinco entregas sin ack`. Así la regla del contrato —«a la sexta, va a muertos»— y el «muerto al quinto intento» dicen lo mismo: no existe una sexta entrega.

El motivo del último nack se conserva en `resultado` hasta que un ack lo sustituye por el hash.

### 2.4. Espera creciente

Tras un nack, el evento queda disponible en `30 s × 2^intentos`, con tope de 300 s. El exponente es el número de entregas ya hechas, tal como está escrito en el contrato: la primera espera es de 60 s, después 120 s y 240 s, y a partir de la cuarta toca el tope. La espera es interna: `disponible_en` no viaja en el sobre v1, porque el estado `fallido` ya dice que el evento no está en la cola.

### 2.5. Fencing por entrega, no solo por lease

Comprobar que el lease está vigente no basta. Si A reclama, su lease vence y B vuelve a reclamar, un ack tardío de A puede llegar antes del nuevo vencimiento y marcar `aplicado` la entrega de B: dos consumidores aplican el mismo cierre. Por eso el ack y el nack llevan el número de entrega que cierran —el `intentos` que traía el evento reclamado— y se rechazan con `409` si no coincide con el actual. La persistencia añadirá además el control de concurrencia de la fila; el dominio ya no acepta el cierre de una entrega anterior.

Por la misma razón, la carga se congela en cuanto una entrega se abandona: el ack viejo se rechaza por el número de entrega, pero el efecto del consumidor rezagado ya pudo ocurrir. Un evento que perdió el lease sin ack ni nack no admite cambios de `tipo` ni de `carga`; el aviso queda en el `409` de la publicación.

### 2.6. Autómata puro

Las reglas viven en `Evento`, sin EF Core, sin HTTP y sin reloj propio: cada operación recibe el instante. Una transición no permitida lanza `TransicionInvalidaException`; el host la traducirá a `409` con problem details. Que el dominio no dependa de nada permite probar las transiciones sin base, que es justo lo que exige este contrato. El host mapea la entidad a un DTO: la espera interna `DisponibleEn` no se serializa.

## 3. Alternativas descartadas

- **Un sobre por tipo de evento.** El servicio tendría que conocer y validar cada carga. La opacidad de 64 KiB deja el esquema en el consumidor y mantiene al servicio sin dominio.
- **Estados implícitos en filas de ack y nack.** Dos fuentes de verdad para lo mismo: una fila de ack y una columna de estado acaban discrepando. El estado es una columna y las operaciones son transiciones.
- **Lease sin fencing.** Ya descartado en §2.5: un consumidor lento puede ackear la entrega de otro. El lease vigente solo acota el tiempo, no identifica la entrega.
- **Reintentos sin contador ni muertos.** Un evento que siempre falla se reentrega para siempre y llena la cola. El contador y el estado `muerto` lo apartan con su historial.
- **Generar el OpenAPI solo desde el código.** Los endpoints no existen todavía; el contrato se escribe antes y guía la implementación y las pruebas. Cuando existan, el documento versionado se contrasta con la generación automática en CI, sin que una sustituya a la otra.
- **Resultado como texto libre.** No se distingue el hash del motivo y la consulta no puede verificar el cierre. El objeto con una sola clave obliga a elegir.
- **Devolver todos los eventos en una lista.** El historial crece sin tope. La lectura va paginada con un límite máximo de 200 y un cursor opaco.

## 4. Consecuencias

- La tabla `evento` de la fase de persistencia usa `estado` con `check` sobre los cinco valores e `intentos` con `check (intentos between 0 and 5)`; el dominio ya no deja pasar otro valor.
- `EntregaAbandonada` también se persiste (`entrega_abandonada`, por defecto `false`). Sin esa columna, al recargar el evento se perdería el congelado de la carga y volvería el caso que cierra §2.5.
- La entrega es al menos una vez. El consumidor debe ser idempotente, el hash del commit es la prueba de aplicación y el ack o el nack exigen la entrega vigente.
- La autenticación y los permisos `eventos.publicar`, `eventos.consumir` y `eventos.leer` forman parte del contrato, pero se implementan en la fase de acceso; hasta entonces el OpenAPI describe el objetivo y no una capacidad ya ejecutada. Cada operación declara su permiso con `x-permiso`.
- La sonda se queda en `/health`, la ruta que ya expone el host; el contrato no inventa un alias.
- Un cambio incompatible del sobre o de las rutas abre `v2` bajo `/v2` y conserva `/v1` hasta que no queden consumidores. El número va en el nombre del archivo y en la versión de `info`.
- `disponible_en` es interno. Si el panel necesita ver cuándo toca el reintento, se añade en `v2`; no se cuela en `v1`.
