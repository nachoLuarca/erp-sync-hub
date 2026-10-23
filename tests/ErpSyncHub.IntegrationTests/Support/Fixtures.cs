namespace ErpSyncHub.IntegrationTests.Support;

/// <summary>Acceso a los archivos de ejemplo de tests/Fixtures, copiados junto a los binarios de prueba.</summary>
internal static class Fixtures
{
    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));

    public static byte[] ReadBytes(string relativePath) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));
}
