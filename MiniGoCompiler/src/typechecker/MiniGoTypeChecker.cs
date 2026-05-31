using System.Net.Mail;
using Antlr4.Runtime;
using syntaxchecker.generated;

namespace MiniGoCompiler.typechecker;


public class MiniGoTypeChecker : MiniGoCompilerBaseVisitor<object>

{
    public SymbolsTable symbolsTable;
    public LinkedList<String> errorList;
    private Stack<int> stack = new Stack<int>();
    private Stack<bool> stackBool = new Stack<bool>();


    public MiniGoTypeChecker()
    {
        this.symbolsTable = new SymbolsTable();
        this.errorList = new LinkedList<string>();
    }
    
    public bool hasErrors => this.errorList.Count > 0;

    public void printErrors()
    {
        if (this.errorList.Count != 0)
        {
            Console.WriteLine("Compilatio failed");
            foreach (string error in this.errorList)
            {
                Console.WriteLine(error);
            }
        }
        else
        {
            Console.WriteLine("Compilatio succeeded");
        }
    }
    
    private void syntaxError(string msg, IToken offendingToken) {
        string error = "TYPE ERROR: " + msg + ": (" + offendingToken.Text + ") " + " in [line " + offendingToken.Line + ": " + "Column " + offendingToken.Column + "]";
        this.errorList.AddFirst(error);
    }

    // reporte de error cuando hay dos tipos incompatibles (ej: int y string)
    private void syntaxError(string msg, IToken offendingToken, SymbolsTable.TypeInfo type1, SymbolsTable.TypeInfo  type2) {
        string error = "TYPE ERROR: " + msg + " " + type1.Category+ " and " + type2.Category + ": (" + offendingToken.Text + ") " + " in [line " + offendingToken.Line + ": " + "Column " + offendingToken.Column + "]";
        this.errorList.AddFirst(error);
    }
    

    public override object VisitRoot(MiniGoCompilerParser.RootContext context)
    {
        symbolsTable.OpenScope();
        Visit(context.topDeclarationList());
        symbolsTable.CloseScope();
        return null;

    }

    public override object VisitTopDeclarationList(MiniGoCompilerParser.TopDeclarationListContext context)
    {
        foreach (var child in context.children)
        {
            Visit(child);
        }
        return null;
    }

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

    public override object VisitInnerVarDecls(MiniGoCompilerParser.InnerVarDeclsContext context)
    {
        foreach (MiniGoCompilerParser.SingleVarDeclContext svd in context.singleVarDecl())
        {
            Visit(svd);
        }
        return null;
    }
    
    


