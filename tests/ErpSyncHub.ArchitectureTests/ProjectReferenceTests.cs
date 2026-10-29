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
    public static TheoryData<string, string, string[]> AllowedReferences => new()
    {
        { "src", Layers.Domain, [] },
        { "src", Layers.Application, [Layers.Domain] },
        { "src", Layers.Infrastructure, [Layers.Application, Layers.Domain, Layers.SapClient] },
        { "src", Layers.Api, [Layers.Application, Layers.Infrastructure] },
        { "src", Layers.SapClient, [] },
        { "src", Layers.SapMock, [] },
        { "tests", Layers.UnitTests, [Layers.Domain, Layers.Application] },
        { "tests", Layers.IntegrationTests, [Layers.Api, Layers.Infrastructure, Layers.SapMock] },
        { "tests", Layers.SapMockTests, [Layers.SapMock] },
        { "tests", Layers.SapClientTests, [Layers.SapClient, Layers.SapMock] },
    };

    [Theory]
    [MemberData(nameof(AllowedReferences))]
    public void Cada_proyecto_solo_referencia_a_los_proyectos_permitidos(string folder, string project, string[] allowed)
    {
        var actual = ReadProject(folder, project)
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
        var packages = ReadProject("src", Layers.Domain).Descendants("PackageReference").ToList();

        packages.ShouldBeEmpty("Domain no admite dependencias externas");
    }

    private static XDocument ReadProject(string folder, string project)
    {
        var path = Path.Combine(RepositoryRoot.Find(), folder, project, $"{project}.csproj");
        return XDocument.Load(path);
    }
}
