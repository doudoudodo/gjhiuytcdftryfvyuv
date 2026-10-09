using System.Linq;
using System.IO;

namespace Coclico.Services;

public sealed record ParsedUninstallCommand(
    string ExecutableOrUri,
    string Arguments,
    bool IsUri,
    bool IsMsi);

public static class UninstallCommandParser
{
    public static ParsedUninstallCommand? Parse(string? rawCommand)
    {
        if (string.IsNullOrWhiteSpace(rawCommand))
        {
            return null;
        }

        string trimmed = rawCommand.Trim();

        // 1. URI schemes (Steam, Epic, ms-settings, etc.)
        if (trimmed.StartsWith("steam://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("com.epicgames.launcher://", StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedUninstallCommand(trimmed, string.Empty, IsUri: true, IsMsi: false);
        }

        if (trimmed.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            // Only well-formed ms-settings pages (ASCII slug) — same rule as the
            // security policy, applied here for defense in depth.
            string page = trimmed["ms-settings:".Length..];
            if (page.Length > 0 && page.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return new ParsedUninstallCommand(trimmed, string.Empty, IsUri: true, IsMsi: false);
            }

            return null;
        }

        // 2. MSI installers: msiexec /x {GUID} or msiexec.exe /I{GUID}
        if (trimmed.StartsWith("msiexec", StringComparison.OrdinalIgnoreCase))
        {
            int exeLen = trimmed.StartsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase) ? 11 : 7;
            string msiArgs = trimmed[exeLen..].Trim();

            // Only uninstall (/x, /uninstall) and maintenance (/i) invocations are honored;
            // registry strings carrying other msiexec modes are rejected as suspicious.
            if (!IsMsiUninstallInvocation(msiArgs))
            {
                return null;
            }

            string msiExe = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            return new ParsedUninstallCommand(File.Exists(msiExe) ? msiExe : "msiexec.exe", msiArgs, IsUri: false, IsMsi: true);
        }

        // 3. Quoted path: "C:\Path\To\uninstall.exe" /arg1 /arg2
        if (trimmed.StartsWith('"'))
        {
            int closeQuote = trimmed.IndexOf('"', 1);
            if (closeQuote > 1)
            {
                string exe = trimmed[1..closeQuote].Trim();
                string args = trimmed[(closeQuote + 1)..].Trim();
                return new ParsedUninstallCommand(exe, args, IsUri: false, IsMsi: false);
            }
        }

        // 4. Unquoted path with spaces: e.g. C:\Program Files (x86)\App\unins000.exe /silent
        int exeExtIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (exeExtIndex > 0)
        {
            int candidateEnd = exeExtIndex + 4;
            string candidateExe = trimmed[..candidateEnd].Trim();
            if (File.Exists(candidateExe))
            {
                string args = trimmed[candidateEnd..].Trim();
                return new ParsedUninstallCommand(candidateExe, args, IsUri: false, IsMsi: false);
            }

            exeExtIndex = trimmed.IndexOf(".exe", exeExtIndex + 4, StringComparison.OrdinalIgnoreCase);
        }

        // 5. Fallback: split on first space
        int firstSpace = trimmed.IndexOf(' ');
        if (firstSpace > 0)
        {
            string exe = trimmed[..firstSpace].Trim();
            string args = trimmed[(firstSpace + 1)..].Trim();
            return new ParsedUninstallCommand(exe, args, IsUri: false, IsMsi: false);
        }

        return new ParsedUninstallCommand(trimmed, string.Empty, IsUri: false, IsMsi: false);
    }

    private static bool IsMsiUninstallInvocation(string args)
    {
        return args.StartsWith("/x", StringComparison.OrdinalIgnoreCase)
            || args.StartsWith("/i", StringComparison.OrdinalIgnoreCase)
            || args.StartsWith("/uninstall", StringComparison.OrdinalIgnoreCase);
    }
}
