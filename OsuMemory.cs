using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OsuWineMemReader;

public static class OsuMemory
{
    private const string OsuProcessName = "osu!.exe";
    private const int PtrSize = 4;
    private static readonly byte[] OsuBaseSig = [0xf8, 0x01, 0x74, 0x04, 0x83, 0x65];
    private const int OsuBaseSize = 6;
    private const int ScanChunkSize = 64 * 1024; // 64KB chunks

    private class SigScanStatus
    {
        public int Status { get; set; } = -1;
        public int OsuPid { get; set; } = -1;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern long process_vm_readv(int pid,
        [In] IoVector[] localIoVector,
        ulong localIoVectorLength,
        [In] IoVector[] remoteIoVector,
        ulong remoteIoVectorLength,
        ulong flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);

    private class VmRegion
    {
        public long Start;
        public long Length;
    }

    // This one needs to be a struct because C interop
    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        public IntPtr IoVectorBase;
        public IntPtr IoVectorLength;
    }

    public class OsuMemoryOptions
    {
        public bool RunOnce { get; set; } = false;
        public bool WriteToFile { get; set; } = false;
        public string FilePath { get; set; } = "/tmp/osu_path";
    }

    /// <summary>
    /// Finds the osu! process by checking the /proc filesystem. Write the PID to the status object.
    /// </summary>
    /// <param name="status">The status object to update with the found PID.</param>
    private static void FindOsuProcess(SigScanStatus status)
    {
        if (status.OsuPid > 0 && IsProcessAlive(status.OsuPid))
        {
            status.Status = 1;
            return;
        }

        foreach (var dir in new DirectoryInfo("/proc").EnumerateDirectories())
        {
            if (!int.TryParse(dir.Name, out var pid) || pid <= 0) continue;

            var commPath = Path.Combine(dir.FullName, "comm");
            if (!File.Exists(commPath)) continue;

            try
            {
                if (File.ReadAllText(commPath).Trim() != OsuProcessName) continue;

                status.OsuPid = pid;
                status.Status = 2;
                Debug.WriteLine($"Found PID: {pid}");
                return;
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error reading {commPath}: {ex.Message}");
            }
        }

        status.Status = -1;
    }

    /// <summary>
    /// Sends a signal 0 to the process with the given PID to check if it's alive.
    /// </summary>
    /// <param name="pid"> The PID of the process to check.</param>
    /// <returns>True if the process is alive, false otherwise.</returns>
    private static bool IsProcessAlive(int pid) => kill(pid, 0) == 0;

    /// <summary>
    ///  Reads memory from the process with the given PID.
    /// </summary>
    /// <param name="status">The status object containing the PID.</param>
    /// <param name="address">The address to read from.</param>
    /// <param name="buffer">The buffer to be filled with the read data.</param>
    /// <param name="length">The length of the buffer.</param>
    /// <returns>True if the read was successful, false otherwise.</returns>
    private static bool TryReadMemory(SigScanStatus status, long address, byte[] buffer, int length)
    {
        if (address == 0) return false;

        var localPtr = Marshal.AllocHGlobal(length);
        try
        {
            var localIov = new IoVector { IoVectorBase = localPtr, IoVectorLength = new IntPtr(length) };
            var remoteIov = new IoVector { IoVectorBase = new IntPtr(address), IoVectorLength = new IntPtr(length) };

            var result = process_vm_readv(status.OsuPid, [localIov], 1, [remoteIov], 1, 0);
            if (result == -1)
            {
                return false;
            }

            Marshal.Copy(localPtr, buffer, 0, length);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(localPtr);
        }
    }

