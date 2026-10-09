using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Coclico.Installer.Services;

public record SystemRequirementStatus(
    bool HasDotNet10,
    string DotNet10Version,
    bool HasNvidiaGpu,
    string GpuName,
    long AvailableDiskSpaceBytes,
    string TargetDriveLetter);

public static class SystemRequirementService
{
    public static SystemRequirementStatus CheckRequirements(string targetPath)
    {
        // 1. Vérification .NET 10 Desktop Runtime
        bool hasDotNet10 = false;
        string dotNetVersion = "Non détecté";

        try
        {
            // Vérification dans Program Files\dotnet\shared\Microsoft.WindowsDesktop.App
            string defaultDotNetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "shared", "Microsoft.WindowsDesktop.App");

            if (Directory.Exists(defaultDotNetDir))
            {
                foreach (string dir in Directory.GetDirectories(defaultDotNetDir))
                {
                    string dirName = Path.GetFileName(dir);
                    if (dirName.StartsWith("10.", StringComparison.OrdinalIgnoreCase))
                    {
                        hasDotNet10 = true;
                        dotNetVersion = $"v{dirName}";
                        break;
                    }
                }
            }

            // Vérification Registre Windows
            if (!hasDotNet10)
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App");
                if (key != null)
                {
                    foreach (string valName in key.GetValueNames())
                    {
                        if (valName.StartsWith("10.", StringComparison.OrdinalIgnoreCase))
                        {
                            hasDotNet10 = true;
                            dotNetVersion = $"v{valName}";
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            // Fallback
        }

        // 2. Détection GPU
        var gpu = GpuDetector.DetectGpu();

        // 3. Espace disque disponible sur le lecteur cible
        long freeBytes = 0;
        string driveLetter = "C:";
        try
        {
            string root = Path.GetPathRoot(targetPath) ?? "C:\\";
            driveLetter = root.TrimEnd('\\');
            var driveInfo = new DriveInfo(root);
            if (driveInfo.IsReady)
            {
                freeBytes = driveInfo.AvailableFreeSpace;
            }
        }
        catch { }

        return new SystemRequirementStatus(
            hasDotNet10,
            dotNetVersion,
            gpu.HasNvidia,
            gpu.Name,
            freeBytes,
            driveLetter);
    }
}

