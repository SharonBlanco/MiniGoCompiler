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
    
    private LLVMValueRef currentFunc;
    // Variable reference table: maps variable names to their LLVM alloca pointers
    private Dictionary<string, LLVMValueRef> referenceTable = new Dictionary<string, LLVMValueRef>();

    // Variable type table: maps variable names to their LLVM types (needed for load instructions)
    private Dictionary<string, LLVMTypeRef> typeTable = new Dictionary<string, LLVMTypeRef>();
    // Add this field alongside referenceTable and typeTable
    private Dictionary<string, LLVMTypeRef> userDefinedTypes = new Dictionary<string, LLVMTypeRef>();
    private Stack<LLVMBasicBlockRef> breakTargets = new Stack<LLVMBasicBlockRef>();
    private Stack<LLVMBasicBlockRef> continueTargets = new Stack<LLVMBasicBlockRef>();
    // Maps struct types to their field names in order (needed for field access by name)
    private Dictionary<IntPtr, List<string>> structFieldNames = new Dictionary<IntPtr, List<string>>();
    private byte[] S(string name) => System.Text.Encoding.UTF8.GetBytes(name + "\0");
    private LLVMBasicBlockRef entryBlock;

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
        SetModuleDataLayout(module, dataLayout);
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


    public unsafe override object VisitTopDeclarationList(MiniGoCompilerParser.TopDeclarationListContext context)
    {
        if (context.children == null) return null;

        // Pase 1: registrar tipos (porque las firmas los pueden usar)
        foreach (var child in context.children)
            if (child is MiniGoCompilerParser.TypeDeclContext td) Visit(td);

        // Pase 2: registrar firmas de todas las funciones (sin emitir cuerpo)
        foreach (var child in context.children)
        {
            if (child is MiniGoCompilerParser.FuncDeclContext fd)
            {
                var front = fd.funcFrontDecl();
                string funcName = front.IDENTIFIER().GetText();

                LLVMTypeRef retType = front.declType() != null 
                    ? ResolveLLVMType(front.declType()) 
                    : VoidType();
                if (funcName == "main") retType = intType;

                LLVMTypeRef[] paramTypes = new LLVMTypeRef[0];
                if (front.funcArgDecls() != null)
                {
                    List<LLVMTypeRef> paramList = new List<LLVMTypeRef>();
                    foreach (var param in front.funcArgDecls().singleVarDeclNoExps())
                    {
                        LLVMTypeRef paramType = ResolveLLVMType(param.declType());
                        foreach (var id in param.identifierList().IDENTIFIER())
                            paramList.Add(paramType);
                    }
                    paramTypes = paramList.ToArray();
                }

                LLVMTypeRef funcType = LLVMTypeRef.CreateFunction(retType, paramTypes);
                module.AddFunction(funcName, funcType);
            }
        }

        // Pase 3: emitir variables globales y cuerpos de funciones
        foreach (var child in context.children)
        {
            if (child is MiniGoCompilerParser.TypeDeclContext) continue;
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
        List<string> fieldNamesList = new List<string>();

        if (structDecl.structMemDecls() != null)
        {
            foreach (var member in structDecl.structMemDecls().singleVarDeclNoExps())
            {
                LLVMTypeRef memberType = ResolveLLVMType(member.declType());
                foreach (var id in member.identifierList().IDENTIFIER())
                {
                    fieldTypes.Add(memberType);
                    fieldNamesList.Add(id.Symbol.Text);  // track field name
                }
            }
        }

        LLVMTypeRef structType = LLVMTypeRef.CreateStruct(fieldTypes.ToArray(), false);
        structFieldNames[structType.Handle] = fieldNamesList;  // store for later lookup
        return structType;
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
    public unsafe LLVMValueRef AllocaInEntry(LLVMTypeRef type, string name)
    {
        var current = GetInsertBlock(builder);
        LLVMValueRef firstInst = GetFirstInstruction(entryBlock);

        // Reposicionar el builder al inicio del entry block
        if (firstInst.Handle != IntPtr.Zero)
            PositionBuilderBefore(builder, firstInst);
        else
            PositionBuilderAtEnd(builder, entryBlock);

        LLVMValueRef alloca;
        fixed (byte* p = S(name)) alloca = BuildAlloca(builder, type, (sbyte*)p);

        // Restaurar la posición original para que las instrucciones siguientes
        // (BuildStore, etc.) se sigan emitiendo donde corresponde
        PositionBuilderAtEnd(builder, current);
        return alloca;
    }

public unsafe override object VisitTypedVarDecl(MiniGoCompilerParser.TypedVarDeclContext context)
    {
        LLVMTypeRef type = ResolveLLVMType((context.declType()));
        var identifiers = context.identifierList().IDENTIFIER();
        
        LinkedList<LLVMValueRef> values = (LinkedList<LLVMValueRef>) Visit(context.expressionList());
        for (int i = 0; i < identifiers.Length; i++)
        {
            string name = identifiers[i].Symbol.Text;
            if (currentFunc.Handle == IntPtr.Zero)
            {
                LLVMValueRef global;
                fixed (byte* p = S(name)) global = AddGlobal(module, type, (sbyte*)p);
                SetInitializer(global, ConstNull(type));
                referenceTable[name] = global;
                typeTable[name] = type;
                continue;
            }
            LLVMValueRef alloca = AllocaInEntry(type, name);
            BuildStore(builder, values.ElementAt(i), alloca);
            referenceTable[name] = alloca;
            typeTable[name] = type;
        }
        return null;
    }

    private unsafe LLVMValueRef GlobalString(string text, string name)
    {
        fixed (byte* t = System.Text.Encoding.UTF8.GetBytes(text + "\0"))
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + "\0"))
        {
            return BuildGlobalStringPtr(builder, (sbyte*)t, (sbyte*)p);
        }
    }

    private unsafe LLVMValueRef CallFunction(LLVMTypeRef funcType, LLVMValueRef func,
        LLVMValueRef[] args, string name)
    {
        fixed (LLVMValueRef* argsPtr = args)
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + "\0"))
        {
            return BuildCall2(builder, funcType, func, (LLVMOpaqueValue**)argsPtr, (uint)args.Length, (sbyte*)p);
        }
    }


    public unsafe override object VisitInferredVarDecl(MiniGoCompilerParser.InferredVarDeclContext context)
    {
        LLVMTypeRef type;
        var identifiers = context.identifierList().IDENTIFIER();
        LinkedList<LLVMValueRef> values = (LinkedList<LLVMValueRef>) Visit(context.expressionList());

        for (int i = 0; i < identifiers.Length; i++)
        {
            type = TypeOf(values.ElementAt(i));
            string name = identifiers[i].Symbol.Text;
            if (currentFunc.Handle == IntPtr.Zero)
            {
                LLVMValueRef global;
                fixed (byte* p = S(name)) global = AddGlobal(module, type, (sbyte*)p);
                SetInitializer(global, ConstNull(type));
                referenceTable[name] = global;
                typeTable[name] = type;
                continue;
            }
            LLVMValueRef alloca = AllocaInEntry(type, name);
            BuildStore(builder, values.ElementAt(i), alloca);
            referenceTable[name] = alloca;
            typeTable[name] = type;
        }
        return null;
    }

    public override object VisitNoExpressionVarDecl(MiniGoCompilerParser.NoExpressionVarDeclContext context)
    {
        return Visit(context.singleVarDeclNoExps());
    }

    public unsafe override object VisitSingleVarDeclNoExps(MiniGoCompilerParser.SingleVarDeclNoExpsContext context)
    {
        LLVMTypeRef type = ResolveLLVMType(context.declType());
        foreach (var id in context.identifierList().IDENTIFIER())
        {
            string name = id.Symbol.Text;
            if (currentFunc.Handle == IntPtr.Zero)
            {
                LLVMValueRef global;
                fixed (byte* p = S(name)) global = AddGlobal(module, type, (sbyte*)p);
                SetInitializer(global, ConstNull(type));
                referenceTable[name] = global;
                typeTable[name] = type;
                continue;
            }
            LLVMValueRef alloca = AllocaInEntry(type, name);
            BuildStore(builder, ConstNull(type), alloca); // default value = zero
            referenceTable[name] = alloca;
            typeTable[name] = type;
        }
        return null;
    }

    public override object VisitTypeDecl(MiniGoCompilerParser.TypeDeclContext context)
    {
        
        if (context.singleTypeDecl() != null)
            Visit(context.singleTypeDecl());
        if (context.innerTypeDecls() != null)
            Visit(context.innerTypeDecls());
        return null;
    }

    public override object VisitInnerTypeDecls(MiniGoCompilerParser.InnerTypeDeclsContext context)
    {
        foreach (var decl in context.singleTypeDecl())
            Visit(decl);
        return null;
    }
    
    public override object VisitSingleTypeDecl(MiniGoCompilerParser.SingleTypeDeclContext context)
    {
        LLVMTypeRef resolved = ResolveLLVMType(context.declType());
        string name = context.IDENTIFIER().GetText();
        userDefinedTypes[name] = resolved;
        return null;
    }

    public unsafe override object VisitFuncDecl(MiniGoCompilerParser.FuncDeclContext context)
    {
        
        var front = context.funcFrontDecl();
    string funcName = front.IDENTIFIER().GetText();
    var savedRefs = new Dictionary<string, LLVMValueRef>(referenceTable);
    var savedTypes = new Dictionary<string, LLVMTypeRef>(typeTable);


    // Return type
    LLVMTypeRef retType = front.declType() != null 
        ? ResolveLLVMType(front.declType()) 
        : VoidType();
    if (funcName == "main")
    {
        retType = intType;
    }

    // Parameter types
    LLVMTypeRef[] paramTypes = new LLVMTypeRef[0];
    if (front.funcArgDecls() != null)
    {
        var paramDecls = front.funcArgDecls().singleVarDeclNoExps();
        List<LLVMTypeRef> paramList = new List<LLVMTypeRef>();
        foreach (var param in paramDecls)
        {
            LLVMTypeRef paramType = ResolveLLVMType(param.declType());
            foreach (var id in param.identifierList().IDENTIFIER())
            {
                paramList.Add(paramType);
            }
        }
        paramTypes = paramList.ToArray();
    }

    // Create function type and add to module
    LLVMValueRef func;
    fixed (byte* p = S(funcName)) func = GetNamedFunction(module, (sbyte*)p);
    LLVMTypeRef funcType = GlobalGetValueType(func);
    currentFunc = func;

    // Create entry block and position builder
    LLVMBasicBlockRef entry = func.AppendBasicBlock("entry");
    this.entryBlock = entry;  
    PositionBuilderAtEnd(builder, entry);

    // Store parameters in alloca so they can be used as variables
    if (front.funcArgDecls() != null)
    {
        int paramIndex = 0;
        foreach (var param in front.funcArgDecls().singleVarDeclNoExps())
        {
            LLVMTypeRef paramType = ResolveLLVMType(param.declType());
            foreach (var id in param.identifierList().IDENTIFIER())
            {
                string paramName = id.Symbol.Text;
                LLVMValueRef alloca = AllocaVar(paramType, paramName);
                BuildStore(builder, func.GetParam((uint)paramIndex), alloca);
                referenceTable[paramName] = alloca;
                typeTable[paramName] = paramType;
                paramIndex++;
            }
        }
    }

    // Visit function body
    Visit(context.block());

    // Add default terminator if body didn't end with a return
    LLVMBasicBlockRef currentBlock = GetInsertBlock(builder);
    if (GetBasicBlockTerminator(currentBlock) == null)
    {
        if (retType == VoidType())
            BuildRetVoid(builder);
        else
            BuildRet(builder, ConstNull(retType));
    }

    referenceTable = savedRefs;
    typeTable = savedTypes;
    return null;
    }

    public override object VisitFuncFrontDecl(MiniGoCompilerParser.FuncFrontDeclContext context)
    {
        return null; 
    }

    public override object VisitFuncArgDecls(MiniGoCompilerParser.FuncArgDeclsContext context)
    {
        return null; 
    }

    public override object VisitGroupDeclType(MiniGoCompilerParser.GroupDeclTypeContext context)
    {
        return null; 
    }

    public override object VisitTypeDenoterDeclType(MiniGoCompilerParser.TypeDenoterDeclTypeContext context)
    {
        return null; 
    }

    public override object VisitSliceTypeDecl(MiniGoCompilerParser.SliceTypeDeclContext context)
    {
        return null; 
    }

    public override object VisitArrayTypeDecl(MiniGoCompilerParser.ArrayTypeDeclContext context)
    {
        return null; 
    }

    public override object VisitStructTypeDecl(MiniGoCompilerParser.StructTypeDeclContext context)
    {
        return null; 
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
        return null; 
    }

    public override object VisitIdentifierList(MiniGoCompilerParser.IdentifierListContext context)
    {
        return null; 
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
        return Visit(context.primaryExpression());
    }

    public unsafe override object VisitUnarySubExpr(MiniGoCompilerParser.UnarySubExprContext context)
    {
        LLVMValueRef val = (LLVMValueRef) Visit(context.expression());
        LLVMValueRef result;
        if (TypeOf(val) == floatType)
            fixed (byte* p = S("fnegtmp")) result = BuildFNeg(builder, val, (sbyte*)p);
        else
            fixed (byte* p = S("negtmp")) result = BuildNeg(builder, val, (sbyte*)p);
        return result;
    }

    public unsafe override object VisitAddExpr(MiniGoCompilerParser.AddExprContext context)
    {
        LLVMValueRef left = (LLVMValueRef) Visit(context.expression(0));
        LLVMValueRef right = (LLVMValueRef) Visit(context.expression(1));
        LLVMValueRef result;
        bool isFloat = TypeOf(left) == floatType || TypeOf(right) == floatType;

        if (context.ADD() != null)
        {
            if (isFloat)
                fixed (byte* p = S("faddtmp")) result = BuildFAdd(builder, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("addtmp")) result = BuildAdd(builder, left, right, (sbyte*)p);
        }
        else if (context.SUB() != null)
        {
            if (isFloat)
                fixed (byte* p = S("fsubtmp")) result = BuildFSub(builder, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("subtmp")) result = BuildSub(builder, left, right, (sbyte*)p);
        }
        else if (context.OR() != null)
        {
            fixed (byte* p = S("ortmp")) result = BuildOr(builder, left, right, (sbyte*)p);
        }
        else // HAT (XOR)
        {
            fixed (byte* p = S("xortmp")) result = BuildXor(builder, left, right, (sbyte*)p);
        }

        return result;
    }

    public unsafe override object VisitMulExpr(MiniGoCompilerParser.MulExprContext context)
    {
        LLVMValueRef left = (LLVMValueRef) Visit(context.expression(0));
        LLVMValueRef right = (LLVMValueRef) Visit(context.expression(1));
        LLVMValueRef result;
        bool isFloat = TypeOf(left) == floatType || TypeOf(right) == floatType;

        if (context.MUL() != null)
        {
            if (isFloat)
                fixed (byte* p = S("fmultmp")) result = BuildFMul(builder, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("multmp")) result = BuildMul(builder, left, right, (sbyte*)p);
        }
        else if (context.DIV() != null)
        {
            if (isFloat)
                fixed (byte* p = S("fdivtmp")) result = BuildFDiv(builder, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("divtmp")) result = BuildSDiv(builder, left, right, (sbyte*)p);
        }
        else if (context.MOD() != null)
        {
            if (isFloat)
                fixed (byte* p = S("fmodtmp")) result = BuildFRem(builder, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("modtmp")) result = BuildSRem(builder, left, right, (sbyte*)p);
        }
        else if (context.DLESS() != null)
        {
            fixed (byte* p = S("shltmp")) result = BuildShl(builder, left, right, (sbyte*)p);
        }
        else if (context.DMORE() != null)
        {
            fixed (byte* p = S("shrtmp")) result = BuildAShr(builder, left, right, (sbyte*)p);
        }
        else if (context.AND() != null)
        {
            fixed (byte* p = S("andtmp")) result = BuildAnd(builder, left, right, (sbyte*)p);
        }
        else // ANDHAT (&^ = bit clear)
        {
            LLVMValueRef notRight;
            fixed (byte* p = S("nottmp")) notRight = BuildNot(builder, right, (sbyte*)p);
            fixed (byte* p = S("andnottmp")) result = BuildAnd(builder, left, notRight, (sbyte*)p);
        }

        return result;
    }

    public unsafe override object VisitOrExpr(MiniGoCompilerParser.OrExprContext context)
    {
        LLVMValueRef left = (LLVMValueRef) Visit(context.expression(0));
        LLVMValueRef right = (LLVMValueRef) Visit(context.expression(1));
        LLVMValueRef result;
        fixed (byte* p = S("ortmp")) result = BuildOr(builder, left, right, (sbyte*)p);
        return result;
    }

    public unsafe override object VisitUnaryHatExpr(MiniGoCompilerParser.UnaryHatExprContext context)
    {
        
        LLVMValueRef val = (LLVMValueRef) Visit(context.expression());
        LLVMValueRef result;
        fixed (byte* p = S("xortmp")) result = BuildNot(builder, val, (sbyte*)p); // ^x = bitwise complement = NOT
        return result;
    }

    public override object VisitUnaryAddExpr(MiniGoCompilerParser.UnaryAddExprContext context)
    {
        return Visit(context.expression()); 
    }

    public unsafe override object VisitRelExpr(MiniGoCompilerParser.RelExprContext context)
    {
        LLVMValueRef left = (LLVMValueRef) Visit(context.expression(0));
    LLVMValueRef right = (LLVMValueRef) Visit(context.expression(1));
    LLVMValueRef result;
    bool isFloat = TypeOf(left) == floatType || TypeOf(right) == floatType;

    if (context.EQEQ() != null)
    {
        if (isFloat) fixed (byte* p = S("eqtmp")) result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOEQ, left, right, (sbyte*)p);
        else fixed (byte* p = S("eqtmp")) result = BuildICmp(builder, LLVMIntPredicate.LLVMIntEQ, left, right, (sbyte*)p);
    }
    else if (context.NOTEQ() != null)
    {
        if (isFloat) fixed (byte* p = S("netmp")) result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealONE, left, right, (sbyte*)p);
        else fixed (byte* p = S("netmp")) result = BuildICmp(builder, LLVMIntPredicate.LLVMIntNE, left, right, (sbyte*)p);
    }
    else if (context.LESS() != null)
    {
        if (isFloat) fixed (byte* p = S("lttmp")) result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOLT, left, right, (sbyte*)p);
        else fixed (byte* p = S("lttmp")) result = BuildICmp(builder, LLVMIntPredicate.LLVMIntSLT, left, right, (sbyte*)p);
    }
    else if (context.MORET() != null)
    {
        if (isFloat) fixed (byte* p = S("gttmp")) result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOGT, left, right, (sbyte*)p);
        else fixed (byte* p = S("gttmp")) result = BuildICmp(builder, LLVMIntPredicate.LLVMIntSGT, left, right, (sbyte*)p);
    }
    else if (context.LESSEQ() != null)
    {
        if (isFloat) fixed (byte* p = S("letmp")) result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOLE, left, right, (sbyte*)p);
        else fixed (byte* p = S("letmp")) result = BuildICmp(builder, LLVMIntPredicate.LLVMIntSLE, left, right, (sbyte*)p);
    }
    else // MOREEQ
    {
        if (isFloat) fixed (byte* p = S("getmp")) result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOGE, left, right, (sbyte*)p);
        else fixed (byte* p = S("getmp")) result = BuildICmp(builder, LLVMIntPredicate.LLVMIntSGE, left, right, (sbyte*)p);
    }

    return result;
    }

    public unsafe override object VisitUnaryNotExpr(MiniGoCompilerParser.UnaryNotExprContext context)
    {
        LLVMValueRef val = (LLVMValueRef) Visit(context.expression());
        LLVMValueRef result;
        fixed (byte* p = S("nottmp")) result = BuildNot(builder, val, (sbyte*)p);
        return result;
    }

    public unsafe override object VisitAndExpr(MiniGoCompilerParser.AndExprContext context)
    {
        LLVMValueRef left = (LLVMValueRef) Visit(context.expression(0));
        LLVMValueRef right = (LLVMValueRef) Visit(context.expression(1));
        LLVMValueRef result;
        fixed (byte* p = S("andtmp")) result = BuildAnd(builder, left, right, (sbyte*)p);
        return result;
    }

    public override object VisitLengthPrimaryExpr(MiniGoCompilerParser.LengthPrimaryExprContext context)
    {
        return Visit(context.lengthExpression());
    }

    public override object VisitOperandPrimaryExpr(MiniGoCompilerParser.OperandPrimaryExprContext context)
    {
        return Visit(context.operand());
    }

    public override object VisitAppendPrimaryExpr(MiniGoCompilerParser.AppendPrimaryExprContext context)
    {
        return Visit(context.appendExpression());
    }

    public unsafe override object VisitIndexPrimaryExpr(MiniGoCompilerParser.IndexPrimaryExprContext context)
    {
        string name = context.primaryExpression().GetText();
        if (!referenceTable.ContainsKey(name))
            throw new NotSupportedException(
                "Chained indexing is not supported: '"+ name + "'. " +
                "Indexing is only supported on direct identifiers.");
        LLVMValueRef arrayPtr = referenceTable[name];
        LLVMTypeRef arrayType = typeTable[name];

        // Get the index value
        LLVMValueRef index = (LLVMValueRef) Visit(context.index().expression());

        // GEP (Get Element Pointer) to access the element
        LLVMValueRef[] indices = { ConstInt(intType, 0, 0), index };
        LLVMValueRef elementPtr;
        fixed (LLVMValueRef* idxPtr = indices)
        fixed (byte* p = S("elemptr"))
        {
            elementPtr = BuildGEP2(builder, arrayType, arrayPtr, (LLVMOpaqueValue**)idxPtr, 2, (sbyte*)p);
        }

        // Get the element type from the array type
        LLVMTypeRef elemType = GetElementType(arrayType);
        LLVMValueRef loaded = LoadVar(elemType, elementPtr, "elem");
        return loaded;
    }

    public unsafe override object VisitSelectorPrimaryExpr(MiniGoCompilerParser.SelectorPrimaryExprContext ctx)
    {
        string structName = ctx.primaryExpression().GetText();
        string fieldName = ctx.selector().IDENTIFIER().GetText();
        LLVMValueRef structPtr = referenceTable[structName];
        LLVMTypeRef structType = typeTable[structName];

        // Find field index by looking up the name in the stored field list
        uint fieldIndex = 0;
        if (structFieldNames.TryGetValue(structType.Handle, out List<string> fields))
        {
            int idx = fields.IndexOf(fieldName);
            if (idx >= 0) fieldIndex = (uint) idx;
        }

        LLVMValueRef fieldPtr;
        fixed (byte* p = S("fieldptr"))
        {
            fieldPtr = BuildStructGEP2(builder, structType, structPtr, fieldIndex, (sbyte*)p);
        }

        LLVMTypeRef fieldType = StructGetTypeAtIndex(structType, fieldIndex);
        return LoadVar(fieldType, fieldPtr, fieldName);
    }

    public unsafe override object VisitArgumentsPrimaryExpr(MiniGoCompilerParser.ArgumentsPrimaryExprContext context)
    {
        string funcName = context.primaryExpression().GetText();
        LLVMValueRef func;
        fixed (byte* p = S(funcName)) func = GetNamedFunction(module, (sbyte*)p);
        LLVMTypeRef funcType = GlobalGetValueType(func);

        LLVMValueRef[] args = new LLVMValueRef[0];
        if (context.arguments().expressionList() != null)
        {
            var exprs = context.arguments().expressionList().expression();
            args = new LLVMValueRef[exprs.Length];
            for (int i = 0; i < exprs.Length; i++)
                args[i] = (LLVMValueRef) Visit(exprs[i]);
        }

        return CallFunction(funcType, func, args, "calltmp");
    }

    public unsafe override object VisitCapPrimaryExpr(MiniGoCompilerParser.CapPrimaryExprContext context)
    {
        return Visit(context.capExpression());
    }

    public override object VisitLiteralOperand(MiniGoCompilerParser.LiteralOperandContext context)
    {
        return Visit(context.literal());
    }

    public unsafe override object VisitIdOperand(MiniGoCompilerParser.IdOperandContext context)
    {
        string name = context.identifier().GetText();
        
        if (name == "true") { LLVMValueRef t = ConstInt(boolType, 1, 0); return t; }
        if (name == "false") { LLVMValueRef f = ConstInt(boolType, 0, 0); return f; }
        LLVMTypeRef type = typeTable[name];
        LLVMValueRef value = referenceTable[name];
        LLVMValueRef variable = LoadVar(type, value, name);
        return variable;
    }

    public override object VisitGroupOperand(MiniGoCompilerParser.GroupOperandContext context)
    {
        return Visit(context.expression()); 
    }

    public unsafe override object VisitIntLiteral(MiniGoCompilerParser.IntLiteralContext context)
    {
        long value = long.Parse(context.INTLITERAL().GetText());
        LLVMValueRef result = ConstInt(intType, (ulong)value, 0);
        return result;
    }

    public unsafe override object VisitFloatLiteral(MiniGoCompilerParser.FloatLiteralContext context)
    {
        double value = double.Parse(context.FLOATLITERAL().GetText(), System.Globalization.CultureInfo.InvariantCulture);
        LLVMValueRef result = ConstReal(floatType, value);
        return result;
    }

    public unsafe override object VisitRuneLiteral(MiniGoCompilerParser.RuneLiteralContext context)
    {
        string text = context.RUNELITERAL().GetText(); // comes as 'A' or '\n'
        char c;
        if (text[1] == '\\')
        {
            c = text[2] switch
            {
                'n' => '\n', 't' => '\t', '\\' => '\\',
                '\'' => '\'', 'a' => '\a', 'b' => '\b',
                'f' => '\f', 'r' => '\r', 'v' => '\v',
                _ => text[2]
            };
        }
        else
        {
            c = text[1];
        }
        LLVMValueRef result =  ConstInt(runeType, (ulong)c, 0);
        return result; 
    }

    public override object VisitRawStringLiteral(MiniGoCompilerParser.RawStringLiteralContext context)
    {
        string text = context.RAWSTRINGLITERAL().GetText();
        string content = text.Substring(1, text.Length - 2); // remove backticks
        return GlobalString(content, "str");
    }

    public override object VisitInterpretedStringLiteral(MiniGoCompilerParser.InterpretedStringLiteralContext context)
    {
        string text = context.INTERPRETEDSTRINGLITERAL().GetText();
        string content = text.Substring(1, text.Length - 2); // remove quotes
        return GlobalString(content, "str");
    }

    public override object VisitIndex(MiniGoCompilerParser.IndexContext context)
    {
        return Visit(context.expression());
    }

    public override object VisitArguments(MiniGoCompilerParser.ArgumentsContext context)
    {
        return Visit(context.expressionList());
    }

    public override object VisitSelector(MiniGoCompilerParser.SelectorContext context)
    {
        return null;
    }
    private uint GetTypeSize(LLVMTypeRef type)
    {
        if (type == intType) return 4;
        if (type == floatType) return 8;
        if (type == runeType) return 1;
        if (type == boolType) return 1;
        if (type == stringType) return 8; // pointer size on 64-bit
        return 4; // fallback
    }

    public unsafe override object VisitAppendExpression(MiniGoCompilerParser.AppendExpressionContext context)
    {
        
    // Get the slice value and the element to append
    LLVMValueRef sliceVal = (LLVMValueRef) Visit(context.expression(0));
    LLVMValueRef newElement = (LLVMValueRef) Visit(context.expression(1));
    LLVMTypeRef elemType = TypeOf(newElement);

    // Extract current fields from slice struct: { T*, i32 len, i32 cap }
    LLVMValueRef oldPtr, oldLen, oldCap;
    fixed (byte* p = S("ptr")) oldPtr = BuildExtractValue(builder, sliceVal, 0, (sbyte*)p);
    fixed (byte* p = S("len")) oldLen = BuildExtractValue(builder, sliceVal, 1, (sbyte*)p);
    fixed (byte* p = S("cap")) oldCap = BuildExtractValue(builder, sliceVal, 2, (sbyte*)p);

    // newLen = oldLen + 1
    LLVMValueRef one = ConstInt(intType, 1, 0);
    LLVMValueRef newLen;
    fixed (byte* p = S("newlen")) newLen = BuildAdd(builder, oldLen, one, (sbyte*)p);

    // Calculate byte size: newLen * sizeof(element)
    LLVMValueRef elemSize = ConstInt(intType, GetTypeSize(elemType), 0);
    LLVMValueRef totalBytes;
    fixed (byte* p = S("bytes")) totalBytes = BuildMul(builder, newLen, elemSize, (sbyte*)p);
    LLVMValueRef totalBytes64;
    fixed (byte* p = S("bytes64")) 
        totalBytes64 = BuildZExt(builder, totalBytes, Int64Type(), (sbyte*)p);
    // Declare malloc if not already declared
    LLVMValueRef mallocFunc;
    fixed (byte* p = S("malloc")) mallocFunc = GetNamedFunction(module, (sbyte*)p);
    if (mallocFunc.Handle == IntPtr.Zero)
    {
        LLVMTypeRef sizeT = Int64Type();
        LLVMTypeRef mallocType = LLVMTypeRef.CreateFunction(stringType, new[] { sizeT }, false);
        mallocFunc = module.AddFunction("malloc", mallocType);
    }
    LLVMTypeRef mallocFuncType = GlobalGetValueType(mallocFunc);

    // Allocate new buffer
    LLVMValueRef newBuf = CallFunction(mallocFuncType, mallocFunc, new[] { totalBytes64 }, "newbuf");
    // Cast to element pointer type
    LLVMTypeRef elemPtrType = PointerType(elemType, 0);
    LLVMValueRef newPtr;
    fixed (byte* p = S("newptr")) newPtr = BuildBitCast(builder, newBuf, elemPtrType, (sbyte*)p);

    // Copy old elements: memcpy(newPtr, oldPtr, oldLen * elemSize)
    LLVMValueRef oldBytes;
    fixed (byte* p = S("oldbytes")) oldBytes = BuildMul(builder, oldLen, elemSize, (sbyte*)p);

    LLVMValueRef memcpyFunc;
    fixed (byte* p = S("memcpy")) memcpyFunc = GetNamedFunction(module, (sbyte*)p);
    if (memcpyFunc.Handle == IntPtr.Zero)
    {
        LLVMTypeRef sizeT = Int64Type();
        LLVMTypeRef memcpyType = LLVMTypeRef.CreateFunction(
            stringType, new[] { stringType, stringType, sizeT }, false);  // ✅
        memcpyFunc = module.AddFunction("memcpy", memcpyType);
    }
    LLVMTypeRef memcpyFuncType = GlobalGetValueType(memcpyFunc);

    LLVMValueRef oldPtrCast, newPtrCast;
    fixed (byte* p = S("oldcast")) oldPtrCast = BuildBitCast(builder, oldPtr, stringType, (sbyte*)p);
    fixed (byte* p = S("newcast")) newPtrCast = BuildBitCast(builder, newPtr, stringType, (sbyte*)p);
    LLVMValueRef oldBytes64;
    fixed (byte* p = S("oldbytes64"))
        oldBytes64 = BuildZExt(builder, oldBytes, Int64Type(), (sbyte*)p);
    CallFunction(memcpyFuncType, memcpyFunc, new[] { newPtrCast, oldPtrCast, oldBytes64 }, "");

    // Store new element at index oldLen
    LLVMValueRef[] gepIndices = { oldLen };
    LLVMValueRef newElemPtr;
    fixed (LLVMValueRef* idxPtr = gepIndices)
    fixed (byte* p = S("elemptr"))
    {
        newElemPtr = BuildGEP2(builder, elemType, newPtr, (LLVMOpaqueValue**)idxPtr, 1, (sbyte*)p);
    }
    BuildStore(builder, newElement, newElemPtr);

    // Build the new slice struct: { newPtr, newLen, newLen }
    LLVMTypeRef sliceType = TypeOf(sliceVal);
    LLVMValueRef newSlice = ConstNull(sliceType);
    fixed (byte* p = S("s1")) newSlice = BuildInsertValue(builder, newSlice, newPtr, 0, (sbyte*)p);
    fixed (byte* p = S("s2")) newSlice = BuildInsertValue(builder, newSlice, newLen, 1, (sbyte*)p);
    fixed (byte* p = S("s3")) newSlice = BuildInsertValue(builder, newSlice, newLen, 2, (sbyte*)p);

    return newSlice;
    }

    public unsafe override object VisitLengthExpression(MiniGoCompilerParser.LengthExpressionContext context)
    {
        
        LLVMValueRef val = (LLVMValueRef) Visit(context.expression());
        LLVMTypeRef valType = TypeOf(val);

        // For arrays, length is known at compile time
        if (valType.Kind == LLVMTypeKind.LLVMArrayTypeKind)
        {
            uint len = GetArrayLength(valType);
            LLVMValueRef result = ConstInt(intType, len, 0);
            return result;
        }
        if (valType.Kind == LLVMTypeKind.LLVMStructTypeKind)
        {
            LLVMValueRef len;
            fixed (byte* p = S("len")) len = BuildExtractValue(builder, val, 1, (sbyte*)p);
            return len;
        }

        // For strings, call strlen
        LLVMValueRef strlenFunc;
        fixed (byte* p = S("strlen")) strlenFunc = GetNamedFunction(module, (sbyte*)p);
        if (strlenFunc.Handle == IntPtr.Zero)
        {
            LLVMTypeRef strlenType = LLVMTypeRef.CreateFunction(intType, new[] { stringType }, false);
            strlenFunc = module.AddFunction("strlen", strlenType);
        }
        LLVMTypeRef strlenFuncType = GlobalGetValueType(strlenFunc);
        return CallFunction(strlenFuncType, strlenFunc, new[] { val }, "lentmp");
    }

    public unsafe override object VisitCapExpression(MiniGoCompilerParser.CapExpressionContext context)
    {
        // For arrays, cap == len
        LLVMValueRef result;
        LLVMValueRef val = (LLVMValueRef) Visit(context.expression());
        LLVMTypeRef valType = TypeOf(val);
        if (valType.Kind == LLVMTypeKind.LLVMArrayTypeKind)
        {
            uint len = GetArrayLength(valType);
            result = ConstInt(intType, len, 0);
            return result; 
        }
        if (valType.Kind == LLVMTypeKind.LLVMStructTypeKind)
        {
            fixed (byte* p = S("cap")) result = BuildExtractValue(builder, val, 2, (sbyte*)p);
            return result;
        }
        result = ConstInt(intType, 0, 0);
        return result;
    }

    public unsafe override object VisitStatementList(MiniGoCompilerParser.StatementListContext context)
    {
        foreach (var stmt in context.statement())
        {
            // Don't emit after a terminator (return/break/continue)
            LLVMBasicBlockRef currentBlock = GetInsertBlock(builder);
            if (GetBasicBlockTerminator(currentBlock) != null)
                break;
            Visit(stmt);
        }
        return null;

    }

    public override object VisitBlock(MiniGoCompilerParser.BlockContext context)
    {
        return Visit(context.statementList());
    }

    public unsafe override object VisitPrintStatement(MiniGoCompilerParser.PrintStatementContext context)
    {
        LLVMValueRef printfFunc;
        fixed (byte* t = System.Text.Encoding.UTF8.GetBytes("printf\0"))
        {
            printfFunc = GetNamedFunction(module, (sbyte*)t);
        }
        LLVMTypeRef printfType;
        if (printfFunc.Handle == IntPtr.Zero)
        {
            printfType = LLVMTypeRef.CreateFunction(intType, new[] { stringType }, true);
            printfFunc = module.AddFunction("printf", printfType);
        }
        else
        {
            printfType = GlobalGetValueType(printfFunc);
        }
        // Print each argument
        if (context.expressionList() != null)
        {
            var expressions = context.expressionList().expression();
            for (int i = 0; i < expressions.Length; i++)
            {
                LLVMValueRef value = (LLVMValueRef) Visit(expressions[i]);
                LLVMTypeRef exprType = TypeOf(value);

                // Pick format based on type
                string format;
                if (exprType == intType)
                    format = "%d";
                else if (exprType == floatType)
                    format = "%f";
                else if (exprType == runeType)
                    format = "%c";
                else if (exprType == boolType)
                {
                    fixed (byte* t = System.Text.Encoding.UTF8.GetBytes("boolext" + "\0")) 
                        value = BuildZExt(builder, value, intType, (sbyte*)t);
                    format = "%d";
                }
                else if (exprType == stringType)
                    format = "%s";
                else
                    format = "%d";

                LLVMValueRef formatStr = GlobalString(format, "fmt");
                CallFunction(printfType, printfFunc, new[] { formatStr, value }, "");
            }
        }
        return null;

    }

    public unsafe override object VisitPrintlnStatement(MiniGoCompilerParser.PrintlnStatementContext context)
    {
        LLVMValueRef printfFunc;
        fixed (byte* t = System.Text.Encoding.UTF8.GetBytes("printf\0"))
        {
            printfFunc = GetNamedFunction(module, (sbyte*)t);
        }
        LLVMTypeRef printfType;
        if (printfFunc.Handle == IntPtr.Zero)
        {
            printfType = LLVMTypeRef.CreateFunction(intType, new[] { stringType }, true);
            printfFunc = module.AddFunction("printf", printfType);
        }
        else
        {
            printfType = GlobalGetValueType(printfFunc);
        }

        // Print each argument
        if (context.expressionList() != null)
        {
            var expressions = context.expressionList().expression();
            for (int i = 0; i < expressions.Length; i++)
            {
                LLVMValueRef value = (LLVMValueRef) Visit(expressions[i]);
                LLVMTypeRef exprType = TypeOf(value);

                // Add space between arguments (Go println behavior)
                if (i > 0)
                {
                    LLVMValueRef space = GlobalString(" ", "sp");
                    CallFunction(printfType, printfFunc, new[] { space }, "");
                }

                // Pick format based on type
                string format;
                if (exprType == intType)
                    format = "%d";
                else if (exprType == floatType)
                    format = "%f";
                else if (exprType == runeType)
                    format = "%c";
                else if (exprType == boolType)
                {
                    fixed (byte* t = System.Text.Encoding.UTF8.GetBytes("boolext" + "\0")) 
                    value = BuildZExt(builder, value, intType, (sbyte*)t);
                    format = "%d";
                }
                else if (exprType == stringType)
                    format = "%s";
                else
                    format = "%d";

                LLVMValueRef formatStr = GlobalString(format, "fmt");
                CallFunction(printfType, printfFunc, new[] { formatStr, value }, "");
            }
        }

        // println always adds a newline at the end
        LLVMValueRef newline = GlobalString("\n", "nl");
        CallFunction(printfType, printfFunc, new[] { newline }, "");

        return null;
    }

    public unsafe override object VisitReturnStatement(MiniGoCompilerParser.ReturnStatementContext context)
    {
        if (context.expression() != null)
        {
            LLVMValueRef value = (LLVMValueRef) Visit(context.expression());
            BuildRet(builder, value);
        }
        else
        {
            // Si la función actual retorna no-void (ej. main forzado a i32), retornar 0
            LLVMTypeRef retType = GetReturnType(GlobalGetValueType(currentFunc));
            if (retType == VoidType())
                BuildRetVoid(builder);
            else
                BuildRet(builder, ConstNull(retType));
        }
        return null;
    }

    public unsafe override object VisitBreakStatement(MiniGoCompilerParser.BreakStatementContext context)
    {
        if (breakTargets.Count > 0)
            BuildBr(builder, breakTargets.Peek());
        return null;
    }

    public unsafe override object VisitContinueStatement(MiniGoCompilerParser.ContinueStatementContext context)
    {
        if (continueTargets.Count > 0)
            BuildBr(builder, continueTargets.Peek());
        return null;
    }

    public override object VisitSimpleStmtStatement(MiniGoCompilerParser.SimpleStmtStatementContext context)
    {
        return Visit(context.simpleStatement());
    }

    public override object VisitBlockStatement(MiniGoCompilerParser.BlockStatementContext context)
    {
        return Visit(context.block()); 
    }

    public override object VisitSwitchStatement(MiniGoCompilerParser.SwitchStatementContext context)
    {
        return Visit(context.switchStmt());
    }

    public override object VisitIfStmtStatement(MiniGoCompilerParser.IfStmtStatementContext context)
    {
        return Visit(context.ifStatement());
    }

    public override object VisitLoopStatement(MiniGoCompilerParser.LoopStatementContext context)
    {
        return Visit(context.loop());
    }

    public override object VisitTypeDeclStatement(MiniGoCompilerParser.TypeDeclStatementContext context)
    {
        return Visit(context.typeDecl());
    }

    public override object VisitVariableDeclStatement(MiniGoCompilerParser.VariableDeclStatementContext context)
    {
        return Visit(context.variableDecl());
    }

    public override object VisitEmptySimpleStatement(MiniGoCompilerParser.EmptySimpleStatementContext context)
    {
        return null;
    }

    public unsafe override object VisitExpressionSimpleStatement(MiniGoCompilerParser.ExpressionSimpleStatementContext context)
    {
        
        LLVMValueRef val = (LLVMValueRef) Visit(context.expression());

        if (context.INC() != null || context.DEC() != null)
        {
            string name = context.expression().GetText();
            LLVMValueRef ptr = referenceTable[name];
            LLVMTypeRef type = typeTable[name];
            LLVMValueRef loaded = LoadVar(type, ptr, name + "_val");
            LLVMValueRef one = ConstInt(type, 1, 0);
            LLVMValueRef result;
            if (context.INC() != null)
                fixed (byte* p = S("inctmp")) result = BuildAdd(builder, loaded, one, (sbyte*)p);
            else
                fixed (byte* p = S("dectmp")) result = BuildSub(builder, loaded, one, (sbyte*)p);
            BuildStore(builder, result, ptr);
        }
        return null;
    }

    
    public override object VisitAssignmentSimpleStatement(MiniGoCompilerParser.AssignmentSimpleStatementContext context)
    {
        return Visit(context.assignmentStatement()); 
    }

    public unsafe override object VisitDeclareSimpleStatement(MiniGoCompilerParser.DeclareSimpleStatementContext context)
    {
        LinkedList<LLVMValueRef> values = (LinkedList<LLVMValueRef>) Visit(context.expressionList(1));
        var leftExprs = context.expressionList(0).expression();

        for (int i = 0; i < leftExprs.Length; i++)
        {
            string name = leftExprs[i].GetText();
            LLVMValueRef value = values.ElementAt(i);
            LLVMTypeRef type = TypeOf(value);
            LLVMValueRef alloca = AllocaInEntry(type, name);
            BuildStore(builder, value, alloca);
            referenceTable[name] = alloca;
            typeTable[name] = type;
        }
        return null;
    }

    public unsafe override object VisitEqualAssignment(MiniGoCompilerParser.EqualAssignmentContext context)
    {
        LinkedList<LLVMValueRef> values = (LinkedList<LLVMValueRef>) Visit(context.expressionList(1));
        var leftExprs = context.expressionList(0).expression();

        for (int i = 0; i < leftExprs.Length; i++)
        {
            string name = leftExprs[i].GetText();
            LLVMValueRef ptr = referenceTable[name];
            BuildStore(builder, values.ElementAt(i), ptr);
        }
        return null;
    }
    private unsafe void CompoundAssign(MiniGoCompilerParser.ExpressionContext leftCtx,
    MiniGoCompilerParser.ExpressionContext rightCtx, string op)
{
    string name = leftCtx.GetText();
    LLVMValueRef ptr = referenceTable[name];
    LLVMTypeRef type = typeTable[name];
    LLVMValueRef left = LoadVar(type, ptr, name + "_val");
    LLVMValueRef right = (LLVMValueRef) Visit(rightCtx);
    LLVMValueRef result;
    bool isFloat = type == floatType;

    switch (op)
    {
        case "+":
            if (isFloat) fixed (byte* p = S("addtmp")) result = BuildFAdd(builder, left, right, (sbyte*)p);
            else fixed (byte* p = S("addtmp")) result = BuildAdd(builder, left, right, (sbyte*)p);
            break;
        case "-":
            if (isFloat) fixed (byte* p = S("subtmp")) result = BuildFSub(builder, left, right, (sbyte*)p);
            else fixed (byte* p = S("subtmp")) result = BuildSub(builder, left, right, (sbyte*)p);
            break;
        case "*":
            if (isFloat) fixed (byte* p = S("multmp")) result = BuildFMul(builder, left, right, (sbyte*)p);
            else fixed (byte* p = S("multmp")) result = BuildMul(builder, left, right, (sbyte*)p);
            break;
        case "/":
            if (isFloat) fixed (byte* p = S("divtmp")) result = BuildFDiv(builder, left, right, (sbyte*)p);
            else fixed (byte* p = S("divtmp")) result = BuildSDiv(builder, left, right, (sbyte*)p);
            break;
        case "%":
            fixed (byte* p = S("modtmp")) result = BuildSRem(builder, left, right, (sbyte*)p);
            break;
        case "&":
            fixed (byte* p = S("andtmp")) result = BuildAnd(builder, left, right, (sbyte*)p);
            break;
        case "|":
            fixed (byte* p = S("ortmp")) result = BuildOr(builder, left, right, (sbyte*)p);
            break;
        case "^":
            fixed (byte* p = S("xortmp")) result = BuildXor(builder, left, right, (sbyte*)p);
            break;
        case "<<":
            fixed (byte* p = S("shltmp")) result = BuildShl(builder, left, right, (sbyte*)p);
            break;
        case ">>":
            fixed (byte* p = S("shrtmp")) result = BuildAShr(builder, left, right, (sbyte*)p);
            break;
        default:
            result = left;
            break;
    }
    BuildStore(builder, result, ptr);
}

    public override object VisitAddAssignment(MiniGoCompilerParser.AddAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "+"); return null;
    }

    public override object VisitAndAssignment(MiniGoCompilerParser.AndAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "&"); return null; 
    }

    public override object VisitSubAssignment(MiniGoCompilerParser.SubAssignmentContext context)
    {
        { CompoundAssign(context.expression(0), context.expression(1), "-"); return null; }
    }

    public override object VisitOrAssignment(MiniGoCompilerParser.OrAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "|"); return null; 
    }

    public override object VisitMulAssignment(MiniGoCompilerParser.MulAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "*"); return null; 
    }

    public override object VisitHatAssignment(MiniGoCompilerParser.HatAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "^"); return null; 
    }

    public override object VisitDlessAssignment(MiniGoCompilerParser.DlessAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "<<"); return null; 
    }

    public override object VisitDmoreAssignment(MiniGoCompilerParser.DmoreAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), ">>"); return null;
    }

    public unsafe override object VisitAndHatAssignment(MiniGoCompilerParser.AndHatAssignmentContext context)
    {
        string name = context.expression(0).GetText();
        LLVMValueRef ptr = referenceTable[name];
        LLVMTypeRef type = typeTable[name];
        LLVMValueRef left = LoadVar(type, ptr, name + "_val");
        LLVMValueRef right = (LLVMValueRef) Visit(context.expression(1));
        LLVMValueRef notRight, result;
        
        fixed (byte* p = S("nottmp")) notRight = BuildNot(builder, right, (sbyte*)p);
        fixed (byte* p = S("andnottmp")) result = BuildAnd(builder, left, notRight, (sbyte*)p);
        
        BuildStore(builder, result, ptr);
        return null;
    }

    public override object VisitModAssignment(MiniGoCompilerParser.ModAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "%"); return null;
    }

    public override object VisitDivAssignment(MiniGoCompilerParser.DivAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "/"); return null;
    }

    public unsafe override object VisitNormalIfStatement(MiniGoCompilerParser.NormalIfStatementContext context)
    {
        LLVMValueRef condition = (LLVMValueRef) Visit(context.expression());
        LLVMBasicBlockRef blockThen;
        LLVMBasicBlockRef blockMerge;
        fixed (byte* p = S("then")) blockThen = AppendBasicBlock(this.currentFunc, (sbyte*)p);
        fixed (byte* p = S("merge")) blockMerge = AppendBasicBlock(this.currentFunc, (sbyte*)p);
        BuildCondBr(builder,  condition, blockThen, blockMerge);
        PositionBuilderAtEnd(this.builder, blockThen);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, blockMerge);

        PositionBuilderAtEnd(builder, blockMerge);
        return null; 
    }

    public unsafe override object VisitElseIfStatement(MiniGoCompilerParser.ElseIfStatementContext context)
    {
        LLVMValueRef cond = (LLVMValueRef) Visit(context.expression());
        LLVMBasicBlockRef blockElse;
        LLVMBasicBlockRef blockMerge;
        LLVMBasicBlockRef blockThen;
        fixed (byte* p = S("then")) blockThen = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("else")) blockElse = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("merge")) blockMerge = AppendBasicBlock(currentFunc, (sbyte*)p);
        BuildCondBr(builder, cond, blockThen, blockElse);

        PositionBuilderAtEnd(builder, blockThen);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, blockMerge);

        PositionBuilderAtEnd(builder, blockElse);
        Visit(context.ifStatement()); 
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, blockMerge);

        PositionBuilderAtEnd(builder, blockMerge);
        return null;
    }

    public unsafe override object VisitElseBlockIfStatement(MiniGoCompilerParser.ElseBlockIfStatementContext context)
    {
        LLVMBasicBlockRef thenBlock;
        LLVMBasicBlockRef elseBlock;
        LLVMBasicBlockRef mergeBlock;
        LLVMValueRef cond = (LLVMValueRef) Visit(context.expression());
        fixed (byte* p = S("then")) thenBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("else")) elseBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("merge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        BuildCondBr(builder, cond, thenBlock, elseBlock);

        PositionBuilderAtEnd(builder, thenBlock);
        Visit(context.block(0));
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, mergeBlock);

        PositionBuilderAtEnd(builder, elseBlock);
        Visit(context.block(1));
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, mergeBlock);

        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    public unsafe override object VisitSimpleIfStatement(MiniGoCompilerParser.SimpleIfStatementContext context)
    {
        
        LLVMBasicBlockRef thenBlock;
        LLVMBasicBlockRef mergeBlock;
        Visit(context.simpleStatement());
        LLVMValueRef cond = (LLVMValueRef) Visit(context.expression()); fixed (byte* p = S("then")) 
         thenBlock = AppendBasicBlock(currentFunc, (sbyte*)p); fixed (byte* p = S("merge")) 
         mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        BuildCondBr(builder, cond, thenBlock, mergeBlock);
        PositionBuilderAtEnd(builder, thenBlock);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null) BuildBr(builder, mergeBlock);
        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    public unsafe override object VisitSimpleElseIfStatement(MiniGoCompilerParser.SimpleElseIfStatementContext context)
    {
        Visit(context.simpleStatement());
        LLVMBasicBlockRef thenBlock;
        LLVMBasicBlockRef elseBlock;
        LLVMBasicBlockRef mergeBlock;
        LLVMValueRef cond = (LLVMValueRef) Visit(context.expression());
        fixed (byte* p = S("then")) thenBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("else")) elseBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("merge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        BuildCondBr(builder, cond, thenBlock, elseBlock);
        PositionBuilderAtEnd(builder, thenBlock);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null) BuildBr(builder, mergeBlock);
        PositionBuilderAtEnd(builder, elseBlock);
        Visit(context.ifStatement());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null) BuildBr(builder, mergeBlock);
        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    public unsafe override object VisitSimpleElseBlockIfStatement(MiniGoCompilerParser.SimpleElseBlockIfStatementContext context)
    {
        Visit(context.simpleStatement());
        LLVMBasicBlockRef thenBlock;
        LLVMBasicBlockRef elseBlock;
        LLVMBasicBlockRef mergeBlock;
        LLVMValueRef cond = (LLVMValueRef) Visit(context.expression());
        fixed (byte* p = S("then")) thenBlock = AppendBasicBlock(currentFunc,  (sbyte*)p);
        fixed (byte* p = S("else")) elseBlock = AppendBasicBlock(currentFunc,  (sbyte*)p);
        fixed (byte* p = S("merge")) mergeBlock = AppendBasicBlock(currentFunc,  (sbyte*)p);
        BuildCondBr(builder, cond, thenBlock, elseBlock);
        PositionBuilderAtEnd(builder, thenBlock);
        Visit(context.block(0));
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null) BuildBr(builder, mergeBlock);
        PositionBuilderAtEnd(builder, elseBlock);
        Visit(context.block(1));
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null) BuildBr(builder, mergeBlock);
        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    public unsafe override object VisitInfiniteLoop(MiniGoCompilerParser.InfiniteLoopContext context)
    {
        LLVMBasicBlockRef bodyBlock;
        LLVMBasicBlockRef mergeBlock;
        fixed (byte* p = S("forBody")) bodyBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forMerge")) mergeBlock = AppendBasicBlock(currentFunc,(sbyte*)p);

        breakTargets.Push(mergeBlock);
        continueTargets.Push(bodyBlock);

        BuildBr(builder, bodyBlock);
        PositionBuilderAtEnd(builder, bodyBlock);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, bodyBlock);

        breakTargets.Pop();
        continueTargets.Pop();
        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    public unsafe override object VisitConditionLoop(MiniGoCompilerParser.ConditionLoopContext context)
    {
        LLVMBasicBlockRef condBlock;
        LLVMBasicBlockRef bodyBlock;
        LLVMBasicBlockRef mergeBlock;
        fixed (byte* p = S("forCond")) condBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forBody")) bodyBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forMerge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);

        breakTargets.Push(mergeBlock);
        continueTargets.Push(condBlock);

        BuildBr(builder, condBlock);
        PositionBuilderAtEnd(builder, condBlock);
        LLVMValueRef cond = (LLVMValueRef) Visit(context.expression());
        BuildCondBr(builder, cond, bodyBlock, mergeBlock);

        PositionBuilderAtEnd(builder, bodyBlock);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, condBlock);

        breakTargets.Pop();
        continueTargets.Pop();
        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    public unsafe override object VisitCompleteForLoop(MiniGoCompilerParser.CompleteForLoopContext context)
    {
        
        Visit(context.simpleStatement(0)); // init
        LLVMBasicBlockRef condBlock;
        LLVMBasicBlockRef bodyBlock;
        LLVMBasicBlockRef mergeBlock;
        LLVMBasicBlockRef postBlock;

        fixed (byte* p = S("forCond")) condBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forBody")) bodyBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forPost")) postBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forMerge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);

        breakTargets.Push(mergeBlock);
        continueTargets.Push(postBlock);

        BuildBr(builder, condBlock);
        PositionBuilderAtEnd(builder, condBlock);
        LLVMValueRef cond = (LLVMValueRef) Visit(context.expression());
        BuildCondBr(builder, cond, bodyBlock, mergeBlock);

        PositionBuilderAtEnd(builder, bodyBlock);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, postBlock);

        PositionBuilderAtEnd(builder, postBlock);
        Visit(context.simpleStatement(1)); 
        BuildBr(builder, condBlock);

        breakTargets.Pop();
        continueTargets.Pop();
        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    public unsafe override object VisitNoConditionForLoop(MiniGoCompilerParser.NoConditionForLoopContext context)
    {
        LLVMBasicBlockRef postBlock;
        LLVMBasicBlockRef bodyBlock;
        LLVMBasicBlockRef mergeBlock;
        Visit(context.simpleStatement(0)); // init
        fixed (byte* p = S("forBody")) bodyBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forPost")) postBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forMerge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);

        breakTargets.Push(mergeBlock);
        continueTargets.Push(postBlock);

        BuildBr(builder, bodyBlock);
        PositionBuilderAtEnd(builder, bodyBlock);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, postBlock);

        PositionBuilderAtEnd(builder, postBlock);
        Visit(context.simpleStatement(1)); // post
        BuildBr(builder, bodyBlock);

        breakTargets.Pop();
        continueTargets.Pop();
        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    // : SWITCH simpleStatement SEMI expression LEFTCB expressionCaseClauseList RIGHTCB #simpleExpressionSwitch
    public unsafe override object VisitSimpleExpressionSwitch(MiniGoCompilerParser.SimpleExpressionSwitchContext context)
    { Visit(context.simpleStatement());
    LLVMValueRef switchVal = (LLVMValueRef) Visit(context.expression());
    LLVMBasicBlockRef mergeBlock;
    fixed (byte* p = S("switchMerge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
    breakTargets.Push(mergeBlock);

    var clauses = context.expressionCaseClauseList().expressionCaseClause();
    LLVMBasicBlockRef[]  caseBlocks = new LLVMBasicBlockRef[clauses.Length];
    LLVMBasicBlockRef defaultBlock = mergeBlock;

    for (int i = 0; i < clauses.Length; i++)
        fixed (byte* p = S("case" + i))caseBlocks[i] = AppendBasicBlock(currentFunc, (sbyte*)p);

    for (int i = 0; i < clauses.Length; i++)
        if (clauses[i].expressionSwitchCase() is MiniGoCompilerParser.DefaultSwitchContext)
            defaultBlock = caseBlocks[i];

    LLVMBasicBlockRef nextTest;
    fixed (byte* p = S("test0")) nextTest= (clauses.Length > 0) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
    BuildBr(builder, nextTest);

    for (int i = 0; i < clauses.Length; i++)
    {
        var switchCase = clauses[i].expressionSwitchCase();
        if (switchCase is MiniGoCompilerParser.CaseSwitchContext caseCtx)
        {
            PositionBuilderAtEnd(builder, nextTest);
            var caseExprs = caseCtx.expressionList().expression();
            LLVMValueRef match = null;
            for (int j = 0; j < caseExprs.Length; j++)
            {
                LLVMValueRef caseVal = (LLVMValueRef) Visit(caseExprs[j]);
                LLVMValueRef cmp;
                if (TypeOf(switchVal) == floatType)
                    fixed (byte* p = S("cmptmp")) cmp = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOEQ, switchVal, caseVal, (sbyte*)p);
                else
                    fixed (byte* p = S("cmptmp")) cmp = BuildICmp(builder, LLVMIntPredicate.LLVMIntEQ, switchVal, caseVal, (sbyte*)p);
                if (match == null) match = cmp;
                else fixed (byte* p = S("ortmp")) match = BuildOr(builder, match, cmp, (sbyte*)p);
            }
            fixed (byte* p = S("test" + (i + 1)))  nextTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc,(sbyte*)p) : defaultBlock;
            BuildCondBr(builder, match, caseBlocks[i], nextTest);
        }
        else
        {
            LLVMBasicBlockRef newTest;
            fixed (byte* p = S("test" + (i + 1))) 
                newTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
            PositionBuilderAtEnd(builder, nextTest);
            BuildBr(builder, newTest);   // fall-through al siguiente test
            nextTest = newTest;
        }
        PositionBuilderAtEnd(builder, caseBlocks[i]);
        Visit(clauses[i].statementList());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, mergeBlock);
    }

    breakTargets.Pop();
    PositionBuilderAtEnd(builder, mergeBlock);
    return null;
    }
