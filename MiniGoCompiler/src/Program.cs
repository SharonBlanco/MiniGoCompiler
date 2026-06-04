using Antlr4.Runtime;
using syntaxchecker.generated;
using MiniGoCompiler.typechecker;
using MiniGoCompiler.ide;

class Program
{
    static void Main(string[] args)
    {
        var server = new CompilerServer();
        server.Start();

        string filePath = "/home/sharonblancopiedra/Documents/VII SEMESTRE/COMPILADORES/SEMANA 12/MiniGoCompiler/MiniGoCompiler/Tests/test_crash.txt";

        if (File.Exists(filePath))
        {
            
            try
            {
                string input = File.ReadAllText(filePath);

                AntlrInputStream inputStream = new AntlrInputStream(input);
                MiniGoCompilerLexer lexer = new MiniGoCompilerLexer(inputStream);
                CommonTokenStream tokenStream = new CommonTokenStream(lexer);
                MiniGoCompilerParser parser = new MiniGoCompilerParser(tokenStream);

                var tree = parser.root();

                if (parser.NumberOfSyntaxErrors > 0)
                {
                    Console.WriteLine($"Errores de sintaxis: {parser.NumberOfSyntaxErrors}");
                }
                else
                {
                    MiniGoTypeChecker typeChecker = new MiniGoTypeChecker();
                    typeChecker.Visit(tree);
                    typeChecker.printErrors();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Compilation failed: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine($"Archivo de prueba no encontrado: {filePath}");
        }

        // Mantener el proceso vivo para que el servidor HTTP siga corriendo
        var exitEvent = new ManualResetEventSlim(false);
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            exitEvent.Set();
        };
        Console.WriteLine("Servidor corriendo. Presiona Ctrl+C para salir...");
        exitEvent.Wait();
    }
}
