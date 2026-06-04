using System.Net;
using System.Text;
using System.Text.Json;
using Antlr4.Runtime;
using syntaxchecker.generated;
using MiniGoCompiler.typechecker;
using MiniGoCompiler.errors;
using MiniGoCompiler.encoder;

namespace MiniGoCompiler.ide;

// =============================================================================
//  CompilerServer
// -----------------------------------------------------------------------------
//  Lightweight HTTP server that hosts the Monaco-based MiniGo IDE and exposes
//  a /compile endpoint for on-demand lexical, syntactic, and semantic analysis.
//
//  Architecture:
//    Browser (index.html + Monaco Editor)
//        |
//        |  POST /compile  { "code": "..." }
//        v
//    CompilerServer (this class)
//        |
//        ├── ANTLR4 Lexer  → lexical errors
//        ├── ANTLR4 Parser → syntax errors
//        └── MiniGoTypeChecker → semantic/type errors
//        |
//        v
//    JSON response  { "success": bool, "errors": [...] }
//
//  Usage:
//    var server = new CompilerServer();
//    server.Start();
//    // Open http://localhost:5050 in a browser
// =============================================================================

/// <summary>
/// Simple HTTP server that serves the MiniGo IDE frontend and handles
/// compilation requests. Listens on <c>http://localhost:5050</c>.
/// </summary>
public class CompilerServer
{
    /// <summary>Underlying .NET HTTP listener bound to the configured port.</summary>
    private readonly HttpListener _listener;

    /// <summary>Resolved filesystem path to the <c>index.html</c> IDE page.</summary>
    private readonly string _htmlPath;

    /// <summary>TCP port the server listens on.</summary>
    private const int PORT = 5050;

    /// <summary>
    /// Initializes the HTTP listener and resolves the path to the IDE's
    /// <c>index.html</c> file. Tries the path relative to the build output
    /// directory first, then falls back to the project root.
    /// </summary>
    public CompilerServer()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{PORT}/");

