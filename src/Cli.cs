using System;
using System.Linq;

namespace XboxStartupEnabler;

// Console-side dispatcher. Called from App.OnStartup when args are passed, so power users keep the `status / apply / restore / verify` workflow.
internal static class Cli
{
    public static int Run(string[] args)
    {
        string verb = args[0].ToLowerInvariant();
        string? targetDir = null;
        bool autoYes = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--dir" && i + 1 < args.Length) targetDir = args[++i];
            else if (args[i] is "--yes" or "-y") autoYes = true;
        }

        try
        {
            switch (verb)
            {
                case "status":
                case "verify":
                    return Status(targetDir, verbose: verb == "verify");
                case "apply":
                    return Apply(targetDir, autoYes);
                case "restore":
                    return Restore(targetDir);
                default:
                    PrintUsage();
                    return 0;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Status(string? targetDir, bool verbose)
    {
        var summary = Patcher.Inspect(targetDir);
        foreach (var t in summary.Targets)
        {
            Console.WriteLine($"--- {t.FileName} ---");
            Console.WriteLine($"  path: {t.FilePath}");
            if (!t.Exists) { Console.WriteLine("  MISSING"); continue; }
            Console.WriteLine($"  version: {t.Version}   size: {t.Size}");
            foreach (var s in t.Sites)
                Console.WriteLine($"    [{s.State,-8}] {s.Description}  @ 0x{s.FileOffset:X}");
        }
        Console.WriteLine($"\nSummary: original={summary.Original} patched={summary.Patched} unknown={summary.Unknown}");
        return 0;
    }

    private static int Apply(string? targetDir, bool autoYes)
    {
        bool isSystem32 = targetDir is null;
        if (isSystem32 && !Patcher.IsElevated())
        {
            Console.Error.WriteLine("ERROR: 'apply' against System32 requires Administrator.");
            return 2;
        }
        if (isSystem32 && !autoYes)
        {
            Console.Write("Proceed and modify System32 files? (y/N) ");
            string? line = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(line) || (line[0] != 'y' && line[0] != 'Y'))
            {
                Console.WriteLine("Aborted.");
                return 3;
            }
        }
        Patcher.Apply(targetDir, Console.WriteLine);
        Console.WriteLine();
        Console.WriteLine("Done. Reboot or restart Settings + Xbox app, then open Settings → Gaming.");
        return 0;
    }

    private static int Restore(string? targetDir)
    {
        if (targetDir is null && !Patcher.IsElevated())
        {
            Console.Error.WriteLine("ERROR: 'restore' against System32 requires Administrator.");
            return 2;
        }
        Patcher.Restore(targetDir, Console.WriteLine);
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("""
            XboxStartupEnabler — Windows 11 "Choose your home app" / Xbox FSE enabler.
            Usage:
              XboxStartupEnabler.exe                       (launches GUI)
              XboxStartupEnabler.exe status                Show current state of patch sites.
              XboxStartupEnabler.exe verify                Same plus expected/actual bytes.
              XboxStartupEnabler.exe apply [--yes]         Patch the system DLLs.
              XboxStartupEnabler.exe restore               Restore from backup.
              ...        any verb above accepts --dir <path> to operate on copies.
            """);
    }
}
