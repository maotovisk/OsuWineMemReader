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
    public static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        
        var running = true;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            running = false;
            Console.WriteLine("Exiting...");
        };
        OsuMemory.Run(ref running);
    }

    
}