    /// <summary>
    /// Scans the memory regions of the osu! process to find all readable memory regions.
    /// </summary>
    /// <param name="status">The status object containing the PID.</param>
    /// <returns>An enumerable collection of memory regions.</returns>
    private static IEnumerable<VmRegion> EnumerateMemoryRegions(SigScanStatus status)
    {
        foreach (var line in File.ReadAllLines($"/proc/{status.OsuPid}/maps"))
        {
            var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[1].Contains("r")) continue;

            var addresses = parts[0].Split('-');
            if (addresses.Length != 2) continue;

            yield return new VmRegion
            {
                Start = Convert.ToInt64(addresses[0], 16),
                Length = Convert.ToInt64(addresses[1], 16) - Convert.ToInt64(addresses[0], 16)
            };
        }
    }

    /// <summary>
    /// Tries to find a specific byte pattern in the memory of the osu! process.
    /// </summary>
    /// <param name="status">The status object containing the PID.</param>
    /// <param name="pattern">The byte pattern to search for.</param>
    /// <param name="patternSize">The size of the byte pattern.</param>
    /// <param name="result">The address where the pattern was found.</param>
    /// <returns>True if the pattern was found, false otherwise.</returns>
    private static bool TryFindPattern(SigScanStatus status, byte[] pattern, int patternSize,
        out long result)
    {
        result = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(ScanChunkSize + patternSize - 1);

        try
        {
            var patternSpan = pattern.AsSpan(0, patternSize);

            foreach (var region in EnumerateMemoryRegions(status))
            {
                for (long offset = 0; offset < region.Length; offset += ScanChunkSize)
                {
                    var readAddr = region.Start + offset;
                    var readSize = (int)Math.Min(ScanChunkSize + patternSize - 1, region.Length - offset);

                    if (!TryReadMemory(status, readAddr, buffer, readSize))
                        continue;

                    var windowSpan = buffer.AsSpan(0, readSize);

                    if (windowSpan.IndexOf(patternSpan) is var index and >= 0)
                    {
                        result = readAddr + index;
                        return true;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return false;
    }

    /// <summary>
    /// Retrieves the pointer to the beatmap structure from the osu! process memory.
    /// </summary>
    /// <param name="status">The status object containing the PID.</param>
    /// <param name="baseAddress">The base address of the osu! process.</param>
    /// <returns>The pointer to the beatmap structure, or 0 if not found.</returns>
    private static long GetBeatmapPtr(SigScanStatus status, long baseAddress)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(PtrSize);
        try
        {
            if (!TryReadMemory(status, baseAddress - 0xC, buffer, PtrSize))
                return 0;

            long beatmapPtr = BitConverter.ToInt32(buffer, 0);
            return TryReadMemory(status, beatmapPtr, buffer, PtrSize) ? BitConverter.ToInt32(buffer, 0) : 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Retrieves the path of the currently playing beatmap from the osu! process memory.
    /// </summary>
    /// <param name="status">The status object containing the PID.</param>
    /// <param name="baseAddress">The base address of the osu! process.</param>
    /// <returns>The path of the beatmap, or null if not found.</returns>
    private static string? GetMapPath(SigScanStatus status, long baseAddress)
    {
        var beatmapPtr = GetBeatmapPtr(status, baseAddress);
        if (beatmapPtr == 0) return null;

        var buffer = ArrayPool<byte>.Shared.Rent(PtrSize);
        try
        {
            if (!TryReadMemory(status, beatmapPtr + 0x78, buffer, PtrSize)) return null;
            long folderPtr = BitConverter.ToInt32(buffer, 0);

            if (!TryReadMemory(status, beatmapPtr + 0x90, buffer, PtrSize)) return null;
            long pathPtr = BitConverter.ToInt32(buffer, 0);

            if (!TryReadMemory(status, folderPtr + 4, buffer, 4)) return null;
            var folderSize = BitConverter.ToInt32(buffer, 0);

            if (!TryReadMemory(status, pathPtr + 4, buffer, 4)) return null;
            var pathSize = BitConverter.ToInt32(buffer, 0);

            if (folderSize > 256 || pathSize > 256) return null;

            var stringBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(folderSize, pathSize) * 2);
            try
            {
                if (!TryReadMemory(status, folderPtr + 8, stringBuffer, folderSize * 2)) return null;
                var folder = Encoding.Unicode.GetString(stringBuffer, 0, folderSize * 2);

                if (!TryReadMemory(status, pathPtr + 8, stringBuffer, pathSize * 2)) return null;
                var path = Encoding.Unicode.GetString(stringBuffer, 0, pathSize * 2);

                var fullPath = $"{folder}/{path}".Replace('\\', '/');
                return fullPath;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(stringBuffer);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Runs the memory scanning process for osu! in a loop.
    /// </summary>
    /// <param name="running">A reference to a boolean indicating whether the process should continue running.</param>
    /// <param name="result">The current beatmap path, if any.</param>
    /// <param name="options">The options for the memory scanning process.</param>
    public static void StartBeatmapPathReading(ref bool running, out string? result, OsuMemoryOptions options)
    {
        var status = new SigScanStatus();
        long baseAddress = 0;
        string? oldPath = null;
        string? songsPath = null;
        string? winePrefix = null;
        result = null;

        var waitingDisplayed = false;

        while (running)
        {
            FindOsuProcess(status);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (status.Status < 0 || status.OsuPid <= 0)
            {
                if (!waitingDisplayed)
                {
                    Debug.WriteLine("Waiting for osu!...");
                    waitingDisplayed = true;
                }

                baseAddress = 0;
                Thread.Sleep(300);
                continue;
            }

            if (baseAddress == 0)
            {
                Debug.WriteLine("Starting memory scanning...");
                winePrefix ??= ProcUtils.GetWinePrefix(status.OsuPid);

                if (winePrefix != null && string.IsNullOrEmpty(songsPath))
                {
                    songsPath = GetSongsPath(status, winePrefix);
                }

                if (!TryFindPattern(status, OsuBaseSig, OsuBaseSize, out baseAddress))
                {
                    Debug.WriteLine("Scan failed, retrying...");
                    Thread.Sleep(3000);
                    continue;
                }

                Debug.WriteLine($"Base found: 0x{baseAddress:X16}");
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            var songPath = GetMapPath(status, baseAddress);
            if (songPath != null && songPath != oldPath)
            {
                Debug.WriteLine($"New beatmap: {songsPath}/{songPath}");
                if (options.WriteToFile)
                {
                    WriteToFile(options.FilePath, songsPath, songPath);
                }

                oldPath = songPath;
                if (options.RunOnce)
                {
                    result = $"{songsPath}/{songPath}";
                    running = false;
                    break;
                }
            }
            else if (songPath == null)
            {
                baseAddress = 0;
            }

            result = songPath;

            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(500);
        }
    }
    
    /// <summary>
    /// Gets the path to the osu! songs folder.
    /// </summary>
    /// <param name="status">The status object containing the PID.</param>
    /// <param name="winePrefix">The wine prefix path.</param>
    /// <returns>The path to the osu! songs folder, or null if not found.</returns>
    private static string? GetSongsPath(SigScanStatus status, string winePrefix)
    {
        var userId = ProcUtils.GetUserId(status.OsuPid);
        var songsPath = OsuPath.GetOsuSongsPath(winePrefix, userId ?? "1000");
        if (!Directory.Exists(songsPath))
        {
            Debug.WriteLine($"Songs folder not found: {songsPath}");
            return null;
        }

        Debug.WriteLine($"Songs folder found: {songsPath}");
        return songsPath;
    }

    /// <summary>
    /// Writes the current beatmap path to a file.
    /// </summary>
    /// <param name="filePath">The path to the file where the beatmap path will be written.</param>
    /// <param name="songsPath">The path to the osu! songs folder.</param>
    /// <param name="songPath">The path to the current beatmap.</param>
    private static void WriteToFile(string filePath, string? songsPath, string songPath)
    {
        try
        {
            if (!Directory.Exists(filePath))
            {
                File.WriteAllText(filePath, $"0 {songsPath}/{songPath}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"File error: {ex.Message}");
        }
    }
}