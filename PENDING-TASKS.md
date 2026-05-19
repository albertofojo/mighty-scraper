# Tareas pendientes

Mejoras identificadas en el análisis del proyecto (2026-05-20). Ordenadas por prioridad.

---

## 1. Cancelación efectiva con Ctrl+C
**Prioridad:** alta | **Dificultad:** baja

`Program.cs` llama a `app.RunAsync(courseUrl, skipCount, delayMs)` sin pasar ningún `CancellationToken`. Un Ctrl+C aborta el proceso abruptamente, dejando Chromium en memoria y posibles `.md` a medio escribir.

**Implementación:**
- Registrar `Console.CancelKeyPress` en `Program.cs` con un `CancellationTokenSource`.
- Pasar el token a `RunAsync` y de ahí a `GetStructureAsync` y `ScrapeAsync`.
- Asegurarse de que `PlaywrightManager` (registrado como `IAsyncDisposable`) se dispone correctamente en el shutdown, ya sea con `await using var host` o con un bloque `finally` explícito.

---

## 2. `PlaywrightManager` nunca se dispone correctamente
**Prioridad:** alta | **Dificultad:** baja

`PlaywrightManager` implementa `IAsyncDisposable` pero el host no garantiza que se ejecute `DisposeAsync` antes de que el proceso termine, especialmente en caso de excepción. Chromium queda en memoria.

**Implementación:**
- Cambiar `using var host = builder.Build()` por `await using var host = builder.Build()` — `IHost` implementa `IAsyncDisposable`.
- Verificar que el contenedor DI llame a `DisposeAsync` del singleton al hacer shutdown del host.
- Añadir un test que use un `IPlaywrightManager` fake y verifique que `DisposeAsync` se invoca tras un `RunAsync` exitoso y otro con excepción.

---

## 3. Colisiones en el mapa href→filename
**Prioridad:** alta | **Dificultad:** media

En `ScraperApp.RunAsync`, el diccionario `hrefToFile` se construye con `ToDictionary(l => l.Href, …)`. Dos problemas:
1. Si la plataforma devuelve un `Href` duplicado, se lanza `ArgumentException` y cae todo el proceso antes de scrapear nada.
2. Dos títulos distintos que producen el mismo nombre sanitizado (e.g. `"Recursos!"` y `"Recursos?"` → `Recursos.md`) se sobrescriben en silencio.

**Implementación:**
- Usar `GroupBy(l => l.Href).ToDictionary(g => g.Key, …)` para tolerar hrefs duplicados.
- En `MarkdownConverter.BuildFilename`, detectar colisiones de nombre dentro del mismo run y añadir el sufijo `-{Order+1:D2}` para desambiguar.
- Test: `GetFilename_TwoLessonsWithSameSanitisedTitle_ProducesDistinctNames`.

---

## 4. Detección de sesión expirada acoplada al dominio
**Prioridad:** media | **Dificultad:** baja

Las comprobaciones de sesión en `GetStructureAsync` y `TryExtractAsync` hardcodean `training.lidr.co`. La herramienta no funciona en ninguna otra instancia de Mighty Networks sin recompilar. Además, una URL que contenga `/login` como subruta legítima produciría un falso positivo.

**Implementación:**
- Extraer el host esperado de la `courseUrl` original con `new Uri(courseUrl).Host` y comparar contra él en lugar de contra la constante.
- Mover los marcadores de "página de login" (`/sign_in`, `/login`, `accounts.google.com`) a una lista configurable, por ejemplo en `appsettings.json` o como parámetro de `SiteProfile`.
- Tests: `IsLoginPage_ReturnsFalse_WhenLoginIsSubpath` y `IsLoginPage_ReturnsTrue_WhenRedirectedToGoogle`.

---

## 5. Selectores CSS sin punto único de configuración
**Prioridad:** media | **Dificultad:** media

`SidebarSelector` y `ContentSelector` son `private const` en `CourseNavigator.cs`. La lógica del sidebar (`li.menu-list-item-container`, `level-2`, `is-section`) además está duplicada como cadena JS dentro de `EvaluateAsync`. Cuando Mighty Networks actualice sus clases CSS habrá que buscar en dos lugares y recompilar.

**Implementación:**
- Crear un `record SiteProfile(string SidebarSelector, string ContentSelector, string ExpectedHost)` registrado en DI vía `IOptions<SiteProfile>`.
- Cargar el perfil por defecto desde `appsettings.json` y permitir override con `--profile <file.json>`.
- El JS de descubrimiento del sidebar puede leer los selectores dinámicamente si se pasan como argumento de `EvaluateAsync` en lugar de estar hardcodeados.

---

