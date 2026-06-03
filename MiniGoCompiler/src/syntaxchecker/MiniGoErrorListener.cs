using System.Collections.Generic;
using System.IO;
using Antlr4.Runtime;
using syntaxchecker.generated;

namespace MiniGoCompiler.errors
{
    // =========================================================================
    //  MiniGoErrorListener
    // -------------------------------------------------------------------------
    //  Custom ANTLR error listener that captures lexer and parser errors into
    //  a structured list instead of printing them to stderr. Each error includes
    //  the message, source line, column, and token length for precise IDE markers.
    // =========================================================================

    /// <summary>
    /// ANTLR error listener that collects syntax and lexer errors into a
    /// <see cref="CompileError"/> list for JSON serialization to the IDE frontend.
    /// Implements both the token-based (parser) and integer-based (lexer) error
    /// listener interfaces.
    /// </summary>
    public class MiniGoErrorListener : IAntlrErrorListener<IToken>, IAntlrErrorListener<int>
    {
        /// <summary>All errors collected during the parsing phase.</summary>
        public List<CompileError> Errors { get; } = new List<CompileError>();

        /// <summary>Whether any errors were recorded.</summary>
        public bool HasErrors => Errors.Count > 0;

        /// <summary>
        /// Called by the ANTLR parser when a syntax error is detected. Captures
        /// the error message, line, column (1-based), and the length of the
        /// offending token for precise error highlighting in the editor.
        /// </summary>
        public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol,
            int line, int charPositionInLine, string msg, RecognitionException e)
        {
            Errors.Add(new CompileError
            {
                message = $"SYNTAX ERROR: {msg}",
                line = line,
                column = charPositionInLine + 1,
                length = offendingSymbol?.Text?.Length ?? 1
            });
        }

        /// <summary>
        /// Called by the ANTLR lexer when an unrecognized character or malformed
        /// token is encountered. Captures the error with its source position.
        /// </summary>
        public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol,
            int line, int charPositionInLine, string msg, RecognitionException e)
        {
            Errors.Add(new CompileError
            {
                message = $"LEXER ERROR: {msg}",
                line = line,
                column = charPositionInLine + 1
            });
        }
    }

    // =========================================================================
    //  Data Transfer Objects (DTOs)
    // -------------------------------------------------------------------------
    //  Simple POCOs serialized to/from JSON for communication between the
    //  CompilerServer backend and the Monaco IDE frontend.
    // =========================================================================

    /// <summary>
    /// Top-level compilation result sent back to the IDE as a JSON response.
    /// Contains a success flag and the full list of errors (empty on success).
    /// </summary>
    public class CompileResult
    {
        /// <summary>Whether the compilation completed without any errors.</summary>
        public bool success { get; set; }

        /// <summary>List of all errors found during compilation (syntax + semantic).</summary>
        public List<CompileError> errors { get; set; } = new List<CompileError>();
    }

    /// <summary>
    /// Represents a single compilation error with its source location. Used by
    /// the Monaco editor to display inline error markers with red squiggly
    /// underlines at the exact position in the source code.
    /// </summary>
    public class CompileError
    {
        /// <summary>Human-readable error description shown in the error panel.</summary>
        public string message { get; set; } = "";

        /// <summary>1-based line number where the error was detected.</summary>
        public int line { get; set; } = 1;

        /// <summary>1-based column number where the error was detected.</summary>
        public int column { get; set; } = 1;

        /// <summary>Length of the offending token in characters (for underline width).</summary>
        public int length { get; set; } = 1;
    }
}