// SWITCH expression LEFTCB expressionCaseClauseList RIGHTCB  
    public override unsafe object VisitExpressionSwitch(MiniGoCompilerParser.ExpressionSwitchContext context)
    {
        LLVMBasicBlockRef mergeBlock;
    LLVMValueRef switchVal = (LLVMValueRef) Visit(context.expression());
    fixed (byte* p = S("switchMerge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
    breakTargets.Push(mergeBlock);

    var clauses = context.expressionCaseClauseList().expressionCaseClause();
    LLVMBasicBlockRef[] caseBlocks = new LLVMBasicBlockRef[clauses.Length];
    LLVMBasicBlockRef defaultBlock = mergeBlock;

    // Create blocks for each case
    for (int i = 0; i < clauses.Length; i++)
        fixed (byte* p = S("case" + i)) caseBlocks[i] = AppendBasicBlock(currentFunc, (sbyte*)p);

    // Find default block if it exists
    for (int i = 0; i < clauses.Length; i++)
    {
        if (clauses[i].expressionSwitchCase() is MiniGoCompilerParser.DefaultSwitchContext)
            defaultBlock = caseBlocks[i];
    }

    // Build chain of comparisons
    LLVMBasicBlockRef nextTest;
    fixed (byte* p = S("test0")) nextTest = (clauses.Length > 0) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
    BuildBr(builder, nextTest);

    for (int i = 0; i < clauses.Length; i++)
    {
        var switchCase = clauses[i].expressionSwitchCase();

        if (switchCase is MiniGoCompilerParser.CaseSwitchContext caseCtx)
        {
            PositionBuilderAtEnd(builder, nextTest);
            // Compare against each expression in the case list
            var caseExprs = caseCtx.expressionList().expression();
            LLVMValueRef match = null;
            for (int j = 0; j < caseExprs.Length; j++)
            {
                LLVMValueRef caseVal = (LLVMValueRef) Visit(caseExprs[j]);
                LLVMValueRef cmp;
                if (TypeOf(switchVal) == floatType)
                    fixed (byte* p = S("cmptmp")) cmp = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOEQ, switchVal, caseVal, (sbyte*)p);
                else
                    fixed (byte* p = S("cmptmp")) cmp = BuildICmp(builder, LLVMIntPredicate.LLVMIntEQ, switchVal, caseVal, (sbyte*)p);

                if (match == null)
                    match = cmp;
                else
                    fixed (byte* p = S("ortmp")) match = BuildOr(builder, match, cmp, (sbyte*)p);
            }

            LLVMBasicBlockRef newTest;
            fixed (byte* p = S("test" + (i + 1))) 
                newTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
            PositionBuilderAtEnd(builder, nextTest);
            BuildBr(builder, newTest);   // fall-through al siguiente test
            nextTest = newTest;
            BuildCondBr(builder, match, caseBlocks[i], nextTest);
        }
        else // default
        {
            fixed (byte* p = S("test" + (i + 1))) nextTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc,(sbyte*)p ) : defaultBlock;
        }

        // Emit case body
        PositionBuilderAtEnd(builder, caseBlocks[i]);
        Visit(clauses[i].statementList());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, mergeBlock); // Go switches don't fallthrough by default
    }

    breakTargets.Pop();
    PositionBuilderAtEnd(builder, mergeBlock);
    return null;
    }

    //| SWITCH simpleStatement SEMI LEFTCB expressionCaseClauseList RIGHTCB            #simpleSwitch
    public unsafe override object VisitSimpleSwitch(MiniGoCompilerParser.SimpleSwitchContext context)
    {
       Visit(context.simpleStatement());
    LLVMValueRef switchVal = ConstInt(boolType, 1, 0); // no expression = compare against true

    LLVMBasicBlockRef mergeBlock;
    fixed (byte* p = S("switchMerge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
    breakTargets.Push(mergeBlock);

    var clauses = context.expressionCaseClauseList().expressionCaseClause();
    LLVMBasicBlockRef[] caseBlocks = new LLVMBasicBlockRef[clauses.Length];
    LLVMBasicBlockRef defaultBlock = mergeBlock;

    for (int i = 0; i < clauses.Length; i++)
        fixed (byte* p = S("case" + i)) caseBlocks[i] = AppendBasicBlock(currentFunc, (sbyte*)p);

    for (int i = 0; i < clauses.Length; i++)
    {
        if (clauses[i].expressionSwitchCase() is MiniGoCompilerParser.DefaultSwitchContext)
            defaultBlock = caseBlocks[i];
    }

    LLVMBasicBlockRef nextTest;
    fixed (byte* p = S("test0")) nextTest = (clauses.Length > 0) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
    BuildBr(builder, nextTest);

    for (int i = 0; i < clauses.Length; i++)
    {
        var switchCase = clauses[i].expressionSwitchCase();

        if (switchCase is MiniGoCompilerParser.CaseSwitchContext caseCtx)
        {
            // Posicionar en el bloque de test actual y emitir comparaciones
            PositionBuilderAtEnd(builder, nextTest);
            var caseExprs = caseCtx.expressionList().expression();
            LLVMValueRef match = null;
            for (int j = 0; j < caseExprs.Length; j++)
            {
                LLVMValueRef caseVal = (LLVMValueRef) Visit(caseExprs[j]);
                LLVMValueRef cmp;
                fixed (byte* p = S("cmptmp")) cmp = BuildICmp(builder, LLVMIntPredicate.LLVMIntEQ, switchVal, caseVal, (sbyte*)p);
                if (match == null) match = cmp;
                else fixed (byte* p = S("ortmp")) match = BuildOr(builder, match, cmp, (sbyte*)p);
            }

            // Crear siguiente test y emitir UN solo terminator
            fixed (byte* p = S("test" + (i + 1)))
                nextTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
            BuildCondBr(builder, match, caseBlocks[i], nextTest);  // ← solo este
        }
        else // default
        {
            // El test anterior apunta acá — necesita un branch incondicional al siguiente test
            LLVMBasicBlockRef newTest;
            fixed (byte* p = S("test" + (i + 1)))
                newTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
            PositionBuilderAtEnd(builder, nextTest);
            BuildBr(builder, newTest);
            nextTest = newTest;
        }

        // Emitir cuerpo del case/default
        PositionBuilderAtEnd(builder, caseBlocks[i]);
        Visit(clauses[i].statementList());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, mergeBlock);
    }

    breakTargets.Pop();
    PositionBuilderAtEnd(builder, mergeBlock);
    return null;
    }

    public unsafe override object VisitEmptySwitch(MiniGoCompilerParser.EmptySwitchContext context)
    {
         LLVMValueRef switchVal = ConstInt(boolType, 1, 0); // true
    LLVMBasicBlockRef mergeBlock;
    fixed (byte* p = S("switchMerge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
    breakTargets.Push(mergeBlock);

    var clauses = context.expressionCaseClauseList().expressionCaseClause();
    LLVMBasicBlockRef[] caseBlocks = new LLVMBasicBlockRef[clauses.Length];
    LLVMBasicBlockRef defaultBlock = mergeBlock;

    for (int i = 0; i < clauses.Length; i++)
        fixed (byte* p = S("case" + i)) caseBlocks[i] = AppendBasicBlock(currentFunc, (sbyte*)p);

    for (int i = 0; i < clauses.Length; i++)
        if (clauses[i].expressionSwitchCase() is MiniGoCompilerParser.DefaultSwitchContext)
            defaultBlock = caseBlocks[i];

    LLVMBasicBlockRef nextTest;
    fixed (byte* p = S("test0")) nextTest= (clauses.Length > 0) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
    BuildBr(builder, nextTest);

    for (int i = 0; i < clauses.Length; i++)
    {
        var switchCase = clauses[i].expressionSwitchCase();

        if (switchCase is MiniGoCompilerParser.CaseSwitchContext caseCtx)
        {
            // Posicionar en el bloque de test actual y emitir comparaciones
            PositionBuilderAtEnd(builder, nextTest);
            var caseExprs = caseCtx.expressionList().expression();
            LLVMValueRef match = null;
            for (int j = 0; j < caseExprs.Length; j++)
            {
                LLVMValueRef caseVal = (LLVMValueRef) Visit(caseExprs[j]);
                LLVMValueRef cmp;
                
                fixed (byte* p = S("cmptmp")) cmp = BuildICmp(builder, LLVMIntPredicate.LLVMIntEQ, switchVal, caseVal, (sbyte*)p);
                if (match == null) match = cmp;
                else fixed (byte* p = S("ortmp")) match = BuildOr(builder, match, cmp, (sbyte*)p);
            }

            // Crear siguiente test y emitir UN solo terminator
            fixed (byte* p = S("test" + (i + 1)))
                nextTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
            BuildCondBr(builder, match, caseBlocks[i], nextTest);  // ← solo este
        }
        else // default
        {
            // El test anterior apunta acá — necesita un branch incondicional al siguiente test
            LLVMBasicBlockRef newTest;
            fixed (byte* p = S("test" + (i + 1)))
                newTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
            PositionBuilderAtEnd(builder, nextTest);
            BuildBr(builder, newTest);
            nextTest = newTest;
        }

        // Emitir cuerpo del case/default
        PositionBuilderAtEnd(builder, caseBlocks[i]);
        Visit(clauses[i].statementList());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, mergeBlock);
    }
    breakTargets.Pop();
    PositionBuilderAtEnd(builder, mergeBlock);
    return null;
    }

    public override object VisitExpressionCaseClauseList(MiniGoCompilerParser.ExpressionCaseClauseListContext context)
    {
        return null;
    }

    public override object VisitExpressionCaseClause(MiniGoCompilerParser.ExpressionCaseClauseContext context)
    {
        return null;
    }

    public override object VisitCaseSwitch(MiniGoCompilerParser.CaseSwitchContext context)
    {
        return null;
    }

    public override object VisitDefaultSwitch(MiniGoCompilerParser.DefaultSwitchContext context)
    {
        return null;
    }

    public override object VisitIdentifier(MiniGoCompilerParser.IdentifierContext context)
    {
        return null;
    }
}