## 6. Imágenes de CDN privado caducan
**Prioridad:** media | **Dificultad:** media

Las imágenes de las lecciones usan URLs de CDN firmado (`imgix.net`, `amazonaws.com`) con parámetros de firma que expiran. Los `.md` generados apuntan a imágenes que eventualmente quedarán rotas.

**Implementación:**
- Añadir un `IAssetDownloader` que, durante `StripUiElements`, encuentre `<img src=…>` con HtmlAgilityPack.
- Descargar el binario usando `HttpClient` con las cookies del `IBrowserContext` (recuperables con `context.StorageStateAsync`).
- Persistir los assets en `output/<slug>/assets/<order>-<hash>.<ext>` y reescribir el `src` a la ruta relativa antes de pasar a `ReverseMarkdown`.
- Test: `ConvertToMarkdownAsync_WithImg_RewritesSrcToLocalRelativePath`.

---

## 7. Delay lineal ignora el tiempo ya transcurrido
**Prioridad:** media | **Dificultad:** baja-media

Con `--delay 3000` y una lección que tardó 4 s en cargar, el proceso espera 3 s adicionales innecesariamente. En un curso de 141 lecciones con páginas lentas esto puede suponer 5-10 minutos de pausa sin necesidad.

**Implementación:**
- Sustituir `Task.Delay(delayMs)` por un "min-spacing": registrar `lastRequestAt = DateTimeOffset.UtcNow` después de cada extracción y antes de la siguiente calcular `remaining = delayMs - elapsed.TotalMilliseconds`.
- Solo llamar a `Task.Delay(remaining)` si `remaining > 0`.
- El cambio vive íntegramente en `CourseNavigator.ScrapeAsync`.

---

## 8. Reanudación frágil — `--skip N` usa índice, no href
**Prioridad:** media | **Dificultad:** media

Si el scrape aborta en la lección 73, el usuario debe mirar los logs y pasar `--skip 72`. Si el curso cambia de orden entre dos runs (se inserta una lección al principio), el índice ya no corresponde y se descargan lecciones equivocadas.

**Implementación:**
- Guardar `output/<slug>/.progress.json` con `{ "completed": ["<href>", "<href>", …] }` después de cada save exitoso.
- Al inicio de `RunAsync`, leer el archivo si existe y filtrar `structure` eliminando los hrefs ya completados.
- Mantener `--skip N` como override manual de emergencia.
- Test: `RunAsync_ResumesOnlyMissingHrefs_WhenProgressFileExists`.

---

## 9. `auth/state.json` huérfano
**Prioridad:** baja | **Dificultad:** baja

`LoginHelper` escribe `auth/state.json` pero `PlaywrightManager` nunca lo lee — solo usa `LaunchPersistentContextAsync(authDir)`. El comentario en el código dice "fallback" pero no existe tal fallback. Confunde a quien toca el código.

**Implementación (opción recomendada — simplificar):**
- Eliminar la llamada a `context.StorageStateAsync(…)` del `LoginHelper`.
- Eliminar el comentario huérfano de `PlaywrightManager`.

**Implementación alternativa (implementar el fallback de verdad):**
- Si el directorio `auth/` no existe pero sí `auth/state.json`, lanzar `LaunchAsync` (no persistente) con `NewContextAsync(new() { StorageStatePath = "auth/state.json" })`.

---

## 10. `CourseNavigator` sin cobertura de tests
**Prioridad:** media | **Dificultad:** alta

Toda la lógica frágil del proyecto vive en `CourseNavigator` (selectores CSS, jerarquía sección/lección, detección de sesión expirada, fallback de stub) pero no tiene un solo test. Una refactor accidental rompería el scraping sin que ningún test lo detecte.

**Implementación:**
- Servir HTML estático (snapshot de `diagnose-lesson-1.html`) con un servidor Kestrel embebido en el test (`WebApplicationFactory` o `TestServer`).
- Apuntar `PlaywrightManager` a `http://localhost:<port>` con `LaunchAsync` no persistente (sin perfil real de auth).
- Tres tests concretos:
  1. `GetStructureAsync_OnSnapshot_ReturnsExpectedHierarchy` — verifica número de lecciones y que las hijas tienen `ParentHref` no nulo.
  2. `TryExtractAsync_WhenContentSelectorMissing_YieldsStubWithEmptyHtml` — página sin `.detail-layout-description`; el `LessonContent` se yield-ea con `RawHtml == ""`.
  3. `GetStructureAsync_WhenRedirectedToLogin_ThrowsInvalidOperationException` — el handler responde con 302 a `/sign_in`.
- Requiere Chromium disponible en el agente de CI (puede excluirse de runs rápidos con `[Trait("Category", "Integration")]`).
