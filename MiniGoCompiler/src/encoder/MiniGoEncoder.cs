using System.Diagnostics;
using System.Runtime.InteropServices;
using syntaxchecker.generated;
using System;
using LLVMSharp.Interop;
using MiniGoCompiler.typechecker;
using static LLVMSharp.Interop.LLVM;
using ArrayType = LLVMSharp.ArrayType;

namespace MiniGoCompiler.encoder;


public class MiniGoEncoder : MiniGoCompilerBaseVisitor<object>
{
    private LLVMModuleRef module;
    private LLVMBuilderRef builder;

    private LLVMTypeRef intType;
    private LLVMTypeRef floatType;
    private LLVMTypeRef runeType;
    private LLVMTypeRef boolType;
    private LLVMTypeRef stringType;
    private LLVMTypeRef array;
    
    private LLVMValueRef currentFunc;
    // Variable reference table: maps variable names to their LLVM alloca pointers
    private Dictionary<string, LLVMValueRef> referenceTable = new Dictionary<string, LLVMValueRef>();

// Variable type table: maps variable names to their LLVM types (needed for load instructions)
    private Dictionary<string, LLVMTypeRef> typeTable = new Dictionary<string, LLVMTypeRef>();

    public unsafe MiniGoEncoder()
    {
        module = LLVMModuleRef.CreateWithName("minigo");
        builder = module.Context.CreateBuilder();
        intType = Int32Type();
        floatType = DoubleType();
        
        
        runeType = Int8Type();
        boolType = Int1Type();
        stringType = PointerType(Int8Type(), 0);
    }

    /// <summary>El LLVM IR generado como texto.</summary>
    public string GeneratedIR { get; private set; } = "";
 
    /// <summary>Salida estándar del programa ejecutado.</summary>
    public string ProgramOutput { get; private set; } = "";
 
    /// <summary>Mensaje de error si algo falló en la generación/enlazado.</summary>
    public string ErrorMessage { get; private set; } = "";
 