        // Attempt to locate index.html relative to the build output directory
        _htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "ide_gui", "index.html");
        if (!File.Exists(_htmlPath))
        {
            // Fallback: try relative to the working directory (project root)
            _htmlPath =
                "/home/sharonblancopiedra/Documents/VII SEMESTRE/COMPILADORES/SEMANA 12/MiniGoCompiler/MiniGoCompiler/src/ide_gui/index.html";
        }
    }

    /// <summary>
    /// Starts the HTTP listener and begins accepting requests asynchronously.
    /// Prints a startup banner with the URL to open in a browser.
    /// </summary>
    public void Start()
    {
        _listener.Start();
        Console.WriteLine($"╔══════════════════════════════════════════╗");
        Console.WriteLine($"║   MiniGo Compiler IDE                    ║");
        Console.WriteLine($"║   Open: http://localhost:{PORT}            ║");
        Console.WriteLine($"║   Press Ctrl+C to stop                    ║");
        Console.WriteLine($"╚══════════════════════════════════════════╝");

        Task.Run(() => Listen());
    }

    /// <summary>
    /// Main accept loop. Waits for incoming HTTP requests and dispatches
    /// each one to <see cref="HandleRequest"/> on a background thread so
    /// that multiple requests can be served concurrently.
    /// </summary>
    private async Task Listen()
    {
        while (_listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Routes an incoming HTTP request to the appropriate handler based on
    /// the URL path:
    /// <list type="bullet">
    ///   <item><c>/</c> — serves the IDE HTML page.</item>
    ///   <item><c>/compile</c> (POST) — compiles submitted MiniGo code.</item>
    ///   <item>Anything else — returns 404.</item>
    /// </list>
    /// </summary>
    /// <param name="context">The HTTP listener context for the current request.</param>
    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            switch (request.Url?.AbsolutePath)
            {
                case "/":
                    ServeHtml(response);
                    break;
                case "/compile":
                    if (request.HttpMethod == "POST")
                        HandleCompile(request, response);
                    else
                        SendResponse(response, 405, "Method Not Allowed");
                    break;
                default:
                    SendResponse(response, 404, "Not Found");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the IDE's <c>index.html</c> file from disk and writes it to the
    /// HTTP response. Returns a 500 error if the file cannot be found.
    /// </summary>
    /// <param name="response">The HTTP response to write the HTML content to.</param>
    private void ServeHtml(HttpListenerResponse response)
    {
        if (File.Exists(_htmlPath))
        {
            byte[] buffer = File.ReadAllBytes(_htmlPath);
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
        }
        else
        {
            SendResponse(response, 500, $"File not found: {_htmlPath}");
        }

        response.Close();
    }

    /// <summary>
    /// Handles a <c>POST /compile</c> request. Reads the JSON body containing
    /// the MiniGo source code, runs the full compilation pipeline (lexer →
    /// parser → type checker), and returns a JSON response with any errors
    /// found, including their line and column positions for Monaco to display.
    /// </summary>
    /// <param name="request">The incoming HTTP request containing <c>{"code": "..."}</c>.</param>
    /// <param name="response">The HTTP response where the compilation result is written.</param>
    private void HandleCompile(HttpListenerRequest request, HttpListenerResponse response)
    {
        // Read the request body as a UTF-8 string
        string body;
        using (var reader = new System.IO.StreamReader(request.InputStream, Encoding.UTF8))
        {
            body = reader.ReadToEnd();
        }

        // Deserialize the JSON payload and extract the "code" field
        var json = JsonSerializer.Deserialize<JsonElement>(body);
        string code = json.GetProperty("code").GetString() ?? "";

        // Run the compilation pipeline
        var result = CompileCode(code);

        // Serialize and send the result back as JSON
        string jsonResponse = JsonSerializer.Serialize(result);
        byte[] buffer = Encoding.UTF8.GetBytes(jsonResponse);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }

    /// <summary>
    /// Executes the full MiniGo compilation pipeline on the given source code:
    /// <list type="number">
    ///   <item><b>Phase 1 — Lexical &amp; Syntactic Analysis:</b> Creates an ANTLR
    ///         input stream, runs the lexer and parser with custom error listeners
    ///         to capture syntax errors with line/column information.</item>
    ///   <item><b>Phase 2 — Semantic/Type Analysis:</b> If no syntax errors were
    ///         found, instantiates <see cref="MiniGoTypeChecker"/>, visits the
    ///         parse tree, and collects any type errors.</item>
    /// </list>
    /// </summary>
    /// <param name="code">The MiniGo source code string to compile.</param>
    /// <returns>
    /// A <see cref="CompileResult"/> containing a success flag and a list of
    /// <see cref="CompileError"/> objects with messages, line numbers, and columns.
    /// </returns>
    private CompileResult CompileCode(string code)
    {
        var result = new CompileResult();
        var errors = new List<CompileError>();

        try
        {
            // PHASE 1: Lexical and syntactic analysis
            AntlrInputStream input = new AntlrInputStream(code);
            MiniGoCompilerLexer lexer = new MiniGoCompilerLexer(input);
            CommonTokenStream tokens = new CommonTokenStream(lexer);
            MiniGoCompilerParser parser = new MiniGoCompilerParser(tokens);

            // Replace default error listeners with our custom collector
            var errorListener = new MiniGoErrorListener();
            lexer.RemoveErrorListeners();
            parser.RemoveErrorListeners();
            lexer.AddErrorListener(errorListener);
            parser.AddErrorListener(errorListener);

            // Parse the source code into an AST rooted at the 'root' rule
            var tree = parser.root();

            // Collect any syntax errors detected during parsing
            foreach (var err in errorListener.Errors)
            {
                errors.Add(err);
            }

            // PHASE 2: Type checking (only if no syntax errors were found)
            if (!errorListener.HasErrors)
            {
                MiniGoTypeChecker typeChecker = new MiniGoTypeChecker();
                typeChecker.Visit(tree);

                // Convert type checker error strings into structured CompileError objects
                foreach (string errorMsg in typeChecker.errorList)
                {
                    var parsed = ParseTypeError(errorMsg);
                    errors.Add(parsed);
                }
            }

            // PHASE 3: Code generation (only if no syntax/type errors)
            if (errors.Count == 0)
            {
                try
                {
                    var encoder = new MiniGoEncoder();
                    encoder.Visit(tree);

                    result.ir = encoder.GeneratedIR;
                    result.output = encoder.ProgramOutput;

                    if (!encoder.CompilationSuccess && !string.IsNullOrEmpty(encoder.ErrorMessage))
                    {
                        errors.Add(new CompileError
                        {
                            message = "CODE GEN ERROR: " + encoder.ErrorMessage,
                            line = 1,
                            column = 1,
                            length = 1
                        });
                    }
                }
                catch (Exception codeGenEx)
                {
                    Console.WriteLine($"Code generation failed: {codeGenEx}");
                    result.ir = "";
                    result.output = "";
                }
            }
        }

        catch (Exception ex)
        {
            // Catch-all for unexpected internal compiler errors
            errors.Add(new CompileError
            {
                message = $"Internal compiler error: {ex.Message}\n{ex.StackTrace}",
                line = 1,
                column = 1
            });
        }

        result.success = errors.Count == 0;
        result.errors = errors;
        return result;
    }

    /// <summary>
    /// Parses a type checker error message string to extract the line number
    /// and column position. The expected format is:
    /// <c>"TYPE ERROR: msg: (token) in [line X: Column Y]"</c>
    /// If parsing fails, defaults to line 1, column 1.
    /// </summary>
    /// <param name="errorMsg">The raw error message string from the type checker.</param>
    /// <returns>A structured <see cref="CompileError"/> with extracted position data.</returns>
    private CompileError ParseTypeError(string errorMsg)
    {
        var error = new CompileError { message = errorMsg, line = 1, column = 1 };

        try
        {
            // Extract line number from "line X" substring
            int lineIdx = errorMsg.IndexOf("line ");
            if (lineIdx >= 0)
            {
                string afterLine = errorMsg.Substring(lineIdx + 5);
                string lineNum = "";
                foreach (char c in afterLine)
                {
                    if (char.IsDigit(c)) lineNum += c;
                    else break;
                }

                if (int.TryParse(lineNum, out int ln)) error.line = ln;
            }

            // Extract column number from "Column Y" substring
            int colIdx = errorMsg.IndexOf("Column ");
            if (colIdx >= 0)
            {
                string afterCol = errorMsg.Substring(colIdx + 7);
                string colNum = "";
                foreach (char c in afterCol)
                {
                    if (char.IsDigit(c)) colNum += c;
                    else break;
                }

                if (int.TryParse(colNum, out int col)) error.column = col;
            }
        }
        catch
        {
            /* If parsing fails, fall back to default line 1, column 1 */
        }

        return error;
    }
    
     
    public class CompileResult
    {
        /// <summary>True si no hubo errores en ninguna fase.</summary>
        public bool success { get; set; }
 
        /// <summary>Lista de errores de sintaxis o tipos.</summary>
        public List<CompileError> errors { get; set; } = new List<CompileError>();
 
        // ---- NUEVOS: resultados del encoder ----
 
        /// <summary>Salida estándar del programa compilado (lo que imprime con println).</summary>
        public string output { get; set; } = "";
 
        /// <summary>El LLVM IR generado (para que el IDE lo pueda mostrar).</summary>
        public string ir { get; set; } = "";
    }


    /// <summary>
    /// Sends a plain-text HTTP response with the given status code and message body.
    /// Used for error responses (404, 405, 500).
    /// </summary>
    /// <param name="response">The HTTP response object.</param>
    /// <param name="statusCode">The HTTP status code to return.</param>
    /// <param name="message">The plain-text message body.</param>
    private void SendResponse(HttpListenerResponse response, int statusCode, string message)
    {
        response.StatusCode = statusCode;
        byte[] buffer = Encoding.UTF8.GetBytes(message);
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.Close();
    }
}