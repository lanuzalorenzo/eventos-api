# ADR-0005 — Observabilidad de la cola

> **Estado:** Aceptada · 2026-11-26
> **Ámbito:** `src/EventosApi.Application/Eventos/Telemetria.cs` y `ServicioEventos.cs`, `appsettings.json` y las pruebas de observabilidad.
> **Evidencia:** 87 pruebas en verde en local; publicar, reclamar y ack dejan una traza con `evento.id` y suben sus contadores; el paso a muerto cuenta y deja log; la carga no aparece en los logs y el nivel de EF queda en `Warning`.
> **Relacionada con:** [ADR-0001](0001-contrato-del-evento.md), [ADR-0003](0003-publicacion-idempotente-y-reclamo-con-bloqueo.md) y [ADR-0004](0004-ack-nack-consulta-y-listado.md).

## 1. Contexto

La cola ya tenía contrato, persistencia y cierre de entrega, pero ninguna señal propia: un evento atascado o una entrega fallida solo se veía consultando la tabla. La observabilidad no puede depender de que cada operación escriba su log a mano ni de que el consumidor adivine los nombres.

La instrumentación tiene que cumplir tres cosas: una traza que permita seguir un evento por su identificador, métricas que distingan publicar, reclamar, aplicar, fallar y morir, y logs que nunca copien la carga opaca, que puede contener datos del cierre. Además, la carga no debe acabar en el log de comandos de EF.

## 2. Decisión

1. **Una sola fuente.** `Telemetria` en `Application` declara el `ActivitySource` y el `Meter` con el nombre `EventosApi`. El resto del código no crea fuentes ni nombres sueltos; cuando exista el paquete compartido de observabilidad, recogerá estos nombres.
2. **Una actividad por operación.** Publicar, reclamar, listar, consultar, ack y nack abren su actividad. Las que operan sobre un evento concreto ponen `evento.id`; reclamar añade `evento.tipo` e `intentos`, y publicar, `evento.estado`. Las etiquetas se ponen después de validar la entrada y de completar la operación, para no exportar identificadores inválidos ni estados que no se guardaron.
3. **Contadores de transición.** `eventos.publicados`, `eventos.reclamados`, `eventos.aplicados`, `eventos.fallidos` y `eventos.muertos` cuentan operaciones confirmadas. Los muertos se cuentan después del commit, no dentro de la transacción: una vuelta que se revierte no deja una muerte que no existe.
4. **Logs sin carga.** Cada cambio de estado deja una línea con el identificador y el estado; nunca la carga. La prueba de redacción publica un marcador y falla si aparece en cualquier log capturado.
5. **EF en `Warning`.** El log de comandos de EF puede incluir parámetros; queda en `Warning` para que el `jsonb` no se copie. Es defensa en profundidad: sin `EnableSensitiveDataLogging` EF ya no escribe los valores, pero el nivel no se relaja.
6. **Sin exportador todavía.** La instrumentación es local y se prueba con un `ActivityListener` y un `MeterListener`. El exportador y el colector llegan con el paquete compartido y el despliegue; este ADR solo fija los nombres y el contrato.

## 3. Alternativas descartadas

- **Instrumentar en los endpoints.** Repetiría la fuente y los nombres en cada ruta y dejaría fuera los casos de uso que no pasan por HTTP. La capa de aplicación es el punto único.
- **Registrar la carga en el log para depurar.** Convierte un log en una copia del estado y puede filtrar datos del cierre; la traza por identificador basta para seguir el evento.
- **Un gauge de profundidad por estado en este ítem.** Exige consultar la base en cada scrape o mantener un contador en memoria que se desincroniza con varias réplicas. Los contadores de transición cubren la señal; la profundidad llega con la lectura del panel.
- **Adoptar el SDK de OpenTelemetry ahora.** Añade configuración y un exportador antes de que exista el colector; la instrumentación con la BCL es la base que el paquete compartido envolverá.

## 4. Consecuencias

- La traza y las métricas se pueden consumir sin cambiar el servicio; el test las ejerce en proceso.
- Los nombres `EventosApi` y `eventos.*` quedan fijados por este ADR; un cambio de nombre es una ruptura para el colector.
- La instrumentación no exporta nada por sí sola: hasta que exista el colector, el estado del control es Parcial, no Validado en producción.