    /// <summary>True si el programa compiló, enlazó y corrió sin problemas.</summary>
    public bool CompilationSuccess { get; private set; } = false;
    
    
       // =========================================================================
    //  VisitRoot — punto de entrada de la generación de código
    // =========================================================================
    public override unsafe object VisitRoot(MiniGoCompilerParser.RootContext context)
    {
        // --- 1. Inicializar LLVM para la arquitectura de esta máquina ---
        LLVM.InitializeNativeTarget();
        LLVM.InitializeNativeAsmPrinter();
        LLVM.InitializeNativeAsmParser();
 
        // --- 2. Crear el módulo y el builder ---
        this.module  = LLVMModuleRef.CreateWithName("minigo");
        this.builder = this.module.Context.CreateBuilder();
 
        // --- 3. Recorrer el árbol: acá se genera todo el IR ---
        Visit(context.topDeclarationList());
 
        // --- 4. Guardar el IR generado ---
        this.GeneratedIR = this.module.PrintToString();

        // --- 4b. Si el módulo no tiene funciones, no hay nada que emitir ---
        // EmitToFile en un módulo vacío causa un LLVM fatal error que mata el proceso.
        if (!this.GeneratedIR.Contains("define "))
        {
            this.CompilationSuccess = true;
            Cleanup();
            return null;
        }

        // --- 5. Verificar que el módulo esté bien armado ---
        if (!this.module.TryVerify(LLVMVerifierFailureAction.LLVMPrintMessageAction, out string verifyMsg))
        {
            this.ErrorMessage = "Módulo LLVM inválido: " + verifyMsg;
            Cleanup();
            return null;
        }

        // --- 6. Configurar el target de compilación ---
        string triple = LLVMTargetRef.DefaultTriple;
        this.module.Target = triple;
 
        LLVMTargetRef target = LLVMTargetRef.GetTargetFromTriple(triple);
 
        LLVMTargetMachineRef targetMachine = target.CreateTargetMachine(
            triple,
            "generic",
            "",
            LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
            LLVMRelocMode.LLVMRelocDefault,
            LLVMCodeModel.LLVMCodeModelDefault
        );
 
        LLVMTargetDataRef dataLayout = targetMachine.CreateTargetDataLayout();
        this.module.DataLayout = dataLayout.ToString();
 
        // --- 7. Crear directorio de salida ---
        Directory.CreateDirectory("output");
 
        // --- 8. Generar el archivo objeto ---
        string objFile = Path.Combine("output", "output.o");
        try
        {
            targetMachine.EmitToFile(this.module, objFile, LLVMCodeGenFileType.LLVMObjectFile);
        }
        catch (Exception e)
        {
            this.ErrorMessage = "Error generando el objeto: " + e.Message;
            DisposeTargetMachine(targetMachine);
            Cleanup();
            return null;
        }
 
        // --- 9. Detectar sistema operativo y sandbox ---
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool inFlatpak = !isWindows && (
            Environment.GetEnvironmentVariable("FLATPAK_ID") != null ||
            File.Exists("/.flatpak-info"));
 
        // --- 10. Enlazar con clang ---
        string exeFile = isWindows
            ? Path.Combine("output", "output.exe")
            : Path.Combine("output", "output");
 
        if (!LinkWithClang(objFile, exeFile, isWindows, inFlatpak))
        {
            DisposeTargetMachine(targetMachine);
            Cleanup();
            return null;
        }
 
        // --- 11. Ejecutar el programa y capturar la salida ---
        RunAndCapture(exeFile, isWindows, inFlatpak);
 
        // --- 12. Limpiar recursos de LLVM ---
        DisposeTargetMachine(targetMachine);
        Cleanup();
 
        return null;
    }
 
 
    // =========================================================================
    //  LinkWithClang — enlaza el .o con clang para producir el ejecutable
    // =========================================================================
    private bool LinkWithClang(string objFile, string exeFile, bool isWindows, bool inFlatpak)
    {
        string program;
        string args;
 
        if (!isWindows && inFlatpak)
        {
            program = "flatpak-spawn";
            args    = "--host clang " + objFile + " -o " + exeFile;
        }
        else
        {
            program = "clang";
            args    = objFile + " -o " + exeFile;
        }
 
        try
        {
            Process linker = new Process();
            linker.StartInfo.FileName               = program;
            linker.StartInfo.Arguments               = args;
            linker.StartInfo.UseShellExecute          = false;
            linker.StartInfo.RedirectStandardError    = true;  // capturar errores de clang
            linker.StartInfo.RedirectStandardOutput   = true;
            linker.Start();
 
            string stderr = linker.StandardError.ReadToEnd();
            linker.WaitForExit();
 
            if (linker.ExitCode != 0)
            {
                this.ErrorMessage = "El enlazado falló (código " + linker.ExitCode + "): " + stderr;
                return false;
            }
 
            return true;
        }
        catch (Exception e)
        {
            this.ErrorMessage = "No se pudo ejecutar clang: " + e.Message;
            return false;
        }
    }
 
 
    // =========================================================================
    //  RunAndCapture — ejecuta el binario y guarda su stdout en ProgramOutput
    // =========================================================================
    private void RunAndCapture(string exeFile, bool isWindows, bool inFlatpak)
    {
        string program;
        string args;
 
        if (!isWindows && inFlatpak)
        {
            program = "flatpak-spawn";
            args    = "--host ./" + exeFile;
        }
        else
        {
            program = isWindows ? exeFile : "./" + exeFile;
            args    = "";
        }
 
        try
        {
            Process run = new Process();
            run.StartInfo.FileName               = program;
            run.StartInfo.Arguments               = args;
            run.StartInfo.UseShellExecute          = false;
            run.StartInfo.RedirectStandardOutput   = true;   // capturar lo que imprime el programa
            run.StartInfo.RedirectStandardError    = true;
            run.Start();
 
            // leer la salida del programa compilado
            this.ProgramOutput = run.StandardOutput.ReadToEnd();
            string stderr      = run.StandardError.ReadToEnd();
            run.WaitForExit();
 
            if (run.ExitCode == 0)
            {
                this.CompilationSuccess = true;
            }
            else
            {
                this.ErrorMessage = "El programa terminó con código " + run.ExitCode;
                if (!string.IsNullOrEmpty(stderr))
                    this.ErrorMessage += ": " + stderr;
                // aún así guardamos el output parcial que haya dado
                this.CompilationSuccess = false;
            }
        }
        catch (Exception e)
        {
            this.ErrorMessage = "No se pudo ejecutar el programa: " + e.Message;
            this.CompilationSuccess = false;
        }
    }
 
 
    // =========================================================================
    //  Cleanup — libera el builder y el módulo
    // =========================================================================
    private unsafe void Cleanup()
    {
        DisposeBuilder(this.builder);
        DisposeModule(this.module);
    }


    public override object VisitTopDeclarationList(MiniGoCompilerParser.TopDeclarationListContext context)
    {
        foreach (var child in context.children)
        {
            Visit(child);
        }

        return null;
    }

