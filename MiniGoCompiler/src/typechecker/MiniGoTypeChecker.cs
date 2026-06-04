using System.ComponentModel.Design;
using System.Net.Mail;
using Antlr4.Runtime;
using syntaxchecker.generated;

namespace MiniGoCompiler.typechecker;

// =============================================================================
//  MiniGoTypeChecker
// -----------------------------------------------------------------------------
//  Semantic analyzer for the MiniGo language. Implemented as an ANTLR visitor
//  that traverses the parse tree produced by MiniGoCompilerParser and performs:
//
//    * Type inference and type compatibility verification for expressions,
//      assignments, declarations, function calls, and control-flow predicates.
//    * Scope management through a SymbolsTable, opening and closing scopes
//      around blocks, functions, loops, conditionals, and switches.
//    * Detection and reporting of semantic errors (redeclarations, undefined
//      identifiers, invalid operand types, mismatched return types, etc.).
//    * Validation that every execution path within a typed function reaches a
//      return statement (see GuaranteesReturn / IfGuaranteesReturn /
//      SwitchGuaranteesReturn).
//
//  Each Visit* override returns either a SymbolsTable.TypeInfo describing the
//  resulting type of an expression, a collection of TypeInfo for expression
//  lists, a Dictionary<string, TypeInfo> for struct field descriptors, or null
//  when no type information is meaningful (statements and declarations).
// =============================================================================

/// <summary>
/// Visitor-based semantic/type checker for the MiniGo language.
/// Walks the parse tree, manages lexical scopes through <see cref="SymbolsTable"/>,
/// verifies type compatibility, and accumulates a list of semantic errors.
/// </summary>
public class MiniGoTypeChecker : MiniGoCompilerBaseVisitor<object>

{
    /// <summary>Symbol table used for identifier resolution and scope management.</summary>
    public SymbolsTable symbolsTable;

    /// <summary>Accumulated semantic errors detected during the tree traversal.</summary>
    public LinkedList<String> errorList;

    /// <summary>
    /// Stack of expected return types, pushed on function entry and popped on exit.
    /// Enables nested function-context-aware validation of <c>return</c> statements.
    /// </summary>
    private Stack<SymbolsTable.TypeInfo> returnTypeStack = new Stack<SymbolsTable.TypeInfo>();


    /// <summary>
    /// Initializes a fresh type checker with an empty symbol table and error list.
    /// </summary>
    public MiniGoTypeChecker()
    {
        this.symbolsTable = new SymbolsTable();
        this.errorList = new LinkedList<string>();
    }

    /// <summary>Indicates whether any semantic error has been collected.</summary>
    public bool hasErrors => this.errorList.Count > 0;

    /// <summary>
    /// Prints all accumulated semantic errors to the console, or a success
    /// message when no errors are present.
    /// </summary>
    public void printErrors()
    {
        if (this.errorList.Count != 0)
        {
            Console.WriteLine("Compilation failed");
            foreach (string error in this.errorList)
            {
                Console.WriteLine(error);
            }
        }
        else
        {
            Console.WriteLine("Compilation succeeded");
        }
    }

    /// <summary>
    /// Records a semantic error tied to a specific token, including its source
    /// line and column for diagnostic clarity.
    /// </summary>
    /// <param name="msg">Human-readable description of the error.</param>
    /// <param name="offendingToken">Token that triggered the error.</param>
    private void syntaxError(string msg, IToken offendingToken)
    {
        string error = "TYPE ERROR: " + msg + ": (" + offendingToken.Text + ") " + " in [line " + offendingToken.Line +
                       ": " + "Column " + offendingToken.Column + "]";
        this.errorList.AddFirst(error);
    }

    /// <summary>
    /// Records a semantic error involving two incompatible types (e.g. <c>int</c>
    /// vs <c>string</c>), formatting both type names into the diagnostic message.
    /// </summary>
    /// <param name="msg">Human-readable description of the error.</param>
    /// <param name="offendingToken">Token where the mismatch was detected.</param>
    /// <param name="type1">Type encountered on one side of the operation.</param>
    /// <param name="type2">Type encountered on the other side of the operation.</param>
    private void syntaxError(string msg, IToken offendingToken, SymbolsTable.TypeInfo type1,
        SymbolsTable.TypeInfo type2)
    {
        string tipo1 = type1.Category == "simple" ? GetSimpleTypeName(type1.SimpleType) : type1.Category;
        string tipo2 = type2.Category == "simple" ? GetSimpleTypeName(type2.SimpleType) : type2.Category;

        string error = "TYPE ERROR: " + msg + " " + tipo1 + " and " + tipo2 + ": (" + offendingToken.Text + ")" +
                       " in [line " + offendingToken.Line + ": Column " + offendingToken.Column + "]";
        this.errorList.AddFirst(error);
    }

    /// <summary>
    /// Maps the internal numeric encoding of a simple/primitive type to its
    /// MiniGo source-level name. Used solely for diagnostic formatting.
    /// </summary>
    /// <remarks>
    /// Encoding: 0 = int, 1 = float64, 2 = string, 3 = rune, 4 = bool.
    /// </remarks>
    private string GetSimpleTypeName(int simpleType)
    {
        switch (simpleType)
        {
            case 0: return "int";
            case 1: return "float64";
            case 2: return "string";
            case 3: return "rune";
            case 4: return "bool";
            default: return "unknown";
        }
    }


    // -------------------------------------------------------------------------
    //  Program root and top-level declarations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Entry point of the semantic analysis. Opens the global scope,
    /// predeclares the built-in identifiers <c>true</c> and <c>false</c>
    /// as bool-typed variables (mirroring Go's universe block), visits
    /// every top-level declaration, then closes the scope before returning.
    /// </summary>
    public override object VisitRoot(MiniGoCompilerParser.RootContext context)
    {
        symbolsTable.OpenScope();
        SymbolsTable.TypeInfo boolType = new SymbolsTable.TypeInfo("simple", 4, 0, null, null);

        IToken trueToken  = new CommonToken(MiniGoCompilerLexer.IDENTIFIER, "true");
        IToken falseToken = new CommonToken(MiniGoCompilerLexer.IDENTIFIER, "false");

        symbolsTable.InsertVariableLevel(trueToken,  boolType, symbolsTable.GetActualLevel(), context);
        symbolsTable.InsertVariableLevel(falseToken, boolType, symbolsTable.GetActualLevel(), context);

        Visit(context.topDeclarationList());
        symbolsTable.CloseScope();
        return null;
    }

    /// <summary>
    /// Iterates through each top-level declaration node (variables, types,
    /// functions) and dispatches the visitor for individual analysis.
    /// </summary>
    public override object VisitTopDeclarationList(MiniGoCompilerParser.TopDeclarationListContext context)
    {
        if (context.children == null)
            return null;

        foreach (var child in context.children)
        {
            Visit(child);
        }

        return null;
    }

