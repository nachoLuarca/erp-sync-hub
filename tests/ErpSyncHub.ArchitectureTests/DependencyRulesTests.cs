using NetArchTest.Rules;

namespace ErpSyncHub.ArchitectureTests;

/// <summary>
/// Valida a nivel de tipos la regla de dependencias entre capas del proyecto.
/// </summary>
public class DependencyRulesTests
{
    private static readonly string[] InfrastructureConcerns =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Npgsql",
        "Serilog",
    ];

    [Fact]
    public void Domain_no_depende_de_ninguna_otra_capa_ni_de_infraestructura()
    {
        AssertNoDependencies(
            Layers.Domain,
            [
                Layers.Prefix(Layers.Application),
                Layers.Prefix(Layers.Infrastructure),
                Layers.Prefix(Layers.Api),
                Layers.Prefix(Layers.SapClient),
                Layers.Prefix(Layers.SapMock),
                .. InfrastructureConcerns,
            ]);
    }

    [Fact]
    public void Application_solo_depende_de_Domain()
    {
        AssertNoDependencies(
            Layers.Application,
            [
                Layers.Prefix(Layers.Infrastructure),
                Layers.Prefix(Layers.Api),
                Layers.Prefix(Layers.SapClient),
                Layers.Prefix(Layers.SapMock),
                .. InfrastructureConcerns,
            ]);
    }

    [Fact]
    public void Infrastructure_no_depende_de_Api_ni_de_SapB1_Mock()
    {
        AssertNoDependencies(
            Layers.Infrastructure,
            [
                Layers.Prefix(Layers.Api),
                Layers.Prefix(Layers.SapMock),
            ]);
    }

    [Fact]
    public void Api_no_depende_directamente_de_SapB1_Client_ni_de_SapB1_Mock()
    {
        AssertNoDependencies(
            Layers.Api,
            [
                Layers.Prefix(Layers.SapClient),
                Layers.Prefix(Layers.SapMock),
            ]);
    }

    [Fact]
    public void SapB1_Client_es_independiente_del_hub()
    {
        AssertNoDependencies(Layers.SapClient, [Layers.Prefix("ErpSyncHub")]);
    }

    [Fact]
    public void SapB1_Mock_es_independiente_del_hub()
    {
        AssertNoDependencies(Layers.SapMock, [Layers.Prefix("ErpSyncHub")]);
    }

    private static void AssertNoDependencies(string assemblyName, string[] forbidden)
    {
        var result = Types
            .InAssembly(Layers.Load(assemblyName))
            .ShouldNot()
            .HaveDependencyOnAny(forbidden)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(
            $"{assemblyName} no debe depender de [{string.Join(", ", forbidden)}]. " +
            $"Tipos infractores: {string.Join(", ", result.FailingTypeNames ?? [])}");
    }
}
