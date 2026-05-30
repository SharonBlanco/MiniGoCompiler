
using Antlr4.Runtime;
using Microsoft.CSharp.RuntimeBinder;

namespace MiniGoCompiler.typechecker;


public class TypeErrorException : RuntimeBinderException
{
    private readonly List<String> errorList = new List<String>();
    public bool HasErrors => errorList.Count > 0;

    public void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line,
        int charPositionInLine, string msg, RecognitionException e)
    {
        string type;
        
        if (recognizer is MiniGoTypeChecker)
        {
            type = "TYPE ERROR" + " in [line: " + "- column:" + charPositionInLine + "]  expected: " +
                   offendingSymbol;
        }
        else
        {
            type = "UNKNOWN ERROR";
        }
        errorList.Add($"{type}: {msg} [line{line}- column:{charPositionInLine}]");
    }
    
    
}