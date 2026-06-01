# Especificación funcional — API REST de MightyScraper

> **Estado:** Propuesta (diferida). Documentado el 2026-06-01 para tratar más adelante.
> **Contexto:** exponer la funcionalidad actual del CLI (scrape, descarga de medios, diagnóstico, check) a través de una API REST. Objetivo final **multi-usuario**, pero el arranque se hará local/un usuario (ver fases en `03-fases-y-tareas.md`).

---

## 1. Propósito

Permitir que clientes (web, scripts, otros servicios) disparen y consulten las operaciones de scraping y descarga de cursos de Mighty Networks **sin usar la línea de comandos**, de forma asíncrona y con seguimiento del progreso.

Hoy estas operaciones son comandos CLI largos y manuales. La API las convierte en **trabajos (jobs)** gestionables y consultables, y expone el contenido ya descargado (lecciones `.md`, vídeos, transcripciones, índice).

## 2. Alcance

### Dentro del alcance
- Lanzar y consultar las 4 operaciones existentes como jobs asíncronos:
  - **Scrape** (descarga de `.md` + `index.md`, incremental).
  - **Descarga de medios** (vídeo y/o subtítulos, reanudable, enriquece los `.md`).
  - **Diagnóstico de medios** (inspección, sin descargar).
  - **Check** (estado curso-vs-disco, solo lectura).
- Consultar el resultado de un curso: estructura, lecciones, contenido `.md`, ficheros de medios, índice.
- Historial de jobs y su estado/progreso.
- Informar del estado de la sesión de autenticación.
- (Objetivo) Multi-usuario: cada usuario gestiona sus propios cursos y credenciales.

### Fuera del alcance (inicial)
- **Automatizar el login** (sigue siendo manual y headed por Cloudflare + usuario/contraseña). La API informa del estado de sesión, pero no inicia sesión sola en un servidor sin escritorio.
- Transcripción por IA (Whisper) — descartada por decisión de producto.
- Edición del contenido descargado desde la API.
- Streaming/transcodificación de vídeo bajo demanda (solo se sirven los ficheros ya descargados).

## 3. Usuarios y roles

| Rol | Descripción |
|---|---|
| **Usuario** | Registra cursos, lanza jobs sobre sus cursos, consulta resultados y estado. |
| **Administrador** | (Objetivo multi-usuario) gestiona usuarios, ve todos los jobs, configura límites. |

> En la fase local inicial existe un único usuario implícito; los roles aplican plenamente al pasar a multi-usuario.

## 4. Capacidades funcionales

### 4.1 Gestión de cursos
- Registrar un curso a partir de su URL.
- Listar los cursos del usuario y su estado resumido (lecciones totales, descargadas, medios descargados).

### 4.2 Jobs (operaciones asíncronas)
- Crear un job indicando tipo (`scrape` | `download` | `diagnose` | `check`) y opciones.
- Consultar el estado de un job: `queued`, `running`, `completed`, `failed` (o `interrupted` tras un reinicio).
- Ver el progreso (p.ej. "12/151 lecciones", "vídeo 30/95").
- Listar el historial de jobs (filtrable por curso y estado).
- Cancelar un job en cola o en ejecución (deseable).
- **Idempotencia**: relanzar un `scrape`/`download` solo procesa lo que falta (comportamiento ya existente).

### 4.3 Consulta de contenido
- Obtener la estructura/lecciones de un curso (con estado por lección: `.md`, vídeo, subtítulos).
- Obtener el contenido `.md` de una lección.
- Obtener el `index.md` (puerta de entrada).
- Descargar/servir los ficheros de medios (`.mp4`, `.vtt`).

### 4.4 Estado y mantenimiento
- Endpoint de **check** (estado curso-vs-disco) servido al instante desde la persistencia.
- Estado de la **sesión** de autenticación (válida / caducada) con instrucción de re-login cuando proceda.

## 5. Casos de uso (historias de usuario)

