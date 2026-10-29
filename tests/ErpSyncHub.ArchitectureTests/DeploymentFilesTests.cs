using System.Text.RegularExpressions;

namespace ErpSyncHub.ArchitectureTests;

/// <summary>Garantías sobre los archivos de despliegue versionados: ningún secreto vive en el repositorio.</summary>
public partial class DeploymentFilesTests
{
    [GeneratedRegex("password|secret|apikey|connectionstring|token", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKey();

    [Fact]
    public void El_Blueprint_de_Render_no_trae_valores_de_variables_sensibles()
    {
        var lines = File.ReadAllLines(Path.Combine(RepositoryRoot.Find(), "render.yaml"));
        var checkedKeys = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("- key:", StringComparison.Ordinal))
            {
                continue;
            }

            var key = trimmed["- key:".Length..].Trim();
            if (!SensitiveKey().IsMatch(key))
            {
                continue;
            }

            checkedKeys++;
            var next = i + 1 < lines.Length ? lines[i + 1].Trim() : string.Empty;
            next.ShouldBe("sync: false", $"La variable sensible {key} debe cargarse a mano en Render (sync: false), no llevar valor en el repositorio");
        }

        checkedKeys.ShouldBeGreaterThan(0, "El Blueprint debería declarar al menos una variable sensible");
    }
}
