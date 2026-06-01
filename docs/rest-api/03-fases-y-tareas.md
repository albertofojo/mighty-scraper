# Desglose de fases y tareas — API REST de MightyScraper

> **Estado:** Propuesta (diferida). Documentado el 2026-06-01.
> **Acompaña a:** `01-especificacion-funcional.md` · `02-especificacion-tecnica.md`.
> Las estimaciones son orientativas (jornadas de desarrollo, ~) y asumen un único desarrollador familiarizado con el repo.

---

## Visión general

| Fase | Objetivo | Resultado |
|---|---|---|
| **0** | Refactor a librería compartida | Core reutilizable por CLI y API |
| **1** | MVP de jobs (check + scrape) | API funcional que dispara y monitoriza scrapes |
| **2** | Descarga, diagnóstico y consulta de contenido | API completa para 1 usuario (local) |
| **3** | Progreso en vivo, sesión y programación | Calidad de uso + "mantener actualizado" |
| **4** | Multi-usuario | Despliegue para varios usuarios |

> Hasta la Fase 3 inclusive, el alcance es **local / un usuario**. La Fase 4 aborda el objetivo multi-usuario (hoy aplazado).

---

## Fase 0 — Refactor a librería compartida  · ~1 día

**Objetivo:** que la lógica actual sea referenciable por la API sin duplicar.

- [ ] Crear proyecto `MightyScraper.Core` (librería, `net9.0`).
- [ ] Mover `src/Core/*` y `src/Models/*` a `MightyScraper.Core` (sin cambios de lógica).
- [ ] Convertir la consola actual en `MightyScraper.Cli` referenciando `Core`.
- [ ] Actualizar `MightyScraper.sln` y los `.csproj` (incluido `tests`).
- [ ] `dotnet build` + `dotnet test` en verde; el CLI funciona igual que antes.

**Criterio de salida:** todos los comandos CLI existentes siguen funcionando idénticos.

---

## Fase 1 — MVP de jobs (check + scrape)  · ~3-4 días

**Objetivo:** API mínima que crea jobs persistidos y los ejecuta en segundo plano.

- [ ] Crear proyecto `MightyScraper.Api` (Minimal API) referenciando `Core`.
- [ ] Añadir EF Core + SQLite; entidad `Job` + migración inicial; `JobStore`.
- [ ] Implementar cola en memoria (`Channel<Guid>`) + `JobWorker : BackgroundService` (consumo secuencial, scope DI por job).
- [ ] Añadir `IProgress<JobProgress>` **opcional** a `ScraperApp.RunAsync` y `CourseStatus.RunAsync`; publicar hitos.
- [ ] Endpoints: `POST /courses`, `POST /courses/{id}/jobs` (tipos `scrape`, `check`), `GET /jobs/{id}`, `GET /jobs`.
- [ ] Middleware de **API key** (`X-Api-Key`).
- [ ] Al arrancar, marcar jobs `running` previos como `interrupted`.
- [ ] Mapear sesión caducada → `503` accionable.

**Criterio de salida:** lanzar un scrape vía `POST`, ver progreso por `GET /jobs/{id}` y que el `.md` se genere en `output/`. El estado del job sobrevive a un reinicio.

---

## Fase 2 — Descarga, diagnóstico y consulta de contenido  · ~4-5 días

**Objetivo:** cubrir las 4 operaciones y exponer el contenido descargado. API completa para 1 usuario.

- [ ] `IProgress` en `MediaDownloader.RunAsync` y `MediaDiagnostic.RunAsync`.
- [ ] Tipos de job `download` y `diagnose` en `POST /courses/{id}/jobs`.
- [ ] Entidades `Course` y `Lesson` + migración; el worker las **actualiza** al scrapear/descargar (md/vídeo/subs por lección).
- [ ] `CourseStatus` vuelca su resultado a `Course`/`Lesson` para servir `GET /courses/{id}/status` al instante.
- [ ] Endpoints de consulta: `GET /courses/{id}/lessons`, `/lessons/{n}` (`text/markdown`), `/index`, `/status`.
- [ ] Servir medios: `GET /courses/{id}/media/{file}` (stream con `Content-Type` correcto, rangos para vídeo).
- [ ] `409` / dedupe: si ya hay un job equivalente en curso para el curso, devolver el existente.
- [ ] Cancelación: `POST /jobs/{id}/cancel` (propaga `CancellationToken`).

**Criterio de salida:** flujo completo por API — registrar curso → scrape → download → consultar lecciones, `.md`, medios y `status`; relanzar download tras corte y que continúe.

---

## Fase 3 — Progreso en vivo, sesión y programación  · ~3-4 días

**Objetivo:** mejorar la experiencia y habilitar "mantener actualizado" automático.

- [ ] Progreso en vivo vía **SSE** (`GET /jobs/{id}/events`) o WebSocket.
- [ ] `GET /session` (estado de auth) y, en despliegue local con escritorio, `POST /session/login` que lanza el `LoginHelper` headed.
- [ ] **Programación** (cron interno o `IHostedService` temporizado): re-ejecutar `check` + `scrape`/`download` incremental periódicamente por curso → cubre la acción "tener el curso actualizado" de forma automática.
- [ ] Notificación de resultado (webhook opcional al terminar un job).

**Criterio de salida:** un cliente puede suscribirse al progreso en vivo; un curso puede programarse para mantenerse al día solo.

---

## Fase 4 — Multi-usuario  · ~5-7 días (a planificar en detalle)

**Objetivo:** soportar varios usuarios aislados. **Aplazada** (hoy no necesaria).

- [ ] Entidad `User` + auth por usuario (API key/JWT por usuario, hash de credenciales).
- [ ] Asociar `Course`/`Job` a `UserId`; autorización por propiedad del recurso.
- [ ] **Decisión crítica de diseño**: sesiones/credenciales de Mighty Networks **por usuario** — perfiles `auth/<userId>/` independientes.
- [ ] **Decisión crítica**: concurrencia del navegador con varios usuarios:
  - Opción A: un solo navegador, cola global (simple, no escala — un usuario bloquea a otro).
  - Opción B: un navegador/contexto por usuario (escala, más recursos y complejidad; revisar viabilidad headed/Cloudflare en servidor).
- [ ] Resolver el **headed en servidor** (escritorio virtual/VNC) o evaluar headless con mitigaciones anti-Cloudflare.
- [ ] Límites/cuotas por usuario (jobs concurrentes, almacenamiento).

**Criterio de salida:** dos usuarios operan en paralelo (según la opción elegida) sin ver ni interferir en los recursos del otro.

**Riesgos abiertos de esta fase:** re-login manual por usuario, coste de varios navegadores, Cloudflare en entorno servidor. Requiere su propia mini-spec antes de implementar.

---

## Dependencias entre fases

```
Fase 0  →  Fase 1  →  Fase 2  →  Fase 3
                         └──────→  Fase 4
```

- La Fase 0 es prerrequisito de todo.
- Las Fases 1-3 entregan valor incremental para 1 usuario y pueden pararse en cualquier punto.
- La Fase 4 depende de la 2 (modelo de datos) y conviene tras la 3.

## Definición de "hecho" (transversal)

- `dotnet build` + `dotnet test` en verde.
- Endpoints documentados (OpenAPI/Swagger generado por Minimal API).
- README/efímeros actualizados con cómo arrancar la API.
- Sin regresiones en el CLI.
