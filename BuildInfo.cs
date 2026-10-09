using System.Reflection;

// Version and repository come from TipAura.csproj (<Version>, <RepositoryUrl>) through assembly attributes.
internal static class BuildInfo
{
    private static readonly Assembly Assembly = typeof(BuildInfo).Assembly;

    internal static string Version { get; } =
        Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    internal static string RepositoryUrl { get; } = Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "RepositoryUrl")?.Value ?? "";
}