- **HU-1**: Como usuario, registro un curso por su URL para empezar a trabajar con él.
- **HU-2**: Como usuario, lanzo un scrape y consulto su progreso hasta que termina, sin bloquear mi cliente.
- **HU-3**: Como usuario, tras el scrape lanzo la descarga de vídeos+subtítulos y, si se interrumpe, la relanzo y continúa donde iba.
- **HU-4**: Como usuario, consulto "¿tengo el curso al día?" y obtengo qué lecciones/vídeos faltan, al instante.
- **HU-5**: Como usuario, descargo el `.md` de una lección y reproduzco su vídeo desde el enlace local servido por la API.
- **HU-6**: Como usuario, veo el historial de mis jobs y por qué falló uno (mensaje de error).
- **HU-7**: Como usuario, recibo un aviso claro de que mi sesión caducó y debo re-loguear.
- **HU-8 (multi-usuario)**: Como usuario, solo veo mis cursos y jobs; no interfiero con los de otros.

## 6. Requisitos funcionales

- **RF-1**: La creación de cualquier operación larga devuelve de inmediato (202) un identificador de job; nunca se ejecuta de forma síncrona.
- **RF-2**: El estado y progreso de un job son consultables en todo momento y sobreviven a reinicios del servicio.
- **RF-3**: Los jobs de un curso se ejecutan respetando la idempotencia/reanudación existentes.
- **RF-4**: El `check` se responde sin re-navegar el curso (desde datos persistidos), salvo refresco explícito.
- **RF-5**: El contenido descargado (`.md`, índice, medios) es accesible vía API.
- **RF-6**: La API informa del estado de sesión y devuelve un error accionable cuando caduca.
- **RF-7 (multi-usuario)**: Cada recurso (curso, job, contenido) pertenece a un usuario y solo es accesible por él (o un admin).

## 7. Requisitos no funcionales

- **RNF-1 (Concurrencia)**: Las operaciones que usan el navegador se ejecutan **de una en una** (un solo contexto de navegador). El resto se encola. Documentar este límite.
- **RNF-2 (Persistencia)**: El estado de jobs y de cursos/lecciones debe persistir en disco y ser consultable tras reinicios.
- **RNF-3 (Simplicidad)**: Infraestructura mínima (un proceso, una base de datos embebida). Sin dependencias pesadas en la fase inicial.
- **RNF-4 (Seguridad)**: Acceso protegido (API key como mínimo; auth por usuario en multi-usuario). No exponer sin protección porque dispara descargas y sirve contenido.
- **RNF-5 (Observabilidad)**: Logs por job; errores legibles en la respuesta.
- **RNF-6 (Reanudabilidad)**: Un job interrumpido por caída se marca como tal y puede relanzarse sin duplicar trabajo.

## 8. Restricciones conocidas

- **Re-login manual y headed**: la sesión se crea con el `LoginHelper` (navegador visible, usuario/contraseña, Cloudflare). No automatizable en servidor sin escritorio. Implica que el modelo multi-usuario en servidor necesita una estrategia de sesiones/credenciales por usuario aún por definir (ver técnica).
- **Un navegador por instancia**: limita la concurrencia real a 1 job de navegador simultáneo por instancia.
- **Coste de los medios**: las descargas de vídeo son pesadas (decenas de GB) y largas; el diseño asíncrono es obligatorio.

## 9. Criterios de aceptación (alto nivel)

- Lanzar un scrape vía API y ver el `.md` resultante servido por la API.
- Consultar el progreso de un job en ejecución y su estado final.
- Reiniciar el servicio durante un job y, al volver, ver el job marcado correctamente (no "perdido").
- Obtener el `check` de un curso en milisegundos desde la persistencia.
- (Multi-usuario) Dos usuarios no ven los recursos del otro.

## 10. Referencias

- Especificación técnica: `02-especificacion-tecnica.md`
- Desglose de fases y tareas: `03-fases-y-tareas.md`
- Funcionalidad CLI existente reutilizada: `ScraperApp`, `MediaDownloader`, `MediaDiagnostic`, `CourseStatus` (ver `README.md`).
