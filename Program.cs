using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Buffers;

namespace OsuMemReader;

public static class Program
{
    private const string OsuProcessName = "osu!.exe";
    private const int PtrSize = 4;
    private static readonly byte[] OsuBaseSig = [0xf8, 0x01, 0x74, 0x04, 0x83, 0x65];
    private const int OsuBaseSize = 6;
    private const int ScanChunkSize = 16 * 1024; // 16KB chunks

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
            Console.WriteLine("Exiting...");
        };

        while (running)
        {
            FindOsuProcess(status);
            GC.Collect();
            GC.WaitForPendingFinalizers();

            if (status is { Status: >= 0, OsuPid: > 0 })
            {
                if (baseAddress == 0)
                {
                    Console.WriteLine("Starting memory scanning...");
                    if (FindPattern(status, OsuBaseSig, OsuBaseSize, null, out baseAddress))
                    {
                        Console.WriteLine($"Base found: 0x{baseAddress:X16}");
                    }
                    else
                    {
                        Console.WriteLine("Scan failed, retrying...");
                        Thread.Sleep(3000);
                        continue;
                    }    
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                string? songPath = GetMapPath(status, baseAddress, out var length);
                if (songPath != null && songPath != oldPath)
                {
                    Console.WriteLine($"New beatmap: {songPath}");
                    try { File.WriteAllText("/tmp/osu_path", $"0 {songPath}"); }
                    catch (Exception ex) { Console.WriteLine($"File error: {ex.Message}"); }
                    oldPath = songPath;
                }
                else if (songPath == null)
                {
                    baseAddress = 0;
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            else
            {
                Console.WriteLine("Waiting for osu!...");
                baseAddress = 0;
            }
            Thread.Sleep(300);
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
                    if (File.Exists(commPath) && File.ReadAllText(commPath).Trim() == OsuProcessName)
                    {
                        status.OsuPid = pid;
                        status.Status = 2;
                        Console.WriteLine($"Found PID: {pid}");
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        return;
                    }
                }
                catch { /* Ignore errors */ }
            }
        }    
        status.Status = -1;
    }

    static bool IsProcessAlive(int pid) => kill(pid, 0) == 0;

    static bool ReadMemory(SigScanStatus status, long address, byte[] buffer, int length)
    {
        if (address == 0) return false;

        IntPtr localPtr = Marshal.AllocHGlobal(length);
        try
        {
            var localIov = new IoVector { iov_base = localPtr, iov_len = new IntPtr(length) };
            var remoteIov = new IoVector { iov_base = new IntPtr(address), iov_len = new IntPtr(length) };

            long result = process_vm_readv(status.OsuPid, [localIov], 1, [remoteIov], 1, 0);
            if (result == -1)
            {
                Console.WriteLine($"Read error: {Marshal.GetLastWin32Error()}");
                return false;
            }

            Marshal.Copy(localPtr, buffer, 0, length);
            return true;
        }
        finally { Marshal.FreeHGlobal(localPtr); }
    }

    static IEnumerable<VmRegion> EnumerateMemoryRegions(SigScanStatus status)
    {
        foreach (string line in File.ReadAllLines($"/proc/{status.OsuPid}/maps"))
        {
            string[] parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[1].Contains("r")) continue;

            string[] addresses = parts[0].Split('-');
            if (addresses.Length != 2) continue;

            yield return new VmRegion
            {
                Start = Convert.ToInt64(addresses[0], 16),
                Length = Convert.ToInt64(addresses[1], 16) - Convert.ToInt64(addresses[0], 16)
            };
        }
    }

    static bool FindPattern(SigScanStatus status, byte[] pattern, int patternSize, bool[]? mask, out long result)
    {
        result = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(ScanChunkSize + patternSize - 1);

        try
        {
            foreach (var region in EnumerateMemoryRegions(status))
            {
                for (long offset = 0; offset < region.Length; offset += ScanChunkSize)
                {
                    long readAddr = region.Start + offset;
                    int readSize = (int)Math.Min(ScanChunkSize + patternSize - 1, region.Length - offset);

                    if (!ReadMemory(status, readAddr, buffer, readSize)) continue;

                    for (int i = 0; i <= readSize - patternSize; i++)
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
                            result = readAddr + i;
                            return true;
                        }
                    }
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return false;
    }

    static long GetBeatmapPtr(SigScanStatus status, long baseAddress)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PtrSize);
        try
        {
            if (!ReadMemory(status, baseAddress - 0xC, buffer, PtrSize))
                return 0;

            long beatmapPtr = BitConverter.ToInt32(buffer, 0);
            return ReadMemory(status, beatmapPtr, buffer, PtrSize) ? BitConverter.ToInt32(buffer, 0) : 0;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static string? GetMapPath(SigScanStatus status, long baseAddress, out int length)
    {
        length = 0;
        long beatmapPtr = GetBeatmapPtr(status, baseAddress);
        if (beatmapPtr == 0) return null;

        byte[] buffer = ArrayPool<byte>.Shared.Rent(PtrSize);
        try
        {
            if (!ReadMemory(status, beatmapPtr + 0x78, buffer, PtrSize)) return null;
            long folderPtr = BitConverter.ToInt32(buffer, 0);
        
            if (!ReadMemory(status, beatmapPtr + 0x90, buffer, PtrSize)) return null;
            long pathPtr = BitConverter.ToInt32(buffer, 0);

            if (!ReadMemory(status, folderPtr + 4, buffer, 4)) return null;
            int folderSize = BitConverter.ToInt32(buffer, 0);
        
            if (!ReadMemory(status, pathPtr + 4, buffer, 4)) return null;
            int pathSize = BitConverter.ToInt32(buffer, 0);

            if (folderSize > 256 || pathSize > 256) return null;

            byte[] stringBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(folderSize, pathSize) * 2);
            try
            {
                if (!ReadMemory(status, folderPtr + 8, stringBuffer, folderSize * 2)) return null;
                string folder = Encoding.Unicode.GetString(stringBuffer, 0, folderSize * 2);

                if (!ReadMemory(status, pathPtr + 8, stringBuffer, pathSize * 2)) return null;
                string path = Encoding.Unicode.GetString(stringBuffer, 0, pathSize * 2);

                var fullPath = $"{folder}/{path}".Replace('\\', '/');
                length = fullPath.Length;
                return fullPath;
            }
            finally { ArrayPool<byte>.Shared.Return(stringBuffer); }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        
    }
}