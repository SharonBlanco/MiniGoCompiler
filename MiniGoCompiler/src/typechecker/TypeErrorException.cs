using Antlr4.Runtime;
using Microsoft.CSharp.RuntimeBinder;

namespace MiniGoCompiler.typechecker;

/// <summary>
/// Excepción personalizada para errores de tipo detectados durante el análisis semántico del compilador MiniGo.
/// Hereda de <see cref="RuntimeBinderException"/> y acumula múltiples errores de tipo
/// en una lista interna, permitiendo reportarlos todos al finalizar la verificación.
/// </summary>
public class TypeErrorException : RuntimeBinderException
{
    /// <summary>Lista interna que almacena los mensajes de error de tipo encontrados.</summary>
    private readonly List<String> errorList = new List<String>();

    /// <summary>
    /// Indica si se han registrado errores de tipo durante el análisis.
    /// </summary>
    /// <value><c>true</c> si hay al menos un error registrado; <c>false</c> en caso contrario.</value>
    public bool HasErrors => errorList.Count > 0;

    /// <summary>
    /// Registra un error de tipo encontrado durante la fase de verificación semántica.
    /// Formatea el mensaje según el tipo de reconocedor que lo reporta:
    /// si proviene de <see cref="MiniGoTypeChecker"/>, se clasifica como "TYPE ERROR";
    /// de lo contrario, se clasifica como "UNKNOWN ERROR".
    /// </summary>
    /// <param name="output">Writer de salida para el reporte de errores.</param>
    /// <param name="recognizer">Reconocedor que generó el error (se verifica si es MiniGoTypeChecker).</param>
    /// <param name="offendingSymbol">Token que causó el error de tipo.</param>
    /// <param name="line">Número de línea donde ocurrió el error.</param>
    /// <param name="charPositionInLine">Posición del carácter dentro de la línea.</param>
    /// <param name="msg">Mensaje descriptivo del error.</param>
    /// <param name="e">Excepción de reconocimiento original de ANTLR, si aplica.</param>
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