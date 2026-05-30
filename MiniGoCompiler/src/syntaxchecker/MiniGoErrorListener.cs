using System.Collections.Generic;
using Antlr4.Runtime;
using syntaxchecker.generated;

namespace SyntaxChecker
{
    public class AlphaErrorListener : BaseErrorListener
    {
        private readonly List<string> errorList = new List<string>();

        public bool HasErrors => errorList.Count > 0;


        public override void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
        {
            string type;

            if (recognizer is MiniGoCompilerLexer lexer)
            {
                type = "LEXER ERROR" + " in [line: " + "- column:" + charPositionInLine + "]  expected: " +
                       offendingSymbol;
            }
            else if (recognizer is MiniGoCompilerParser parser)
            {
                type = "PARSER ERROR" + " in [line: " + "- column:" + charPositionInLine + "]  expected: " +
                       offendingSymbol;
            }
            else
            {
                type = "UNKNOWN ERROR";
            }

            errorList.Add($"{type}: {msg} [line:{line}- column:{charPositionInLine}]");
        }
    }
}