using System.Xml.Linq;

namespace ErpSyncHub.ArchitectureTests;

/// <summary>
/// Valida la regla de dependencias sobre los <c>ProjectReference</c> de cada .csproj, con lista
/// exacta de referencias permitidas. Complementa a <see cref="DependencyRulesTests"/>: sigue
/// siendo efectiva aunque una capa aún no tenga tipos o el compilador descarte una referencia
/// sin uso.
/// </summary>
public class ProjectReferenceTests
{
    public static TheoryData<string, string[]> AllowedReferences => new()
    {
        { Layers.Domain, [] },
        { Layers.Application, [Layers.Domain] },
        { Layers.Infrastructure, [Layers.Application, Layers.Domain, Layers.SapClient] },
        { Layers.Api, [Layers.Application, Layers.Infrastructure] },
        { Layers.SapClient, [] },
        { Layers.SapMock, [] },
    };

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void Cada_proyecto_solo_referencia_a_los_proyectos_permitidos(string project, string[] allowed)
    {
        var actual = ReadProject(project)
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")!.Value.Replace('\\', '/'))
            .Select(include => Path.GetFileNameWithoutExtension(include)!)
            .Order()
            .ToArray();

        actual.ShouldBe(allowed.Order().ToArray(), $"Referencias de proyecto inesperadas en {project}");
    }

    [Fact]
    public void Domain_no_tiene_paquetes_NuGet()
    {
        var packages = ReadProject(Layers.Domain).Descendants("PackageReference").ToList();

        packages.ShouldBeEmpty("Domain no admite dependencias externas");
    }

    private static XDocument ReadProject(string project)
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", project, $"{project}.csproj");
        return XDocument.Load(path);
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
