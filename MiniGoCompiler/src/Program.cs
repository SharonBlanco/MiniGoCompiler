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

        Console.WriteLine("Servidor corriendo. Presiona Ctrl+C para salir...");
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
                Console.WriteLine($"Abre manualmente: {url}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"No se pudo abrir el navegador automáticamente: {ex.Message}");
            Console.WriteLine($"Abre manualmente: {url}");
        }
    }
}