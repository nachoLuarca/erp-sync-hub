using System.Diagnostics;

namespace ErpSyncHub.UnitTests.Publishing;

/// <summary>
/// Prueba <c>scripts/changelog.sh</c> contra repositorios git reales y desechables, con la misma forma que el historial del
/// proyecto: Conventional Commits en español, ramas de feature y commits de merge.
/// </summary>
public sealed class ChangelogScriptTests : IDisposable
{
    private const string Repo = "dueño/proyecto";

    private readonly string _directory = Directory.CreateTempSubdirectory("changelog-").FullName;

    public ChangelogScriptTests()
    {
        Git("init", "--quiet", "--initial-branch=main");
    }

    public void Dispose()
    {
        // Los objetos de git son de solo lectura en algunos sistemas de archivos.
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Agrupa_los_commits_entre_dos_tags_por_tipo_y_ambito()
    {
        Commit("feat: version inicial");
        Git("tag", "v0.1.0");
        Commit("feat(api): agregar el panel de jobs");
        Commit("fix: corregir el reintento manual");
        Commit("docs: documentar el despliegue");
        Commit("test: cubrir el backoff");
        Commit("refactor: simplificar la cola");
        Commit("chore: actualizar paquetes");
        Commit("ci: agregar el release");
        Git("tag", "v0.2.0");

        var changelog = Changelog("v0.2.0");

        changelog.ShouldContain("## Funcionalidades\n\n- **api:** Agregar el panel de jobs (");
        changelog.ShouldContain("## Correcciones\n\n- Corregir el reintento manual (");
        changelog.ShouldContain("## Documentación\n\n- Documentar el despliegue (");
        changelog.ShouldContain("## Pruebas\n\n- Cubrir el backoff (");
        changelog.ShouldContain("## Refactorizaciones\n\n- Simplificar la cola (");
        changelog.ShouldContain("## Mantenimiento\n\n");
        changelog.ShouldContain("- Agregar el release (");
        changelog.ShouldContain("- Actualizar paquetes (");
        changelog.ShouldNotContain("Version inicial", Case.Insensitive, "el commit anterior al tag previo no pertenece a este release");
        changelog.ShouldNotContain("## Cambios incompatibles");
        changelog.ShouldContain($"**Historial completo:** https://github.com/{Repo}/compare/v0.1.0...v0.2.0");
    }

    [Fact]
    public void Los_enlaces_apuntan_al_commit_completo()
    {
        Commit("feat: algo");
        Git("tag", "v0.1.0");
        var sha = Commit("fix: otra cosa");
        Git("tag", "v0.1.1");

        var changelog = Changelog("v0.1.1");

        changelog.ShouldContain($"([`{sha[..7]}`](https://github.com/{Repo}/commit/{sha}))");
    }

    [Fact]
    public void Omite_los_commits_de_merge_y_conserva_los_de_la_rama()
    {
        Commit("feat: base");
        Git("tag", "v0.1.0");
        Git("checkout", "--quiet", "-b", "feature/algo");
        Commit("feat: funcionalidad de la rama");
        Git("checkout", "--quiet", "main");
        Git("merge", "--quiet", "--no-ff", "feature/algo", "-m", "Merge branch 'feature/algo'");
        Git("tag", "v0.2.0");

        var changelog = Changelog("v0.2.0");

        changelog.ShouldContain("Funcionalidad de la rama");
        changelog.ShouldNotContain("Merge branch");
    }

    [Fact]
    public void Los_cambios_incompatibles_van_primero_y_no_se_repiten_en_su_tipo()
    {
        Commit("feat: base");
        Git("tag", "v1.0.0");
        Commit("feat!: reemplazar la clave única del panel");
        Commit("fix: ajustar un mensaje\n\nBREAKING CHANGE: cambia el código de error");
        Commit("feat: algo compatible");
        Git("tag", "v2.0.0");

        var changelog = Changelog("v2.0.0");

        changelog.ShouldStartWith("## Cambios incompatibles");
        var features = changelog.IndexOf("## Funcionalidades", StringComparison.Ordinal);
        changelog[..features].ShouldContain("Reemplazar la clave única del panel");
        changelog[..features].ShouldContain("Ajustar un mensaje");
        changelog[features..].ShouldNotContain("Reemplazar");
        changelog.ShouldNotContain("## Correcciones");
    }

    [Fact]
    public void Los_commits_sin_formato_convencional_van_a_otros_cambios()
    {
        Commit("feat: base");
        Git("tag", "v0.1.0");
        Commit("arreglar cosas sueltas");
        Commit("Feat: tipo con mayúscula no es convencional");
        Git("tag", "v0.2.0");

        var changelog = Changelog("v0.2.0");

        changelog.ShouldContain("## Otros cambios");
        changelog.ShouldContain("- Arreglar cosas sueltas (");
        changelog.ShouldContain("- Feat: tipo con mayúscula no es convencional (");
    }

    [Fact]
    public void Sin_tag_anterior_incluye_todo_el_historial_y_no_arma_el_enlace_de_comparacion()
    {
        Commit("feat: primero");
        Commit("fix: segundo");
        Git("tag", "v0.1.0");

        var changelog = Changelog("v0.1.0");

        changelog.ShouldContain("Primero");
        changelog.ShouldContain("Segundo");
        changelog.ShouldNotContain("compare");
    }

    [Fact]
    public void Una_referencia_que_no_es_tag_usa_el_ultimo_tag_como_base()
    {
        Commit("feat: base");
        Git("tag", "v0.1.0");
        Commit("fix: pendiente de release");

        var changelog = Changelog("HEAD");

        changelog.ShouldContain("Pendiente de release");
        changelog.ShouldNotContain("Base");
        changelog.ShouldContain("compare/v0.1.0...HEAD");
    }

    [Fact]
    public void Con_un_tag_pre_release_toma_como_base_el_tag_inmediatamente_anterior()
    {
        Commit("feat: base");
        Git("tag", "v0.1.0");
        Commit("feat: candidata");
        Git("tag", "v1.0.0-rc.1");
        Commit("fix: ultimo ajuste");
        Git("tag", "v1.0.0");

        var changelog = Changelog("v1.0.0");

        changelog.ShouldContain("Ultimo ajuste");
        changelog.ShouldNotContain("Candidata");
        changelog.ShouldContain("compare/v1.0.0-rc.1...v1.0.0");
    }

    [Fact]
    public void Un_rango_sin_commits_lo_dice()
    {
        Commit("feat: base");
        Git("tag", "v0.1.0");

        var changelog = Changelog("v0.1.0", "v0.1.0");

        changelog.ShouldContain("Sin cambios registrados");
    }

    private string Commit(string message)
    {
        Git("commit", "--quiet", "--allow-empty", "-m", message);
        return Git("rev-parse", "HEAD").Trim();
    }

    private string Changelog(string to, string? from = null)
    {
        var args = new List<string> { Path.Combine(FindRepositoryRoot(), "scripts", "changelog.sh"), "--repo", Repo, to };
        if (from is not null)
        {
            args.Add(from);
        }

        return Run("bash", [.. args]);
    }

    private string Git(params string[] args) =>
        Run("git", ["-c", "commit.gpgsign=false", "-c", "tag.gpgsign=false", .. args]);

    private string Run(string fileName, string[] args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = _directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Prueba";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "prueba@example.com";
        start.Environment.Remove("GITHUB_REPOSITORY");

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        process.ExitCode.ShouldBe(0, $"{fileName} {string.Join(' ', args)} falló: {error.Result}");
        return output;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ErpSyncHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (ErpSyncHub.slnx).");
    }
}