    // -------------------------------------------------------------------------
    //  Variable declarations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Visits a <c>var</c> declaration block, handling both the single-line
    /// form and the parenthesized multi-declaration form.
    /// </summary>
    public override object VisitVariableDecl(MiniGoCompilerParser.VariableDeclContext context)
    {
        try
        {
            if (context.singleVarDecl() != null)
            {
                Visit(context.singleVarDecl());
            }

            if (context.innerVarDecls() != null)
            {
                Visit(context.innerVarDecls());
            }
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Visits each declaration inside a parenthesized <c>var (...)</c> block.
    /// </summary>
    public override object VisitInnerVarDecls(MiniGoCompilerParser.InnerVarDeclsContext context)
    {
        foreach (MiniGoCompilerParser.SingleVarDeclContext svd in context.singleVarDecl())
        {
            Visit(svd);
        }

        return null;
    }


    /// <summary>
    /// Handles a typed variable declaration of the form
    /// <c>var x, y T = expr1, expr2</c>. Verifies that each identifier is not
    /// already declared in the current scope and that each initializer's type
    /// matches the declared type before registering the variable.
    /// </summary>
    public override object VisitTypedVarDecl(MiniGoCompilerParser.TypedVarDeclContext context)
    {
        try
        {
            SymbolsTable.TypeInfo declaredType = (SymbolsTable.TypeInfo)Visit(context.declType());
            var identList = context.identifierList().IDENTIFIER();
            LinkedList<SymbolsTable.TypeInfo> exprTypes =
                (LinkedList<SymbolsTable.TypeInfo>)Visit(context.expressionList());

            if (identList.Length != exprTypes.Count)
            {
                syntaxError("Identifier count does not match expression count", identList[0].Symbol);
                return null;
            }

            for (int i = 0; i < identList.Length; i++)
            {
                IToken token = identList[i].Symbol;
                SymbolsTable.TypeInfo exprType = exprTypes.ElementAt(i);

                SymbolsTable.Ident ident = symbolsTable.SearchActualLevel(token.Text);
                if (ident != null)
                {
                    syntaxError("Variable already declared", token);
                }
                else
                {
                    if (declaredType != null && exprType != null &&
                        (declaredType.Category != exprType.Category ||
                         declaredType.SimpleType != exprType.SimpleType))
                    {
                        syntaxError("Invalid types in assign ", token, declaredType, exprType);
                    }

                    symbolsTable.InsertVariableLevel(token, declaredType,
                        symbolsTable.GetActualLevel(), context);
                }
            }

            context.decl = context;
        }
        catch (TypeErrorException)
        {
        }

        return null;
    }


    /// <summary>
    /// Handles a type-inferred declaration of the form <c>var x = expr</c>
    /// (no explicit type). Verifies arity between identifiers and expressions,
    /// rejects redeclarations, and registers each variable using the inferred
    /// type from its initializer.
    /// </summary>
    public override object VisitInferredVarDecl(MiniGoCompilerParser.InferredVarDeclContext context)
    {
        try
        {
            LinkedList<SymbolsTable.TypeInfo> exprTypes =
                (LinkedList<SymbolsTable.TypeInfo>)Visit(context.expressionList());
            var identList = context.identifierList().IDENTIFIER();
            if (identList.Length != exprTypes.Count)
            {
                syntaxError("Identifier count does not match expression count", identList[0].Symbol);
                return null;
            }

            for (int i = 0; i < identList.Length; i++)
            {
                IToken token = identList[i].Symbol;
                SymbolsTable.TypeInfo exprType = exprTypes.ElementAt(i);
                SymbolsTable.Ident ident = symbolsTable.SearchActualLevel(token.Text);

                if (ident != null)
                {
                    syntaxError("Variable already declared", token);
                }
                else if (exprType != null && exprType.Category != "simple")
                {
                    syntaxError("Type inference only allowed for primitive types", token);
                }
                else
                {
                    symbolsTable.InsertVariableLevel(token, exprType,
                        symbolsTable.GetActualLevel(), context);
                }
            }
        }
        catch (TypeErrorException)
        {
        }

        context.decl = context;
        return null;
    }

    /// <summary>
    /// Dispatches to <see cref="VisitSingleVarDeclNoExps"/> for declarations
    /// without an initializer (e.g. <c>var x int</c>).
    /// </summary>
    public override object VisitNoExpressionVarDecl(MiniGoCompilerParser.NoExpressionVarDeclContext context)
    {
        return Visit(context.singleVarDeclNoExps());
    }

    /// <summary>
    /// Handles a typed variable declaration without initializer, such as
    /// <c>var x, y T</c>. Registers each identifier with the declared type,
    /// reporting an error if any identifier is already declared in scope.
    /// </summary>
    public override object VisitSingleVarDeclNoExps(MiniGoCompilerParser.SingleVarDeclNoExpsContext context)
    {
        try
        {
            SymbolsTable.TypeInfo declaredType = (SymbolsTable.TypeInfo)Visit(context.declType());
            foreach (var id in context.identifierList().IDENTIFIER())
            {
                IToken token = id.Symbol;
                SymbolsTable.Ident ident = symbolsTable.SearchActualLevel(token.Text);
                if (ident != null)
                {
                    syntaxError("Variable already declared", token);
                }
                else
                {
                    symbolsTable.InsertVariableLevel(token, declaredType, symbolsTable.GetActualLevel(), context);
                }
            }

            context.decl = context;
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    // -------------------------------------------------------------------------
    //  Type declarations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Visits a <c>type</c> declaration, dispatching to either the single-line
    /// or the parenthesized multi-declaration form.
    /// </summary>
    public override object VisitTypeDecl(MiniGoCompilerParser.TypeDeclContext context)
    {
        try
        {
            if (context.singleTypeDecl() != null)
            {
                Visit(context.singleTypeDecl());
            }

            if (context.innerTypeDecls() != null)
            {
                Visit(context.innerTypeDecls());
            }
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Visits each declaration inside a parenthesized <c>type (...)</c> block.
    /// </summary>
    public override object VisitInnerTypeDecls(MiniGoCompilerParser.InnerTypeDeclsContext context)
    {
        try
        {
            foreach (var std in context.singleTypeDecl())
            {
                Visit(std);
            }
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Handles a single <c>type Name T</c> declaration: resolves the right-hand
    /// type, ensures the alias name is not already taken in the current scope,
    /// and registers it in the symbol table.
    /// </summary>
    public override object VisitSingleTypeDecl(MiniGoCompilerParser.SingleTypeDeclContext context)
    {
        try
        {
            SymbolsTable.TypeInfo declaredType = (SymbolsTable.TypeInfo)Visit(context.declType());
            SymbolsTable.Ident ident = symbolsTable.SearchActualLevel(context.IDENTIFIER().GetText());
            if (ident != null)
            {
                syntaxError("Type already declared", context.IDENTIFIER().Symbol);
            }
            else
            {
                symbolsTable.InsertTypeLevel(context.IDENTIFIER().Symbol, declaredType, symbolsTable.GetActualLevel(),
                    context);
            }
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    // -------------------------------------------------------------------------
    //  Function declarations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Visits a function declaration. Performs the following actions in order:
    /// <list type="number">
    ///   <item>Resolves the (optional) return type.</item>
    ///   <item>Registers the function in the current scope, checking for redeclaration.</item>
    ///   <item>Collects parameter types.</item>
    ///   <item>Opens a new scope and inserts the parameters as local variables.</item>
    ///   <item>Pushes the expected return type on <see cref="returnTypeStack"/>.</item>
    ///   <item>Visits the function body.</item>
    ///   <item>Verifies that every execution path returns a value when one is required.</item>
    ///   <item>Pops the stack and closes the scope.</item>
    /// </list>
    /// </summary>
    public override object VisitFuncDecl(MiniGoCompilerParser.FuncDeclContext context)
    {
        try
        {
            var front = context.funcFrontDecl();
            SymbolsTable.TypeInfo returnType = null;
            if (front.declType() != null)
                returnType = (SymbolsTable.TypeInfo)Visit(front.declType());

            SymbolsTable.Ident ident = symbolsTable.SearchActualLevel(front.IDENTIFIER().GetText());
            if (ident != null)
            {
                syntaxError("Function already declared", front.IDENTIFIER().Symbol);
                return null; // <- evita insertar duplicado
            }

            LinkedList<SymbolsTable.TypeInfo> paramTypes = new LinkedList<SymbolsTable.TypeInfo>();
            if (front.funcArgDecls() != null)
            {
                foreach (var param in front.funcArgDecls().singleVarDeclNoExps())
                {
                    SymbolsTable.TypeInfo t = (SymbolsTable.TypeInfo)Visit(param.declType());
                    paramTypes.AddLast(t);
                }
            }

            symbolsTable.InsertMethod(front.IDENTIFIER().Symbol, returnType,
                symbolsTable.GetActualLevel(), paramTypes, context);
            symbolsTable.OpenScope();
            if (front.funcArgDecls() != null)
            {
                Visit(front.funcArgDecls());
            }

            returnTypeStack.Push(returnType);
            Visit(context.block());
            if (returnType != null && !GuaranteesReturn(context.block().statementList()))
            {
                syntaxError("Not all paths return a value", front.IDENTIFIER().Symbol);
            }

            returnTypeStack.Pop();
            symbolsTable.CloseScope();
            context.decl = context;
        }
        catch (TypeErrorException)
        {
        }

        return null;
    }

    /// <summary>
    /// Front-of-function declaration node (signature container).
    /// Handled directly inside <see cref="VisitFuncDecl"/>, so this override is a no-op.
    /// </summary>
    public override object VisitFuncFrontDecl(MiniGoCompilerParser.FuncFrontDeclContext context)
    {
        return null;
    }

    /// <summary>
    /// Walks each formal parameter declaration of a function and registers
    /// them as local variables in the function's freshly opened scope.
    /// </summary>
    public override object VisitFuncArgDecls(MiniGoCompilerParser.FuncArgDeclsContext context)
    {
        foreach (var fad in context.singleVarDeclNoExps())
        {
            Visit(fad);
        }

        return null;
    }

    // -------------------------------------------------------------------------
    //  Type denoters (resolution of type expressions to TypeInfo)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves a parenthesized/grouped type declaration to its inner
    /// <see cref="SymbolsTable.TypeInfo"/>.
    /// </summary>
    public override object VisitGroupDeclType(MiniGoCompilerParser.GroupDeclTypeContext context)
    {
        SymbolsTable.TypeInfo declareType = (SymbolsTable.TypeInfo)Visit(context.declType());
        return declareType;
    }

    /// <summary>
    /// Resolves an identifier appearing in a type position to a
    /// <see cref="SymbolsTable.TypeInfo"/>. Recognizes built-in primitive
    /// names (<c>int</c>, <c>float64</c>, <c>string</c>, <c>rune</c>,
    /// <c>bool</c>) directly, and otherwise looks up the alias in the symbol
    /// table. Reports an error if the type cannot be resolved.
    /// </summary>
    public override object VisitTypeDenoterDeclType(MiniGoCompilerParser.TypeDenoterDeclTypeContext context)
    {
        string typeName = context.identifier().IDENTIFIER().GetText();

        switch (typeName)
        {
            case "int": return new SymbolsTable.TypeInfo("simple", 0, 0, null, null);
            case "float64": return new SymbolsTable.TypeInfo("simple", 1, 0, null, null);
            case "string": return new SymbolsTable.TypeInfo("simple", 2, 0, null, null);
            case "rune": return new SymbolsTable.TypeInfo("simple", 3, 0, null, null);
            case "bool": return new SymbolsTable.TypeInfo("simple", 4, 0, null, null);
            default:
                SymbolsTable.Ident ident = symbolsTable.Search(typeName);
                if (ident is SymbolsTable.TypeIdent typeIdent)
                {
                    return typeIdent.Type;
                }

                if (ident != null)
                {
                    syntaxError("Identifier is not a type", context.identifier().IDENTIFIER().Symbol);
                    return null;
                }

                syntaxError("Undefined type", context.identifier().IDENTIFIER().Symbol);
                return null;
        }
    }

    /// <summary>
    /// Constructs a <see cref="SymbolsTable.TypeInfo"/> representing a slice
    /// type (<c>[]T</c>), capturing the element type as the inner type.
    /// </summary>
    public override object VisitSliceTypeDecl(MiniGoCompilerParser.SliceTypeDeclContext context)
    {
        SymbolsTable.TypeInfo declareType = (SymbolsTable.TypeInfo)Visit(context.sliceDeclType().declType());
        SymbolsTable.TypeInfo slice = new SymbolsTable.TypeInfo("slice", -1, -1, declareType, null);
        return slice;
    }

    /// <summary>
    /// Constructs a <see cref="SymbolsTable.TypeInfo"/> representing an array
    /// type (<c>[N]T</c>), parsing the fixed length from the integer literal.
    /// </summary>
    public override object VisitArrayTypeDecl(MiniGoCompilerParser.ArrayTypeDeclContext context)
    {
        SymbolsTable.TypeInfo declareType = (SymbolsTable.TypeInfo)Visit(context.arrayDeclType().declType());
        int size = int.Parse(context.arrayDeclType().INTLITERAL().GetText());
        SymbolsTable.TypeInfo array = new SymbolsTable.TypeInfo("array", -1, size, declareType, null);
        return array;
    }

    /// <summary>
    /// Constructs a <see cref="SymbolsTable.TypeInfo"/> for a struct type by
    /// collecting its member declarations into a field map.
    /// </summary>
    public override object VisitStructTypeDecl(MiniGoCompilerParser.StructTypeDeclContext context)
    {
        Dictionary<string, SymbolsTable.TypeInfo> fields = new Dictionary<string, SymbolsTable.TypeInfo>();

        if (context.structDeclType().structMemDecls() != null)
            fields = (Dictionary<string, SymbolsTable.TypeInfo>)Visit(context.structDeclType().structMemDecls());

        return new SymbolsTable.TypeInfo("struct", -1, -1, null, fields);
    }

    /// <summary>
    /// Intermediate node used by <see cref="VisitSliceTypeDecl"/>; the parent
    /// override already extracts the element type, so this is a no-op.
    /// </summary>
    public override object VisitSliceDeclType(MiniGoCompilerParser.SliceDeclTypeContext context)
    {
        return null;
    }

    /// <summary>
    /// Intermediate node used by <see cref="VisitArrayTypeDecl"/>; the parent
    /// override already extracts size and element type, so this is a no-op.
    /// </summary>
    public override object VisitArrayDeclType(MiniGoCompilerParser.ArrayDeclTypeContext context)
    {
        return null;
    }

    /// <summary>
    /// Intermediate node used by <see cref="VisitStructTypeDecl"/>; the parent
    /// override already extracts the field map, so this is a no-op.
    /// </summary>
    public override object VisitStructDeclType(MiniGoCompilerParser.StructDeclTypeContext context)
    {
        return null;
    }

    /// <summary>
    /// Resolves the member declarations of a struct into a dictionary that
    /// maps each field name to its <see cref="SymbolsTable.TypeInfo"/>.
    /// </summary>
    /// <returns>A <c>Dictionary&lt;string, TypeInfo&gt;</c> describing the struct fields.</returns>
    public override object VisitStructMemDecls(MiniGoCompilerParser.StructMemDeclsContext context)
    {
        Dictionary<string, SymbolsTable.TypeInfo> fields = new Dictionary<string, SymbolsTable.TypeInfo>();

        foreach (var member in context.singleVarDeclNoExps())
        {
            SymbolsTable.TypeInfo memberType = (SymbolsTable.TypeInfo)Visit(member.declType());
            foreach (var id in member.identifierList().IDENTIFIER())
            {
                fields[id.GetText()] = memberType;
            }
        }

        return fields;
    }

    /// <summary>
    /// Identifier list nodes are consumed contextually by their parents
    /// (declarations, assignments). No standalone analysis is required.
    /// </summary>
    public override object VisitIdentifierList(MiniGoCompilerParser.IdentifierListContext context)
    {
        return null;
    }

    /// <summary>
    /// Evaluates each expression in a list, returning a
    /// <c>LinkedList&lt;TypeInfo&gt;</c> with one entry per expression. Used by
    /// declarations, assignments, and function-call arguments.
    /// </summary>
    public override object VisitExpressionList(MiniGoCompilerParser.ExpressionListContext context)
    {
        LinkedList<SymbolsTable.TypeInfo> types = new LinkedList<SymbolsTable.TypeInfo>();
        foreach (var expr in context.expression())
        {
            SymbolsTable.TypeInfo t = (SymbolsTable.TypeInfo)Visit(expr);
            types.AddLast(t);
        }

        return types;
    }

    // -------------------------------------------------------------------------
    //  Expressions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Forwards to the inner primary expression and returns its computed type.
    /// </summary>
    public override object VisitPrimaryExpr(MiniGoCompilerParser.PrimaryExprContext context)
    {
        return Visit(context.primaryExpression());
    }

    /// <summary>
    /// Type-checks the unary minus expression (<c>-expr</c>). The operand must
    /// be numeric (<c>int</c> or <c>float64</c>); otherwise an error is reported.
    /// </summary>
    public override object VisitUnarySubExpr(MiniGoCompilerParser.UnarySubExprContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo)Visit(context.expression());
        if (type != null && (type.Category != "simple" ||
                             (type.SimpleType != 0 && type.SimpleType != 1)))
        {
            syntaxError("Cannot apply '-' to non-numeric type", context.SUB().Symbol);
        }

        return type;
    }

    /// <summary>
    /// Type-checks additive expressions (<c>+</c>, <c>-</c>, <c>|</c>, <c>^</c>).
    /// Both operands must share the same category and simple type. The bitwise
    /// operators (<c>|</c>, <c>^</c>) additionally require integer operands.
    /// </summary>
    public override object VisitAddExpr(MiniGoCompilerParser.AddExprContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));
        IToken op = (context.ADD() ?? context.SUB() ?? context.OR() ?? context.HAT()).Symbol;

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
        {
            syntaxError("Incompatible types in expression", op);
        }
        else if (left != null && (context.HAT() != null || context.OR() != null) &&
                 (left.Category != "simple" || left.SimpleType != 0))
        {
            syntaxError("Bitwise operator requires integer type", op);
        }

        return left;
    }

    /// <summary>
    /// Type-checks multiplicative expressions (<c>*</c>, <c>/</c>, <c>%</c>,
    /// <c>&lt;&lt;</c>, <c>&gt;&gt;</c>, <c>&amp;</c>, <c>&amp;^</c>). Verifies
    /// operand compatibility, restricts bitwise/shift/modulo operators to
    /// integers, and restricts <c>*</c>/<c>/</c> to numeric operands.
    /// </summary>
    public override object VisitMulExpr(MiniGoCompilerParser.MulExprContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));
        IToken op = (context.MUL() ?? context.DIV() ?? context.MOD() ?? context.DLESS()
            ?? context.DMORE() ?? context.AND() ?? context.ANDHAT()).Symbol;

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
        {
            syntaxError("Incompatible types in expression", op);
        }
        else if (left != null &&
                 (context.ANDHAT() != null || context.MOD() != null || context.AND() != null
                  || context.DLESS() != null || context.DMORE() != null) &&
                 (left.Category != "simple" || left.SimpleType != 0))
        {
            syntaxError("Bitwise operator requires integer type", op);
        }
        else if (right != null && (context.MUL() != null || context.DIV() != null) &&
                 (right.Category != "simple" || (right.SimpleType != 0 && right.SimpleType != 1)))
        {
            syntaxError("Operation requires numeric type", op);
        }

        return left;
    }

    /// <summary>
    /// Type-checks the logical OR expression (<c>||</c>). Operands must both
    /// be of type <c>bool</c>; mismatches are reported as type errors.
    /// </summary>
    public override object VisitOrExpr(MiniGoCompilerParser.OrExprContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));
        IToken op = context.DOR().Symbol;

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
        {
            syntaxError("Incompatible types in expression", op, left, right);
        }
        else if (left != null && right != null &&
                 (left.SimpleType != 4 || right.SimpleType != 4))
        {
            syntaxError("Logical operator || requires boolean type", op);
        }

        return new SymbolsTable.TypeInfo("simple", 4, 0, null, null);
    }

    /// <summary>
    /// Type-checks the unary bitwise complement (<c>^expr</c>). The operand
    /// must be of type <c>int</c>; otherwise an error is reported.
    /// </summary>
    public override object VisitUnaryHatExpr(MiniGoCompilerParser.UnaryHatExprContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo)Visit(context.expression());
        if (type != null && (type.Category != "simple" || type.SimpleType != 0))
        {
            syntaxError("Cannot apply '^' to non-integer type", context.HAT().Symbol);
        }

        return type;
    }

    /// <summary>
    /// Type-checks the unary plus expression (<c>+expr</c>). The operand must
    /// be of a numeric or rune type (<c>int</c>, <c>float64</c>, <c>rune</c>).
    /// </summary>
    public override object VisitUnaryAddExpr(MiniGoCompilerParser.UnaryAddExprContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo)Visit(context.expression());
        if (type != null && (type.Category != "simple" ||
                             (type.SimpleType != 0 && type.SimpleType != 1 && type.SimpleType != 3)))
        {
            syntaxError("Cannot apply '+' to non-numeric type", context.ADD().Symbol);
        }

        return type;
    }

    /// <summary>
    /// Type-checks relational expressions (<c>==</c>, <c>!=</c>, <c>&lt;</c>,
    /// <c>&lt;=</c>, <c>&gt;</c>, <c>&gt;=</c>). Operands must have matching
    /// types; ordering operators additionally cannot be applied to <c>bool</c>.
    /// Always yields a <c>bool</c> result, regardless of operand type.
    /// </summary>
    public override object VisitRelExpr(MiniGoCompilerParser.RelExprContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));
        IToken op = (context.EQEQ() ?? context.NOTEQ() ?? context.LESS() ?? context.LESSEQ()
            ?? context.MORET() ?? context.MOREEQ()).Symbol;

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
        {
            syntaxError("Incompatible types in expression", op, left, right);
        }
        else if (left != null &&
                 (context.LESS() != null || context.LESSEQ() != null
                                         || context.MORET() != null || context.MOREEQ() != null) &&
                 left.SimpleType == 4)
        {
            syntaxError("Cannot compare bool with ordering operator", op);
        }
        else if (left != null && (context.EQEQ() != null || context.NOTEQ() != null) &&
                 left.Category == "slice")
        {
            syntaxError("Slices cannot be compared with == or !=", op);
        }

