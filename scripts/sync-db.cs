// Keeps EF migrations in step with the model. Runs before every F5 launch (VS Code preLaunchTask "sync-db").
//  - No migrations yet  -> creates InitialCreate
//  - Model changed      -> adds a migration named Auto_<timestamp>
//  - Model unchanged    -> does nothing
// The app applies pending migrations on startup (after backing up the database), so the next launch is up to date.
// Same on every OS:  dotnet run --file scripts/sync-db.cs
using System.Diagnostics;

var root = RepoRoot();
const string Project = "src/GuitarPractice.Infrastructure";
string[] efArgs = ["-p", Project, "-s", Project, "--no-build"];

Run("dotnet", ["tool", "restore"], quiet: true);
Run("dotnet", ["build", Project, "-nologo", "-v", "q"]);

if (!Directory.Exists(Path.Combine(root, Project, "Persistence", "Migrations")))
{
    Console.WriteLine("No migrations found; creating InitialCreate.");
    return Run("dotnet", ["ef", "migrations", "add", "InitialCreate", .. efArgs, "-o", "Persistence/Migrations"]);
}

// Exit code 0 = no changes; non-zero = changes pending (or an error, told apart by the output).
var (pendingExit, output) = Capture("dotnet", ["ef", "migrations", "has-pending-model-changes", .. efArgs]);
if (pendingExit == 0)
{
    Console.WriteLine("Database model is up to date.");
    return 0;
}
if (!output.Contains("Changes have been made", StringComparison.Ordinal))
{
    Console.WriteLine(output);
    Console.Error.WriteLine("FAILED: could not check for model changes.");
    return 1;
}

var name = $"Auto_{DateTime.Now:yyyyMMdd_HHmmss}";
Console.WriteLine($"Model changed; adding migration {name}.");
return Run("dotnet", ["ef", "migrations", "add", name, .. efArgs, "-o", "Persistence/Migrations"]);

string RepoRoot() =>
    Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory(), ".."));

int Run(string file, string[] args, bool quiet = false)
{
    var (code, text) = Capture(file, args, echo: !quiet);
    if (code != 0)
    {
        if (quiet) Console.WriteLine(text);
        Console.Error.WriteLine($"FAILED: {file} {string.Join(' ', args)}");
        Environment.Exit(code);
    }
    return code;
}

(int Code, string Output) Capture(string file, string[] args, bool echo = false)
{
    var start = new ProcessStartInfo(file) { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in args) start.ArgumentList.Add(a);
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    var stdout = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    var text = stdout + stderr.Result;
    if (echo && text.Length > 0) Console.Write(text);
    return (process.ExitCode, text);
}
