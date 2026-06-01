using Antlr4.Runtime;
using syntaxchecker.generated;
using MiniGoCompiler.ide;

class Program
{
    static void Main(string[] args)
    {
        string filePath = Path.Combine(AppContext.BaseDirectory, "test.txt");

        if (!File.Exists(filePath))
        {
            Console.WriteLine($"No se encontró el archivo: {filePath}");
            return;
        }

        string input = File.ReadAllText(filePath);

        AntlrInputStream inputStream = new AntlrInputStream(input);

        MiniGoCompilerLexer lexer = new MiniGoCompilerLexer(inputStream);
        CommonTokenStream tokenStream = new CommonTokenStream(lexer);

        MiniGoCompilerParser parser = new MiniGoCompilerParser(tokenStream);

        // Regla inicial de tu gramática
        parser.root();

        if (parser.NumberOfSyntaxErrors == 0)
        {
            Console.WriteLine("Archivo válido sintácticamente.");
        }
        else
        {
            Console.WriteLine($"Archivo inválido. Errores encontrados: {parser.NumberOfSyntaxErrors}");
        }
        var server = new CompilerServer();
        server.Start();
        Console.WriteLine("Presionar Enter para salir...");
        Console.ReadLine();


    }
}