        return new SymbolsTable.TypeInfo("simple", 4, 0, null, null);
    }

    /// <summary>
    /// Type-checks the unary logical NOT expression (<c>!expr</c>). The
    /// operand must be of type <c>bool</c>.
    /// </summary>
    public override object VisitUnaryNotExpr(MiniGoCompilerParser.UnaryNotExprContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo)Visit(context.expression());
        if (type != null && (type.Category != "simple" || type.SimpleType != 4))
        {
            syntaxError("Cannot apply '!' to non-bool type", context.NOT().Symbol);
        }

        return type;
    }

    /// <summary>
    /// Type-checks the logical AND expression (<c>&amp;&amp;</c>). Both
    /// operands must be of type <c>bool</c>.
    /// </summary>
    public override object VisitAndExpr(MiniGoCompilerParser.AndExprContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));
        IToken op = context.DAND().Symbol;

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
        {
            syntaxError("Incompatible types in expression", op, left, right);
        }
        else if (left != null && right != null &&
                 (left.SimpleType != 4 || right.SimpleType != 4))
        {
            syntaxError("Logical operator && requires boolean type", op);
        }

        return new SymbolsTable.TypeInfo("simple", 4, 0, null, null);
    }

    // -------------------------------------------------------------------------
    //  Primary expressions
    // -------------------------------------------------------------------------

    /// <summary>Forwards to the built-in <c>len(...)</c> expression visitor.</summary>
    public override object VisitLengthPrimaryExpr(MiniGoCompilerParser.LengthPrimaryExprContext context)
    {
        return Visit(context.lengthExpression());
    }

    /// <summary>Forwards to the operand visitor (identifier, literal, or grouped expression).</summary>
    public override object VisitOperandPrimaryExpr(MiniGoCompilerParser.OperandPrimaryExprContext context)
    {
        return Visit(context.operand());
    }

    /// <summary>Forwards to the built-in <c>append(...)</c> expression visitor.</summary>
    public override object VisitAppendPrimaryExpr(MiniGoCompilerParser.AppendPrimaryExprContext context)
    {
        return Visit(context.appendExpression());
    }

    /// <summary>
    /// Type-checks an indexing expression (<c>expr[idx]</c>). Verifies that
    /// the index is an integer and returns the element type for arrays/slices,
    /// or <c>rune</c> when indexing into a <c>string</c>.
    /// </summary>
    public override object VisitIndexPrimaryExpr(MiniGoCompilerParser.IndexPrimaryExprContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo)Visit(context.primaryExpression());
        SymbolsTable.TypeInfo indexType = (SymbolsTable.TypeInfo)Visit(context.index());
        if (indexType != null && indexType.SimpleType != 0)
        {
            syntaxError("Index must be integer", context.index().LEFTB().Symbol);
        }

        if (type != null)
        {
            if (type.Category == "array" || type.Category == "slice")
            {
                return type.InsideType;
            }

            if (type.Category == "simple" && type.SimpleType == 2)
            {
                return new SymbolsTable.TypeInfo("simple", 3, 0, null, null); // string[i] retorna rune
            }

            syntaxError("Cannot index this type", context.index().RIGHTB().Symbol);
        }

        return null;
    }

    /// <summary>
    /// Type-checks a struct field access (<c>expr.field</c>). The base
    /// expression must denote a struct value, and the selector must reference
    /// an existing field; the field's type becomes the expression result.
    /// </summary>
    public override object VisitSelectorPrimaryExpr(MiniGoCompilerParser.SelectorPrimaryExprContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo)Visit(context.primaryExpression());
        string selector = context.selector().IDENTIFIER().GetText();

        if (type == null) return null;

        if (type.Category != "struct")
        {
            syntaxError("Invalid use of selector in a non-struct type",
                context.selector().IDENTIFIER().Symbol);
            return null;
        }

        if (type.Fields == null || !type.Fields.ContainsKey(selector))
        {
            syntaxError("Selector '" + selector + "' does not exist in struct",
                context.selector().IDENTIFIER().Symbol);
            return null;
        }

        return type.Fields[selector];
    }

    /// <summary>
    /// Type-checks a function-call expression (<c>f(args)</c>). Verifies that:
    /// <list type="bullet">
    ///   <item>The callee is defined and is, in fact, a function.</item>
    ///   <item>The argument count matches the function's parameter count.</item>
    ///   <item>Each actual argument's type matches the corresponding formal.</item>
    /// </list>
    /// The function's declared return type becomes the resulting expression type.
    /// </summary>
    public override object VisitArgumentsPrimaryExpr(MiniGoCompilerParser.ArgumentsPrimaryExprContext context)
    {
        if (context.primaryExpression() is not MiniGoCompilerParser.OperandPrimaryExprContext opExpr ||
            opExpr.operand() is not MiniGoCompilerParser.IdOperandContext idOp)
        {
            syntaxError("Invalid function call", context.primaryExpression().Start);
            return null;
        }

        IToken funcToken = idOp.identifier().IDENTIFIER().Symbol;
        string funcName = funcToken.Text;

        SymbolsTable.Ident ident = symbolsTable.Search(funcName);
        if (ident == null)
        {
            syntaxError("Undefined function", funcToken);
            return null;
        }

        if (ident is not SymbolsTable.FunctionIdent func)
        {
            syntaxError("Cannot call a non-function", funcToken);
            return null;
        }

        LinkedList<SymbolsTable.TypeInfo> argumentList = new LinkedList<SymbolsTable.TypeInfo>();
        if (context.arguments().expressionList() != null)
        {
            argumentList = (LinkedList<SymbolsTable.TypeInfo>)Visit(context.arguments().expressionList());
        }

        if (argumentList.Count < func.Parameters.Count)
        {
            syntaxError("Missing arguments", funcToken);
        }
        else if (argumentList.Count > func.Parameters.Count)
        {
            syntaxError("Too many arguments", funcToken);
        }
        else
        {
            for (int i = 0; i < argumentList.Count; i++)
            {
                var arg = argumentList.ElementAt(i);
                var param = func.Parameters.ElementAt(i);
                if (arg != null && param != null &&
                    (arg.Category != param.Category || arg.SimpleType != param.SimpleType))
                {
                    syntaxError("Invalid argument type", funcToken);
                }
            }
        }

        return ident.Type;
    }

    /// <summary>Forwards to the built-in <c>cap(...)</c> expression visitor.</summary>
    public override object VisitCapPrimaryExpr(MiniGoCompilerParser.CapPrimaryExprContext context)
    {
        return Visit(context.capExpression());
    }


    // -------------------------------------------------------------------------
    //  Operands and literals
    // -------------------------------------------------------------------------

    /// <summary>Forwards to the literal visitor, returning the literal's type.</summary>
    public override object VisitLiteralOperand(MiniGoCompilerParser.LiteralOperandContext context)
    {
        return Visit(context.literal());
    }

    /// <summary>Forwards to the identifier visitor for identifier-as-operand resolution.</summary>
    public override object VisitIdOperand(MiniGoCompilerParser.IdOperandContext context)
    {
        return Visit(context.identifier());
    }

    /// <summary>Resolves a parenthesized expression to the type of its inner expression.</summary>
    public override object VisitGroupOperand(MiniGoCompilerParser.GroupOperandContext context)
    {
        return Visit(context.expression());
    }

    /// <summary>Produces the <c>int</c> type for integer literals.</summary>
    public override object VisitIntLiteral(MiniGoCompilerParser.IntLiteralContext context)
    {
        return new SymbolsTable.TypeInfo("simple", 0, 0, null, null);
    }

    /// <summary>Produces the <c>float64</c> type for floating-point literals.</summary>
    public override object VisitFloatLiteral(MiniGoCompilerParser.FloatLiteralContext context)
    {
        return new SymbolsTable.TypeInfo("simple", 1, 0, null, null);
    }

    /// <summary>Produces the <c>rune</c> type for character/rune literals.</summary>
    public override object VisitRuneLiteral(MiniGoCompilerParser.RuneLiteralContext context)
    {
        return new SymbolsTable.TypeInfo("simple", 3, 0, null, null);
    }

    /// <summary>Produces the <c>string</c> type for raw string literals (backticks).</summary>
    public override object VisitRawStringLiteral(MiniGoCompilerParser.RawStringLiteralContext context)
    {
        return new SymbolsTable.TypeInfo("simple", 2, 0, null, null);
    }

    /// <summary>Produces the <c>string</c> type for interpreted string literals (double quotes).</summary>
    public override object VisitInterpretedStringLiteral(MiniGoCompilerParser.InterpretedStringLiteralContext context)
    {
        return new SymbolsTable.TypeInfo("simple", 2, 0, null, null);
    }

    /// <summary>Resolves the index expression contained in <c>[ ... ]</c> brackets.</summary>
    public override object VisitIndex(MiniGoCompilerParser.IndexContext context)
    {
        return Visit(context.expression());
    }

    /// <summary>
    /// Visits an argument list passed to a function call, returning the list
    /// of argument types or <c>null</c> when no arguments are present.
    /// </summary>
    public override object VisitArguments(MiniGoCompilerParser.ArgumentsContext context)
    {
        if (context.expressionList() != null)
        {
            return Visit(context.expressionList());
        }

        return null;
    }

    /// <summary>
    /// The selector identifier is consumed by <see cref="VisitSelectorPrimaryExpr"/>,
    /// so this override does not need to perform additional analysis.
    /// </summary>
    public override object VisitSelector(MiniGoCompilerParser.SelectorContext context)
    {
        return null;
    }

    /// <summary>
    /// Type-checks the built-in <c>append(slice, elem)</c> expression. The
    /// first argument must be a slice; the second argument must match the
    /// slice's element type. The resulting type is the slice itself.
    /// </summary>
    public override object VisitAppendExpression(MiniGoCompilerParser.AppendExpressionContext context)
    {
        SymbolsTable.TypeInfo sliceType = (SymbolsTable.TypeInfo)Visit(context.expression()[0]);
        SymbolsTable.TypeInfo elemType = (SymbolsTable.TypeInfo)Visit(context.expression()[1]);

        if (sliceType != null && sliceType.Category != "slice")
        {
            syntaxError("First argument of append must be a slice", context.APPEND().Symbol);
            return sliceType;
        }

        if (sliceType != null && sliceType.InsideType != null && elemType != null &&
            (sliceType.InsideType.Category != elemType.Category ||
             sliceType.InsideType.SimpleType != elemType.SimpleType))
        {
            syntaxError("Incompatible type for append", context.APPEND().Symbol);
        }

        return sliceType;
    }

    /// <summary>
    /// Type-checks the built-in <c>len(...)</c> expression. The argument must
    /// be an array, slice, or string. Always returns an <c>int</c>.
    /// </summary>
    public override object VisitLengthExpression(MiniGoCompilerParser.LengthExpressionContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo)Visit(context.expression());
        if (type != null && type.Category != "array" && type.Category != "slice" &&
            !(type.Category == "simple" && type.SimpleType == 2))
        {
            syntaxError("Cannot apply length to a " + type.Category + " type", context.LEN().Symbol);
        }

        return new SymbolsTable.TypeInfo("simple", 0, 0, null, null);
    }

    /// <summary>
    /// Type-checks the built-in <c>cap(...)</c> expression. The argument must
    /// be an array or slice. Always returns an <c>int</c>.
    /// </summary>
    public override object VisitCapExpression(MiniGoCompilerParser.CapExpressionContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo)Visit(context.expression());
        if (type != null && type.Category != "array" && type.Category != "slice")
        {
            syntaxError("Cannot apply cap to a " + type.Category + " type", context.CAP().Symbol);
        }

        return new SymbolsTable.TypeInfo("simple", 0, 0, null, null);
    }

    // -------------------------------------------------------------------------
    //  Statements and blocks
    // -------------------------------------------------------------------------

    /// <summary>
    /// Visits each statement of a statement list in source order.
    /// Scope management is performed by the enclosing block visitor.
    /// </summary>
    public override object VisitStatementList(MiniGoCompilerParser.StatementListContext context)
    {
        foreach (var stmt in context.statement())
        {
            Visit(stmt);
        }

        return null;
    }

    /// <summary>
    /// Visits the inner statement list of a brace-delimited block. Scope
    /// management around this block is the responsibility of the caller.
    /// </summary>
    public override object VisitBlock(MiniGoCompilerParser.BlockContext context)
    {
        return Visit(context.statementList());
    }

    /// <summary>
    /// Type-checks a <c>print(...)</c> statement by visiting each argument
    /// expression. Arguments accept any type; no further restriction applies.
    /// </summary>
    public override object VisitPrintStatement(MiniGoCompilerParser.PrintStatementContext context)
    {
        if (context.expressionList() != null)
        {
            Visit(context.expressionList());
        }

        return null;
    }

    /// <summary>
    /// Type-checks a <c>println(...)</c> statement by visiting each argument
    /// expression. Arguments accept any type; no further restriction applies.
    /// </summary>
    public override object VisitPrintlnStatement(MiniGoCompilerParser.PrintlnStatementContext context)
    {
        if (context.expressionList() != null)
        {
            Visit(context.expressionList());
        }

        return null;
    }

    // -------------------------------------------------------------------------
    //  Return-path analysis
    //
    //  These helpers determine whether every execution path through a given
    //  statement list, conditional, or switch ends in a return statement. This
    //  is used by VisitFuncDecl to ensure that functions with a declared
    //  return type cannot fall off the end without returning a value.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> when at least one statement in the list
    /// unconditionally guarantees a return on every execution path.
    /// </summary>
    private bool GuaranteesReturn(MiniGoCompilerParser.StatementListContext ctx)
    {
        if (ctx == null) return false;

        foreach (var stmt in ctx.statement())
        {
            if (StmtGuaranteesReturn(stmt)) return true;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a single statement guarantees a return. Returns are
    /// trivially terminal; blocks, <c>if</c>, and <c>switch</c> statements
    /// delegate to their specialized analyzers.
    /// </summary>
    private bool StmtGuaranteesReturn(MiniGoCompilerParser.StatementContext stmt)
    {
        if (stmt is MiniGoCompilerParser.ReturnStatementContext)
            return true;

        if (stmt is MiniGoCompilerParser.BlockStatementContext blockStmt)
            return GuaranteesReturn(blockStmt.block().statementList());

        if (stmt is MiniGoCompilerParser.IfStmtStatementContext ifStmt)
            return IfGuaranteesReturn(ifStmt.ifStatement());

        if (stmt is MiniGoCompilerParser.SwitchStatementContext switchStmt)
            return SwitchGuaranteesReturn(switchStmt.switchStmt());

        return false;
    }

    /// <summary>
    /// Determines whether an <c>if</c>/<c>else</c> chain guarantees a return on
    /// every branch. An <c>if</c> without an <c>else</c> can never guarantee a
    /// return because the implicit fall-through path bypasses the body.
    /// </summary>
    private bool IfGuaranteesReturn(MiniGoCompilerParser.IfStatementContext ctx)
    {
        if (ctx is MiniGoCompilerParser.ElseBlockIfStatementContext elseBlock)
            return GuaranteesReturn(elseBlock.block(0).statementList())
                   && GuaranteesReturn(elseBlock.block(1).statementList());

        if (ctx is MiniGoCompilerParser.ElseIfStatementContext elseIf)
            return GuaranteesReturn(elseIf.block().statementList())
                   && IfGuaranteesReturn(elseIf.ifStatement());

        if (ctx is MiniGoCompilerParser.SimpleElseBlockIfStatementContext simpleElse)
            return GuaranteesReturn(simpleElse.block(0).statementList())
                   && GuaranteesReturn(simpleElse.block(1).statementList());

        if (ctx is MiniGoCompilerParser.SimpleElseIfStatementContext simpleElseIf)
            return GuaranteesReturn(simpleElseIf.block().statementList())
                   && IfGuaranteesReturn(simpleElseIf.ifStatement());

        return false;
    }

    /// <summary>
    /// Determines whether a <c>switch</c> statement guarantees a return on every
    /// path. Every case clause (including <c>default</c>) must guarantee a
    /// return, and a <c>default</c> branch must exist; otherwise some
    /// unmatched switch value could fall through without returning.
    /// </summary>
    private bool SwitchGuaranteesReturn(MiniGoCompilerParser.SwitchStmtContext ctx)
    {
        MiniGoCompilerParser.ExpressionCaseClauseListContext caseList = null;

        if (ctx is MiniGoCompilerParser.ExpressionSwitchContext es)
            caseList = es.expressionCaseClauseList();
        else if (ctx is MiniGoCompilerParser.SimpleExpressionSwitchContext ses)
            caseList = ses.expressionCaseClauseList();
        else if (ctx is MiniGoCompilerParser.SimpleSwitchContext ss)
            caseList = ss.expressionCaseClauseList();
        else if (ctx is MiniGoCompilerParser.EmptySwitchContext ems)
            caseList = ems.expressionCaseClauseList();

        if (caseList == null) return false;

        bool hasDefault = false;
        foreach (var clause in caseList.expressionCaseClause())
        {
            if (clause.expressionSwitchCase() is MiniGoCompilerParser.DefaultSwitchContext)
                hasDefault = true;

            if (!GuaranteesReturn(clause.statementList()))
                return false;
        }

        return hasDefault;
    }

    /// <summary>
    /// Type-checks a <c>return</c> statement, comparing the actual returned
    /// type (if any) against the expected type sitting on top of
    /// <see cref="returnTypeStack"/>. Diagnoses returns outside of functions,
    /// missing values when one is expected, and mismatched return types.
    /// </summary>
    public override object VisitReturnStatement(MiniGoCompilerParser.ReturnStatementContext context)
    {
        if (returnTypeStack.Count == 0)
        {
            syntaxError("Return outside of function", context.RETURN().Symbol);
            return null;
        }

        SymbolsTable.TypeInfo expected = returnTypeStack.Peek();
        if (context.expression() != null)
        {
            SymbolsTable.TypeInfo actual = (SymbolsTable.TypeInfo)Visit(context.expression());
            if (expected == null)
                syntaxError("Function does not return a value", context.RETURN().Symbol);
            else if (actual != null &&
                     (actual.Category != expected.Category || actual.SimpleType != expected.SimpleType))
                syntaxError("Incompatible return type", context.RETURN().Symbol);
        }
        else if (expected != null)
        {
            syntaxError("Missing return value", context.RETURN().Symbol);
        }

        return null;
    }

    /// <summary>
    /// <c>break</c> requires no type information; control-flow validity is
    /// enforced lexically by the grammar.
    /// </summary>
    public override object VisitBreakStatement(MiniGoCompilerParser.BreakStatementContext context)
    {
        return null;
    }

    /// <summary>
    /// <c>continue</c> requires no type information; control-flow validity is
    /// enforced lexically by the grammar.
    /// </summary>
    public override object VisitContinueStatement(MiniGoCompilerParser.ContinueStatementContext context)
    {
        return null;
    }

    /// <summary>Forwards to the inner simple statement visitor.</summary>
    public override object VisitSimpleStmtStatement(MiniGoCompilerParser.SimpleStmtStatementContext context)
    {
        return Visit(context.simpleStatement());
    }

    /// <summary>
    /// Opens a new scope before visiting a nested block and closes it
    /// afterward, so local declarations do not leak into the enclosing scope.
    /// </summary>
    public override object VisitBlockStatement(MiniGoCompilerParser.BlockStatementContext context)
    {
        symbolsTable.OpenScope();
        Visit(context.block());
        symbolsTable.CloseScope();
        return null;
    }

    /// <summary>Forwards to the switch-statement visitor.</summary>
    public override object VisitSwitchStatement(MiniGoCompilerParser.SwitchStatementContext context)
    {
        return Visit(context.switchStmt());
    }

    /// <summary>Forwards to the if-statement visitor.</summary>
    public override object VisitIfStmtStatement(MiniGoCompilerParser.IfStmtStatementContext context)
    {
        return Visit(context.ifStatement());
    }

    /// <summary>Forwards to the loop visitor (for / for-condition / for-init).</summary>
    public override object VisitLoopStatement(MiniGoCompilerParser.LoopStatementContext context)
    {
        return Visit(context.loop());
    }

    /// <summary>Forwards to the type-declaration visitor when used as a statement.</summary>
    public override object VisitTypeDeclStatement(MiniGoCompilerParser.TypeDeclStatementContext context)
    {
        return Visit(context.typeDecl());
    }

    /// <summary>Forwards to the variable-declaration visitor when used as a statement.</summary>
    public override object VisitVariableDeclStatement(MiniGoCompilerParser.VariableDeclStatementContext context)
    {
        return Visit(context.variableDecl());
    }

    /// <summary>Empty simple statements (placeholders) require no analysis.</summary>
    public override object VisitEmptySimpleStatement(MiniGoCompilerParser.EmptySimpleStatementContext context)
    {
        return null;
    }

    /// <summary>
    /// Type-checks an expression used as a simple statement (typically a
    /// function call). When postfix <c>++</c> or <c>--</c> is present, the
    /// expression must be of a numeric/rune type.
    /// </summary>
    public override object VisitExpressionSimpleStatement(MiniGoCompilerParser.ExpressionSimpleStatementContext context)
    {
        SymbolsTable.TypeInfo exprType = (SymbolsTable.TypeInfo)Visit(context.expression());

        if (context.INC() != null || context.DEC() != null)
        {
            IToken op = (context.INC() ?? context.DEC()).Symbol;

            if (!IsAddressable(context.expression()))
            {
                syntaxError("Cannot apply " + op.Text + " to non-addressable expression", op);
            }
            else if (exprType != null &&
                     (exprType.Category != "simple" ||
                      (exprType.SimpleType != 0 && exprType.SimpleType != 1 && exprType.SimpleType != 3)))
            {
                syntaxError("Cannot apply " + op.Text + " to this type", op);
            }
        }

        return null;
    }
    /// <summary>
    /// Determines whether an expression can appear on the left-hand side of a
    /// mutating operation such as <c>++</c> or <c>--</c>. An expression is treated
    /// as addressable only when it is a primary expression representing an
    /// identifier, an indexed element, or a selected struct field.
    /// </summary>
    /// <param name="expr">Expression node to validate as addressable.</param>
    /// <returns>
    /// <c>true</c> when the expression is an identifier, index access, or selector
    /// access; otherwise, <c>false</c>.
    /// </returns>
    
    private bool IsAddressable(MiniGoCompilerParser.ExpressionContext expr)
    {
        if (expr is not MiniGoCompilerParser.PrimaryExprContext pe) return false;
        var pExpr = pe.primaryExpression();
        return pExpr is MiniGoCompilerParser.IndexPrimaryExprContext
               || pExpr is MiniGoCompilerParser.SelectorPrimaryExprContext
               || (pExpr is MiniGoCompilerParser.OperandPrimaryExprContext op &&
                   op.operand() is MiniGoCompilerParser.IdOperandContext);
    }

    /// <summary>Forwards to the appropriate assignment-statement visitor.</summary>
    public override object VisitAssignmentSimpleStatement(MiniGoCompilerParser.AssignmentSimpleStatementContext context)
    {
        return Visit(context.assignmentStatement());
    }

    /// <summary>
    /// Type-checks the short variable declaration syntax (<c>x := expr</c> or
    /// <c>x, y := a, b</c>). Verifies arity, rejects redeclarations in the
    /// current scope, and registers each name with its inferred type.
    /// </summary>
    public override object VisitDeclareSimpleStatement(MiniGoCompilerParser.DeclareSimpleStatementContext context)
    {
        try
        {
            LinkedList<SymbolsTable.TypeInfo> exprTypes =
                (LinkedList<SymbolsTable.TypeInfo>)Visit(context.expressionList()[1]);
            var leftExprs = context.expressionList(0).expression();

            if (leftExprs.Length != exprTypes.Count)
            {
                syntaxError("Identifier count does not match expression count", context.DECLARE().Symbol);
                return null;
            }

            for (int i = 0; i < leftExprs.Length; i++)
            {
                if (leftExprs[i] is not MiniGoCompilerParser.PrimaryExprContext pe ||
                    pe.primaryExpression() is not MiniGoCompilerParser.OperandPrimaryExprContext op ||
                    op.operand() is not MiniGoCompilerParser.IdOperandContext idOp)
                {
                    syntaxError("Left-hand side of := must be an identifier", leftExprs[i].Start);
                    continue;
                }

                IToken token = idOp.identifier().IDENTIFIER().Symbol;
                string name = token.Text;
                SymbolsTable.TypeInfo exprType = exprTypes.ElementAt(i);

                SymbolsTable.Ident ident = symbolsTable.SearchActualLevel(name);
                if (ident != null)
                {
                    syntaxError("Variable already declared", token);
                }
                else if (exprType != null && exprType.Category != "simple")
                {
                    syntaxError("Type inference only allowed for primitive types", token);
                }
                else
                {
                    symbolsTable.InsertVariableLevel(token, exprType,
                        symbolsTable.GetActualLevel(), context);
                }
            }
        }
        catch (TypeErrorException)
        {
        }

        return null;
    }

    // -------------------------------------------------------------------------
    //  Assignment statements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Type-checks a plain <c>=</c> assignment with parallel left- and
    /// right-hand expression lists. Verifies arity and per-position type
    /// compatibility.
    /// </summary>
    public override object VisitEqualAssignment(MiniGoCompilerParser.EqualAssignmentContext context)
    {
        try
        {
            LinkedList<SymbolsTable.TypeInfo> leftTypes =
                (LinkedList<SymbolsTable.TypeInfo>)Visit(context.expressionList()[0]);
            LinkedList<SymbolsTable.TypeInfo> rightTypes =
                (LinkedList<SymbolsTable.TypeInfo>)Visit(context.expressionList()[1]);
            if (leftTypes.Count != rightTypes.Count)
            {
                syntaxError("Identifier count does not match expression count", context.EQUAL().Symbol);
                return null;
            }

            for (int i = 0; i < leftTypes.Count; i++)
            {
                if (leftTypes.ElementAt(i) != null && rightTypes.ElementAt(i) != null &&
                    (leftTypes.ElementAt(i).Category != rightTypes.ElementAt(i).Category
                     || leftTypes.ElementAt(i).SimpleType != rightTypes.ElementAt(i).SimpleType))
                {
                    syntaxError("Incompatible types in assignment",
                        context.expressionList(0).expression(i).Start, leftTypes.ElementAt(i), rightTypes.ElementAt(i));
                }
            }
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks <c>+=</c>. Both operands must share the same type, and the
    /// left-hand side must be of a numeric/rune simple type.
    /// </summary>
    public override object VisitAddAssignment(MiniGoCompilerParser.AddAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
        {
            syntaxError("Incompatible types in assignment", context.ADDEQ().Symbol, left, right);
        }
        else if (left != null && (left.Category != "simple" ||
                                  (left.SimpleType != 0 && left.SimpleType != 1 &&
                                   left.SimpleType != 2 && left.SimpleType != 3)))
        {
            syntaxError("Cannot apply += to this type", context.ADDEQ().Symbol);
        }

        return null;
    }

    /// <summary>
    /// Type-checks <c>&amp;=</c>. Operands must match and the left-hand side
    /// must be of integer type.
    /// </summary>
    public override object VisitAndAssignment(MiniGoCompilerParser.AndAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
            syntaxError("Incompatible types in assignment", context.ANDEQ().Symbol, left, right);

        if (left != null && (left.Category != "simple" || left.SimpleType != 0))
            syntaxError("Cannot apply &= to this type", context.ANDEQ().Symbol);

        return null;
    }

    /// <summary>
    /// Type-checks <c>-=</c>. Operands must match and the left-hand side must
    /// be of a numeric/rune simple type.
    /// </summary>
    public override object VisitSubAssignment(MiniGoCompilerParser.SubAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
            syntaxError("Incompatible types in assignment", context.SUBEQ().Symbol);

        if (left != null && (left.Category != "simple" ||
                             (left.SimpleType != 0 && left.SimpleType != 1 && left.SimpleType != 3)))
            syntaxError("Cannot apply -= to this type", context.SUBEQ().Symbol);

        return null;
    }

    /// <summary>
    /// Type-checks <c>|=</c>. Operands must match and the left-hand side must
    /// be of integer type.
    /// </summary>
    public override object VisitOrAssignment(MiniGoCompilerParser.OrAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
            syntaxError("Incompatible types in assignment", context.OREQ().Symbol);

        if (left != null && (left.Category != "simple" || left.SimpleType != 0))
            syntaxError("Cannot apply |= to this type", context.OREQ().Symbol);

        return null;
    }

    /// <summary>
    /// Type-checks <c>*=</c>. Operands must match and the left-hand side must
    /// be of a numeric/rune simple type.
    /// </summary>
    public override object VisitMulAssignment(MiniGoCompilerParser.MulAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
            syntaxError("Incompatible types in assignment", context.MULEQ().Symbol);

        if (left != null && (left.Category != "simple" ||
                             (left.SimpleType != 0 && left.SimpleType != 1 && left.SimpleType != 3)))
            syntaxError("Cannot apply *= to this type", context.MULEQ().Symbol);

        return null;
    }

    /// <summary>
    /// Type-checks <c>^=</c>. Operands must match and the left-hand side must
    /// be of integer type.
    /// </summary>
    public override object VisitHatAssignment(MiniGoCompilerParser.HatAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
            syntaxError("Incompatible types in assignment", context.HATEQ().Symbol);

        if (left != null && (left.Category != "simple" || left.SimpleType != 0))
            syntaxError("Cannot apply ^= to this type", context.HATEQ().Symbol);

        return null;
    }

    /// <summary>
    /// Type-checks <c>&lt;&lt;=</c>. Operands must match and the left-hand
    /// side must be of integer type.
    /// </summary>
    public override object VisitDlessAssignment(MiniGoCompilerParser.DlessAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
            syntaxError("Incompatible types in assignment", context.DLESSEQ().Symbol);

        if (left != null && (left.Category != "simple" || left.SimpleType != 0))
            syntaxError("Cannot apply <<= to this type", context.DLESSEQ().Symbol);

        return null;
    }

    /// <summary>
    /// Type-checks <c>&gt;&gt;=</c>. Operands must match and the left-hand
    /// side must be of integer type.
    /// </summary>
    public override object VisitDmoreAssignment(MiniGoCompilerParser.DmoreAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
            syntaxError("Incompatible types in assignment", context.DMOREEQ().Symbol);

        if (left != null && (left.Category != "simple" || left.SimpleType != 0))
            syntaxError("Cannot apply >>= to this type", context.DMOREEQ().Symbol);

        return null;
    }

    /// <summary>
    /// Type-checks <c>&amp;^=</c> (bit clear). Operands must match and the
    /// left-hand side must be of integer type.
    /// </summary>
    public override object VisitAndHatAssignment(MiniGoCompilerParser.AndHatAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
            syntaxError("Incompatible types in assignment", context.ANDHATEQ().Symbol);

        if (left != null && (left.Category != "simple" || left.SimpleType != 0))
            syntaxError("Cannot apply &^= to this type", context.ANDHATEQ().Symbol);

        return null;
    }

    /// <summary>
    /// Type-checks <c>%=</c>. Operands must match and the left-hand side must
    /// be of integer type.
    /// </summary>
    public override object VisitModAssignment(MiniGoCompilerParser.ModAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
            syntaxError("Incompatible types in assignment", context.MODEQ().Symbol);

        if (left != null && (left.Category != "simple" || left.SimpleType != 0))
            syntaxError("Cannot apply %= to this type", context.MODEQ().Symbol);

        return null;
    }

    /// <summary>
    /// Type-checks <c>/=</c>. Operands must match and the left-hand side must
    /// be of a numeric/rune simple type.
    /// </summary>
    public override object VisitDivAssignment(MiniGoCompilerParser.DivAssignmentContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));

        if (left != null && right != null &&
            (left.Category != right.Category || left.SimpleType != right.SimpleType))
            syntaxError("Incompatible types in assignment", context.DIVEQ().Symbol);

        if (left != null && (left.Category != "simple" ||
                             (left.SimpleType != 0 && left.SimpleType != 1 && left.SimpleType != 3)))
            syntaxError("Cannot apply /= to this type", context.DIVEQ().Symbol);

        return null;
    }

    // -------------------------------------------------------------------------
    //  If-statements
    //
    //  MiniGo supports several if-statement variants depending on the presence
    //  of an init-simple-statement and/or an else clause. Each variant follows
    //  the same pattern: verify the condition is bool, open a scope for each
    //  branch (so locals do not leak), visit the body, and close the scope.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Type-checks an <c>if expr { ... }</c> statement without an else clause.
    /// </summary>
    public override object VisitNormalIfStatement(MiniGoCompilerParser.NormalIfStatementContext context)
    {
        try
        {
            SymbolsTable.TypeInfo exprType = (SymbolsTable.TypeInfo)Visit(context.expression());
            if (exprType != null && exprType.SimpleType != 4)
            {
                syntaxError("Invalid type in an if-statement", context.IF().Symbol);
            }

            symbolsTable.OpenScope();
            Visit(context.block());
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks an <c>if expr { ... } else if ...</c> chain. Visits the
    /// then-branch in an isolated scope and recurses into the inner
    /// if-statement for the chained <c>else</c>.
    /// </summary>
    public override object VisitElseIfStatement(MiniGoCompilerParser.ElseIfStatementContext context)
    {
        try
        {
            SymbolsTable.TypeInfo exprType = (SymbolsTable.TypeInfo)Visit(context.expression());
            if (exprType != null && exprType.SimpleType != 4)
                syntaxError("Invalid type in an if-statement", context.IF().Symbol);

            symbolsTable.OpenScope();
            Visit(context.block());
            symbolsTable.CloseScope();

            Visit(context.ifStatement());
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks an <c>if expr { ... } else { ... }</c> statement, opening
    /// a separate scope for each branch.
    /// </summary>
    public override object VisitElseBlockIfStatement(MiniGoCompilerParser.ElseBlockIfStatementContext context)
    {
        try
        {
            SymbolsTable.TypeInfo exprType = (SymbolsTable.TypeInfo)Visit(context.expression());
            if (exprType != null && exprType.SimpleType != 4)
                syntaxError("Invalid type in an if-statement", context.IF().Symbol);

            symbolsTable.OpenScope();
            Visit(context.block(0)); // bloque del if
            symbolsTable.CloseScope();

            symbolsTable.OpenScope();
            Visit(context.block(1)); // bloque del else
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks the form <c>if init; expr { ... }</c>. Opens a scope that
    /// covers both the init-statement and the body so that any declarations
    /// inside the init are visible to the condition and body.
    /// </summary>
    public override object VisitSimpleIfStatement(MiniGoCompilerParser.SimpleIfStatementContext context)
    {
        try
        {
            symbolsTable.OpenScope();
            Visit(context.simpleStatement());

            SymbolsTable.TypeInfo exprType = (SymbolsTable.TypeInfo)Visit(context.expression());
            if (exprType != null && exprType.SimpleType != 4)
                syntaxError("Invalid type in an if-statement", context.IF().Symbol);

            Visit(context.block());
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks the form <c>if init; expr { ... } else if ...</c>,
    /// combining init-scope handling with recursion into the chained else-if.
    /// </summary>
    public override object VisitSimpleElseIfStatement(MiniGoCompilerParser.SimpleElseIfStatementContext context)
    {
        try
        {
            symbolsTable.OpenScope();
            Visit(context.simpleStatement());

            SymbolsTable.TypeInfo exprType = (SymbolsTable.TypeInfo)Visit(context.expression());
            if (exprType != null && exprType.SimpleType != 4)
                syntaxError("Invalid type in an if-statement", context.IF().Symbol);

            Visit(context.block());
            symbolsTable.CloseScope();

            Visit(context.ifStatement());
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks the form <c>if init; expr { ... } else { ... }</c>. The
    /// init-statement and condition share a scope with the then-branch; the
    /// else-branch opens its own scope independent of the init bindings.
    /// </summary>
    public override object VisitSimpleElseBlockIfStatement(
        MiniGoCompilerParser.SimpleElseBlockIfStatementContext context)
    {
        try
        {
            symbolsTable.OpenScope();
            Visit(context.simpleStatement());

            SymbolsTable.TypeInfo exprType = (SymbolsTable.TypeInfo)Visit(context.expression());
            if (exprType != null && exprType.SimpleType != 4)
                syntaxError("Invalid type in an if-statement", context.IF().Symbol);

            Visit(context.block(0));
            symbolsTable.CloseScope();

            symbolsTable.OpenScope();
            Visit(context.block(1));
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    // -------------------------------------------------------------------------
    //  Loops
    // -------------------------------------------------------------------------

    /// <summary>
    /// Type-checks an infinite <c>for { ... }</c> loop. Opens a scope for the
    /// body and closes it on exit.
    /// </summary>
    public override object VisitInfiniteLoop(MiniGoCompilerParser.InfiniteLoopContext context)
    {
        symbolsTable.OpenScope();
        Visit(context.block());
        symbolsTable.CloseScope();
        return null;
    }

    /// <summary>
    /// Type-checks a condition-only <c>for expr { ... }</c> loop, requiring
    /// the condition to be of type <c>bool</c>.
    /// </summary>
    public override object VisitConditionLoop(MiniGoCompilerParser.ConditionLoopContext context)
    {
        SymbolsTable.TypeInfo exprType = (SymbolsTable.TypeInfo)Visit(context.expression());
        if (exprType != null && exprType.SimpleType != 4)
        {
            syntaxError("Invalid type in an for-statement", context.FOR().Symbol);
        }

        symbolsTable.OpenScope();
        Visit(context.block());
        symbolsTable.CloseScope();

        return null;
    }

    /// <summary>
    /// Type-checks a complete C-style <c>for init; cond; post { ... }</c>
    /// loop. The opened scope covers init, condition, post-statement, and the
    /// body so that loop variables declared in the init are visible everywhere.
    /// </summary>
    public override object VisitCompleteForLoop(MiniGoCompilerParser.CompleteForLoopContext context)
    {
        try
        {
            symbolsTable.OpenScope();
            Visit(context.simpleStatement(0));

            SymbolsTable.TypeInfo exprType = (SymbolsTable.TypeInfo)Visit(context.expression());
            if (exprType != null && exprType.SimpleType != 4)
            {
                syntaxError("Invalid type in an for-statement", context.FOR().Symbol);
            }

            Visit(context.simpleStatement(1));
            Visit(context.block());
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks a <c>for init; ; post { ... }</c> loop (no condition). The
    /// loop runs until an internal <c>break</c>; init and post share the body's scope.
    /// </summary>
    public override object VisitNoConditionForLoop(MiniGoCompilerParser.NoConditionForLoopContext context)
    {
        try
        {
            symbolsTable.OpenScope();
            Visit(context.simpleStatement(0));
            Visit(context.simpleStatement(1));
            Visit(context.block());
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    // -------------------------------------------------------------------------
    //  Switch statements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Type-checks <c>switch init; expr { ... }</c>. The init-statement and
    /// switch expression share the case-clause-list scope.
    /// </summary>
    public override object VisitSimpleExpressionSwitch(MiniGoCompilerParser.SimpleExpressionSwitchContext context)
    {
        try
        {
            symbolsTable.OpenScope();
            Visit(context.simpleStatement());
            Visit(context.expression());
            Visit(context.expressionCaseClauseList());
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks <c>switch expr { ... }</c> (no init-statement). The
    /// expression and case clauses share a single scope.
    /// </summary>
    public override object VisitExpressionSwitch(MiniGoCompilerParser.ExpressionSwitchContext context)
    {
        try
        {
            symbolsTable.OpenScope();
            Visit(context.expression());
            Visit(context.expressionCaseClauseList());
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks <c>switch init; { ... }</c> (init-statement, no scrutinee
    /// expression). The init-statement's bindings are visible in all clauses.
    /// </summary>
    public override object VisitSimpleSwitch(MiniGoCompilerParser.SimpleSwitchContext context)
    {
        try
        {
            symbolsTable.OpenScope();
            Visit(context.simpleStatement());
            Visit(context.expressionCaseClauseList());
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks <c>switch { ... }</c> (no init, no scrutinee). Each
    /// <c>case</c> behaves like a chained <c>if</c> with a boolean predicate.
    /// </summary>
    public override object VisitEmptySwitch(MiniGoCompilerParser.EmptySwitchContext context)
    {
        try
        {
            symbolsTable.OpenScope();
            Visit(context.expressionCaseClauseList());
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Visits each <c>case</c>/<c>default</c> clause in a switch statement.
    /// </summary>
    public override object VisitExpressionCaseClauseList(MiniGoCompilerParser.ExpressionCaseClauseListContext context)
    {
        if (context.expressionCaseClause() != null)
        {
            foreach (var clause in context.expressionCaseClause())
            {
                Visit(clause);
            }
        }

        return null;
    }

    /// <summary>
    /// Type-checks an individual switch clause: the case expression(s),
    /// followed by the body in its own freshly opened scope.
    /// </summary>
    public override object VisitExpressionCaseClause(MiniGoCompilerParser.ExpressionCaseClauseContext context)
    {
        try
        {
            Visit(context.expressionSwitchCase());
            symbolsTable.OpenScope();
            Visit(context.statementList());
            symbolsTable.CloseScope();
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// Type-checks the expression list attached to a <c>case</c> label by
    /// visiting each candidate expression.
    /// </summary>
    public override object VisitCaseSwitch(MiniGoCompilerParser.CaseSwitchContext context)
    {
        try
        {
            Visit(context.expressionList());
        }
        catch (TypeErrorException e)
        {
        }

        return null;
    }

    /// <summary>
    /// <c>default</c> labels carry no expressions; nothing to verify here.
    /// </summary>
    public override object VisitDefaultSwitch(MiniGoCompilerParser.DefaultSwitchContext context)
    {
        return null;
    }

    /// <summary>
    /// Resolves an identifier reference. Looks up the name in every visible
    /// scope, attaches the resolved declaration back to the parse-tree node
    /// for downstream phases, and returns the identifier's type.
    /// Reports an "Undefined identifier" error when the name is not bound.
    /// </summary>
    public override object VisitIdentifier(MiniGoCompilerParser.IdentifierContext context)
    {
        IToken token = context.IDENTIFIER().Symbol;
        SymbolsTable.Ident ident = symbolsTable.Search(token.Text);
        if (ident == null)
        {
            syntaxError("Undefined identifier", token);
            return null;
        }

        context.decl = ident.Decl;
        return ident.Type;
    }
}