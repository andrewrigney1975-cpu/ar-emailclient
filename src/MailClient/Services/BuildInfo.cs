using System.Reflection;

namespace MailClient;

/// Exposes the auto-incrementing build number embedded by the SetBuildNumber MSBuild target
/// (see MailClient.csproj). Falls back to "dev" when built without the target.
public static class BuildInfo
{
    public static string Number { get; } =
        typeof(BuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildNumber")?.Value
        ?? "dev";
}
