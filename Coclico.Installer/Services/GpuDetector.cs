using System;
using System.Management;
using Microsoft.Win32;

namespace Coclico.Installer.Services;

public static class GpuDetector
{
    public record GpuInfo(bool HasNvidia, string Name);

    public static GpuInfo DetectGpu()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (var obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString() ?? string.Empty;
                if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("RTX", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("GTX", StringComparison.OrdinalIgnoreCase))
                {
                    return new GpuInfo(true, name);
                }
            }
        }
        catch
        {
            // Silently fallback if WMI is restricted
        }

        try
        {
            using var pciKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pciKey != null)
            {
                foreach (string subKeyName in pciKey.GetSubKeyNames())
                {
                    if (subKeyName.StartsWith("VEN_10DE", StringComparison.OrdinalIgnoreCase))
                    {
                        return new GpuInfo(true, "GPU NVIDIA Compatible");
                    }
                }
            }
        }
        catch
        {
            // Silently ignore if registry is restricted
        }

        return new GpuInfo(false, "Carte graphique standard");
    }
}
