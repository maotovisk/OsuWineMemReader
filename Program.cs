using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace OsuMemReader;

public static partial class Program
{
    private const string OsuProcessName = "osu!.exe";
    private const int PtrSize = 4; // int32 (4 bytes)

    private static readonly byte[] OsuBaseSig = [0xf8, 0x01, 0x74, 0x04, 0x83, 0x65];
    private const int OsuBaseSize = 6;

    private class SigScanStatus
    {
        public int Status { get; set; } = -1;
        public int OsuPid { get; set; } = -1;
    }

    private struct VmRegion
    {
        public long Start;
        public long Length;
    }
    
    [DllImport("libc", SetLastError = true)]
    private static extern long process_vm_readv(int pid,
        [In] IoVector[] localIov,
        ulong liovcnt,
        [In] IoVector[] remoteIov,
        ulong riovcnt,
        ulong flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoVector
    {
        public IntPtr iov_base;
        public IntPtr iov_len;
    }

    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("Starting memory reader...");

        var status = new SigScanStatus();
        long baseAddress = 0;
        string? oldPath = null;

        var running = true;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            running = false;
            Console.WriteLine("Saindo...");
        };

        while (running)
        {
            FindOsuProcess(status);

            if (status is { Status: >= 0, OsuPid: > 0 })
            {
                if (baseAddress == 0)
                {
                    Console.WriteLine("Starting memory scanning...");

                    if (FindPattern(status, OsuBaseSig, OsuBaseSize, null, out baseAddress))
                    {
                        Console.WriteLine($"Scan successfully concluded. Base: 0x{baseAddress:X16}");
                    }
                    else
                    {
                        Console.WriteLine("Fail during memory scan. Trying again in 3 seconds...");
                        Thread.Sleep(3000);
                        continue;
                    }
                }

                string? songPath = GetMapPath(status, baseAddress, out var length);
                if (songPath != null)
                {
                    if (songPath != oldPath)
                    {
                        Console.WriteLine($"Current beatmap: {songPath}");

                        try
                        {
                            File.WriteAllText("/tmp/osu_path", $"0 {songPath}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error on saving temp file: {ex.Message}");
                        }

                        oldPath = songPath;
                    }
                }
                else
                {
                    Console.WriteLine("Failure on getting beatmap path. Scanning again...");
                    baseAddress = 0;
                }
            }
            else
            {
                Console.WriteLine("Waiting for osu! process...");
                baseAddress = 0;
            }

            Thread.Sleep(1000);
        }
    }

    static void FindOsuProcess(SigScanStatus status)
    {
        if (status.OsuPid > 0 && IsProcessAlive(status.OsuPid))
        {
            status.Status = 1;
            return;
        }

        var procDir = new DirectoryInfo("/proc");
        foreach (var dir in procDir.GetDirectories())
        {
            if (int.TryParse(dir.Name, out int pid) && pid > 0)
            {
                try
                {
                    string commPath = Path.Combine(dir.FullName, "comm");
                    if (File.Exists(commPath))
                    {
                        string procName = File.ReadAllText(commPath).Trim();
                        if (procName == OsuProcessName)
                        {
                            status.OsuPid = pid;
                            status.Status = 2;
                            Console.WriteLine($"Found osu! process, PID: {pid}");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error while accessing process {pid}: {ex.Message}");
                }
            }
        }

        status.Status = -1;
    }

    static bool IsProcessAlive(int pid)
    {
        return kill(pid, 0) == 0;
    }

    static bool ReadMemory(SigScanStatus status, long address, byte[] buffer, int length)
    {
        if (address == 0)
            return false;

        IntPtr localPtr = Marshal.AllocHGlobal(length);

        try
        {
            var localIov = new IoVector
            {
                iov_base = localPtr,
                iov_len = new IntPtr(length)
            };

            var remoteIov = new IoVector
            {
                iov_base = new IntPtr(address),
                iov_len = new IntPtr(length)
            };

            long result = process_vm_readv(status.OsuPid,
                [localIov],
                1,
                [remoteIov],
                1,
                0);

            if (result == -1)
            {
                int errno = Marshal.GetLastWin32Error();
                Console.WriteLine($"Error while reading memory at address 0x{address:X16}: {errno}");
                return false;
            }

            Marshal.Copy(localPtr, buffer, 0, length);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception while reading memory: {ex.Message}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(localPtr);
        }
    }

    static IEnumerable<VmRegion> EnumerateMemoryRegions(SigScanStatus status)
    {
        string mapsPath = $"/proc/{status.OsuPid}/maps";

        string[] lines = File.ReadAllLines(mapsPath);

        foreach (string line in lines)
        {
            string?[] parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            string? perms = null;
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i] is { Length: 4 } && parts[i]!.Contains("r"))
                {
                    perms = parts[i];
                    break;
                }
            }

            if (perms == null || !perms.Contains("r"))
                continue;

            string[]? addresses = parts[0]?.Split('-');
            if (addresses != null && addresses.Length != 2)
                continue;

            long start = Convert.ToInt32(addresses?[0], 16);
            long end = Convert.ToInt32(addresses?[1], 16);
            long length = end - start;

            if (length > 50 * 1024 * 1024)
                continue;

            yield return new VmRegion
            {
                Start = start,
                Length = length
            };
        }
    }

    private static bool FindPattern(SigScanStatus status, byte[] pattern, int patternSize, bool[]? mask, out long result)
    {
        result = 0;

        foreach (var region in EnumerateMemoryRegions(status))
        {
            if (region.Length < patternSize)
                continue;

            try
            {
                byte[] buffer = new byte[region.Length];
                if (!ReadMemory(status, region.Start, buffer, (int)region.Length))
                    continue;

                for (long i = 0; i <= region.Length - patternSize; i++)
                {
                    bool match = true;

                    for (int j = 0; j < patternSize; j++)
                    {
                        if (buffer[i + j] != pattern[j] && (mask == null || !mask[j]))
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        result = region.Start + i;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error while scanning region 0x{region.Start:X16}: {ex.Message}");
            }
        }

        return false;
    }

    static long GetBeatmapPtr(SigScanStatus status, long baseAddress)
    {
        byte[] buffer = new byte[PtrSize];

        if (baseAddress <= 0xC)
            return 0;

        if (!ReadMemory(status, baseAddress - 0xC, buffer, PtrSize))
            return 0;

        long beatmapPtr = BitConverter.ToInt32(buffer, 0);

        if (beatmapPtr <= 0)
            return 0;

        if (!ReadMemory(status, beatmapPtr, buffer, PtrSize))
            return 0;

        return BitConverter.ToInt32(buffer, 0);
    }

    private static int GetMapId(SigScanStatus status, long baseAddress)
    {
        long beatmapPtr = GetBeatmapPtr(status, baseAddress);
        if (beatmapPtr == 0)
            return -1;

        byte[] buffer = new byte[4];
        if (!ReadMemory(status, beatmapPtr + 0xC8, buffer, 4))
            return -1;

        return BitConverter.ToInt32(buffer, 0);
    }

    private static string? GetMapPath(SigScanStatus status, long baseAddress, out int length)
    {
        length = 0;
        long beatmapPtr = GetBeatmapPtr(status, baseAddress);
        if (beatmapPtr == 0)
        {
            Console.WriteLine("Invalid beatmap pointer");
            return null;
        }

        byte[] buffer = new byte[PtrSize];

        if (!ReadMemory(status, beatmapPtr + 0x78, buffer, PtrSize))
        {
            Console.WriteLine("Failure to read folder pointer");
            return null;
        }

        long folderPtr = BitConverter.ToInt32(buffer, 0);
        if (folderPtr <= 0)
        {
            Console.WriteLine("Invalid folder pointer");
            return null;
        }

        // Obter tamanho da pasta
        byte[] intBuffer = new byte[4];
        if (!ReadMemory(status, folderPtr + 4, intBuffer, 4))
        {
            Console.WriteLine("Falha ao ler tamanho da pasta");
            return null;
        }

        int folderSize = BitConverter.ToInt32(intBuffer, 0);
        if (folderSize <= 0 || folderSize > 1000) // Limite de segurança
        {
            Console.WriteLine($"Invalid folder size: {folderSize}");
            return null;
        }

        if (!ReadMemory(status, beatmapPtr + 0x90, buffer, PtrSize))
        {
            Console.WriteLine("Failure to read path pointer");
            return null;
        }

        long pathPtr = BitConverter.ToInt32(buffer, 0);
        if (pathPtr <= 0)
        {
            Console.WriteLine("Invalid path pointer");
            return null;
        }

        if (!ReadMemory(status, pathPtr + 4, intBuffer, 4))
        {
            Console.WriteLine("Failure to read path size");
            return null;
        }

        int pathSize = BitConverter.ToInt32(intBuffer, 0);
        if (pathSize <= 0 || pathSize > 1000) // Limite de segurança
        {
            Console.WriteLine($"Invalid path size: {pathSize}");
            return null;
        }

        byte[] folderBuffer = new byte[folderSize * 2];
        if (!ReadMemory(status, folderPtr + 8, folderBuffer, folderSize * 2))
        {
            Console.WriteLine("Failure to read folder data");
            return null;
        }

        byte[] pathBuffer = new byte[pathSize * 2];
        if (!ReadMemory(status, pathPtr + 8, pathBuffer, pathSize * 2))
        {
            Console.WriteLine("Failure to read path data");
            return null;
        }

        var folder = Encoding.Unicode.GetString(folderBuffer, 0, folderSize * 2);
        var path = Encoding.Unicode.GetString(pathBuffer, 0, pathSize * 2);

        var fullPath = $"{folder}/{path}".Replace('\\', '/');

        length = fullPath.Length;
        return fullPath;
    }
}