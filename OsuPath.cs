using System;
using System.IO;
using System.Linq;

namespace OsuMemReader;

public static class OsuPath
{
    /// <summary>
    /// Tries to extract the osu! installation path from the Wine registry files.
    /// </summary>
    /// <param name="winePrefix">The Wine prefix directory.</param>
    /// <param name="regFile">The registry file name (e.g., "system.reg").</param>
    /// <param name="subkeys">An array of subkey strings to search for.</param>
    /// <returns>The extracted osu! folder path or null if not found.</returns>
    private static string? TryGetOsuPath(string winePrefix, string regFile, string[] subkeys)
    {
        var regPath = Path.Combine(winePrefix, regFile);

        try
        {
            using var file = new StreamReader(regPath);
            string? result = null;

            while (file.ReadLine() is { } line)
            {
                foreach (var subkey in subkeys)
                {
                    if (line == null || !line.Contains(subkey, StringComparison.OrdinalIgnoreCase)) continue;

                    while ((line = file.ReadLine()) != null)
                    {
                        var findIndex = line.IndexOf("osu!.exe", StringComparison.Ordinal);
                        if (findIndex < 0) continue;

                        line = line[..findIndex]; // Equivalent to setting '\0' in C

                        var firstIndex = line.IndexOf(@":\\", StringComparison.Ordinal);
                        if (firstIndex < 0)
                            return result;

                        var path = line[(firstIndex - 1)..];
                        result = path;
                        return result;
                    }
                }
            }

            if (result == null)
                Console.Error.WriteLine("Couldn't find song folder!");

            return result;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(regPath + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Returns the osu! installation folder path by searching in the Wine registry files.
    /// </summary>
    /// <param name="winePrefix">The Wine prefix directory.</param>
    /// <returns>The osu! installation folder path or null if not found.</returns>
    private static string? GetOsuPath(string winePrefix)
    {
        string[] list =
        [
            @"osu\\shell\\open\\command",
            @"osustable.File.osz\\shell\\open\\command"
        ];

        return TryGetOsuPath(winePrefix, "system.reg", list) ?? TryGetOsuPath(winePrefix, "user.reg", list);
    }

    /// <summary>
    /// Retrieves the osu! songs folder path.
    /// </summary>
    /// <param name="winePrefix">The Wine prefix directory.</param>
    /// <param name="uid">The UID string of the user running osu!.</param>
    /// <returns>The full path to the songs folder or null if not found.</returns>
    public static string? GetOsuSongsPath(string? winePrefix, string uid)
    {
        if (!File.Exists("/etc/passwd"))
        {
            Console.Error.WriteLine("/etc/passwd: File not found. Are you sure you're running this on Linux?");
            return null;
        }

        string? userName = null;

        foreach (var line in File.ReadLines("/etc/passwd"))
        {
            var parts = line.Split(':');

            if (parts.Length < 6 || parts[2] != uid) continue;

            userName = parts[0];
            winePrefix ??= Path.Combine(parts[5], ".wine");

            break;
        }

        if (userName == null || winePrefix == null)
            return null;

        var osuPath = GetOsuPath(winePrefix);
        if (osuPath == null)
            return null;

        osuPath = osuPath.Replace('\\', '/');
        if (osuPath.Length > 0)
            osuPath = char.ToLower(osuPath[0]) + osuPath[1..];

        const string dosDevices = "dosdevices";
        var basePath = Path.Combine(winePrefix, dosDevices, osuPath);

        var unixPath = Path.GetFullPath(basePath);

        // check if the path is a symlink, if so, resolve it
        if (File.Exists(unixPath) || Directory.Exists(unixPath))
        {
            var info = new FileInfo(unixPath);
            if (info.Directory!.Exists && info.Directory.LinkTarget != null)
            {
                unixPath = Path.GetFullPath(info.Directory?.LinkTarget ?? "");
                basePath = unixPath;
            }
        }

        var cfgPath = Path.Combine(basePath, $"osu!.{userName}.cfg");

        var convertedCfgPath = TryConvertWinPath(cfgPath, basePath.Length + 1);
        if (convertedCfgPath == null || !File.Exists(convertedCfgPath))
            return null;

        // Find beatmap directory in config file
        string? beatmapDir = null;
        const string marker = "BeatmapDirectory = ";

        using (var stream = new FileStream(convertedCfgPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new StreamReader(stream))
        {
            while (reader.ReadLine() is { } line)
            {
                if (!line.StartsWith(marker)) continue;

                beatmapDir = line[marker.Length..].Trim().Replace('\\', '/');
                break;
            }
        }

        if (beatmapDir == null)
            return null;

        string result;
        var isAbsolute = beatmapDir.Length > 1 && beatmapDir[1] == ':';

        if (isAbsolute)
        {
            if (beatmapDir.Length > 0)
                beatmapDir = char.ToLower(beatmapDir[0]) + beatmapDir[1..];

            result = Path.Combine(winePrefix, dosDevices, beatmapDir);
        }
        else
        {
            result = Path.Combine(basePath, beatmapDir);
        }
        var finalPath = TryConvertWinPath(result, basePath.Length + 1);
        
        return finalPath != null ? Path.GetFullPath(finalPath) : null;
    }


    /// <summary>
    /// Tries to convert a Windows-style path to a Unix-style path.
    /// </summary>
    /// <param name="path"> The Windows-style path to convert.</param>
    /// <param name="pos"> The position in the path to start searching for segments.</param>
    /// <returns> The converted path if found, otherwise null.</returns>
    private static string? TryConvertWinPath(string path, int pos)
    {
        if (File.Exists(path) || Directory.Exists(path))
            return path;

        var workingPath = path;
        var startPos = pos;

        while (true)
        {
            var endPos = workingPath.IndexOf('/', startPos);
            if (endPos >= 0)
            {
                while (endPos + 1 < workingPath.Length && workingPath[endPos + 1] == '/')
                    endPos++;
            }

            var isLastSegment = endPos < 0;

            if (!isLastSegment && endPos == workingPath.Length - 1)
            {
                workingPath = workingPath.Substring(0, endPos);
                endPos = workingPath.Length;
                isLastSegment = true;
            }

            var segmentEnd = isLastSegment ? workingPath.Length - 1 : endPos - 1;
            while (segmentEnd > startPos &&
                   (char.IsWhiteSpace(workingPath[segmentEnd]) || workingPath[segmentEnd] == '.' ||
                    workingPath[segmentEnd] == '/'))
            {
                segmentEnd--;
            }

            if (segmentEnd < startPos)
                return null;

            if (segmentEnd != (isLastSegment ? workingPath.Length - 1 : endPos - 1))
            {
                if (isLastSegment)
                    workingPath = workingPath.Substring(0, segmentEnd + 1);
                else
                    workingPath = workingPath.Substring(0, segmentEnd + 1) + workingPath.Substring(endPos);

                if (!isLastSegment)
                    endPos = segmentEnd + 1;
            }

            if (File.Exists(workingPath) || Directory.Exists(workingPath))
                return workingPath;

            var parentPath = startPos > 0 ? workingPath.Substring(0, startPos - 1) : "";
            var segment = isLastSegment
                ? workingPath.Substring(startPos)
                : workingPath.Substring(startPos, endPos - startPos);

            try
            {
                var dirInfo = new DirectoryInfo(parentPath);
                var foundEntry = dirInfo.EnumerateFileSystemInfos()
                    .FirstOrDefault(e => string.Equals(e.Name, segment, StringComparison.OrdinalIgnoreCase));

                if (foundEntry != null)
                {
                    workingPath = Path.Combine(parentPath, foundEntry.Name) +
                                  (isLastSegment ? "" : workingPath.Substring(endPos));
                    if (isLastSegment)
                        return workingPath;
                    startPos = endPos + 1;
                }
                else
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{parentPath}: {ex.Message}");
                return null;
            }
        }
    }
}