    public unsafe override object VisitVariableDecl(MiniGoCompilerParser.VariableDeclContext context)
    {
        if (context.singleVarDecl() != null)
        {
            Visit(context.singleVarDecl());
        }

        if (context.innerVarDecls() != null)
        {
            Visit(context.innerVarDecls());
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

    // Add this field alongside referenceTable and typeTable
private Dictionary<string, LLVMTypeRef> userDefinedTypes = new Dictionary<string, LLVMTypeRef>();

private unsafe LLVMTypeRef ResolveLLVMType(MiniGoCompilerParser.DeclTypeContext ctx)
{
    // Simple/primitive types and user-defined type aliases
    if (ctx is MiniGoCompilerParser.TypeDenoterDeclTypeContext typeDenoter)
    {
        string name = typeDenoter.identifier().IDENTIFIER().GetText();
        return name switch
        {
            "int"     => intType,
            "float64" => floatType,
            "string"  => stringType,
            "rune"    => runeType,
            "bool"    => boolType,
            _         => userDefinedTypes.TryGetValue(name, out LLVMTypeRef resolved)
                         ? resolved
                         : intType
        };
    }

    // Parenthesized type: (T) → just unwrap
    if (ctx is MiniGoCompilerParser.GroupDeclTypeContext group)
        return ResolveLLVMType(group.declType());

    // Array type: [N]T → fixed-size LLVM array
    if (ctx is MiniGoCompilerParser.ArrayTypeDeclContext arrayCtx)
    {
        var arrayDecl = arrayCtx.arrayDeclType();
        uint size = uint.Parse(arrayDecl.INTLITERAL().GetText());
        LLVMTypeRef elementType = ResolveLLVMType(arrayDecl.declType());
        return ArrayType(elementType, size);
    }

    // Slice type: []T → struct { T*, i32 len, i32 cap }
    if (ctx is MiniGoCompilerParser.SliceTypeDeclContext sliceCtx)
    {
        var sliceDecl = sliceCtx.sliceDeclType();
        LLVMTypeRef elementType = ResolveLLVMType(sliceDecl.declType());
        LLVMTypeRef pointerToElement = PointerType(elementType, 0);
        LLVMTypeRef[] sliceFields = { pointerToElement, intType, intType };
        return LLVMTypeRef.CreateStruct(sliceFields, false);
    }

    // Struct type: struct { field1 T1; field2 T2; ... }
    if (ctx is MiniGoCompilerParser.StructTypeDeclContext structCtx)
    {
        var structDecl = structCtx.structDeclType();
        List<LLVMTypeRef> fieldTypes = new List<LLVMTypeRef>();

        if (structDecl.structMemDecls() != null)
        {
            foreach (var member in structDecl.structMemDecls().singleVarDeclNoExps())
            {
                LLVMTypeRef memberType = ResolveLLVMType(member.declType());
                foreach (var id in member.identifierList().IDENTIFIER())
                {
                    fieldTypes.Add(memberType);
                }
            }
        }
        return LLVMTypeRef.CreateStruct(fieldTypes.ToArray(), false);
    }

    return intType;
}

    public unsafe LLVMValueRef LoadVar(LLVMTypeRef type, LLVMValueRef ptr, string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + "\0"))
        {
            return BuildLoad2(builder, type, ptr, (sbyte*)p);
        }
    }
    public unsafe LLVMValueRef AllocaVar(LLVMTypeRef type, string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + "\0"))
        {
            return BuildAlloca(builder, type, (sbyte*)p);
        }
    }

