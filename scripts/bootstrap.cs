// First-time setup: restore tools, create the initial EF migration (only if there are none), build, test.
// Same on every OS:  dotnet run --file scripts/bootstrap.cs [-- --reset]
// --reset also deletes YOUR LOCAL DATABASE (practice.db and its -wal/-shm/.bak files in the GuitarPractice data folder);
// the app creates a fresh one on its next start. Migrations are never deleted: released versions depend on them.
using System.Diagnostics;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory(), ".."));

if (args.Contains("--reset"))
{
    // The app's data folder (AppPaths.DataDirectory): LocalApplicationData/GuitarPractice on every OS.
    var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GuitarPractice");
    Console.WriteLine($"\n== Reset: deleting the local database in {data} ==");
    foreach (var file in Directory.Exists(data) ? Directory.GetFiles(data, "practice.db*") : [])
    {
        File.Delete(file);
        Console.WriteLine($"   deleted {Path.GetFileName(file)}");
    }
}

Step("SDK check", "--version");
Step("Restore local tools (dotnet-ef, vpk)", "tool", "restore");
if (!Directory.Exists(Path.Combine(root, "src/GuitarPractice.Infrastructure/Persistence/Migrations")))
    Step("Create InitialCreate migration", "ef", "migrations", "add", "InitialCreate",
        "-p", "src/GuitarPractice.Infrastructure", "-s", "src/GuitarPractice.Infrastructure", "-o", "Persistence/Migrations");
Step("Build", "build", "GuitarPractice.slnx", "-c", "Debug");
Step("Test", "test", "GuitarPractice.slnx", "-c", "Debug", "--no-build");
Console.WriteLine("\nDone. Run the app with:  dotnet run --project src/GuitarPractice.App");

void Step(string title, params string[] dotnetArgs)
{
    Console.WriteLine($"\n== {title} ==");
    var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root };
    foreach (var a in dotnetArgs) start.ArgumentList.Add(a);
    using var process = Process.Start(start)!;
    process.WaitForExit();
    if (process.ExitCode != 0)
    {
        Console.Error.WriteLine($"FAILED: {title}");
        Environment.Exit(process.ExitCode);
    }
}
