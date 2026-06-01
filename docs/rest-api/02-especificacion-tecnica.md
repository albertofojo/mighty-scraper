# Especificación técnica — API REST de MightyScraper

> **Estado:** Propuesta (diferida). Documentado el 2026-06-01.
> **Acompaña a:** `01-especificacion-funcional.md` · Plan de ejecución en `03-fases-y-tareas.md`.

---

## 1. Principio de diseño

La lógica de negocio **ya existe** como servicios inyectables (`ScraperApp`, `MediaDownloader`, `MediaDiagnostic`, `CourseStatus`, `IPlaywrightManager`, `ICourseNavigator`, `IMarkdownConverter`). La API es una **capa fina** que:

1. Recibe peticiones HTTP y crea **jobs** persistidos.
2. Un **worker en segundo plano** ejecuta los jobs **secuencialmente** llamando a esos servicios.
3. Sirve resultados (estado de jobs, lecciones, `.md`, medios) desde la persistencia y el sistema de ficheros.

No se reescribe la lógica de scraping/descarga.

## 2. Arquitectura y proyectos

Refactor a **librería compartida** + dos hosts:

```
MightyScraper.Core      (lib)   ← toda la lógica actual de /src/Core + /src/Models
MightyScraper.Cli       (exe)   ← la consola actual (Program.cs), referencia Core
MightyScraper.Api       (web)   ← ASP.NET Core Minimal API, referencia Core
tools/LoginHelper       (exe)   ← sin cambios
```

- Extraer `src/Core` y `src/Models` a `MightyScraper.Core` (mover ficheros + ajustar `.csproj`). Sin cambios de lógica.
- `MightyScraper.Cli` mantiene el comportamiento actual.
- `MightyScraper.Api` añade: endpoints, EF Core + SQLite, el worker, `JobService` y `JobStore`.

## 3. Stack tecnológico

| Pieza | Elección | Motivo |
|---|---|---|
| Framework web | ASP.NET Core **Minimal API** (`net9.0`) | Ligero, encaja con el generic host ya usado. |
| Persistencia | **SQLite** + **EF Core** | Embebida, cero infraestructura, consultas e historial gratis. |
| Cola de jobs | `System.Threading.Channels` (en memoria) + worker `BackgroundService` | Lo más simple; durabilidad la aporta la BD (no la cola). |
| Serialización | `System.Text.Json` | Ya en uso en el proyecto. |
| Auth (inicial) | API key por header (`X-Api-Key`) | Mínimo viable; auth por usuario en fase multi-usuario. |

## 4. Modelo de ejecución (jobs)

### 4.1 Flujo
1. `POST /courses/{id}/jobs` valida, **persiste** el job con estado `queued`, lo encola en el `Channel` y responde **202** con el `jobId`.
2. El `JobWorker` (un `BackgroundService`) consume el canal **de uno en uno**, marca `running`, resuelve el servicio correspondiente de un **scope DI por job** y lo ejecuta.
3. El servicio reporta progreso vía `IProgress<JobProgress>`; el worker lo escribe en la BD.
4. Al terminar: `completed` (+ `ResultJson`) o `failed` (+ `Error`).

### 4.2 Por qué secuencial
`IPlaywrightManager` es **singleton con una sola `IPage`** y el navegador headed mantiene la huella anti-Cloudflare. Ejecutar dos jobs de navegador a la vez rompería la sesión. → **un worker, un job de navegador a la vez**. El `check` (que solo necesita discovery) y la lectura de contenido pueden ir aparte si se quiere, pero de inicio todo va por la misma cola por simplicidad.

### 4.3 Reanudación tras reinicio
Al arrancar, los jobs en estado `running` se marcan `interrupted` (no se pueden continuar a medias de forma fiable) y quedan disponibles para **relanzar**; gracias a la idempotencia (scrape incremental, `_archive.txt` de yt-dlp), relanzar solo hace lo que falta.

### 4.4 Cambio mínimo en el Core
Añadir un parámetro **opcional** `IProgress<JobProgress>? progress = null` a los `RunAsync` de los servicios y emitir hitos (ya logean esos hitos; basta con publicarlos también al `IProgress`). No rompe el CLI (pasa `null`).

## 5. Diseño de la API

### 5.1 Endpoints

| Método | Ruta | Cuerpo / Query | Respuesta |
|---|---|---|---|
| `POST` | `/courses` | `{ "url": "..." }` | `201` + curso (`id`=slug) |
| `GET` | `/courses` | — | lista de cursos del usuario |
| `GET` | `/courses/{id}` | — | curso + resumen |
| `POST` | `/courses/{id}/jobs` | `{ "type", "options" }` | `202` + job |
| `GET` | `/jobs/{jobId}` | — | job (estado, progreso, resultado) |
| `GET` | `/jobs` | `?courseId=&status=` | historial |
| `POST` | `/jobs/{jobId}/cancel` | — | `202` (si cancelable) |
| `GET` | `/courses/{id}/status` | — | resultado del `check` desde BD |
| `GET` | `/courses/{id}/lessons` | — | lecciones + estado por lección |
| `GET` | `/courses/{id}/lessons/{n}` | — | `.md` de la lección (`text/markdown`) |
| `GET` | `/courses/{id}/index` | — | `index.md` |
| `GET` | `/courses/{id}/media/{file}` | — | fichero de medios (stream) |
| `GET` | `/session` | — | estado de la sesión de auth |

