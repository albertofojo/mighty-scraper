# MightyScraper

Herramienta de consola en **C# / .NET 9** que descarga el contenido de un curso alojado en una plataforma **Mighty Networks** (SPA basada en React) y lo exporta como ficheros **Markdown** estructurados, navegables entre sí mediante enlaces cruzados.

Está pensada para cursos privados a los que se accede mediante **Google SSO** (por ejemplo `https://training.lidr.co`). El login **no se automatiza**: se hace una sola vez de forma manual mediante un *helper*, y a partir de ahí el scraper reutiliza el perfil persistente del navegador.

---

## Tabla de contenidos
- [¿Para qué sirve?](#para-qué-sirve)
- [Requisitos](#requisitos)
- [Instalación](#instalación)
- [Uso](#uso)
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
| **Google Chrome instalado** | Recomendado — el `LoginHelper` lo usa por defecto para máxima compatibilidad con Google SSO. Si no está, cae al Chromium bundled |
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
   dotnet run --project tools/LoginHelper -- https://training.lidr.co
   ```
   Se abrirá una ventana del navegador. Completa el login de Google manualmente, navega hasta que veas el curso, vuelve a la consola y pulsa **ENTER**. Esto creará la carpeta `auth/` con el perfil persistente y un `state.json`.

---

## Uso

Desde la raíz del repositorio:

```powershell
dotnet run --project src -- <course-url> [--skip N] [--delay MS]
```

### Parámetros de CLI

| Argumento | Tipo | Por defecto | Descripción |
|---|---|---|---|
| `<course-url>` | posicional, **obligatorio** | — | URL completa del curso (ej. `https://training.lidr.co/spaces/21956761-content`). Es el primer argumento posicional. |
| `--skip N` | entero | `0` | Salta las primeras `N` lecciones de la estructura descubierta. Útil para reanudar un scrape interrumpido sin volver a descargar lo que ya tienes. |
| `--delay MS` | entero | `3000` | Pausa en milisegundos entre lección y lección para no saturar el servidor. Hay un retraso inicial cero (la primera lección se descarga inmediatamente). |

### Ejemplos

Scrape completo:
```powershell
dotnet run --project src -- https://training.lidr.co/spaces/21956761-content
```

Reanudar a partir de la lección 51 con una pausa de 5 segundos:
```powershell
dotnet run --project src -- https://training.lidr.co/spaces/21956761-content --skip 50 --delay 5000
```

Scrape agresivo (sin pausa) para una prueba rápida en local:
```powershell
dotnet run --project src -- https://training.lidr.co/spaces/21956761-content --delay 0
```

### Salida

El resultado se guarda en `output/<slug-de-la-url>/`, donde `<slug>` se deriva sanitizando los segmentos de la URL del curso. Ejemplo: `https://training.lidr.co/spaces/21956761-content` produce `output/spaces-21956761-content/`.

---

## Estructura del proyecto

```
mighty-scrapper/
├── src/                            # Proyecto principal (ejecutable)
│   ├── Program.cs                  # Entry point: parsea CLI, configura DI, arranca ScraperApp
│   ├── MightyScraper.csproj        # net9.0, refs a Playwright + ReverseMarkdown + Hosting
│   ├── Core/                       # Lógica de negocio
│   │   ├── IPlaywrightManager.cs   # Contrato: obtener una IPage autenticada
│   │   ├── PlaywrightManager.cs    # Lanza Chromium headless con perfil persistente desde ./auth
│   │   ├── ICourseNavigator.cs     # Contrato: GetStructureAsync + ScrapeAsync
│   │   ├── CourseNavigator.cs      # Descubre el sidebar y extrae HTML por lección
│   │   ├── IMarkdownConverter.cs   # Contrato: convertir + guardar markdown
│   │   ├── MarkdownConverter.cs    # HTML → Markdown, limpieza, nombrado de ficheros
│   │   └── ScraperApp.cs           # Orquestador: pipeline de discovery → scrape → save
│   └── Models/                     # Records inmutables (DTOs)
│       ├── LessonMeta.cs           # Metadatos (href, título, orden, parentHref, isSection)
│       ├── LessonContent.cs        # LessonMeta + RawHtml extraído
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
│   ├── Default/                    # Perfil persistente de Chromium con cookies de Google
│   └── state.json                  # Snapshot de cookies/localStorage (fallback)
│
├── output/                         # (Generado por el scraper)
│   └── <slug>/                     # Una carpeta por curso, con NN-titulo.md
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

`ICourseNavigator.GetStructureAsync(courseUrl)` abre la URL del curso, espera a `NetworkIdle` y ejecuta un fragmento JavaScript en la página (`Page.EvaluateAsync`) que recorre el sidebar (`li.menu-list-item-container`) y devuelve la lista plana de lecciones con sus metadatos:

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

> **Crítico:** la plataforma usa Google SSO. **No se intenta automatizar el login**, porque las protecciones anti-bot de Google bloquean Playwright.

### Cómo se gestiona

- `tools/LoginHelper/Program.cs` abre Chromium **headed** con `LaunchPersistentContextAsync` apuntando a `./auth/`.
- Intenta usar el canal `chrome` (Google Chrome del sistema, mejor compatibilidad con SSO). Si no hay Chrome instalado, cae al Chromium bundled.
- Pasa los flags `--disable-blink-features=AutomationControlled` e `IgnoreDefaultArgs=["--enable-automation"]` para esconder los indicadores típicos de automatización.
- El usuario hace login a mano y pulsa ENTER. El profile (cookies + IndexedDB + service workers + localStorage) queda persistido en `./auth/Default/`. Además se exporta `state.json` como fallback de cookies.
- `PlaywrightManager` reutiliza ese mismo directorio en modo **headless** para todas las ejecuciones siguientes.

### Si la sesión expira

El scraper detecta una redirección a `/sign_in`, `/login`, o a un dominio distinto del esperado y aborta con:

```
Session expired — redirected to '<url>'.
Run the login helper again: dotnet run --project tools/LoginHelper -- <url>
```

Ejecuta de nuevo `LoginHelper` con la misma URL base y vuelve a iniciar sesión.

> El directorio `auth/` contiene cookies de tu cuenta de Google personal. **Nunca lo subas a git.**

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
