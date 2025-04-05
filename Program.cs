using System;
using System.Text;

namespace OsuWineMemReader;

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