### 5.2 Opciones por tipo de job (`options`)
- `scrape`: `{ "skip": int, "delay": int, "force": bool }`
- `download`: `{ "mode": "both|video|subs", "skip": int, "limit": int }`
- `diagnose`: `{ "sample": int }`
- `check`: `{}`

### 5.3 Contratos (esbozo)
```jsonc
// Job
{
  "id": "guid",
  "courseId": "spaces-21956761-content",
  "type": "download",
  "status": "running",          // queued|running|completed|failed|interrupted|cancelled
  "options": { "mode": "both" },
  "progress": { "current": 30, "total": 95, "message": "Vídeo 30/95" },
  "createdAt": "...", "startedAt": "...", "finishedAt": null,
  "error": null,
  "result": null
}
```

### 5.4 Códigos de estado
- `202` jobs aceptados · `201` recurso creado · `200` consultas · `404` no existe ·
- `409` si ya hay un job equivalente en curso para el curso (devolver el existente) ·
- `401/403` auth · `422` opciones inválidas · `503` si el navegador/sesión no está disponible.

## 6. Modelo de datos (SQLite / EF Core)

```
User(    Id, Name, ApiKeyHash, CreatedAt )                       -- (fase multi-usuario)

Course(  Id PK,            -- slug derivado de la URL
         UserId FK,        -- (multi-usuario)
         Url, Title, LastDiscoveredAt, LessonCount )

Lesson(  Id PK, CourseId FK, Order, Title, Href, ParentHref, IsSection,
         HasVideo, MdSavedAt, VideoFile, SubsFile, MediaDownloadedAt )

Job(     Id PK, CourseId FK, UserId FK, Type, Status, OptionsJson,
         CreatedAt, StartedAt, FinishedAt,
         ProgressCurrent, ProgressTotal, ProgressMessage,
         Error, ResultJson )
```

- `Course`/`Lesson` permiten responder `status`/`lessons` al instante y se actualizan según el worker avanza.
- `Job` es la fuente de verdad del seguimiento; sobrevive a reinicios.
- Migraciones EF Core; fichero `mightyscraper.db` junto a `output/` (configurable).
- **Alternativa ultra-simple** (si se quisiera evitar EF en el MVP): persistir jobs como ficheros JSON en `jobs/`. Se recomienda SQLite por el historial y las consultas.

## 7. Reutilización del Core (mapeo job → servicio)

| Job | Servicio | Llamada |
|---|---|---|
| `scrape` | `ScraperApp` | `RunAsync(url, skip, delay, force, progress, ct)` |
| `download` | `MediaDownloader` | `RunAsync(url, skip, mode, limit, progress, ct)` |
| `diagnose` | `MediaDiagnostic` | `RunAsync(url, skip, sample, progress, ct)` |
| `check` | `CourseStatus` | `RunAsync(url, progress, ct)` → además vuelca a `Course`/`Lesson` |

El worker crea un **scope DI por job**, resuelve el servicio, pasa el `CancellationToken` (para cancelar) y el `IProgress`.

## 8. Sesión y Cloudflare

- `IPlaywrightManager` sigue usando el perfil persistente `./auth` (headed, Chrome del sistema).
- `GET /session`: navega ligero / comprueba la última URL conocida y devuelve `valid|expired`.
- **Re-login**: en despliegue **local con escritorio**, un `POST /session/login` puede lanzar el `LoginHelper` headed. En **servidor sin escritorio no es posible** → la API devuelve instrucciones y un `503` en jobs hasta que la sesión se regenere por un medio out-of-band.
- **Multi-usuario** (pendiente de diseño): cada usuario necesitaría su propio perfil/sesión `auth/<userId>/` y, potencialmente, su propia instancia de navegador → afecta a RNF-1 (concurrencia). Decisión a tomar en la fase multi-usuario.

## 9. Seguridad

- Inicial: **API key** por header, validada en un middleware. Sin key → `401`.
- Multi-usuario: API key/JWT por usuario; autorización por propiedad del recurso (`UserId`).
- No exponer a Internet sin auth: la API dispara descargas y sirve contenido del curso (posibles implicaciones de licencia/ToS).

## 10. Observabilidad y errores

- Logging estándar de ASP.NET Core + un **log por job** (capturar las líneas relevantes a `ResultJson`/un campo de log).
- Errores de negocio → respuesta JSON `{ error, detail }`.
- Sesión caducada (`InvalidOperationException` de los navigators) → mapear a `503` con mensaje accionable.

## 11. Configuración y despliegue

- `appsettings.json`: ruta de `output/`, ruta de la BD, API key(s), modo headed/headless, timeouts.
- Despliegue inicial: **local**, un proceso (`dotnet run --project MightyScraper.Api`).
- Despliegue multi-usuario/servidor: requiere resolver navegador headed (escritorio virtual/VNC o headless con riesgo Cloudflare) — **fuera del MVP**, anotado como riesgo.

## 12. Riesgos técnicos

| Riesgo | Mitigación |
|---|---|
| Cloudflare bloquea en headless/servidor | Mantener headed + Chrome del sistema; documentar que el servidor necesita escritorio. |
| Concurrencia de navegador | Worker único secuencial; cola. |
| Jobs largos y caídas | Persistencia + reanudación idempotente. |
| Multi-usuario y sesiones | Diseño de perfiles por usuario aplazado a su fase; no comprometer el MVP. |
| Acoplar progreso al Core | Solo un `IProgress` opcional; el CLI no se ve afectado. |