public unsafe override object VisitTypedVarDecl(MiniGoCompilerParser.TypedVarDeclContext context)
    {
        LLVMTypeRef type = ResolveLLVMType((context.declType()));
        var identifiers = context.identifierList().IDENTIFIER();
        
        LinkedList<LLVMValueRef> values = (LinkedList<LLVMValueRef>) Visit(context.expressionList());
        for (int i = 0; i < identifiers.Length; i++)
        {
            string name = identifiers[i].Symbol.Text;
            LLVMValueRef alloca = AllocaVar(type, name);
            BuildStore(builder, values.ElementAt(i), alloca);
            referenceTable[name] = alloca;
            typeTable[name] = type;
        }
        return null;
    }


    public override object VisitInferredVarDecl(MiniGoCompilerParser.InferredVarDeclContext context)
    {
        return base.VisitInferredVarDecl(context);
    }

    public override object VisitNoExpressionVarDecl(MiniGoCompilerParser.NoExpressionVarDeclContext context)
    {
        return base.VisitNoExpressionVarDecl(context);
    }

    public override object VisitSingleVarDeclNoExps(MiniGoCompilerParser.SingleVarDeclNoExpsContext context)
    {
        return base.VisitSingleVarDeclNoExps(context);
    }

    public override object VisitTypeDecl(MiniGoCompilerParser.TypeDeclContext context)
    {
        return base.VisitTypeDecl(context);
    }

    public override object VisitInnerTypeDecls(MiniGoCompilerParser.InnerTypeDeclsContext context)
    {
        return base.VisitInnerTypeDecls(context);
    }
    
    public override object VisitSingleTypeDecl(MiniGoCompilerParser.SingleTypeDeclContext context)
    {
        LLVMTypeRef resolved = ResolveLLVMType(context.declType());
        string name = context.IDENTIFIER().GetText();
        userDefinedTypes[name] = resolved;
        return null;
    }

    public override object VisitFuncDecl(MiniGoCompilerParser.FuncDeclContext context)
    {
        return base.VisitFuncDecl(context);
    }

    public override object VisitFuncFrontDecl(MiniGoCompilerParser.FuncFrontDeclContext context)
    {
        return base.VisitFuncFrontDecl(context);
    }

    public override object VisitFuncArgDecls(MiniGoCompilerParser.FuncArgDeclsContext context)
    {
        return base.VisitFuncArgDecls(context);
    }

    public override object VisitGroupDeclType(MiniGoCompilerParser.GroupDeclTypeContext context)
    {
        return base.VisitGroupDeclType(context);
    }

    public override object VisitTypeDenoterDeclType(MiniGoCompilerParser.TypeDenoterDeclTypeContext context)
    {
        return base.VisitTypeDenoterDeclType(context);
    }

    public override object VisitSliceTypeDecl(MiniGoCompilerParser.SliceTypeDeclContext context)
    {
        return base.VisitSliceTypeDecl(context);
    }

    public override object VisitArrayTypeDecl(MiniGoCompilerParser.ArrayTypeDeclContext context)
    {
        return base.VisitArrayTypeDecl(context);
    }

    public override object VisitStructTypeDecl(MiniGoCompilerParser.StructTypeDeclContext context)
    {
        return base.VisitStructTypeDecl(context);
    }

    public override object VisitSliceDeclType(MiniGoCompilerParser.SliceDeclTypeContext context)
    {
        return base.VisitSliceDeclType(context);
    }

    public override object VisitArrayDeclType(MiniGoCompilerParser.ArrayDeclTypeContext context)
    {
        return base.VisitArrayDeclType(context);
    }

    public override object VisitStructDeclType(MiniGoCompilerParser.StructDeclTypeContext context)
    {
        return base.VisitStructDeclType(context);
    }

    public override object VisitStructMemDecls(MiniGoCompilerParser.StructMemDeclsContext context)
    {
        return base.VisitStructMemDecls(context);
    }

    public override object VisitIdentifierList(MiniGoCompilerParser.IdentifierListContext context)
    {
        return base.VisitIdentifierList(context);
    }

    public override object VisitExpressionList(MiniGoCompilerParser.ExpressionListContext context)
    {
        LinkedList<LLVMValueRef> values = new LinkedList<LLVMValueRef>();
        foreach (var expr in context.expression())
        {
            LLVMValueRef val = (LLVMValueRef)Visit(expr);
            values.AddLast(val);
        }

        return values;
    }

    public override object VisitPrimaryExpr(MiniGoCompilerParser.PrimaryExprContext context)
    {
        return base.VisitPrimaryExpr(context);
    }

    public override object VisitUnarySubExpr(MiniGoCompilerParser.UnarySubExprContext context)
    {
        return base.VisitUnarySubExpr(context);
    }

    public override object VisitAddExpr(MiniGoCompilerParser.AddExprContext context)
    {
        return base.VisitAddExpr(context);
    }

    public override object VisitMulExpr(MiniGoCompilerParser.MulExprContext context)
    {
        return base.VisitMulExpr(context);
    }

    public override object VisitOrExpr(MiniGoCompilerParser.OrExprContext context)
    {
        return base.VisitOrExpr(context);
    }

    public override object VisitUnaryHatExpr(MiniGoCompilerParser.UnaryHatExprContext context)
    {
        return base.VisitUnaryHatExpr(context);
    }

    public override object VisitUnaryAddExpr(MiniGoCompilerParser.UnaryAddExprContext context)
    {
        return base.VisitUnaryAddExpr(context);
    }

    public override object VisitRelExpr(MiniGoCompilerParser.RelExprContext context)
    {
        return base.VisitRelExpr(context);
    }

    public override object VisitUnaryNotExpr(MiniGoCompilerParser.UnaryNotExprContext context)
    {
        return base.VisitUnaryNotExpr(context);
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

    public override object VisitIdentifier(MiniGoCompilerParser.IdentifierContext context)
    {
        return base.VisitIdentifier(context);
    }
}