# MightyScraper

Herramienta de consola en **C# / .NET 9** que descarga el contenido de un curso alojado en una plataforma **Mighty Networks** (SPA basada en React) y lo exporta como ficheros **Markdown** estructurados, navegables entre sí mediante enlaces cruzados.

Está pensada para cursos privados a los que se accede mediante **login con usuario y contraseña** (por ejemplo `https://training.lidr.co`). El login **no se automatiza**: se hace una sola vez de forma manual mediante un *helper*, y a partir de ahí el scraper reutiliza el perfil persistente del navegador.

---

## Tabla de contenidos
- [¿Para qué sirve?](#para-qué-sirve)
- [Requisitos](#requisitos)
- [Instalación](#instalación)
- [Uso](#uso)
- [Descarga de vídeos y transcripciones](#descarga-de-vídeos-y-transcripciones)
- [Estructura del proyecto](#estructura-del-proyecto)
- [Cómo funciona (arquitectura)](#cómo-funciona-arquitectura)
- [Autenticación](#autenticación)
- [Formato de la salida](#formato-de-la-salida)
- [Reglas de scraping](#reglas-de-scraping)

---

## ¿Para qué sirve?

Mighty Networks renderiza todo el contenido del curso en cliente, lo que hace que herramientas tradicionales (`wget`, `curl`, `httrack`) no puedan capturar las lecciones. MightyScraper resuelve esto:

- Lanza **Chromium** vía Playwright con una sesión autenticada.
- Recorre el **sidebar del curso** y descubre la estructura completa (secciones + lecciones).
- Visita cada lección, extrae **solo el contenedor de contenido** y lo convierte a Markdown limpio.
- Guarda cada lección como un fichero `.md` independiente, con **breadcrumb al módulo padre** y **listado de lecciones hijas** cuando aplica, formando un mini-wiki navegable.

---

## Requisitos

| Componente | Versión |
|---|---|
| **.NET SDK** | 9.0 o superior |
| **Microsoft.Playwright** | 1.59.0 (NuGet, se resuelve con `dotnet restore`) |
| **Navegadores Playwright** | Chromium (se instala con un comando, ver abajo) |
| **Google Chrome instalado** | Recomendado — tanto el `LoginHelper` como el scraper lo usan por defecto (`Channel = "chrome"`) para superar el reto de Cloudflare. Si no está, ambos caen al Chromium bundled |
| **yt-dlp** | Solo para `--download-media`. `pip install -U yt-dlp` (o `winget install yt-dlp.yt-dlp`). El scraper lo invoca por PATH o, si no está, como `python -m yt_dlp` |
| **ffmpeg** | Solo para descargar **vídeo** (fusiona los segmentos HLS en MP4). `winget install Gyan.FFmpeg`. No hace falta para `--download-media subs` |
| **Sistema operativo** | Windows / macOS / Linux |

Dependencias NuGet principales:

- `Microsoft.Playwright` — automatización del navegador.
- `Microsoft.Extensions.Hosting` — generic host + inyección de dependencias.
- `ReverseMarkdown` — conversión HTML → Markdown (GitHub-flavored).
- `HtmlAgilityPack` — limpieza del HTML antes de la conversión.

---

## Instalación

1. **Clonar el repositorio** y entrar en la carpeta:
   ```powershell
   git clone <repo-url> mighty-scrapper
   cd mighty-scrapper
   ```

2. **Restaurar y compilar** la solución:
   ```powershell
   dotnet restore
   dotnet build
   ```

3. **Instalar los navegadores de Playwright** (solo la primera vez):
   ```powershell
   pwsh src/bin/Debug/net9.0/playwright.ps1 install chromium
   ```
   Si `pwsh` no está disponible, ejecuta el script con PowerShell desde la ruta correspondiente al output de `dotnet build`.

4. **Autenticarse** (paso obligatorio antes del primer scrape):
   ```powershell
   dotnet run --project tools/LoginHelper -- https://training.lidr.co/spaces/21956761-content
   ```
   Se abrirá una ventana del navegador. Introduce tu **usuario y contraseña** manualmente, navega hasta que veas el curso, vuelve a la consola y pulsa **ENTER**. Esto creará la carpeta `auth/` con el perfil persistente y un `state.json`.

---

## Uso

Desde la raíz del repositorio:

```powershell
dotnet run --project src -- <course-url> [--skip N] [--delay MS] [--force] [--check] [--diagnose-media [N]] [--download-media [both|video|subs]] [--limit N]
```

La herramienta tiene **cuatro modos**, según los flags:

- **Scrape** (por defecto, sin flags de medios): descarga el contenido de cada lección a `.md` y genera `index.md`. Es **incremental**: salta las lecciones cuyo `.md` ya existe (usa `--force` para rehacerlas).
- **Check** (`--check`): informe de estado **de solo lectura** — compara el curso online con lo que tienes en disco (qué `.md`, vídeos y subtítulos faltan, lecciones nuevas, huérfanos). No descarga nada.
- **Diagnóstico de medios** (`--diagnose-media`): inspecciona qué exponen los reproductores de vídeo (streams de descarga + subtítulos). No descarga nada.
- **Descarga de medios** (`--download-media`): descarga vídeos y/o subtítulos vía `yt-dlp`, enriquece los `.md` con los enlaces locales y reescribe `index.md`. Es **reanudable** (no re-descarga lo ya bajado).

### Parámetros de CLI

| Argumento | Tipo | Por defecto | Descripción |
|---|---|---|---|
| `<course-url>` | posicional, **obligatorio** | — | URL completa del curso (ej. `https://training.lidr.co/spaces/21956761/content`). Es el primer argumento posicional. |
| `--skip N` | entero | `0` | Salta las primeras `N` lecciones. Útil para empezar la descarga de medios más adelante (para reanudar el scrape ya no hace falta: es incremental). |
| `--delay MS` | entero | `3000` | Pausa en ms entre lección y lección durante el **scrape** (la primera lección no espera). |
| `--force` | flag | (off) | En el **scrape**, re-genera también las lecciones cuyo `.md` ya existe (por defecto se saltan). |
| `--check` | flag | (off) | Activa el **modo check**: informe de estado curso-vs-disco. No scrapea ni descarga. |
| `--diagnose-media [N]` | flag + entero opcional | `5` | Activa el **modo diagnóstico**. Recorre lecciones hasta encontrar `N` con vídeo y reporta proveedor, id, hash, streams (MP4/HLS/DASH/DRM) y subtítulos. Escribe `output/<slug>/_media-diagnostic.json`. |
| `--download-media [modo]` | flag + valor opcional | `both` | Activa el **modo descarga**. `both` = vídeo + subtítulos, `video` = solo MP4, `subs` = solo `.vtt` (no requiere ffmpeg). |
| `--limit N` | entero | `0` (sin límite) | En modo descarga, descarga como máximo `N` vídeos. Pensado para pruebas (`--limit 1`). |

### Ejemplos

Scrape completo (genera `.md` + `index.md`):
```powershell
dotnet run --project src -- https://training.lidr.co/spaces/21956761/content
```

Reanudar el scrape a partir de la lección 51 con una pausa de 5 segundos:
```powershell
dotnet run --project src -- https://training.lidr.co/spaces/21956761/content --skip 50 --delay 5000
```

Diagnosticar los vídeos (sin descargar) de las primeras 5 lecciones con vídeo:
```powershell
dotnet run --project src -- https://training.lidr.co/spaces/21956761/content --diagnose-media 5
```

Probar la descarga con 1 solo vídeo (+ su transcripción):
```powershell
dotnet run --project src -- https://training.lidr.co/spaces/21956761/content --download-media both --limit 1
```

Descargar todos los vídeos + subtítulos del curso:
```powershell
dotnet run --project src -- https://training.lidr.co/spaces/21956761/content --download-media both
```

Comprobar si tienes el curso al día (sin descargar nada):
```powershell
dotnet run --project src -- https://training.lidr.co/spaces/21956761/content --check
```

### Mantener el curso actualizado

Los modos son **idempotentes**, así que el mismo par de comandos sirve tanto para un curso desde cero como para completar lo que falta:

| Quiero… | Comando(s) |
|---|---|
| **Descargar el curso completo** (md + vídeos + subtítulos) | `… <url>` &nbsp;→&nbsp; `… <url> --download-media both` |
| **Descargar solo lo que resta** (módulos pendientes) | Los **mismos** comandos: el scrape salta los `.md` que ya tienes y la descarga salta los vídeos ya bajados. |
| **Comprobar que estoy al día** | `… <url> --check` |
| **Rehacer todo desde cero** | `… <url> --force` (re-scrapea) y borrar `media/` antes de `--download-media`. |

> El scrape es incremental (salta `.md` existentes) y la descarga es reanudable (`_archive.txt`). Por eso "descargar lo que resta" = volver a lanzar los mismos comandos.

### Salida

El resultado se guarda en `output/<slug-de-la-url>/`, donde `<slug>` se deriva sanitizando los segmentos de la URL del curso. Ejemplo: `https://training.lidr.co/spaces/21956761/content` produce `output/spaces-21956761-content/`, con:

- `NN-Titulo.md` — una lección por fichero.
- `index.md` — **puerta de entrada**: índice navegable de todas las lecciones agrupadas por módulo.
- `media/` — vídeos `NN-Titulo-<id>.mp4` y transcripciones `NN-Titulo-<id>.es.vtt` (solo si usas `--download-media`).
- `_media-diagnostic.json` — informe del modo diagnóstico (solo si usas `--diagnose-media`).

---

## Descarga de vídeos y transcripciones

El curso aloja sus vídeos en **Vimeo** (privados, restringidos por dominio, sin DRM), servidos como **HLS/DASH adaptativo**. El flag `--download-media` los descarga reutilizando la sesión autenticada para obtener el `hash` privado de cada vídeo (que los `.md` ya no conservan) y delega en `yt-dlp` + `ffmpeg`.

### Requisitos previos

```powershell
pip install -U yt-dlp          # imprescindible
winget install Gyan.FFmpeg     # solo si quieres el vídeo (no para 'subs')
```

### Flujo recomendado (dos pasos)

No hay un único comando que haga *scrape + descarga*; son dos fases (el scrape crea los `.md`, la descarga los **enriquece**):

```powershell
# 1) Scrape: genera los .md + index.md
dotnet run --project src -- <course-url>

# 2) Descarga: vídeos + subtítulos, enriquece los .md y reescribe index.md
dotnet run --project src -- <course-url> --download-media both
```

> El orden importa: la descarga inserta los enlaces locales en los `.md` que produjo el scrape. Si los `.md` no existen, los vídeos se descargan igual pero no se enriquece nada.

### Qué hace exactamente

1. **Descarga** (`MediaDownloader`): por cada lección navega, extrae los embeds de Vimeo (id + hash) y llama a `yt-dlp` con `--referer <lección>`:
   * **Vídeo** → HLS fusionado a MP4 (`--merge-output-format mp4`), **reanudable** con `--download-archive` (si lo interrumpes, al relanzar no re-descarga lo ya hecho).
   * **Subtítulos** → `.vtt` en español/inglés donde el creador los haya subido (transcripción **gratis**).
   * Ficheros nombrados **alineados con el `.md`**: `NN-Titulo-<id>.mp4` y `NN-Titulo-<id>.es.vtt`.
2. **Enriquecido del `.md`**: sustituye el enlace online-solo del vídeo por una línea con **ambos** destinos — el enlace online funcional (con el hash restaurado, `vimeo.com/{id}/{hash}`) y los ficheros locales:
   ```markdown
   🎬 **Vídeo:** [▶️ Ver online](<https://vimeo.com/675812999/2366a806fc>) · [⬇️ MP4](<media/03-Expectativas-675812999.mp4>) · [📝 Transcripción (es)](<media/03-Expectativas-675812999.es.vtt>)
   ```
   Es **idempotente** (marcador `<!-- media:{id} -->`): re-ejecutar no duplica enlaces.
3. **`index.md`**: se regenera como índice navegable de todas las lecciones agrupadas por módulo.

### Modos y pruebas

| Comando | Efecto |
|---|---|
| `--download-media both` | Vídeo + subtítulos (requiere ffmpeg) |
| `--download-media video` | Solo MP4 |
| `--download-media subs` | Solo `.vtt` (no requiere ffmpeg — ideal para validar el flujo) |
| `--download-media both --limit 1` | Solo el primer vídeo (prueba rápida) |
| `--download-media both --skip 50` | Empieza en la lección 51 |

> **Coste real**: el software es gratis; el vídeo ocupa disco (estimado ~15-40 GB para el curso completo) y tiempo. La transcripción `.vtt` es ligera (KB) y depende de que el creador haya subido subtítulos.

---

## Estructura del proyecto

```
mighty-scrapper/
├── src/                            # Proyecto principal (ejecutable)
│   ├── Program.cs                  # Entry point: parsea CLI, configura DI, arranca ScraperApp
│   ├── MightyScraper.csproj        # net9.0, refs a Playwright + ReverseMarkdown + Hosting
│   ├── Core/                       # Lógica de negocio
│   │   ├── IPlaywrightManager.cs   # Contrato: obtener una IPage autenticada
│   │   ├── PlaywrightManager.cs    # Lanza Chrome (headed) con perfil persistente desde ./auth
│   │   ├── ICourseNavigator.cs     # Contrato: GetStructureAsync + ScrapeAsync
│   │   ├── CourseNavigator.cs      # Descubre el sidebar y extrae HTML por lección
│   │   ├── IMarkdownConverter.cs   # Contrato: convertir + guardar markdown
│   │   ├── MarkdownConverter.cs    # HTML → Markdown, limpieza, nombrado de ficheros
│   │   ├── ScraperApp.cs           # Orquestador: pipeline de discovery → scrape → save
│   │   ├── EmbedExtractor.cs       # Extrae embeds de vídeo (Vimeo/YouTube/Loom) + hash de los iframes
│   │   ├── MediaDiagnostic.cs      # Modo --diagnose-media: inspecciona streams + subtítulos
│   │   ├── MediaDownloader.cs      # Modo --download-media: yt-dlp + enriquecido de .md
│   │   ├── CourseIndex.cs          # Genera index.md (índice de lecciones por módulo)
│   │   └── CourseStatus.cs         # Modo --check: informe de estado curso-vs-disco
│   └── Models/                     # Records inmutables (DTOs)
│       ├── LessonMeta.cs           # Metadatos (href, título, orden, parentHref, isSection)
│       ├── LessonContent.cs        # LessonMeta + RawHtml extraído
│       ├── MediaEmbed.cs           # Embed de vídeo (proveedor, id, hash, url)
│       ├── CourseLesson.cs         # Modelo agregado opcional (legacy)
│       ├── CourseSection.cs        # Modelo agregado opcional (legacy)
│       └── CourseStructure.cs      # Modelo agregado opcional (legacy)
│
├── tools/
│   └── LoginHelper/                # Programa auxiliar para crear la sesión persistente
│       ├── Program.cs              # Headed browser → login manual → guardar profile + state.json
│       └── LoginHelper.csproj      # Proyecto independiente, solo depende de Playwright
│
├── tests/
│   └── MightyScraper.Tests/        # xUnit
│       ├── MarkdownConverterTests.cs   # Cubre conversión, limpieza UI, nombrado, cross-links
│       └── MightyScraper.Tests.csproj
│
├── auth/                           # (Generado por LoginHelper — IGNORAR en git)
│   ├── Default/                    # Perfil persistente de Chromium con las cookies de sesión
│   └── state.json                  # Snapshot de cookies/localStorage (fallback)
│
├── output/                         # (Generado por el scraper)
│   └── <slug>/                     # Una carpeta por curso
│       ├── NN-titulo.md            #   Una lección por fichero
│       ├── index.md                #   Puerta de entrada: índice por módulos
│       └── media/                  #   Vídeos .mp4 + transcripciones .vtt (--download-media)
│
├── .claude/                        # Configuración local de Claude Code (skills, reglas)
│   ├── rules/scraping-guidelines.md
│   └── skills/{playright-auditor,spec-driven-scaffold,markdown-parser-debug}/SKILL.md
│
├── CLAUDE.md                       # Instrucciones de proyecto para el agente
├── MightyScraper.sln               # Solución con los 3 proyectos (src + tests + LoginHelper)
└── README.md                       # Este fichero
```

---

## Cómo funciona (arquitectura)

El pipeline está dividido en **dos fases**, ambas orquestadas por `ScraperApp.RunAsync`:

### Fase 1 — Discovery (una sola carga de página)

`ICourseNavigator.GetStructureAsync(courseUrl)` abre la URL del curso, espera a `DOMContentLoaded` y ejecuta un fragmento JavaScript en la página (`Page.EvaluateAsync`) que recorre el sidebar (`li.menu-list-item-container`) y devuelve la lista plana de lecciones con sus metadatos:

- `Href` — URL absoluta de la lección.
- `Title` — texto del enlace.
- `Order` — índice secuencial (0..N).
- `ParentHref` — href del módulo padre (si la lección está en nivel 2).
- `IsSection` — `true` si la fila representa un módulo/cabecera en lugar de una lección.

Si la URL devuelta tras la navegación contiene `/sign_in`, `/login` o no es del dominio esperado, se lanza `InvalidOperationException` pidiendo al usuario que vuelva a ejecutar `LoginHelper`.

Si el selector del sidebar no aparece en 8 segundos, se vuelca un **diagnóstico DOM** (título, URL, clases de los padres de `<a>`, candidatos alternativos) y se aborta con un mensaje accionable.

Con la estructura ya en memoria, `ScraperApp` construye un mapa **`href → nombre-de-fichero`** antes de tocar la primera lección. Esto garantiza que cuando se generen las lecciones individuales, los enlaces cruzados ya apunten al nombre final del fichero, sin necesidad de una segunda pasada.

### Fase 2 — Scrape progresivo y guardado en streaming

`ICourseNavigator.ScrapeAsync(structure, delayMs)` devuelve un `IAsyncEnumerable<LessonContent>`. Por cada lección:

1. Espera `delayMs` ms desde la lección anterior (sin esperar antes de la primera).
2. Navega al `Href` y espera a `DOMContentLoaded`.
3. Vuelve a comprobar si la sesión sigue viva (mismo check que en discovery). Si expiró → excepción y aborta el run.
4. Espera al selector de contenido (`.detail-layout-description`) hasta 20 s.
5. Si llega → extrae `InnerHTMLAsync()`. Si no llega → **fallback suave**: emite un `LessonContent` con HTML vacío para que el fichero stub se genere igualmente y los cross-links no se rompan.

`ScraperApp` consume el async stream y, por cada lección recibida:

1. Construye el **header** (breadcrumb `> **Módulo:** [Padre](padre.md)`) si la lección tiene `ParentHref`.
2. Construye el **footer** (listado de lecciones hijas como bullets) si es una sección con hijas.
3. Llama a `IMarkdownConverter.SaveAsync(lesson, outputDir, header, footer)`.

El converter:

1. Carga el HTML en `HtmlAgilityPack`.
2. **Reemplaza iframes** (`<iframe src="...vimeo.com/embed/..." />`) por enlaces `<a>` apuntando a la URL pública (`vimeo.com/<id>`, `youtube.com/watch?v=...`, `loom.com/share/...`).
3. Elimina UI chrome irrelevante: `//button`, `*[class*=modal]`, `*[class*=complete-btn]`, `*[class*=overlay]`.
4. Pasa el HTML resultante por `ReverseMarkdown` con GitHub-flavored, `RemoveComments=true` y `UnknownTags=Drop`.
5. Construye el nombre del fichero a partir del título: limpia emoji de status (🟢🔴🟡) y la coletilla `— NN min`, sustituye caracteres ilegales y espacios por `-`.
6. Escribe `header + markdown + footer` al disco.

El guardado es **progresivo**: si el proceso se interrumpe a mitad, todos los ficheros saved hasta ese punto siguen en disco y se pueden reanudar con `--skip <N>`.

---

## Autenticación

> **Crítico:** el login se hace con **usuario y contraseña** en la propia plataforma. **No se automatiza** el formulario: lo completas tú a mano una sola vez y el scraper reutiliza la sesión guardada.

### Cómo se gestiona

- `tools/LoginHelper/Program.cs` abre Chromium **headed** con `LaunchPersistentContextAsync` apuntando a `./auth/`.
- Intenta usar el canal `chrome` (Google Chrome del sistema, mejor compatibilidad). Si no hay Chrome instalado, cae al Chromium bundled.
- Pasa los flags `--disable-blink-features=AutomationControlled` e `IgnoreDefaultArgs=["--enable-automation"]` para esconder los indicadores típicos de automatización.
- El usuario introduce sus credenciales a mano y pulsa ENTER. El profile (cookies + IndexedDB + service workers + localStorage) queda persistido en `./auth/Default/`. Además se exporta `state.json` como fallback de cookies.
- `PlaywrightManager` reutiliza ese mismo directorio para todas las ejecuciones siguientes, lanzando **Chrome del sistema (`Channel = "chrome"`) en modo headed**. Si no hay Chrome instalado, cae al Chromium bundled (también headed).

### Cloudflare ("Just a moment...")

La plataforma está detrás de **Cloudflare**. Su cookie de validación (`cf_clearance`) va ligada al *fingerprint* exacto del navegador que superó el reto: el **User-Agent** del Chrome del sistema y el hecho de correr en **modo headed**. Por eso el scraper **no** corre headless ni con el Chromium bundled por defecto: si lo hiciera, el User-Agent no coincidiría (`HeadlessChrome`), Cloudflare rechazaría la cookie y volvería a mostrar la pantalla *"Just a moment..."*, que el bot no puede resolver.

Consecuencias prácticas:

- Durante el scrape **se abre una ventana de Chrome visible**. Déjala trabajar de fondo; no la cierres.
- Si en los logs ves `Page title : Just a moment...` y el error `Sidebar selector ... not found`, es que Cloudflare bloqueó la sesión. Vuelve a pasar el `LoginHelper` para refrescar la validación y reintenta.

### Re-autenticarse (sesión expirada)

Las sesiones caducan con el tiempo. Síntomas típicos de sesión expirada:

- El scraper aborta con un mensaje de `Session expired — redirected to '<url>'` al detectar una redirección a `/sign_in`, `/login` o a un dominio distinto del esperado.
- La **fase de descubrimiento** (`GetStructureAsync`) se cuelga: en lugar del curso se carga la pantalla de login y la espera del sidebar agota su tiempo.

Para regenerar la sesión, vuelve a ejecutar el `LoginHelper` con la URL del curso:

```powershell
dotnet run --project tools/LoginHelper -- https://training.lidr.co/spaces/21956761-content
```

1. Se abre una ventana de Chrome.
2. Introduce tu **usuario y contraseña** y completa el login.
3. Espera a ver el contenido del curso cargado.
4. Vuelve a la consola y pulsa **ENTER**.

Esto refresca el perfil persistente en `./auth/` y reescribe `auth/state.json`. Después puedes reanudar el scrape donde lo dejaste con `--skip <N>`:

```powershell
dotnet run --project src -- https://training.lidr.co/spaces/21956761-content --skip 141 --delay 3000
```

> El directorio `auth/` contiene las cookies de tu sesión personal. **Nunca lo subas a git.**

---

## Formato de la salida

### Nombrado

`NN-Titulo-Sanitizado.md`, donde:

- `NN` es el orden 1-indexed con padding de 2 dígitos (`01`, `02`, ..., `99`, `100`).
- El título se limpia de emojis de status y tiempo (`🟢 — 53 min`), se reemplazan caracteres inválidos de Windows/POSIX y los espacios pasan a `-`.

Ejemplos:
- `01-¿Cómo-usar-la-plataforma.md`
- `34-🎥-Caso-práctico-Lanza-tu-negocio-en-menos-de-1-hora.md`
- `100-Módulo-8.-Bases-de-datos.md`

### Estructura interna de cada fichero

```markdown
> **Módulo:** [Nombre del módulo padre](NN-modulo.md)   ← solo en lecciones hijas

# Título de la lección
... cuerpo en markdown ...

[📹 https://vimeo.com/123456789](https://vimeo.com/123456789)   ← iframes reescritos

---

## Lecciones del módulo                                  ← solo en lecciones-sección
- [Lección 1](NN-leccion-1.md)
- [Lección 2](NN-leccion-2.md)
- ...
```

Esto produce un grafo navegable donde:
- Cada módulo lista sus lecciones hijas al final.
- Cada lección hija enlaza de vuelta a su módulo al principio.
- Los enlaces a vídeos embebidos (Vimeo / YouTube / Loom) se conservan como URLs públicas en lugar de iframes rotos.

---

## Reglas de scraping

Las reglas estrictas que el código debe cumplir están en [`.claude/rules/scraping-guidelines.md`](./.claude/rules/scraping-guidelines.md). Resumen:

- **Prohibido** `Task.Delay` / `Thread.Sleep` para esperar a elementos UI. (El único `Task.Delay` permitido es el de la pausa cortés entre lecciones, configurable con `--delay`.)
- **Obligatorio** usar `WaitForSelectorAsync` / `Locator.WaitForAsync` / `WaitForLoadStateAsync` para waits.
- **Locators**: preferir `GetByRole` / `GetByText` / `GetByTestId`. CSS solo como último recurso.
- **Errores**: cada lección está envuelta en `try/catch`. Un fallo en una lección no aborta el run completo, salvo que se trate de una sesión expirada.
- **Auth**: nunca tipear credenciales. Si no hay sesión válida → excepción y mensaje al usuario.
