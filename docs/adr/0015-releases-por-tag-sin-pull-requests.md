# 0015. Releases disparados por tag, sin pull requests

- **Estado:** Aceptada
- **Fecha:** 2026-10-29

## Contexto

`SapB1.Client` se publica en NuGet y el repositorio entero tiene versiones (`v1.0.0`). Hace falta un proceso de release repetible, que:

- no dependa de pull requests (el proyecto trabaja con ramas y `git merge --no-ff` hechos a mano por su autor);
- calcule la versión sin un archivo que haya que recordar editar;
- no publique nada si los tests fallan;
- deje el changelog como consecuencia de los commits, no como texto aparte que se desactualiza.

Herramientas como release-please abren un PR de release en cada cambio, lo que contradice la forma de trabajo; el flujo de PRs automáticos tampoco encaja con un historial lineal de ramas revisadas commit a commit.

## Decisión

- **El tag `v*` es el disparador y la fuente de la versión.** `release.yml` se ejecuta al subir el tag; la versión del paquete es el tag sin la `v`, pasada con `-p:Version`. Los `.csproj` no llevan versión (el valor `0.0.0-local` solo cubre empaquetados locales). Un tag que no sea una versión semántica se rechaza.
- **Un release estable debe salir de `main`.** El workflow comprueba que el commit del tag es ancestro de `origin/main`. Los pre-release (`-rc.1`) quedan exentos y se publican como tales.
- **El release reutiliza el CI.** `ci.yml` se declara también como `workflow_call` y `release.yml` lo invoca: una sola definición de build, formato y tests. `ci.yml` ignora los tags `v*` para no ejecutarse dos veces.
- **El paquete se verifica antes de publicarse** (`scripts/verificar-paquete.ps1`): metadatos, licencia, README, aviso de no afiliación con SAP SE, dependencias, SourceLink.
- **El changelog sale de los Conventional Commits** entre el tag anterior y el actual, con un script propio en bash (`scripts/changelog.sh`) que tiene pruebas con repositorios git reales. No usa ninguna acción de terceros.
- **NuGet primero, GitHub Release después, y ambos idempotentes.** Publicar en NuGet es lo irreversible, así que va antes y con `--skip-duplicate`; la Release se crea o se actualiza (`gh release`). Volver a ejecutar el job tras un fallo parcial es seguro.
- **Mínimo privilegio.** Los permisos por defecto son de solo lectura; solo el job `publicar` pide `contents: write`, y `NUGET_API_KEY` solo se lee ahí. Las acciones de terceros se fijan por SHA del commit, con el tag en un comentario. Los valores que vienen del evento (el nombre del tag) llegan al script por variables de entorno, nunca interpolados en el código del paso.
- **Ensayo sin publicar.** `workflow_dispatch` ejecuta todo menos `publicar`, con una versión de prueba.
- **Nombre del paquete `Luarca.SapB1.Client`**, para que no parezca un paquete oficial de SAP; el ensamblado y los espacios de nombres siguen siendo `SapB1.Client`. El README y la descripción del paquete aclaran que no está afiliado ni respaldado por SAP SE y que SAP y SAP Business One son marcas de SAP SE.

## Alternativas descartadas

- **release-please:** abre PRs y mantiene un archivo de versión y un `CHANGELOG.md` en el repositorio; ambas cosas van contra la forma de trabajo.
- **Versionado con MinVer o GitVersion:** resuelven la versión desde los tags en el build, pero añaden una dependencia para algo que `-p:Version` ya cubre cuando solo se empaqueta un proyecto.
- **git-cliff u otra herramienta de changelog:** potente, pero una dependencia más (y una acción más que fijar) para agrupar cuatro tipos de commit; el script propio es corto y está probado.
- **Publicación confiable de NuGet con OIDC (sin clave):** elimina el secreto, pero exige configurar la política en nuget.org tras el primer push; se decidió empezar con una clave de alcance acotado al paquete (`Luarca.SapB1.Client*`) y migrar más adelante si se quiere.
- **Un workflow de release que repita los pasos del CI:** se desincronizaría; se reutiliza `ci.yml`.

## Consecuencias

- Publicar es `git tag` y `git push origin <tag>`; no hay PR de release que revisar. La disciplina se traslada a los mensajes de commit: si no siguen la convención, el changelog los muestra como "Otros cambios".
- NuGet no admite republicar una versión: un paquete defectuoso se corrige con una versión nueva.
- Una clave de NuGet con vencimiento obliga a rotarla; el workflow falla con un mensaje claro cuando falta o venció.
- El generador de changelog depende de que los tags `v*` existan y estén en el historial; el workflow clona con `fetch-depth: 0`.
- La cobertura se sube a Codecov solo desde ramas y solo si `CODECOV_TOKEN` existe, así que los forks y los tags no la necesitan.
