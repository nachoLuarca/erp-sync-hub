using System.Reflection;

namespace ErpSyncHub.ArchitectureTests;

/// <summary>
/// Nombres de los ensamblados y prefijos de espacio de nombres de cada capa.
/// El punto final en los prefijos evita falsos positivos: NetArchTest compara con
/// <c>StartsWith</c> y "ErpSyncHub.Api" también coincidiría con "ErpSyncHub.Application".
/// </summary>
internal static class Layers
{
    public const string Domain = "ErpSyncHub.Domain";
    public const string Application = "ErpSyncHub.Application";
    public const string Infrastructure = "ErpSyncHub.Infrastructure";
    public const string Api = "ErpSyncHub.Api";
    public const string SapClient = "SapB1.Client";
    public const string SapMock = "SapB1.Mock";

    public static Assembly Load(string name) => name switch
    {
        Domain => ErpSyncHub.Domain.AssemblyReference.Assembly,
        Application => ErpSyncHub.Application.AssemblyReference.Assembly,
        Infrastructure => ErpSyncHub.Infrastructure.AssemblyReference.Assembly,
        SapClient => SapB1.Client.AssemblyReference.Assembly,
        _ => Assembly.Load(name),
    };

    public static string Prefix(string layer) => layer + ".";
}
