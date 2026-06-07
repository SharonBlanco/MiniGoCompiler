using System.Diagnostics;
using MiniGoCompiler.ide;

class Program
{
    private const string IdeUrl = "http://localhost:5050/";

    static void Main(string[] args)
    {
        var server = new CompilerServer();
        server.Start();

        OpenIdeInBrowser(IdeUrl);

        var exitEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            exitEvent.Set();
        };

        Console.WriteLine("Server running. Press Ctrl+C to exit...");
        exitEvent.Wait();
    }

    private static void OpenIdeInBrowser(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo
                    
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("open", url);
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start("xdg-open", url);
            }
            else
            {
                Console.WriteLine($"Open manually: {url}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not open the browser automatically: {ex.Message}");
            Console.WriteLine($"Open manually: {url}");
        }
    }
}