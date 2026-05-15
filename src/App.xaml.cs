using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

namespace XboxStartupEnabler;

public partial class App : Application
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")] private static extern bool AllocConsole();
    [DllImport("kernel32.dll")] private static extern bool FreeConsole();
    private const int ATTACH_PARENT_PROCESS = -1;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0)
        {
            // CLI mode: attach to the parent console (cmd / pwsh) and re-bind Console.{Out,Error,In} so writes actually surface.
            // Without the re-bind, the WinExe process has Console initialized to no-op streams.
            if (AttachConsole(ATTACH_PARENT_PROCESS) || AllocConsole())
            {
                var stdout = new StreamWriter(Console.OpenStandardOutput(), Encoding.UTF8) { AutoFlush = true };
                var stderr = new StreamWriter(Console.OpenStandardError(), Encoding.UTF8) { AutoFlush = true };
                Console.SetOut(stdout);
                Console.SetError(stderr);
                Console.SetIn(new StreamReader(Console.OpenStandardInput(), Encoding.UTF8));
            }

            int code = Cli.Run(e.Args);
            Console.Out.Flush();
            Console.Error.Flush();
            FreeConsole();
            Shutdown(code);
            return;
        }

        new MainWindow().Show();
    }
}
