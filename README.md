# OsuWineMemReader

OsuWineMemReader is a library for reading the memory of the osu! process running in a Wine environment. 

This library allows reading the path of the currently playing beatmap in osu!, for now. It was built to be used in a .NET application, more specifically [MapWizard](https://github.com/maotovisk/MapWizard), but it can be used in any .NET application.

It was also heavily inspired by hwsmm's `osumem` implementation, used in [cosutrainer](https://github.com/hwsmm/cosutrainer).

> **Note:** This library relies on the `process_vm_readv` function, which can not be available in all Linux distributions.
> Furthermore, the user running the application must be the same as the user running the osu! process in Wine to it work without issues. If you are using a different user, you will need to run the application with `sudo` or change the permissions of the osu! process.
## Requirements

- .NET 9.0
- Wine environment configured with osu! installed (check [osu-winello](https://github.com/NelloKudo/osu-winello), if you need help with that).
## Installation

To install the library, add a reference to the `OsuWineMemReader` project in your .NET project.

### NuGet Package
You can install the library via NuGet Package Manager Console:

```bash
dotnet add package OsuWineMemReader
```

## Usage

### Example Usage

```csharp
using OsuWineMemReader;

class Program
{
    static void Main()
    {
        bool running = true;
        string? result;
        var options = new OsuMemoryOptions
        {
            WriteToFile = true,
            FilePath = "/path/to/output/file",
            RunOnce = false // if set to true OsuMemory will run in a loop until stopped (running is set to false)
        };
        
        OsuMemory.StartBeatmapPathReading(ref running, out result, options);

        if (result != null)
        {
            Console.WriteLine($"Beatmap path: {result}");
        }
    }
}
```

### Options

- `WriteToFile`: Indicates whether the beatmap path should be written to a file.
- `FilePath`: Path to the file where the beatmap path will be written.
- `RunOnce`: Indicates whether the reading should be executed only once.

## Contribution

1. Fork the repository.
2. Create a branch for your feature (`git checkout -b feature/new-feature`).
3. Commit your changes (`git commit -am \`Add new feature\``).
4. Push to the branch (`git push origin feature/new-feature`).
5. Create a new Pull Request.

## License

This project is licensed under the terms of the MIT license. See the `LICENSE` file for more details.
