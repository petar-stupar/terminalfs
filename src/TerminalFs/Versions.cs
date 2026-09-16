using System.Reflection;
using NineP.Protocol;
using NineP.Server;

namespace TerminalFs;

/// <summary>What <c>--version</c> reports: this program, and the 9P library underneath it.</summary>
/// <remarks>
/// Read from the loaded assemblies rather than from a constant, because the point of the flag is
/// to say what is actually running. A published build carries the library it was built against
/// inside it, so "which NineP is this binary using" cannot be answered from the repository once
/// the binary has left it — and that is exactly when the question gets asked. A bug report that
/// names both versions is one that can be reproduced.
/// </remarks>
internal static class Versions
{
    /// <summary>The report: this program first, then the library it sits on.</summary>
    internal static string Report => string.Join(
        Environment.NewLine,
        $"terminalfs {Of(typeof(Versions).Assembly)}",
        $"NineP.Server {Of(typeof(NinePServer).Assembly)}",
        $"NineP.Protocol {Of(typeof(Qid).Assembly)}");

    /// <summary>
    /// The version <paramref name="assembly"/> states, without the build metadata that a
    /// source-linked build appends to it.
    /// </summary>
    /// <remarks>
    /// The informational version is the one that carries the package version, including a
    /// prerelease suffix; <see cref="AssemblyName.Version"/> is the four-part assembly version,
    /// which drops it. The <c>+</c> and everything after it is the commit the build came from,
    /// which is not what was asked for.
    /// </remarks>
    private static string Of(Assembly assembly)
    {
        string? stated = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrEmpty(stated))
        {
            return assembly.GetName().Version?.ToString() ?? "unknown";
        }

        int metadata = stated.IndexOf('+', StringComparison.Ordinal);

        return metadata < 0 ? stated : stated[..metadata];
    }
}