    public override object VisitTypedVarDecl(MiniGoCompilerParser.TypedVarDeclContext context)
    {
        try
        {
            SymbolsTable.TypeInfo declaredType = (SymbolsTable.TypeInfo)Visit(context.declType());
            var identList = context.identifierList().IDENTIFIER();
            LinkedList<SymbolsTable.TypeInfo> exprTypes = (LinkedList<SymbolsTable.TypeInfo>)Visit(context.expressionList());
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
                    if (declaredType.Equals(exprType)!)
                    {
                        syntaxError("Invalid types in assign ", token, declaredType, exprType);
                    }
                    symbolsTable.InsertVariableLevel(token, declaredType, symbolsTable.GetActualLevel(), context);
                }
            }
            context.decl = context;
        } catch (TypeErrorException e){}

        return null;
    }
        

    public override object VisitInferredVarDecl(MiniGoCompilerParser.InferredVarDeclContext context)
    {
        try
        {
            LinkedList<SymbolsTable.TypeInfo> exprTypes = (LinkedList<SymbolsTable.TypeInfo>)Visit(context.expressionList());
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
                else
                {
                    symbolsTable.InsertVariableLevel(token, exprType, symbolsTable.GetActualLevel(), context);
                    
                }

            }
            
        } catch (TypeErrorException e){}
        context.decl = context;

        return null;
    }

    public override object VisitNoExpressionVarDecl(MiniGoCompilerParser.NoExpressionVarDeclContext context)
    {
        return Visit(context.singleVarDeclNoExps());
    }

    public override object VisitSingleVarDeclNoExps(MiniGoCompilerParser.SingleVarDeclNoExpsContext context)
    {
        try {
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
                symbolsTable.InsertVariableLevel(token, declaredType, symbolsTable.GetActualLevel(),context);
            }
        }
        context.decl = context;
        } catch (TypeErrorException e){}
        return null; 
    }

    public override object VisitTypeDecl(MiniGoCompilerParser.TypeDeclContext context)
    {
        try {
        if (context.singleTypeDecl() != null)
        {
            Visit(context.singleTypeDecl());
        }
        if (context.innerTypeDecls() != null)
        {
            Visit(context.innerTypeDecls());
        }
        } catch (TypeErrorException e){}
        return null;
    }

    public override object VisitInnerTypeDecls(MiniGoCompilerParser.InnerTypeDeclsContext context)
    {
        try
        {
            foreach (var std in context.singleTypeDecl())
            {
                Visit(std);
            }
        } catch  (TypeErrorException e){}
        return null;
    }

    public override object VisitSingleTypeDecl(MiniGoCompilerParser.SingleTypeDeclContext context)
    {
        try
        {
            SymbolsTable.TypeInfo declaredType = (SymbolsTable.TypeInfo) Visit(context.declType());
            SymbolsTable.Ident ident = symbolsTable.SearchActualLevel(context.IDENTIFIER().GetText());
            if (ident != null)
            {
                syntaxError("Type already declared", context.IDENTIFIER().Symbol);
            }
            else
            {
                symbolsTable.InsertTypeLevel(context.IDENTIFIER().Symbol, declaredType,  symbolsTable.GetActualLevel(), context);
            }
        } catch (TypeErrorException e){}

        return null;
    }

    public override object VisitFuncDecl(MiniGoCompilerParser.FuncDeclContext context)
    { 
        try
        {
            var front = context.funcFrontDecl();
            SymbolsTable.TypeInfo returnType = null;
            if (front.declType() != null)
                returnType = (SymbolsTable.TypeInfo) Visit(front.declType());
            
            SymbolsTable.Ident ident = symbolsTable.SearchActualLevel(front.IDENTIFIER().GetText());
            if (ident != null)
                syntaxError("Function already declared", front.IDENTIFIER().Symbol);
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
                Visit(front.funcArgDecls());
            Visit(context.block());
            symbolsTable.CloseScope();
            context.decl = context;
        }
        catch (TypeErrorException e) { }

        return null;
    }

    public override object VisitFuncFrontDecl(MiniGoCompilerParser.FuncFrontDeclContext context)
    {
        return null;
    }

    public override object VisitFuncArgDecls(MiniGoCompilerParser.FuncArgDeclsContext context)
    {
        foreach (var fad in context.singleVarDeclNoExps())
        {
            Visit(fad);
        }
        return null;
    }

    public override object VisitGroupDeclType(MiniGoCompilerParser.GroupDeclTypeContext context)
    {
        SymbolsTable.TypeInfo declareType =  (SymbolsTable.TypeInfo) Visit(context.declType());
        return declareType;
    }

    public override object VisitTypeDenoterDeclType(MiniGoCompilerParser.TypeDenoterDeclTypeContext context)
    {
        string typeName = context.identifier().IDENTIFIER().GetText();
    
        switch (typeName)
        {
            case "int":
                return new SymbolsTable.TypeInfo("simple", 0, 0, null, null);
            case "float64":
                return new SymbolsTable.TypeInfo("simple", 1, 0, null, null);
            case "string":
                return new SymbolsTable.TypeInfo("simple", 2, 0, null, null);
            case "rune":
                return new SymbolsTable.TypeInfo("simple", 3, 0, null, null);
            case "bool":
                return new SymbolsTable.TypeInfo("simple", 4, 0, null, null);
            default:
                SymbolsTable.Ident ident = symbolsTable.Search(typeName);
                if (ident != null) {
                    return ident.Type;
                }
                syntaxError("Undefined type", context.identifier().IDENTIFIER().Symbol);
                return null;
        }
    }

    public override object VisitSliceTypeDecl(MiniGoCompilerParser.SliceTypeDeclContext context)
    {
        SymbolsTable.TypeInfo declareType = (SymbolsTable.TypeInfo) Visit(context.sliceDeclType().declType());
        SymbolsTable.TypeInfo slice =new SymbolsTable.TypeInfo("slice", -1, -1, declareType, null);
        return slice;
    }

    public override object VisitArrayTypeDecl(MiniGoCompilerParser.ArrayTypeDeclContext context)
    {
        SymbolsTable.TypeInfo declareType = (SymbolsTable.TypeInfo) Visit(context.arrayDeclType().declType());
        int size = int.Parse(context.arrayDeclType().INTLITERAL().GetText());
        SymbolsTable.TypeInfo array =new SymbolsTable.TypeInfo("array", -1, size , declareType, null);
        return array;
    }

    public override object VisitStructTypeDecl(MiniGoCompilerParser.StructTypeDeclContext context)
    {
        Dictionary<string, SymbolsTable.TypeInfo> fields = new Dictionary<string, SymbolsTable.TypeInfo>();
    
        if (context.structDeclType().structMemDecls() != null)
            fields = (Dictionary<string, SymbolsTable.TypeInfo>)Visit(context.structDeclType().structMemDecls());
    
        return new SymbolsTable.TypeInfo("struct", -1, -1, null, fields);
    }

    public override object VisitSliceDeclType(MiniGoCompilerParser.SliceDeclTypeContext context)
    {
        return null;
    }

    public override object VisitArrayDeclType(MiniGoCompilerParser.ArrayDeclTypeContext context)
    {
        return null;
    }

    public override object VisitStructDeclType(MiniGoCompilerParser.StructDeclTypeContext context)
    {
        return null;
    }

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

    public override object VisitIdentifierList(MiniGoCompilerParser.IdentifierListContext context)
    {
        return null;
    }

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

    public override object VisitPrimaryExpr(MiniGoCompilerParser.PrimaryExprContext context)
    {
        return Visit(context.primaryExpression());
    }

    public override object VisitUnarySubExpr(MiniGoCompilerParser.UnarySubExprContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo) Visit(context.expression());
        if (type != null && type.Category == "simple" && type.SimpleType != 0 && type.SimpleType != 1)
        {
            syntaxError("Cannot apply '-' to non-numeric type", context.SUB().Symbol);
        }
        return type;
    }

    public override object VisitAddExpr(MiniGoCompilerParser.AddExprContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));
        IToken op = (context.ADD() ?? context.SUB() ?? context.OR() ?? context.HAT()).Symbol;
    
        if (!(left == right))
        {
            syntaxError("Incompatible types in expression", op);
        }

        if ((context.HAT() != null || context.OR() != null) && left.SimpleType != 0)
        {
            syntaxError("Bitwise operator requires integer type", op);
        }
        return left;
    }

    public override object VisitMulExpr(MiniGoCompilerParser.MulExprContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));
        IToken op = (context.MUL() ?? context.DIV() ?? context.MOD() ?? context.DLESS() ?? context.DMORE() ?? context.AND() ?? context.ANDHAT()).Symbol;
        if (left != right)
        {
            syntaxError("Incompatible types in expression", op);
        }
        if ((context.ANDHAT() != null || context.MOD() != null || context.AND() != null
             || context.DLESS() != null || context.DMORE() != null) && left.SimpleType != 0)
        {
            syntaxError("Bitwise operator requires integer type", op);
        }
        if ((context.MUL() !=  null || context.DIV() != null ) && right.SimpleType != 0 && right.SimpleType != 1)
        {
            syntaxError("Operation requires numeric type", op);
        }
        return left;
        
    }

    public override object VisitOrExpr(MiniGoCompilerParser.OrExprContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));
        IToken op = context.DOR().Symbol;

        if (left.SimpleType != 4 || right.SimpleType != 4)
        {
            syntaxError("Logical operator requires boolean type", op);
        }
        return left;
    }

    public override object VisitUnaryHatExpr(MiniGoCompilerParser.UnaryHatExprContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo)Visit(context.expression());
        if (type != null && type.SimpleType != 0)
        {
            syntaxError("Cannot apply '^' to non-integer type", context.HAT().Symbol);
        }
        return type;
    }

    public override object VisitUnaryAddExpr(MiniGoCompilerParser.UnaryAddExprContext context)
    {
        return base.VisitUnaryAddExpr(context);
    }

    public override object VisitRelExpr(MiniGoCompilerParser.RelExprContext context)
    {
        SymbolsTable.TypeInfo left = (SymbolsTable.TypeInfo)Visit(context.expression(0));
        SymbolsTable.TypeInfo right = (SymbolsTable.TypeInfo)Visit(context.expression(1));
        IToken op = (context.EQEQ() ?? context.NOTEQ() ?? context.LESS() ?? context.LESSEQ() ?? context.MORET() ?? context.MOREEQ()).Symbol;
        if (left != right)
        {
            syntaxError("Incompatible types in expression", op);
        }
        if ((context.LESS() != null || context.LESSEQ() != null || context.MORET() != null || context.MOREEQ() != null) 
            && left.SimpleType == 4)
        {
            syntaxError("Cannot compare bool with ordering operator", op);
        }

        return new SymbolsTable.TypeInfo("simple", 4, 0, null, null);
        
        
    }

    public override object VisitUnaryNotExpr(MiniGoCompilerParser.UnaryNotExprContext context)
    {
        SymbolsTable.TypeInfo type = (SymbolsTable.TypeInfo)Visit(context.expression());
        if (type != null && type.SimpleType != 4)
        {
            syntaxError("Cannot apply '!' to non-bool type", context.NOT().Symbol);
        }
        return type;
    }

    public override object VisitAndExpr(MiniGoCompilerParser.AndExprContext context)
    {
        return base.VisitAndExpr(context);
    }

    public override object VisitLengthPrimaryExpr(MiniGoCompilerParser.LengthPrimaryExprContext context)
    {
        return base.VisitLengthPrimaryExpr(context);
    }

    public override object VisitOperandPrimaryExpr(MiniGoCompilerParser.OperandPrimaryExprContext context)
    {
        return base.VisitOperandPrimaryExpr(context);
    }

    public override object VisitAppendPrimaryExpr(MiniGoCompilerParser.AppendPrimaryExprContext context)
    {
        return base.VisitAppendPrimaryExpr(context);
    }

    public override object VisitIndexPrimaryExpr(MiniGoCompilerParser.IndexPrimaryExprContext context)
    {
        return base.VisitIndexPrimaryExpr(context);
    }

    public override object VisitSelectorPrimaryExpr(MiniGoCompilerParser.SelectorPrimaryExprContext context)
    {
        return base.VisitSelectorPrimaryExpr(context);
    }

    public override object VisitArgumentsPrimaryExpr(MiniGoCompilerParser.ArgumentsPrimaryExprContext context)
    {
        return base.VisitArgumentsPrimaryExpr(context);
    }

    public override object VisitCapPrimaryExpr(MiniGoCompilerParser.CapPrimaryExprContext context)
    {
        return base.VisitCapPrimaryExpr(context);
    }
    

    public override object VisitLiteralOperand(MiniGoCompilerParser.LiteralOperandContext context)
    {
        return base.VisitLiteralOperand(context);
    }

    public override object VisitIdOperand(MiniGoCompilerParser.IdOperandContext context)
    {
        return base.VisitIdOperand(context);
    }

    public override object VisitGroupOperand(MiniGoCompilerParser.GroupOperandContext context)
    {
        return base.VisitGroupOperand(context);
    }

    public override object VisitIntLiteral(MiniGoCompilerParser.IntLiteralContext context)
    {
        return base.VisitIntLiteral(context);
    }

    public override object VisitFloatLiteral(MiniGoCompilerParser.FloatLiteralContext context)
    {
        return base.VisitFloatLiteral(context);
    }

    public override object VisitRuneLiteral(MiniGoCompilerParser.RuneLiteralContext context)
    {
        return base.VisitRuneLiteral(context);
    }

    public override object VisitRawStringLiteral(MiniGoCompilerParser.RawStringLiteralContext context)
    {
        return base.VisitRawStringLiteral(context);
    }

    public override object VisitInterpretedStringLiteral(MiniGoCompilerParser.InterpretedStringLiteralContext context)
    {
        return base.VisitInterpretedStringLiteral(context);
    }

    public override object VisitIndex(MiniGoCompilerParser.IndexContext context)
    {
        return base.VisitIndex(context);
    }

    public override object VisitArguments(MiniGoCompilerParser.ArgumentsContext context)
    {
        return base.VisitArguments(context);
    }

    public override object VisitSelector(MiniGoCompilerParser.SelectorContext context)
    {
        return base.VisitSelector(context);
    }

    public override object VisitAppendExpression(MiniGoCompilerParser.AppendExpressionContext context)
    {
        return base.VisitAppendExpression(context);
    }

    public override object VisitLengthExpression(MiniGoCompilerParser.LengthExpressionContext context)
    {
        return base.VisitLengthExpression(context);
    }

    public override object VisitCapExpression(MiniGoCompilerParser.CapExpressionContext context)
    {
        return base.VisitCapExpression(context);
    }

    public override object VisitStatementList(MiniGoCompilerParser.StatementListContext context)
    {
        return base.VisitStatementList(context);
    }

    public override object VisitBlock(MiniGoCompilerParser.BlockContext context)
    {
        return base.VisitBlock(context);
    }

    public override object VisitPrintStatement(MiniGoCompilerParser.PrintStatementContext context)
    {
        return base.VisitPrintStatement(context);
    }

    public override object VisitPrintlnStatement(MiniGoCompilerParser.PrintlnStatementContext context)
    {
        return base.VisitPrintlnStatement(context);
    }

    public override object VisitReturnStatement(MiniGoCompilerParser.ReturnStatementContext context)
    {
        return base.VisitReturnStatement(context);
    }

    public override object VisitBreakStatement(MiniGoCompilerParser.BreakStatementContext context)
    {
        return base.VisitBreakStatement(context);
    }

    public override object VisitContinueStatement(MiniGoCompilerParser.ContinueStatementContext context)
    {
        return base.VisitContinueStatement(context);
    }

    public override object VisitSimpleStmtStatement(MiniGoCompilerParser.SimpleStmtStatementContext context)
    {
        return base.VisitSimpleStmtStatement(context);
    }

    public override object VisitBlockStatement(MiniGoCompilerParser.BlockStatementContext context)
    {
        return base.VisitBlockStatement(context);
    }

    public override object VisitSwitchStatement(MiniGoCompilerParser.SwitchStatementContext context)
    {
        return base.VisitSwitchStatement(context);
    }

    public override object VisitIfStmtStatement(MiniGoCompilerParser.IfStmtStatementContext context)
    {
        return base.VisitIfStmtStatement(context);
    }

    public override object VisitLoopStatement(MiniGoCompilerParser.LoopStatementContext context)
    {
        return base.VisitLoopStatement(context);
    }

    public override object VisitTypeDeclStatement(MiniGoCompilerParser.TypeDeclStatementContext context)
    {
        return base.VisitTypeDeclStatement(context);
    }

    public override object VisitVariableDeclStatement(MiniGoCompilerParser.VariableDeclStatementContext context)
    {
        return base.VisitVariableDeclStatement(context);
    }

    public override object VisitEmptySimpleStatement(MiniGoCompilerParser.EmptySimpleStatementContext context)
    {
        return base.VisitEmptySimpleStatement(context);
    }

    public override object VisitExpressionSimpleStatement(MiniGoCompilerParser.ExpressionSimpleStatementContext context)
    {
        return base.VisitExpressionSimpleStatement(context);
    }

    public override object VisitAssignmentSimpleStatement(MiniGoCompilerParser.AssignmentSimpleStatementContext context)
    {
        return base.VisitAssignmentSimpleStatement(context);
    }

    public override object VisitDeclareSimpleStatement(MiniGoCompilerParser.DeclareSimpleStatementContext context)
    {
        return base.VisitDeclareSimpleStatement(context);
    }

    public override object VisitEqualAssignment(MiniGoCompilerParser.EqualAssignmentContext context)
    {
        return base.VisitEqualAssignment(context);
    }

    public override object VisitAddAssignment(MiniGoCompilerParser.AddAssignmentContext context)
    {
        return base.VisitAddAssignment(context);
    }

    public override object VisitAndAssignment(MiniGoCompilerParser.AndAssignmentContext context)
    {
        return base.VisitAndAssignment(context);
    }

    public override object VisitSubAssignment(MiniGoCompilerParser.SubAssignmentContext context)
    {
        return base.VisitSubAssignment(context);
    }

    public override object VisitOrAssignment(MiniGoCompilerParser.OrAssignmentContext context)
    {
        return base.VisitOrAssignment(context);
    }

    public override object VisitMulAssignment(MiniGoCompilerParser.MulAssignmentContext context)
    {
        return base.VisitMulAssignment(context);
    }

    public override object VisitHatAssignment(MiniGoCompilerParser.HatAssignmentContext context)
    {
        return base.VisitHatAssignment(context);
    }

    public override object VisitDlessAssignment(MiniGoCompilerParser.DlessAssignmentContext context)
    {
        return base.VisitDlessAssignment(context);
    }

    public override object VisitDmoreAssignment(MiniGoCompilerParser.DmoreAssignmentContext context)
    {
        return base.VisitDmoreAssignment(context);
    }

    public override object VisitAndHatAssignment(MiniGoCompilerParser.AndHatAssignmentContext context)
    {
        return base.VisitAndHatAssignment(context);
    }

    public override object VisitModAssignment(MiniGoCompilerParser.ModAssignmentContext context)
    {
        return base.VisitModAssignment(context);
    }

    public override object VisitDivAssignment(MiniGoCompilerParser.DivAssignmentContext context)
    {
        return base.VisitDivAssignment(context);
    }

    public override object VisitNormalIfStatement(MiniGoCompilerParser.NormalIfStatementContext context)
    {
        return base.VisitNormalIfStatement(context);
    }

    public override object VisitElseIfStatement(MiniGoCompilerParser.ElseIfStatementContext context)
    {
        return base.VisitElseIfStatement(context);
    }

    public override object VisitElseBlockIfStatement(MiniGoCompilerParser.ElseBlockIfStatementContext context)
    {
        return base.VisitElseBlockIfStatement(context);
    }

    public override object VisitSimpleIfStatement(MiniGoCompilerParser.SimpleIfStatementContext context)
    {
        return base.VisitSimpleIfStatement(context);
    }

    public override object VisitSimpleElseIfStatement(MiniGoCompilerParser.SimpleElseIfStatementContext context)
    {
        return base.VisitSimpleElseIfStatement(context);
    }

    public override object VisitSimpleElseBlockIfStatement(MiniGoCompilerParser.SimpleElseBlockIfStatementContext context)
    {
        return base.VisitSimpleElseBlockIfStatement(context);
    }

    public override object VisitInfiniteLoop(MiniGoCompilerParser.InfiniteLoopContext context)
    {
        return base.VisitInfiniteLoop(context);
    }

    public override object VisitConditionLoop(MiniGoCompilerParser.ConditionLoopContext context)
    {
        return base.VisitConditionLoop(context);
    }

    public override object VisitCompleteForLoop(MiniGoCompilerParser.CompleteForLoopContext context)
    {
        return base.VisitCompleteForLoop(context);
    }

    public override object VisitNoConditionForLoop(MiniGoCompilerParser.NoConditionForLoopContext context)
    {
        return base.VisitNoConditionForLoop(context);
    }

    public override object VisitSimpleExpressionSwitch(MiniGoCompilerParser.SimpleExpressionSwitchContext context)
    {
        return base.VisitSimpleExpressionSwitch(context);
    }

    public override object VisitExpressionSwitch(MiniGoCompilerParser.ExpressionSwitchContext context)
    {
        return base.VisitExpressionSwitch(context);
    }

    public override object VisitSimpleSwitch(MiniGoCompilerParser.SimpleSwitchContext context)
    {
        return base.VisitSimpleSwitch(context);
    }

    public override object VisitEmptySwitch(MiniGoCompilerParser.EmptySwitchContext context)
    {
        return base.VisitEmptySwitch(context);
    }

    public override object VisitExpressionCaseClauseList(MiniGoCompilerParser.ExpressionCaseClauseListContext context)
    {
        return base.VisitExpressionCaseClauseList(context);
    }

    public override object VisitExpressionCaseClause(MiniGoCompilerParser.ExpressionCaseClauseContext context)
    {
        return base.VisitExpressionCaseClause(context);
    }

    public override object VisitCaseSwitch(MiniGoCompilerParser.CaseSwitchContext context)
    {
        return base.VisitCaseSwitch(context);
    }

    public override object VisitDefaultSwitch(MiniGoCompilerParser.DefaultSwitchContext context)
    {
        return base.VisitDefaultSwitch(context);
    }
}