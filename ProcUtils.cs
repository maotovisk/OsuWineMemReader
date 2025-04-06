using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace OsuWineMemReader;

public static class ProcUtils
{
    /// <summary>
    /// Reads the /proc/{osuPid}/environ file and looks for the WINEPREFIX variable.
    /// Returns the Wine prefix value or null if not found.
    /// </summary>
    /// <param name="osuPid">The PID of the osu! process</param>
    /// <returns>The Wine prefix string or null</returns>
    public static string? GetWinePrefix(int osuPid)
    {
        var envPath = $"/proc/{osuPid}/environ";
        if (!File.Exists(envPath))
        {
            Debug.WriteLine($"File {envPath} not found.");
            return null;
        }

        try
        {
            var envVars = File.ReadAllText(envPath).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            const string prefixKey = "WINEPREFIX=";

            foreach (var env in envVars)
            {
                if (!env.StartsWith(prefixKey, StringComparison.Ordinal)) continue;
                
                var winePrefix = env[prefixKey.Length..];
                Debug.WriteLine("Found WINEPREFIX: " + winePrefix);
                return winePrefix;
            }

            Debug.WriteLine("WINEPREFIX not found in env...");
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Error reading the environment file: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Retrieves the user ID for the specified process by reading /proc/{pid}/loginuid.
    /// </summary>
    /// <param name="pid">The process ID.</param>
    /// <returns>The user ID as a string, or null if not found or an error occurs.</returns>
    public static string? GetUserId(int pid)
    {
        var loginUidPath = $"/proc/{pid}/loginuid";
        if (!File.Exists(loginUidPath))
        {
            Debug.WriteLine($"File {loginUidPath} does not exist.");
            return null;
        }

        try
        {
            var content = File.ReadAllText(loginUidPath);
            return content.Trim();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error reading file {loginUidPath}: {ex.Message}");
            return null;
        }
    }
}