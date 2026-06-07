using System.Diagnostics;
using System.Runtime.InteropServices;
using syntaxchecker.generated;
using System;
using LLVMSharp.Interop;
using MiniGoCompiler.typechecker;
using static LLVMSharp.Interop.LLVM;
using ArrayType = LLVMSharp.ArrayType;

namespace MiniGoCompiler.encoder;

// =============================================================================
//  MiniGoEncoder
// -----------------------------------------------------------------------------
//  LLVM IR code generator for the MiniGo language. Implemented as an ANTLR
//  visitor that traverses the parse tree produced by MiniGoCompilerParser and
//  emits LLVM IR instructions through the LLVMSharp interop layer.
//
//  The encoder performs the following responsibilities:
//
//    * Translation of MiniGo source constructs into LLVM IR, including
//      variable declarations, expressions, control flow, function definitions,
//      and composite types (arrays, slices, structs).
//    * Management of symbol references through dictionaries that map variable
//      names to their LLVM alloca pointers and corresponding LLVM types.
//    * Multi-pass processing of top-level declarations: type aliases are
//      registered first, then function signatures, and finally variable
//      declarations and function bodies are emitted.
//    * Native compilation pipeline: LLVM module verification, object file
//      emission via the target machine, linking with clang, and execution
//      of the resulting binary with output capture.
//
//  Each Visit* override returns either an LLVMValueRef representing the
//  computed value of an expression, a LinkedList<LLVMValueRef> for expression
//  lists, or null when no value is meaningful (statements and declarations).
// =============================================================================

/// <summary>
/// Visitor-based LLVM IR code generator for the MiniGo language.
/// Walks the parse tree, emits LLVM instructions through <see cref="LLVMBuilderRef"/>,
/// compiles and links the resulting module, and captures program output.
/// </summary>
public class MiniGoEncoder : MiniGoCompilerBaseVisitor<object>
{
    /// <summary>LLVM module that holds all generated IR (functions, globals, types).</summary>
    private LLVMModuleRef module;

    /// <summary>LLVM instruction builder used to emit IR instructions at the current insertion point.</summary>
    private LLVMBuilderRef builder;

    /// <summary>LLVM type reference for 32-bit integers (<c>i32</c>).</summary>
    private LLVMTypeRef intType;

    /// <summary>LLVM type reference for 64-bit floating point (<c>double</c>).</summary>
    private LLVMTypeRef floatType;

    /// <summary>LLVM type reference for rune/character values (<c>i8</c>).</summary>
    private LLVMTypeRef runeType;

    /// <summary>LLVM type reference for boolean values (<c>i1</c>).</summary>
    private LLVMTypeRef boolType;

    /// <summary>LLVM type reference for string pointers (<c>i8*</c>).</summary>
    private LLVMTypeRef stringType;

    /// <summary>Reference to the LLVM function currently being generated.</summary>
    private LLVMValueRef currentFunc;

    /// <summary>Maps variable names to their LLVM alloca/global pointers for load/store operations.</summary>
    private Dictionary<string, LLVMValueRef> referenceTable = new Dictionary<string, LLVMValueRef>();

    /// <summary>Maps variable names to their LLVM types, needed for typed load instructions.</summary>
    private Dictionary<string, LLVMTypeRef> typeTable = new Dictionary<string, LLVMTypeRef>();

    /// <summary>Maps user-defined type alias names to their resolved LLVM type representations.</summary>
    private Dictionary<string, LLVMTypeRef> userDefinedTypes = new Dictionary<string, LLVMTypeRef>();

    /// <summary>Stack of merge blocks for break statement targets in switch constructs.</summary>
    private Stack<LLVMBasicBlockRef> breakTargets = new Stack<LLVMBasicBlockRef>();

    /// <summary>Maps struct type handles to their ordered field name lists for name-based field access.</summary>
    private Dictionary<IntPtr, List<string>> structFieldNames = new Dictionary<IntPtr, List<string>>();

    /// <summary>Helper that converts a C# string to a null-terminated UTF-8 byte array for LLVM interop.</summary>
    private byte[] S(string name) => System.Text.Encoding.UTF8.GetBytes(name + "\0");

    /// <summary>Reference to the entry basic block of the current function, used for alloca placement.</summary>
    private LLVMBasicBlockRef entryBlock;

    public List<string> CodeGenErrors { get; } = new List<string>();

    /// <summary>
    /// Initializes a new encoder with default LLVM primitive type references.
    /// </summary>
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

    /// <summary>The generated LLVM IR as a text string.</summary>
    public string GeneratedIR { get; private set; } = "";

    /// <summary>Standard output captured from the compiled program's execution.</summary>
    public string ProgramOutput { get; private set; } = "";

    /// <summary>Error message if code generation, linking, or execution failed.</summary>
    public string ErrorMessage { get; private set; } = "";

    /// <summary>True if the program was successfully compiled, linked, and executed.</summary>
    public bool CompilationSuccess { get; private set; } = false;


    // -------------------------------------------------------------------------
    //  Program root and compilation pipeline
    // -------------------------------------------------------------------------

    /// <summary>
    /// Entry point for code generation. Initializes the LLVM backend, visits
    /// all top-level declarations to emit IR, verifies the module, compiles
    /// to an object file, links with clang, and executes the resulting binary.
    /// </summary>
    public override unsafe object VisitRoot(MiniGoCompilerParser.RootContext context)
    {
        LLVM.InitializeNativeTarget();
        LLVM.InitializeNativeAsmPrinter();
        LLVM.InitializeNativeAsmParser();

        this.module = LLVMModuleRef.CreateWithName("minigo");
        this.builder = this.module.Context.CreateBuilder();

        Visit(context.topDeclarationList());
        if (CodeGenErrors.Count > 0)
        {
            this.GeneratedIR = this.module.PrintToString();
            Cleanup();
            return null;
        }

        this.GeneratedIR = this.module.PrintToString();

        if (!this.GeneratedIR.Contains("define "))
        {
            this.CompilationSuccess = true;
            Cleanup();
            return null;
        }

        if (!this.module.TryVerify(LLVMVerifierFailureAction.LLVMPrintMessageAction, out string verifyMsg))
        {
            this.ErrorMessage = "Módulo LLVM inválido: " + verifyMsg;
            Cleanup();
            return null;
        }

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

        Directory.CreateDirectory("output");

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

        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool inFlatpak = !isWindows && (
            Environment.GetEnvironmentVariable("FLATPAK_ID") != null ||
            File.Exists("/.flatpak-info"));

        string exeFile = isWindows
            ? Path.Combine("output", "output.exe")
            : Path.Combine("output", "output");

        if (!LinkWithClang(objFile, exeFile, isWindows, inFlatpak))
        {
            DisposeTargetMachine(targetMachine);
            Cleanup();
            return null;
        }

        RunAndCapture(exeFile, isWindows, inFlatpak);

        DisposeTargetMachine(targetMachine);
        Cleanup();

        return null;
    }


    // -------------------------------------------------------------------------
    //  Compilation helpers (linking, execution, cleanup)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Links the compiled object file with clang to produce a native executable.
    /// Handles Flatpak sandboxed environments by delegating to <c>flatpak-spawn</c>.
    /// </summary>
    /// <param name="objFile">Path to the LLVM-generated object file.</param>
    /// <param name="exeFile">Desired path for the output executable.</param>
    /// <param name="isWindows">Whether the host OS is Windows.</param>
    /// <param name="inFlatpak">Whether the process is running inside a Flatpak sandbox.</param>
    /// <returns>True if linking succeeded, false otherwise.</returns>
    private bool LinkWithClang(string objFile, string exeFile, bool isWindows, bool inFlatpak)
    {
        string program;
        string args;

        if (!isWindows && inFlatpak)
        {
            program = "flatpak-spawn";
            args = "--host clang " + objFile + " -o " + exeFile;
        }
        else
        {
            program = "clang";
            args = objFile + " -o " + exeFile;
        }

        try
        {
            Process linker = new Process();
            linker.StartInfo.FileName = program;
            linker.StartInfo.Arguments = args;
            linker.StartInfo.UseShellExecute = false;
            linker.StartInfo.RedirectStandardError = true;
            linker.StartInfo.RedirectStandardOutput = true;
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


    /// <summary>
    /// Executes the linked binary and captures its standard output into
    /// <see cref="ProgramOutput"/>. Sets <see cref="CompilationSuccess"/>
    /// based on the process exit code.
    /// </summary>
    /// <param name="exeFile">Path to the executable to run.</param>
    /// <param name="isWindows">Whether the host OS is Windows.</param>
    /// <param name="inFlatpak">Whether the process is running inside a Flatpak sandbox.</param>
    private void RunAndCapture(string exeFile, bool isWindows, bool inFlatpak)
    {
        string program;
        string args;

        if (!isWindows && inFlatpak)
        {
            program = "flatpak-spawn";
            args = "--host ./" + exeFile;
        }
        else
        {
            program = isWindows ? exeFile : "./" + exeFile;
            args = "";
        }

        try
        {
            Process run = new Process();
            run.StartInfo.FileName = program;
            run.StartInfo.Arguments = args;
            run.StartInfo.UseShellExecute = false;
            run.StartInfo.RedirectStandardOutput = true;
            run.StartInfo.RedirectStandardError = true;
            run.Start();

            this.ProgramOutput = run.StandardOutput.ReadToEnd();
            string stderr = run.StandardError.ReadToEnd();
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
                this.CompilationSuccess = false;
            }
        }
        catch (Exception e)
        {
            this.ErrorMessage = "No se pudo ejecutar el programa: " + e.Message;
            this.CompilationSuccess = false;
        }
    }


    /// <summary>
    /// Disposes the LLVM builder and module to free native resources.
    /// </summary>
    private unsafe void Cleanup()
    {
        DisposeBuilder(this.builder);
        DisposeModule(this.module);
    }


    // -------------------------------------------------------------------------
    //  Top-level declarations (multi-pass processing)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Processes all top-level declarations in three passes: first registers
    /// user-defined types, then declares function signatures (without bodies),
    /// and finally emits global variables and function bodies.
    /// </summary>
    public unsafe override object VisitTopDeclarationList(MiniGoCompilerParser.TopDeclarationListContext context)
    {
        if (context.children == null) return null;

        foreach (var child in context.children)
            if (child is MiniGoCompilerParser.TypeDeclContext td)
            {
                try
                {
                    Visit(child);
                }
                catch (Exception ex)
                {
                    var token = (child as Antlr4.Runtime.ParserRuleContext)?.Start;
                    int line = token?.Line ?? 1;
                    int col = token?.Column ?? 0;
                    CodeGenErrors.Add("CODE GEN: " + ex.Message
                                                   + " [line " + line + ", col " + col + "]");
                }
            }

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

        foreach (var child in context.children)
        {
            if (child is MiniGoCompilerParser.TypeDeclContext) continue;
            Visit(child);
        }

        return null;
    }


    // -------------------------------------------------------------------------
    //  Variable declarations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Visits a variable declaration, delegating to either a single or
    /// grouped (inner) variable declaration form.
    /// </summary>
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

    /// <summary>
    /// Visits each single variable declaration within a grouped <c>var (...)</c> block.
    /// </summary>
    public override object VisitInnerVarDecls(MiniGoCompilerParser.InnerVarDeclsContext context)
    {

        foreach (MiniGoCompilerParser.SingleVarDeclContext svd in context.singleVarDecl())
        {
            Visit(svd);
        }

        return null;
    }


    // -------------------------------------------------------------------------
    //  Type resolution
    // -------------------------------------------------------------------------

    /// <summary>
    /// Resolves a MiniGo type declaration context into its corresponding LLVM
    /// type reference. Handles primitive types, user-defined aliases,
    /// parenthesized types, fixed-size arrays, slices, and structs.
    /// </summary>
    /// <remarks>
    /// Slices are represented as <c>{ T*, i32 len, i32 cap }</c> structs.
    /// Unknown type names fall back to <c>i32</c>.
    /// </remarks>
    private unsafe LLVMTypeRef ResolveLLVMType(MiniGoCompilerParser.DeclTypeContext ctx)
    {
        if (ctx is MiniGoCompilerParser.TypeDenoterDeclTypeContext typeDenoter)
        {
            string name = typeDenoter.identifier().IDENTIFIER().GetText();
            return name switch
            {
                "int" => intType,
                "float64" => floatType,
                "string" => stringType,
                "rune" => runeType,
                "bool" => boolType,
                _ => userDefinedTypes.TryGetValue(name, out LLVMTypeRef resolved)
                    ? resolved
                    : intType
            };
        }

        if (ctx is MiniGoCompilerParser.GroupDeclTypeContext group)
            return ResolveLLVMType(group.declType());

        if (ctx is MiniGoCompilerParser.ArrayTypeDeclContext arrayCtx)
        {
            var arrayDecl = arrayCtx.arrayDeclType();
            uint size = uint.Parse(arrayDecl.INTLITERAL().GetText());
            LLVMTypeRef elementType = ResolveLLVMType(arrayDecl.declType());
            return ArrayType(elementType, size);
        }

        if (ctx is MiniGoCompilerParser.SliceTypeDeclContext sliceCtx)
        {
            var sliceDecl = sliceCtx.sliceDeclType();
            LLVMTypeRef elementType = ResolveLLVMType(sliceDecl.declType());
            LLVMTypeRef pointerToElement = PointerType(elementType, 0);
            LLVMTypeRef[] sliceFields = { pointerToElement, intType, intType };
            return LLVMTypeRef.CreateStruct(sliceFields, false);
        }

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
                        fieldNamesList.Add(id.Symbol.Text);
                    }
                }
            }

            LLVMTypeRef structType = LLVMTypeRef.CreateStruct(fieldTypes.ToArray(), false);
            structFieldNames[structType.Handle] = fieldNamesList;
            return structType;
        }

        return intType;
    }


    // -------------------------------------------------------------------------
    //  Low-level LLVM helpers (alloca, load, global string, function call)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emits a <c>load</c> instruction for the given type and pointer.
    /// </summary>
    public unsafe LLVMValueRef LoadVar(LLVMTypeRef type, LLVMValueRef ptr, string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + "\0"))
        {
            return BuildLoad2(builder, type, ptr, (sbyte*)p);
        }
    }

    /// <summary>
    /// Emits an <c>alloca</c> instruction at the current insertion point.
    /// </summary>
    public unsafe LLVMValueRef AllocaVar(LLVMTypeRef type, string name)
    {
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + "\0"))
        {
            return BuildAlloca(builder, type, (sbyte*)p);
        }
    }

    /// <summary>
    /// Emits an <c>alloca</c> instruction at the beginning of the current
    /// function's entry block, then restores the builder position. This
    /// ensures all allocas are in the entry block for correct SSA form.
    /// </summary>
    public unsafe LLVMValueRef AllocaInEntry(LLVMTypeRef type, string name)
    {
        var current = GetInsertBlock(builder);
        LLVMValueRef firstInst = GetFirstInstruction(entryBlock);

        if (firstInst.Handle != IntPtr.Zero)
            PositionBuilderBefore(builder, firstInst);
        else
            PositionBuilderAtEnd(builder, entryBlock);

        LLVMValueRef alloca;
        fixed (byte* p = S(name)) alloca = BuildAlloca(builder, type, (sbyte*)p);

        PositionBuilderAtEnd(builder, current);
        return alloca;
    }


    // -------------------------------------------------------------------------
    //  Typed and inferred variable declarations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emits code for a typed variable declaration (<c>var x int = expr</c>).
    /// Handles both global variables (via <c>AddGlobal</c>) and local
    /// variables (via entry-block <c>alloca</c>).
    /// </summary>
    public unsafe override object VisitTypedVarDecl(MiniGoCompilerParser.TypedVarDeclContext context)
    {
        LLVMTypeRef type = ResolveLLVMType((context.declType()));
        var identifiers = context.identifierList().IDENTIFIER();

        LinkedList<LLVMValueRef> values = (LinkedList<LLVMValueRef>)Visit(context.expressionList());
        for (int i = 0; i < identifiers.Length; i++)
        {
            string name = identifiers[i].Symbol.Text;
            if (currentFunc.Handle == IntPtr.Zero)
            {
                LLVMValueRef global;
                fixed (byte* p = S(name)) global = AddGlobal(module, type, (sbyte*)p);

                LLVMValueRef initialValue = values.ElementAt(i);

                SetInitializer(global, initialValue);

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

    /// <summary>
    /// Creates a global null-terminated string constant and returns a pointer to it.
    /// </summary>
    private unsafe LLVMValueRef GlobalString(string text, string name)
    {
        if (currentFunc.Handle == IntPtr.Zero)
            return ConstNull(stringType);
        fixed (byte* t = System.Text.Encoding.UTF8.GetBytes(text + "\0"))
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(name + "\0"))
        {
            return BuildGlobalStringPtr(builder, (sbyte*)t, (sbyte*)p);
        }
    }

    /// <summary>
    /// Emits a <c>call</c> instruction to the given function with the provided arguments.
    /// </summary>
    private unsafe LLVMValueRef CallFunction(
        LLVMTypeRef funcType,
        LLVMValueRef func,
        LLVMValueRef[] args,
        string name)
    {
        LLVMTypeRef returnType = GetReturnType(funcType);

        string safeName = returnType == VoidType() ? "" : name;

        fixed (LLVMValueRef* argsPtr = args)
        fixed (byte* p = System.Text.Encoding.UTF8.GetBytes(safeName + "\0"))
        {
            return BuildCall2(
                builder,
                funcType,
                func,
                (LLVMOpaqueValue**)argsPtr,
                (uint)args.Length,
                (sbyte*)p
            );
        }
    }


    /// <summary>
    /// Emits code for an inferred variable declaration (<c>var x = expr</c>).
    /// The type is derived from the expression's LLVM type via <c>TypeOf</c>.
    /// </summary>
    public unsafe override object VisitInferredVarDecl(MiniGoCompilerParser.InferredVarDeclContext context)
    {
        LLVMTypeRef type;
        var identifiers = context.identifierList().IDENTIFIER();
        LinkedList<LLVMValueRef> values = (LinkedList<LLVMValueRef>)Visit(context.expressionList());

        for (int i = 0; i < identifiers.Length; i++)
        {
            type = TypeOf(values.ElementAt(i));
            string name = identifiers[i].Symbol.Text;
            if (currentFunc.Handle == IntPtr.Zero)
            {
                LLVMValueRef global;
                fixed (byte* p = S(name)) global = AddGlobal(module, type, (sbyte*)p);
                SetInitializer(global, values.ElementAt(i));
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

    /// <summary>
    /// Delegates a no-expression variable declaration to <see cref="VisitSingleVarDeclNoExps"/>.
    /// </summary>
    public override object VisitNoExpressionVarDecl(MiniGoCompilerParser.NoExpressionVarDeclContext context)
    {
        return Visit(context.singleVarDeclNoExps());
    }

    /// <summary>
    /// Emits code for a variable declaration without an initializer expression.
    /// Variables are zero-initialized with <c>ConstNull</c>.
    /// </summary>
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
            BuildStore(builder, ConstNull(type), alloca);
            referenceTable[name] = alloca;
            typeTable[name] = type;
        }

        return null;
    }


    // -------------------------------------------------------------------------
    //  Type declarations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Visits a type declaration, delegating to single or grouped forms.
    /// </summary>
    public override object VisitTypeDecl(MiniGoCompilerParser.TypeDeclContext context)
    {

        if (context.singleTypeDecl() != null)
            Visit(context.singleTypeDecl());
        if (context.innerTypeDecls() != null)
            Visit(context.innerTypeDecls());
        return null;
    }

    /// <summary>
    /// Visits each type declaration inside a grouped <c>type (...)</c> block.
    /// </summary>
    public override object VisitInnerTypeDecls(MiniGoCompilerParser.InnerTypeDeclsContext context)
    {
        foreach (var decl in context.singleTypeDecl())
            Visit(decl);
        return null;
    }

    /// <summary>
    /// Registers a single user-defined type alias by resolving its underlying
    /// LLVM type and storing it in <see cref="userDefinedTypes"/>.
    /// </summary>
    public override object VisitSingleTypeDecl(MiniGoCompilerParser.SingleTypeDeclContext context)
    {
        LLVMTypeRef resolved = ResolveLLVMType(context.declType());
        string name = context.IDENTIFIER().GetText();
        userDefinedTypes[name] = resolved;
        return null;
    }


    // -------------------------------------------------------------------------
    //  Function declarations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emits a complete function definition: creates the LLVM function entry
    /// block, allocas for parameters, visits the function body, and adds a
    /// default terminator if the body does not end with a return statement.
    /// Saves and restores the reference/type tables to implement lexical scoping.
    /// </summary>
    public unsafe override object VisitFuncDecl(MiniGoCompilerParser.FuncDeclContext context)
    {

        var front = context.funcFrontDecl();
        string funcName = front.IDENTIFIER().GetText();
        var savedRefs = new Dictionary<string, LLVMValueRef>(referenceTable);
        var savedTypes = new Dictionary<string, LLVMTypeRef>(typeTable);


        LLVMTypeRef retType = front.declType() != null
            ? ResolveLLVMType(front.declType())
            : VoidType();
        if (funcName == "main")
        {
            retType = intType;
        }

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

        LLVMValueRef func;
        fixed (byte* p = S(funcName)) func = GetNamedFunction(module, (sbyte*)p);
        LLVMTypeRef funcType = GlobalGetValueType(func);
        currentFunc = func;

        LLVMBasicBlockRef entry = func.AppendBasicBlock("entry");
        this.entryBlock = entry;
        PositionBuilderAtEnd(builder, entry);

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

        Visit(context.block());

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

    /// <summary>No-op: function front declarations are handled by <see cref="VisitFuncDecl"/>.</summary>
    public override object VisitFuncFrontDecl(MiniGoCompilerParser.FuncFrontDeclContext context)
    {
        return null;
    }

    /// <summary>No-op: function argument declarations are handled by <see cref="VisitFuncDecl"/>.</summary>
    public override object VisitFuncArgDecls(MiniGoCompilerParser.FuncArgDeclsContext context)
    {
        return null;
    }

    /// <summary>No-op: grouped type contexts are resolved through <see cref="ResolveLLVMType"/>.</summary>
    public override object VisitGroupDeclType(MiniGoCompilerParser.GroupDeclTypeContext context)
    {
        return null;
    }

    /// <summary>No-op: type denoter contexts are resolved through <see cref="ResolveLLVMType"/>.</summary>
    public override object VisitTypeDenoterDeclType(MiniGoCompilerParser.TypeDenoterDeclTypeContext context)
    {
        return null;
    }

    /// <summary>No-op: slice type contexts are resolved through <see cref="ResolveLLVMType"/>.</summary>
    public override object VisitSliceTypeDecl(MiniGoCompilerParser.SliceTypeDeclContext context)
    {
        return null;
    }

    /// <summary>No-op: array type contexts are resolved through <see cref="ResolveLLVMType"/>.</summary>
    public override object VisitArrayTypeDecl(MiniGoCompilerParser.ArrayTypeDeclContext context)
    {
        return null;
    }

    /// <summary>No-op: struct type contexts are resolved through <see cref="ResolveLLVMType"/>.</summary>
    public override object VisitStructTypeDecl(MiniGoCompilerParser.StructTypeDeclContext context)
    {
        return null;
    }

    /// <summary>No-op: slice declaration types are resolved through <see cref="ResolveLLVMType"/>.</summary>
    public override object VisitSliceDeclType(MiniGoCompilerParser.SliceDeclTypeContext context)
    {
        return null;
    }

    /// <summary>No-op: array declaration types are resolved through <see cref="ResolveLLVMType"/>.</summary>
    public override object VisitArrayDeclType(MiniGoCompilerParser.ArrayDeclTypeContext context)
    {
        return null;
    }

    /// <summary>No-op: struct declaration types are resolved through <see cref="ResolveLLVMType"/>.</summary>
    public override object VisitStructDeclType(MiniGoCompilerParser.StructDeclTypeContext context)
    {
        return null;
    }

    /// <summary>No-op: struct member declarations are resolved through <see cref="ResolveLLVMType"/>.</summary>
    public override object VisitStructMemDecls(MiniGoCompilerParser.StructMemDeclsContext context)
    {
        return null;
    }

    /// <summary>No-op: identifier lists are handled inline by their parent visitors.</summary>
    public override object VisitIdentifierList(MiniGoCompilerParser.IdentifierListContext context)
    {
        return null;
    }


    // -------------------------------------------------------------------------
    //  Expressions
    // -------------------------------------------------------------------------

    /// <summary>
    /// Visits an expression list and returns a <c>LinkedList&lt;LLVMValueRef&gt;</c>
    /// containing the emitted value for each expression.
    /// </summary>
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

    /// <summary>Delegates to the inner primary expression.</summary>
    public override object VisitPrimaryExpr(MiniGoCompilerParser.PrimaryExprContext context)
    {
        return Visit(context.primaryExpression());
    }

    /// <summary>
    /// Emits a unary negation (<c>-x</c>). Uses <c>fneg</c> for floats
    /// and <c>neg</c> for integers.
    /// </summary>
    public unsafe override object VisitUnarySubExpr(MiniGoCompilerParser.UnarySubExprContext context)
    {
        LLVMValueRef val = (LLVMValueRef)Visit(context.expression());
        LLVMValueRef result;
        if (TypeOf(val) == floatType)
            fixed (byte* p = S("fnegtmp"))
                result = BuildFNeg(builder, val, (sbyte*)p);
        else
            fixed (byte* p = S("negtmp"))
                result = BuildNeg(builder, val, (sbyte*)p);
        return result;
    }

    /// <summary>
    /// Emits additive-level binary operations: addition (<c>+</c>),
    /// subtraction (<c>-</c>), bitwise OR (<c>|</c>), and XOR (<c>^</c>).
    /// Selects float or integer variants based on operand types.
    /// </summary>
    public unsafe override object VisitAddExpr(MiniGoCompilerParser.AddExprContext context)
    {
        LLVMValueRef left = (LLVMValueRef)Visit(context.expression(0));
        LLVMValueRef right = (LLVMValueRef)Visit(context.expression(1));
        if (TypeOf(left) == stringType || TypeOf(right) == stringType)
            throw new Exception("Arithmetic operations on strings are not supported in code generation");
        LLVMValueRef result;
        bool isFloat = TypeOf(left) == floatType || TypeOf(right) == floatType;

        if (context.ADD() != null)
        {
            if (isFloat)
                fixed (byte* p = S("faddtmp"))
                    result = BuildFAdd(builder, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("addtmp"))
                    result = BuildAdd(builder, left, right, (sbyte*)p);
        }
        else if (context.SUB() != null)
        {
            if (isFloat)
                fixed (byte* p = S("fsubtmp"))
                    result = BuildFSub(builder, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("subtmp"))
                    result = BuildSub(builder, left, right, (sbyte*)p);
        }
        else if (context.OR() != null)
        {
            fixed (byte* p = S("ortmp")) result = BuildOr(builder, left, right, (sbyte*)p);
        }
        else
        {
            fixed (byte* p = S("xortmp")) result = BuildXor(builder, left, right, (sbyte*)p);
        }

        return result;
    }

    /// <summary>
    /// Emits multiplicative-level binary operations: multiplication (<c>*</c>),
    /// division (<c>/</c>), modulo (<c>%</c>), left shift (<c>&lt;&lt;</c>),
    /// right shift (<c>&gt;&gt;</c>), bitwise AND (<c>&amp;</c>), and
    /// bit clear (<c>&amp;^</c>). Selects float or integer variants as needed.
    /// </summary>
    public unsafe override object VisitMulExpr(MiniGoCompilerParser.MulExprContext context)
    {
        LLVMValueRef left = (LLVMValueRef)Visit(context.expression(0));
        LLVMValueRef right = (LLVMValueRef)Visit(context.expression(1));
        LLVMValueRef result;
        bool isFloat = TypeOf(left) == floatType || TypeOf(right) == floatType;

        if (context.MUL() != null)
        {
            if (isFloat)
                fixed (byte* p = S("fmultmp"))
                    result = BuildFMul(builder, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("multmp"))
                    result = BuildMul(builder, left, right, (sbyte*)p);
        }
        else if (context.DIV() != null)
        {
            if (isFloat)
                fixed (byte* p = S("fdivtmp"))
                    result = BuildFDiv(builder, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("divtmp"))
                    result = BuildSDiv(builder, left, right, (sbyte*)p);
        }
        else if (context.MOD() != null)
        {
            if (isFloat)
                fixed (byte* p = S("fmodtmp"))
                    result = BuildFRem(builder, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("modtmp"))
                    result = BuildSRem(builder, left, right, (sbyte*)p);
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
        else
        {
            LLVMValueRef notRight;
            fixed (byte* p = S("nottmp")) notRight = BuildNot(builder, right, (sbyte*)p);
            fixed (byte* p = S("andnottmp")) result = BuildAnd(builder, left, notRight, (sbyte*)p);
        }

        return result;
    }

    /// <summary>
    /// Emits a short-circuit logical OR (<c>||</c>). If the left operand is
    /// true, the right operand is not evaluated. Uses a phi node to merge
    /// the result from both paths.
    /// </summary>
    public unsafe override object VisitOrExpr(MiniGoCompilerParser.OrExprContext context)
    {
        LLVMValueRef left = (LLVMValueRef)Visit(context.expression(0));

        LLVMBasicBlockRef rhsBlock, mergeBlock;
        fixed (byte* p = S("or.rhs")) rhsBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("or.merge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);

        LLVMBasicBlockRef leftBlock = GetInsertBlock(builder);
        BuildCondBr(builder, left, mergeBlock, rhsBlock);

        PositionBuilderAtEnd(builder, rhsBlock);
        LLVMValueRef right = (LLVMValueRef)Visit(context.expression(1));
        LLVMBasicBlockRef rhsEnd = GetInsertBlock(builder);
        BuildBr(builder, mergeBlock);

        PositionBuilderAtEnd(builder, mergeBlock);
        LLVMValueRef phi;
        fixed (byte* p = S("or.result")) phi = BuildPhi(builder, boolType, (sbyte*)p);

        LLVMValueRef trueval = ConstInt(boolType, 1, 0);
        LLVMValueRef[] vals = { trueval, right };
        LLVMBasicBlockRef[] blocks = { leftBlock, rhsEnd };

        fixed (LLVMValueRef* vp = vals)
        fixed (LLVMBasicBlockRef* bp = blocks)
        {
            AddIncoming(phi, (LLVMOpaqueValue**)vp, (LLVMOpaqueBasicBlock**)bp, 2);
        }

        return phi;
    }

    /// <summary>
    /// Emits a bitwise complement (<c>^x</c>) using LLVM's NOT instruction.
    /// </summary>
    public unsafe override object VisitUnaryHatExpr(MiniGoCompilerParser.UnaryHatExprContext context)
    {

        LLVMValueRef val = (LLVMValueRef)Visit(context.expression());
        LLVMValueRef result;
        fixed (byte* p = S("xortmp")) result = BuildNot(builder, val, (sbyte*)p);
        return result;
    }

    /// <summary>
    /// Emits a unary plus (<c>+x</c>), which is a no-op that returns the operand unchanged.
    /// </summary>
    public override object VisitUnaryAddExpr(MiniGoCompilerParser.UnaryAddExprContext context)
    {
        return Visit(context.expression());
    }

    /// <summary>
    /// Emits relational comparison operations: <c>==</c>, <c>!=</c>, <c>&lt;</c>,
    /// <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>. Uses <c>fcmp</c> for float
    /// operands and <c>icmp</c> for integer operands.
    /// </summary>
    public unsafe override object VisitRelExpr(MiniGoCompilerParser.RelExprContext context)
    {
        LLVMValueRef left = (LLVMValueRef)Visit(context.expression(0));
        LLVMValueRef right = (LLVMValueRef)Visit(context.expression(1));
        LLVMValueRef result;
        bool isFloat = TypeOf(left) == floatType || TypeOf(right) == floatType;

        if (context.EQEQ() != null)
        {
            if (isFloat)
                fixed (byte* p = S("eqtmp"))
                    result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOEQ, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("eqtmp"))
                    result = BuildICmp(builder, LLVMIntPredicate.LLVMIntEQ, left, right, (sbyte*)p);
        }
        else if (context.NOTEQ() != null)
        {
            if (isFloat)
                fixed (byte* p = S("netmp"))
                    result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealONE, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("netmp"))
                    result = BuildICmp(builder, LLVMIntPredicate.LLVMIntNE, left, right, (sbyte*)p);
        }
        else if (context.LESS() != null)
        {
            if (isFloat)
                fixed (byte* p = S("lttmp"))
                    result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOLT, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("lttmp"))
                    result = BuildICmp(builder, LLVMIntPredicate.LLVMIntSLT, left, right, (sbyte*)p);
        }
        else if (context.MORET() != null)
        {
            if (isFloat)
                fixed (byte* p = S("gttmp"))
                    result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOGT, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("gttmp"))
                    result = BuildICmp(builder, LLVMIntPredicate.LLVMIntSGT, left, right, (sbyte*)p);
        }
        else if (context.LESSEQ() != null)
        {
            if (isFloat)
                fixed (byte* p = S("letmp"))
                    result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOLE, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("letmp"))
                    result = BuildICmp(builder, LLVMIntPredicate.LLVMIntSLE, left, right, (sbyte*)p);
        }
        else
        {
            if (isFloat)
                fixed (byte* p = S("getmp"))
                    result = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOGE, left, right, (sbyte*)p);
            else
                fixed (byte* p = S("getmp"))
                    result = BuildICmp(builder, LLVMIntPredicate.LLVMIntSGE, left, right, (sbyte*)p);
        }

        return result;
    }

    /// <summary>
    /// Emits a logical NOT (<c>!x</c>) using LLVM's NOT instruction on an <c>i1</c> value.
    /// </summary>
    public unsafe override object VisitUnaryNotExpr(MiniGoCompilerParser.UnaryNotExprContext context)
    {
        LLVMValueRef val = (LLVMValueRef)Visit(context.expression());
        LLVMValueRef result;
        fixed (byte* p = S("nottmp")) result = BuildNot(builder, val, (sbyte*)p);
        return result;
    }

    /// <summary>
    /// Emits a short-circuit logical AND (<c>&amp;&amp;</c>). If the left operand
    /// is false, the right operand is not evaluated. Uses a phi node to merge
    /// the result from both paths.
    /// </summary>
    public unsafe override object VisitAndExpr(MiniGoCompilerParser.AndExprContext context)
    {
        LLVMValueRef left = (LLVMValueRef)Visit(context.expression(0));

        LLVMBasicBlockRef rhsBlock, mergeBlock;
        fixed (byte* p = S("and.rhs")) rhsBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("and.merge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);

        LLVMBasicBlockRef leftBlock = GetInsertBlock(builder);
        BuildCondBr(builder, left, rhsBlock, mergeBlock);

        PositionBuilderAtEnd(builder, rhsBlock);
        LLVMValueRef right = (LLVMValueRef)Visit(context.expression(1));
        LLVMBasicBlockRef rhsEnd = GetInsertBlock(builder);
        BuildBr(builder, mergeBlock);

        PositionBuilderAtEnd(builder, mergeBlock);
        LLVMValueRef phi;
        fixed (byte* p = S("and.result")) phi = BuildPhi(builder, boolType, (sbyte*)p);

        LLVMValueRef falseval = ConstInt(boolType, 0, 0);
        LLVMValueRef[] vals = { falseval, right };
        LLVMBasicBlockRef[] blocks = { leftBlock, rhsEnd };

        fixed (LLVMValueRef* vp = vals)
        fixed (LLVMBasicBlockRef* bp = blocks)
        {
            AddIncoming(phi, (LLVMOpaqueValue**)vp, (LLVMOpaqueBasicBlock**)bp, 2);
        }

        return phi;
    }


    // -------------------------------------------------------------------------
    //  Primary expressions (operands, indexing, selectors, calls, builtins)
    // -------------------------------------------------------------------------

    /// <summary>Delegates to the inner length expression.</summary>
    public override object VisitLengthPrimaryExpr(MiniGoCompilerParser.LengthPrimaryExprContext context)
    {
        return Visit(context.lengthExpression());
    }

    /// <summary>Delegates to the inner operand.</summary>
    public override object VisitOperandPrimaryExpr(MiniGoCompilerParser.OperandPrimaryExprContext context)
    {
        return Visit(context.operand());
    }

    /// <summary>Delegates to the inner append expression.</summary>
    public override object VisitAppendPrimaryExpr(MiniGoCompilerParser.AppendPrimaryExprContext context)
    {
        return Visit(context.appendExpression());
    }

    /// <summary>
    /// Emits an array element access by computing a GEP pointer and loading the value.
    /// </summary>
    public unsafe override object VisitIndexPrimaryExpr(MiniGoCompilerParser.IndexPrimaryExprContext context)
    {
        LLVMValueRef elementPtr = GetArrayElementPointer(context, out LLVMTypeRef elementType);
        return LoadVar(elementType, elementPtr, "array_elem_load");
    }

    /// <summary>
    /// Emits a struct field access. Resolves the field index by name using
    /// <see cref="structFieldNames"/>, computes a GEP to the field, and loads it.
    /// </summary>
    public unsafe override object VisitSelectorPrimaryExpr(MiniGoCompilerParser.SelectorPrimaryExprContext ctx)
    {
        string structName = ctx.primaryExpression().GetText();
        string fieldName = ctx.selector().IDENTIFIER().GetText();
        LLVMValueRef structPtr = referenceTable[structName];
        LLVMTypeRef structType = typeTable[structName];

        uint fieldIndex = 0;
        if (structFieldNames.TryGetValue(structType.Handle, out List<string> fields))
        {
            int idx = fields.IndexOf(fieldName);
            if (idx >= 0) fieldIndex = (uint)idx;
        }

        LLVMValueRef fieldPtr;
        fixed (byte* p = S("fieldptr"))
        {
            fieldPtr = BuildStructGEP2(builder, structType, structPtr, fieldIndex, (sbyte*)p);
        }

        LLVMTypeRef fieldType = StructGetTypeAtIndex(structType, fieldIndex);
        return LoadVar(fieldType, fieldPtr, fieldName);
    }

    /// <summary>
    /// Emits a function call expression. Resolves the callee by name,
    /// evaluates argument expressions, and emits a <c>call</c> instruction.
    /// </summary>
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
                args[i] = (LLVMValueRef)Visit(exprs[i]);
        }

        return CallFunction(funcType, func, args, "calltmp");
    }

    /// <summary>Delegates to the inner cap expression.</summary>
    public unsafe override object VisitCapPrimaryExpr(MiniGoCompilerParser.CapPrimaryExprContext context)
    {
        return Visit(context.capExpression());
    }


    // -------------------------------------------------------------------------
    //  Operands and literals
    // -------------------------------------------------------------------------

    /// <summary>Delegates to the inner literal.</summary>
    public override object VisitLiteralOperand(MiniGoCompilerParser.LiteralOperandContext context)
    {
        return Visit(context.literal());
    }

    /// <summary>
    /// Emits a variable reference or boolean literal. Recognizes <c>true</c>
    /// and <c>false</c> as constant <c>i1</c> values; all other identifiers
    /// are loaded from their alloca pointers.
    /// </summary>
    public unsafe override object VisitIdOperand(MiniGoCompilerParser.IdOperandContext context)
    {
        string name = context.identifier().GetText();

        if (name == "true")
        {
            LLVMValueRef t = ConstInt(boolType, 1, 0);
            return t;
        }

        if (name == "false")
        {
            LLVMValueRef f = ConstInt(boolType, 0, 0);
            return f;
        }

        LLVMTypeRef type = typeTable[name];
        LLVMValueRef value = referenceTable[name];
        LLVMValueRef variable = LoadVar(type, value, name);
        return variable;
    }

    /// <summary>Delegates to the inner parenthesized expression.</summary>
    public override object VisitGroupOperand(MiniGoCompilerParser.GroupOperandContext context)
    {
        return Visit(context.expression());
    }

    /// <summary>Emits an integer literal as an LLVM <c>i32</c> constant.</summary>
    public unsafe override object VisitIntLiteral(MiniGoCompilerParser.IntLiteralContext context)
    {
        long value = long.Parse(context.INTLITERAL().GetText());
        LLVMValueRef result = ConstInt(intType, (ulong)value, 0);
        return result;
    }

    /// <summary>Emits a floating-point literal as an LLVM <c>double</c> constant.</summary>
    public unsafe override object VisitFloatLiteral(MiniGoCompilerParser.FloatLiteralContext context)
    {
        double value = double.Parse(context.FLOATLITERAL().GetText(),
            System.Globalization.CultureInfo.InvariantCulture);
        LLVMValueRef result = ConstReal(floatType, value);
        return result;
    }

    /// <summary>
    /// Emits a rune literal as an LLVM <c>i8</c> constant, handling escape sequences.
    /// </summary>
    public unsafe override object VisitRuneLiteral(MiniGoCompilerParser.RuneLiteralContext context)
    {
        string text = context.RUNELITERAL().GetText();
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

        LLVMValueRef result = ConstInt(runeType, (ulong)c, 0);
        return result;
    }

    /// <summary>Emits a raw string literal (backtick-delimited) as a global string constant.</summary>
    public override object VisitRawStringLiteral(MiniGoCompilerParser.RawStringLiteralContext context)
    {
        string text = context.RAWSTRINGLITERAL().GetText();
        string content = text.Substring(1, text.Length - 2);
        return GlobalString(content, "str");
    }

    /// <summary>Emits an interpreted string literal (quote-delimited) as a global string constant.</summary>
    public override object VisitInterpretedStringLiteral(MiniGoCompilerParser.InterpretedStringLiteralContext context)
    {
        string text = context.INTERPRETEDSTRINGLITERAL().GetText();
        string content = text.Substring(1, text.Length - 2);
        return GlobalString(content, "str");
        //throw new Exception("Only raw string literals (backticks) are supported in code generation, as specified in the language definition");
    }

    /// <summary>Delegates to the inner index expression.</summary>
    public override object VisitIndex(MiniGoCompilerParser.IndexContext context)
    {
        return Visit(context.expression());
    }

    /// <summary>Delegates to the inner argument expression list.</summary>
    public override object VisitArguments(MiniGoCompilerParser.ArgumentsContext context)
    {
        return Visit(context.expressionList());
    }

    /// <summary>No-op: selectors are handled by <see cref="VisitSelectorPrimaryExpr"/>.</summary>
    public override object VisitSelector(MiniGoCompilerParser.SelectorContext context)
    {
        return null;
    }


    // -------------------------------------------------------------------------
    //  Built-in functions (append, len, cap)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the byte size of a given LLVM type for memory allocation calculations.
    /// </summary>
    private uint GetTypeSize(LLVMTypeRef type)
    {
        if (type == intType) return 4;
        if (type == floatType) return 8;
        if (type == runeType) return 1;
        if (type == boolType) return 1;
        if (type == stringType) return 8;
        return 4;
    }

    /// <summary>
    /// Emits code for the <c>append(slice, element)</c> built-in. Allocates a
    /// new buffer via <c>malloc</c>, copies old elements via <c>memcpy</c>,
    /// stores the new element at the end, and returns an updated slice struct
    /// with incremented length and capacity.
    /// </summary>
    public unsafe override object VisitAppendExpression(MiniGoCompilerParser.AppendExpressionContext context)
    {

        LLVMValueRef sliceVal = (LLVMValueRef)Visit(context.expression(0));
        LLVMValueRef newElement = (LLVMValueRef)Visit(context.expression(1));
        LLVMTypeRef elemType = TypeOf(newElement);

        LLVMValueRef oldPtr, oldLen, oldCap;
        fixed (byte* p = S("ptr")) oldPtr = BuildExtractValue(builder, sliceVal, 0, (sbyte*)p);
        fixed (byte* p = S("len")) oldLen = BuildExtractValue(builder, sliceVal, 1, (sbyte*)p);
        fixed (byte* p = S("cap")) oldCap = BuildExtractValue(builder, sliceVal, 2, (sbyte*)p);

        LLVMValueRef one = ConstInt(intType, 1, 0);
        LLVMValueRef newLen;
        fixed (byte* p = S("newlen")) newLen = BuildAdd(builder, oldLen, one, (sbyte*)p);

        LLVMValueRef elemSize = ConstInt(intType, GetTypeSize(elemType), 0);
        LLVMValueRef totalBytes;
        fixed (byte* p = S("bytes")) totalBytes = BuildMul(builder, newLen, elemSize, (sbyte*)p);
        LLVMValueRef totalBytes64;
        fixed (byte* p = S("bytes64"))
            totalBytes64 = BuildZExt(builder, totalBytes, Int64Type(), (sbyte*)p);

        LLVMValueRef mallocFunc;
        fixed (byte* p = S("malloc")) mallocFunc = GetNamedFunction(module, (sbyte*)p);
        if (mallocFunc.Handle == IntPtr.Zero)
        {
            LLVMTypeRef sizeT = Int64Type();
            LLVMTypeRef mallocType = LLVMTypeRef.CreateFunction(stringType, new[] { sizeT }, false);
            mallocFunc = module.AddFunction("malloc", mallocType);
        }

        LLVMTypeRef mallocFuncType = GlobalGetValueType(mallocFunc);

        LLVMValueRef newBuf = CallFunction(mallocFuncType, mallocFunc, new[] { totalBytes64 }, "newbuf");

        LLVMTypeRef elemPtrType = PointerType(elemType, 0);
        LLVMValueRef newPtr;
        fixed (byte* p = S("newptr")) newPtr = BuildBitCast(builder, newBuf, elemPtrType, (sbyte*)p);

        LLVMValueRef oldBytes;
        fixed (byte* p = S("oldbytes")) oldBytes = BuildMul(builder, oldLen, elemSize, (sbyte*)p);

        LLVMValueRef memcpyFunc;
        fixed (byte* p = S("memcpy")) memcpyFunc = GetNamedFunction(module, (sbyte*)p);
        if (memcpyFunc.Handle == IntPtr.Zero)
        {
            LLVMTypeRef sizeT = Int64Type();
            LLVMTypeRef memcpyType = LLVMTypeRef.CreateFunction(
                stringType, new[] { stringType, stringType, sizeT }, false);
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

        LLVMValueRef[] gepIndices = { oldLen };
        LLVMValueRef newElemPtr;
        fixed (LLVMValueRef* idxPtr = gepIndices)
        fixed (byte* p = S("elemptr"))
        {
            newElemPtr = BuildGEP2(builder, elemType, newPtr, (LLVMOpaqueValue**)idxPtr, 1, (sbyte*)p);
        }

        BuildStore(builder, newElement, newElemPtr);

        LLVMTypeRef sliceType = TypeOf(sliceVal);
        LLVMValueRef newSlice = ConstNull(sliceType);
        fixed (byte* p = S("s1")) newSlice = BuildInsertValue(builder, newSlice, newPtr, 0, (sbyte*)p);
        fixed (byte* p = S("s2")) newSlice = BuildInsertValue(builder, newSlice, newLen, 1, (sbyte*)p);
        fixed (byte* p = S("s3")) newSlice = BuildInsertValue(builder, newSlice, newLen, 2, (sbyte*)p);

        return newSlice;
    }

    /// <summary>
    /// Emits code for the <c>len(x)</c> built-in. Returns compile-time length
    /// for arrays, extracts the length field for slices, and calls <c>strlen</c>
    /// for strings.
    /// </summary>
    public unsafe override object VisitLengthExpression(MiniGoCompilerParser.LengthExpressionContext context)
    {

        LLVMValueRef val = (LLVMValueRef)Visit(context.expression());
        LLVMTypeRef valType = TypeOf(val);

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

    /// <summary>
    /// Emits code for the <c>cap(x)</c> built-in. Returns compile-time length
    /// for arrays (capacity equals length) and extracts the capacity field
    /// for slices.
    /// </summary>
    public unsafe override object VisitCapExpression(MiniGoCompilerParser.CapExpressionContext context)
    {
        LLVMValueRef result;
        LLVMValueRef val = (LLVMValueRef)Visit(context.expression());
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


    // -------------------------------------------------------------------------
    //  Statements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Visits each statement in a statement list, stopping early if a
    /// terminator instruction (return, break) has already been emitted.
    /// </summary>
    public unsafe override object VisitStatementList(MiniGoCompilerParser.StatementListContext context)
    {
        foreach (var stmt in context.statement())
        {
            LLVMBasicBlockRef currentBlock = GetInsertBlock(builder);
            if (GetBasicBlockTerminator(currentBlock) != null)
                break;
            try
            {
                Visit(stmt);
            }
            catch (Exception ex)
            {
                CodeGenErrors.Add("CODE GEN: " + ex.Message
                                               + " [line " + stmt.Start.Line + ", col " + stmt.Start.Column + "]");
            }
        }

        return null;

    }

    /// <summary>
    /// Visits a block statement, saving and restoring the reference and type
    /// tables to implement lexical scoping.
    /// </summary>
    public override object VisitBlock(MiniGoCompilerParser.BlockContext context)
    {
        var savedRefs = new Dictionary<string, LLVMValueRef>(referenceTable);
        var savedTypes = new Dictionary<string, LLVMTypeRef>(typeTable);

        Visit(context.statementList());

        referenceTable = savedRefs;
        typeTable = savedTypes;

        return null;
    }

    /// <summary>
    /// Emits a <c>fmt.Print</c> statement by calling the C <c>printf</c>
    /// function with a format string selected based on the expression type.
    /// Declares <c>printf</c> on first use.
    /// </summary>
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

        if (context.expressionList() != null)
        {
            var expressions = context.expressionList().expression();
            for (int i = 0; i < expressions.Length; i++)
            {
                LLVMValueRef value = (LLVMValueRef)Visit(expressions[i]);
                LLVMTypeRef exprType = TypeOf(value);

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

    /// <summary>
    /// Emits a <c>fmt.Println</c> statement. Similar to <see cref="VisitPrintStatement"/>
    /// but adds spaces between arguments and a trailing newline, matching
    /// Go's <c>Println</c> behavior.
    /// </summary>
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

        if (context.expressionList() != null)
        {
            var expressions = context.expressionList().expression();
            for (int i = 0; i < expressions.Length; i++)
            {
                LLVMValueRef value = (LLVMValueRef)Visit(expressions[i]);
                LLVMTypeRef exprType = TypeOf(value);

                if (i > 0)
                {
                    LLVMValueRef space = GlobalString(" ", "sp");
                    CallFunction(printfType, printfFunc, new[] { space }, "");
                }

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

        LLVMValueRef newline = GlobalString("\n", "nl");
        CallFunction(printfType, printfFunc, new[] { newline }, "");

        return null;
    }

    /// <summary>
    /// Emits a return statement. If an expression is present, its value is
    /// returned; otherwise a void return or zero-value return is emitted
    /// depending on the function's return type.
    /// </summary>
    public unsafe override object VisitReturnStatement(MiniGoCompilerParser.ReturnStatementContext context)
    {
        if (context.expression() != null)
        {
            LLVMValueRef value = (LLVMValueRef)Visit(context.expression());
            BuildRet(builder, value);
        }
        else
        {
            LLVMTypeRef retType = GetReturnType(GlobalGetValueType(currentFunc));
            if (retType == VoidType())
                BuildRetVoid(builder);
            else
                BuildRet(builder, ConstNull(retType));
        }

        return null;
    }
    private void AddCodeGenError(string message, Antlr4.Runtime.ParserRuleContext context)
    {
        var token = context.Start;

        CodeGenErrors.Add(
            "CODE GEN ERROR: " + message +
            " [line " + token.Line + ": Column " + token.Column + "]"
        );
    }

    /// <summary>
    /// Break is recognized by the parser but not supported in LLVM code generation.
    /// </summary>
    public unsafe override object VisitBreakStatement(MiniGoCompilerParser.BreakStatementContext context)
    {
        AddCodeGenError(
            "break is recognized by MiniGo syntax but is not part of the required LLVM subset",
            context
        );

        return null;
    }

    /// <summary>
    /// Continue is recognized by the parser but not supported in LLVM code generation.
    /// </summary>
    public unsafe override object VisitContinueStatement(MiniGoCompilerParser.ContinueStatementContext context)
    {
        AddCodeGenError(
            "continue is recognized by MiniGo syntax but is not part of the required LLVM subset",
            context
        );

        return null;
    }

    /// <summary>Delegates to the inner simple statement.</summary>
    public override object VisitSimpleStmtStatement(MiniGoCompilerParser.SimpleStmtStatementContext context)
    {
        return Visit(context.simpleStatement());
    }

    /// <summary>Delegates to the inner block.</summary>
    public override object VisitBlockStatement(MiniGoCompilerParser.BlockStatementContext context)
    {
        return Visit(context.block());
    }

    /// <summary>Delegates to the inner switch statement.</summary>
    public override object VisitSwitchStatement(MiniGoCompilerParser.SwitchStatementContext context)
    {
        return Visit(context.switchStmt());
    }

    /// <summary>Delegates to the inner if statement.</summary>
    public override object VisitIfStmtStatement(MiniGoCompilerParser.IfStmtStatementContext context)
    {
        return Visit(context.ifStatement());
    }

    /// <summary>Delegates to the inner loop.</summary>
    public override object VisitLoopStatement(MiniGoCompilerParser.LoopStatementContext context)
    {
        return Visit(context.loop());
    }

    /// <summary>Delegates to the inner type declaration.</summary>
    public override object VisitTypeDeclStatement(MiniGoCompilerParser.TypeDeclStatementContext context)
    {
        return Visit(context.typeDecl());
    }

    /// <summary>Delegates to the inner variable declaration.</summary>
    public override object VisitVariableDeclStatement(MiniGoCompilerParser.VariableDeclStatementContext context)
    {
        return Visit(context.variableDecl());
    }

    /// <summary>No-op: empty statements produce no IR.</summary>
    public override object VisitEmptySimpleStatement(MiniGoCompilerParser.EmptySimpleStatementContext context)
    {
        return null;
    }


    // -------------------------------------------------------------------------
    //  Simple statements (expressions, assignments, short declarations)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emits an expression statement, including post-increment (<c>++</c>)
    /// and post-decrement (<c>--</c>) operations.
    /// </summary>
    public unsafe override object VisitExpressionSimpleStatement(
        MiniGoCompilerParser.ExpressionSimpleStatementContext context)
    {

        LLVMValueRef val = (LLVMValueRef)Visit(context.expression());

        if (context.INC() != null || context.DEC() != null)
        {
            LLVMValueRef ptr = GetLValuePointer(context.expression(), out LLVMTypeRef type);
            LLVMValueRef loaded = LoadVar(type, ptr, "inc_val");
            LLVMValueRef one;
            if (type == floatType)
                one = ConstReal(floatType, 1.0);
            else
                one = ConstInt(type, 1, 0);
            LLVMValueRef result;
            if (type == floatType)
            {
                if (context.INC() != null)
                    fixed (byte* p = S("inctmp"))
                        result = BuildFAdd(builder, loaded, one, (sbyte*)p);
                else
                    fixed (byte* p = S("dectmp"))
                        result = BuildFSub(builder, loaded, one, (sbyte*)p);
            }
            else
            {
                if (context.INC() != null)
                    fixed (byte* p = S("inctmp"))
                        result = BuildAdd(builder, loaded, one, (sbyte*)p);
                else
                    fixed (byte* p = S("dectmp"))
                        result = BuildSub(builder, loaded, one, (sbyte*)p);
            }

            BuildStore(builder, result, ptr);
        }

        return null;
    }

    /// <summary>Delegates to the inner assignment statement.</summary>
    public override object VisitAssignmentSimpleStatement(MiniGoCompilerParser.AssignmentSimpleStatementContext context)
    {
        return Visit(context.assignmentStatement());
    }

    /// <summary>
    /// Emits a short variable declaration (<c>x := expr</c>). Infers the type
    /// from the expression, allocates stack space, and stores the value.
    /// </summary>
    public unsafe override object VisitDeclareSimpleStatement(
        MiniGoCompilerParser.DeclareSimpleStatementContext context)
    {
        LinkedList<LLVMValueRef> values = (LinkedList<LLVMValueRef>)Visit(context.expressionList(1));
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


    // -------------------------------------------------------------------------
    //  L-value resolution (array indexing, variable pointers)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Computes a GEP pointer to an array element for indexed access.
    /// Returns the element pointer and outputs the element's LLVM type.
    /// </summary>
    private unsafe LLVMValueRef GetArrayElementPointer(
        MiniGoCompilerParser.IndexPrimaryExprContext context,
        out LLVMTypeRef elementType)
    {
        string arrayName = context.primaryExpression().GetText();

        if (!referenceTable.ContainsKey(arrayName))
        {
            throw new Exception("Undefined array variable: " + arrayName);
        }

        LLVMValueRef arrayPtr = referenceTable[arrayName];
        LLVMTypeRef arrayType = typeTable[arrayName];

        if (arrayType.Kind != LLVMTypeKind.LLVMArrayTypeKind)
        {
            throw new Exception("Indexing is only implemented for arrays in code generation: " + arrayName);
        }

        LLVMValueRef indexValue = (LLVMValueRef)Visit(context.index().expression());

        LLVMValueRef zero = ConstInt(intType, 0, 0);
        LLVMValueRef[] indices = { zero, indexValue };

        LLVMValueRef elementPtr;

        fixed (LLVMValueRef* idxPtr = indices)
        fixed (byte* p = S("array_elem_ptr"))
        {
            elementPtr = BuildGEP2(
                builder,
                arrayType,
                arrayPtr,
                (LLVMOpaqueValue**)idxPtr,
                2,
                (sbyte*)p
            );
        }

        elementType = GetElementType(arrayType);
        return elementPtr;
    }

    /// <summary>
    /// Resolves an expression to an assignable l-value pointer. Handles simple
    /// variable identifiers and array index expressions. Returns the pointer
    /// and outputs the value's LLVM type.
    /// </summary>
    private unsafe LLVMValueRef GetLValuePointer(
        MiniGoCompilerParser.ExpressionContext expr,
        out LLVMTypeRef valueType)
    {
        if (expr is MiniGoCompilerParser.PrimaryExprContext primaryExpr)
        {
            var primary = primaryExpr.primaryExpression();

            if (primary is MiniGoCompilerParser.OperandPrimaryExprContext operandPrimary &&
                operandPrimary.operand() is MiniGoCompilerParser.IdOperandContext idOperand)
            {
                string name = idOperand.identifier().GetText();

                if (!referenceTable.ContainsKey(name))
                {
                    throw new Exception("Undefined variable: " + name);
                }

                valueType = typeTable[name];
                return referenceTable[name];
            }

            if (primary is MiniGoCompilerParser.IndexPrimaryExprContext indexPrimary)
            {
                return GetArrayElementPointer(indexPrimary, out valueType);
            }

            if (primary is MiniGoCompilerParser.SelectorPrimaryExprContext selectorPrimary)
            {
                string structName = selectorPrimary.primaryExpression().GetText();
                string fieldName = selectorPrimary.selector().IDENTIFIER().GetText();
                LLVMValueRef structPtr = referenceTable[structName];
                LLVMTypeRef structType = typeTable[structName];

                uint fieldIndex = 0;
                if (structFieldNames.TryGetValue(structType.Handle, out List<string> fields))
                {
                    int idx = fields.IndexOf(fieldName);
                    if (idx >= 0) fieldIndex = (uint)idx;
                }

                LLVMValueRef fieldPtr;
                fixed (byte* p = S("fieldptr"))
                {
                    fieldPtr = BuildStructGEP2(builder, structType, structPtr, fieldIndex, (sbyte*)p);
                }

                valueType = StructGetTypeAtIndex(structType, fieldIndex);
                return fieldPtr;
            }
        }

        throw new Exception("Invalid assignment target: " + expr.GetText());
    }



// -------------------------------------------------------------------------
    //  Assignment statements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emits a simple assignment (<c>x = expr</c>). Supports multiple
    /// assignments in parallel (<c>a, b = expr1, expr2</c>).
    /// </summary>
    public unsafe override object VisitEqualAssignment(MiniGoCompilerParser.EqualAssignmentContext context)
    {
        LinkedList<LLVMValueRef> values = (LinkedList<LLVMValueRef>) Visit(context.expressionList(1));
        var leftExprs = context.expressionList(0).expression();

        for (int i = 0; i < leftExprs.Length; i++)
        {
            LLVMValueRef ptr = GetLValuePointer(leftExprs[i], out LLVMTypeRef targetType);
            LLVMValueRef value = values.ElementAt(i);

            BuildStore(builder, value, ptr);
        }
        return null;
    }

    /// <summary>
    /// Emits a compound assignment operation. Loads the current value, applies
    /// the specified arithmetic or bitwise operator with the right-hand side,
    /// and stores the result back.
    /// </summary>
    /// <param name="leftCtx">Left-hand side expression (the assignment target).</param>
    /// <param name="rightCtx">Right-hand side expression (the operand).</param>
    /// <param name="op">Operator string: <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>, <c>%</c>, <c>&amp;</c>, <c>|</c>, <c>^</c>, <c>&lt;&lt;</c>, <c>&gt;&gt;</c>.</param>
    private unsafe void CompoundAssign(MiniGoCompilerParser.ExpressionContext leftCtx,
    MiniGoCompilerParser.ExpressionContext rightCtx, string op)
{
    LLVMValueRef ptr = GetLValuePointer(leftCtx, out LLVMTypeRef type);
    if (type == stringType)
        throw new Exception("Compound assignment operators are not supported for strings in code generation");
    LLVMValueRef left = LoadVar(type, ptr, "compound_left");
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

    /// <summary>Emits an addition assignment (<c>+=</c>).</summary>
    public override object VisitAddAssignment(MiniGoCompilerParser.AddAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "+"); return null;
    }

    /// <summary>Emits a bitwise AND assignment (<c>&amp;=</c>).</summary>
    public override object VisitAndAssignment(MiniGoCompilerParser.AndAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "&"); return null;
    }

    /// <summary>Emits a subtraction assignment (<c>-=</c>).</summary>
    public override object VisitSubAssignment(MiniGoCompilerParser.SubAssignmentContext context)
    {
        { CompoundAssign(context.expression(0), context.expression(1), "-"); return null; }
    }

    /// <summary>Emits a bitwise OR assignment (<c>|=</c>).</summary>
    public override object VisitOrAssignment(MiniGoCompilerParser.OrAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "|"); return null;
    }

    /// <summary>Emits a multiplication assignment (<c>*=</c>).</summary>
    public override object VisitMulAssignment(MiniGoCompilerParser.MulAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "*"); return null;
    }

    /// <summary>Emits a XOR assignment (<c>^=</c>).</summary>
    public override object VisitHatAssignment(MiniGoCompilerParser.HatAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "^"); return null;
    }

    /// <summary>Emits a left shift assignment (<c>&lt;&lt;=</c>).</summary>
    public override object VisitDlessAssignment(MiniGoCompilerParser.DlessAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "<<"); return null;
    }

    /// <summary>Emits a right shift assignment (<c>&gt;&gt;=</c>).</summary>
    public override object VisitDmoreAssignment(MiniGoCompilerParser.DmoreAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), ">>"); return null;
    }

    /// <summary>
    /// Emits a bit-clear assignment (<c>&amp;^=</c>). Computes <c>left &amp; (~right)</c>
    /// and stores the result.
    /// </summary>
    public unsafe override object VisitAndHatAssignment(MiniGoCompilerParser.AndHatAssignmentContext context)
    {
        LLVMValueRef ptr = GetLValuePointer(context.expression(0), out LLVMTypeRef type);
        LLVMValueRef left = LoadVar(type, ptr, "andhat_left");
        LLVMValueRef right = (LLVMValueRef) Visit(context.expression(1));
        LLVMValueRef notRight, result;

        fixed (byte* p = S("nottmp")) notRight = BuildNot(builder, right, (sbyte*)p);
        fixed (byte* p = S("andnottmp")) result = BuildAnd(builder, left, notRight, (sbyte*)p);

        BuildStore(builder, result, ptr);
        return null;
    }

    /// <summary>Emits a modulo assignment (<c>%=</c>).</summary>
    public override object VisitModAssignment(MiniGoCompilerParser.ModAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "%"); return null;
    }

    /// <summary>Emits a division assignment (<c>/=</c>).</summary>
    public override object VisitDivAssignment(MiniGoCompilerParser.DivAssignmentContext context)
    {
        CompoundAssign(context.expression(0), context.expression(1), "/"); return null;
    }


    // -------------------------------------------------------------------------
    //  If statements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emits a simple if statement (<c>if cond { ... }</c>) with no else branch.
    /// Creates then and merge basic blocks with a conditional branch.
    /// </summary>
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

    /// <summary>
    /// Emits an if-else if chain (<c>if cond { ... } else if { ... }</c>).
    /// Creates then, else, and merge blocks with the else block recursively
    /// visiting the chained if statement.
    /// </summary>
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

    /// <summary>
    /// Emits an if-else statement (<c>if cond { ... } else { ... }</c>)
    /// with both then and else blocks branching to a common merge block.
    /// </summary>
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

    /// <summary>
    /// Emits an if statement preceded by a simple statement initializer
    /// (<c>if stmt; cond { ... }</c>).
    /// </summary>
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

    /// <summary>
    /// Emits an if-else if statement preceded by a simple statement initializer
    /// (<c>if stmt; cond { ... } else if { ... }</c>).
    /// </summary>
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

    /// <summary>
    /// Emits an if-else statement preceded by a simple statement initializer
    /// (<c>if stmt; cond { ... } else { ... }</c>).
    /// </summary>
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


    // -------------------------------------------------------------------------
    //  Loop statements (for loops)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emits an infinite loop (<c>for { ... }</c>). The body block branches
    /// back to itself unconditionally.
    /// </summary>
    public unsafe override object VisitInfiniteLoop(MiniGoCompilerParser.InfiniteLoopContext context)
    {
        LLVMBasicBlockRef bodyBlock;
        LLVMBasicBlockRef mergeBlock;
        fixed (byte* p = S("forBody")) bodyBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forMerge")) mergeBlock = AppendBasicBlock(currentFunc,(sbyte*)p);

        BuildBr(builder, bodyBlock);
        PositionBuilderAtEnd(builder, bodyBlock);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, bodyBlock);

        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    /// <summary>
    /// Emits a condition-only loop (<c>for cond { ... }</c>). Creates
    /// condition, body, and merge blocks with the condition re-evaluated
    /// after each iteration.
    /// </summary>
    public unsafe override object VisitConditionLoop(MiniGoCompilerParser.ConditionLoopContext context)
    {
        LLVMBasicBlockRef condBlock;
        LLVMBasicBlockRef bodyBlock;
        LLVMBasicBlockRef mergeBlock;
        fixed (byte* p = S("forCond")) condBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forBody")) bodyBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forMerge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);

        BuildBr(builder, condBlock);
        PositionBuilderAtEnd(builder, condBlock);
        LLVMValueRef cond = (LLVMValueRef) Visit(context.expression());
        BuildCondBr(builder, cond, bodyBlock, mergeBlock);

        PositionBuilderAtEnd(builder, bodyBlock);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, condBlock);

        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    /// <summary>
    /// Emits a complete three-clause for loop (<c>for init; cond; post { ... }</c>).
    /// Creates condition, body, post, and merge blocks.
    /// </summary>
    public unsafe override object VisitCompleteForLoop(MiniGoCompilerParser.CompleteForLoopContext context)
    {

        Visit(context.simpleStatement(0));
        LLVMBasicBlockRef condBlock;
        LLVMBasicBlockRef bodyBlock;
        LLVMBasicBlockRef mergeBlock;
        LLVMBasicBlockRef postBlock;

        fixed (byte* p = S("forCond")) condBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forBody")) bodyBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forPost")) postBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forMerge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);

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

        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    /// <summary>
    /// Emits a for loop without a condition (<c>for init;; post { ... }</c>).
    /// The body loops indefinitely with an init and post statement.
    /// </summary>
    public unsafe override object VisitNoConditionForLoop(MiniGoCompilerParser.NoConditionForLoopContext context)
    {
        LLVMBasicBlockRef postBlock;
        LLVMBasicBlockRef bodyBlock;
        LLVMBasicBlockRef mergeBlock;
        Visit(context.simpleStatement(0));
        fixed (byte* p = S("forBody")) bodyBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forPost")) postBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        fixed (byte* p = S("forMerge")) mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);


        BuildBr(builder, bodyBlock);
        PositionBuilderAtEnd(builder, bodyBlock);
        Visit(context.block());
        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, postBlock);

        PositionBuilderAtEnd(builder, postBlock);
        Visit(context.simpleStatement(1));
        BuildBr(builder, bodyBlock);

        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }


    // -------------------------------------------------------------------------
    //  Switch statements
    // -------------------------------------------------------------------------

    /// <summary>
    /// Emits a switch statement preceded by a simple statement initializer
    /// (<c>switch stmt; expr { ... }</c>). Builds a chain of comparison blocks
    /// for each case clause and a merge block for fall-through.
    /// </summary>
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
            BuildBr(builder, newTest);
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

    /// <summary>
    /// Emits an expression switch statement (<c>switch expr { ... }</c>).
    /// Evaluates the switch expression once, then builds a comparison chain
    /// testing each case clause's expressions against the switch value.
    /// </summary>
    public override unsafe object VisitExpressionSwitch(MiniGoCompilerParser.ExpressionSwitchContext context)
    {
         LLVMValueRef switchVal = (LLVMValueRef)Visit(context.expression());

    LLVMBasicBlockRef mergeBlock;
    fixed (byte* p = S("switchMerge"))
        mergeBlock = AppendBasicBlock(currentFunc, (sbyte*)p);

    breakTargets.Push(mergeBlock);

    var clauses = context.expressionCaseClauseList().expressionCaseClause();

    LLVMBasicBlockRef[] caseBlocks = new LLVMBasicBlockRef[clauses.Length];
    LLVMBasicBlockRef defaultBlock = mergeBlock;

    for (int i = 0; i < clauses.Length; i++)
    {
        fixed (byte* p = S("case" + i))
            caseBlocks[i] = AppendBasicBlock(currentFunc, (sbyte*)p);

        if (clauses[i].expressionSwitchCase() is MiniGoCompilerParser.DefaultSwitchContext)
            defaultBlock = caseBlocks[i];
    }

    if (clauses.Length == 0)
    {
        BuildBr(builder, mergeBlock);
        breakTargets.Pop();
        PositionBuilderAtEnd(builder, mergeBlock);
        return null;
    }

    LLVMBasicBlockRef nextTest;
    fixed (byte* p = S("test0"))
        nextTest = AppendBasicBlock(currentFunc, (sbyte*)p);

    BuildBr(builder, nextTest);

    for (int i = 0; i < clauses.Length; i++)
    {
        var switchCase = clauses[i].expressionSwitchCase();

        if (switchCase is MiniGoCompilerParser.DefaultSwitchContext)
        {
            PositionBuilderAtEnd(builder, nextTest);
            BuildBr(builder, caseBlocks[i]);

            PositionBuilderAtEnd(builder, caseBlocks[i]);
            Visit(clauses[i].statementList());

            if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
                BuildBr(builder, mergeBlock);

            continue;
        }

        var caseCtx = (MiniGoCompilerParser.CaseSwitchContext)switchCase;

        PositionBuilderAtEnd(builder, nextTest);

        var caseExprs = caseCtx.expressionList().expression();
        LLVMValueRef match = null;

        for (int j = 0; j < caseExprs.Length; j++)
        {
            LLVMValueRef caseVal = (LLVMValueRef)Visit(caseExprs[j]);
            LLVMValueRef cmp;

            if (TypeOf(switchVal) == floatType)
            {
                fixed (byte* p = S("cmptmp"))
                    cmp = BuildFCmp(builder, LLVMRealPredicate.LLVMRealOEQ, switchVal, caseVal, (sbyte*)p);
            }
            else
            {
                fixed (byte* p = S("cmptmp"))
                    cmp = BuildICmp(builder, LLVMIntPredicate.LLVMIntEQ, switchVal, caseVal, (sbyte*)p);
            }

            if (match.Handle == IntPtr.Zero)
            {
                match = cmp;
            }
            else
            {
                fixed (byte* p = S("ortmp"))
                    match = BuildOr(builder, match, cmp, (sbyte*)p);
            }
        }

        LLVMBasicBlockRef nextBlock;

        if (i + 1 < clauses.Length)
        {
            fixed (byte* p = S("test" + (i + 1)))
                nextBlock = AppendBasicBlock(currentFunc, (sbyte*)p);
        }
        else
        {
            nextBlock = defaultBlock;
        }

        BuildCondBr(builder, match, caseBlocks[i], nextBlock);

        PositionBuilderAtEnd(builder, caseBlocks[i]);
        Visit(clauses[i].statementList());

        if (GetBasicBlockTerminator(GetInsertBlock(builder)) == null)
            BuildBr(builder, mergeBlock);

        nextTest = nextBlock;
    }

    if (GetBasicBlockTerminator(nextTest) == null &&
        nextTest.Handle != mergeBlock.Handle)
    {
        PositionBuilderAtEnd(builder, nextTest);
        BuildBr(builder, defaultBlock);
    }

    breakTargets.Pop();
    PositionBuilderAtEnd(builder, mergeBlock);

    return null;
    }

    /// <summary>
    /// Emits a switch statement preceded by a simple statement but without a
    /// switch expression (<c>switch stmt; { ... }</c>). Case expressions are
    /// compared against <c>true</c>.
    /// </summary>
    public unsafe override object VisitSimpleSwitch(MiniGoCompilerParser.SimpleSwitchContext context)
    {
       Visit(context.simpleStatement());
    LLVMValueRef switchVal = ConstInt(boolType, 1, 0);

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

            fixed (byte* p = S("test" + (i + 1)))
                nextTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
            BuildCondBr(builder, match, caseBlocks[i], nextTest);
        }
        else
        {
            LLVMBasicBlockRef newTest;
            fixed (byte* p = S("test" + (i + 1)))
                newTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
            PositionBuilderAtEnd(builder, nextTest);
            BuildBr(builder, newTest);
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

    /// <summary>
    /// Emits an empty switch statement (<c>switch { ... }</c>) with no expression.
    /// Case expressions are compared against <c>true</c> (equivalent to
    /// <c>switch true { ... }</c>).
    /// </summary>
    public unsafe override object VisitEmptySwitch(MiniGoCompilerParser.EmptySwitchContext context)
    {
         LLVMValueRef switchVal = ConstInt(boolType, 1, 0);
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

            fixed (byte* p = S("test" + (i + 1)))
                nextTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
            BuildCondBr(builder, match, caseBlocks[i], nextTest);
        }
        else
        {
            LLVMBasicBlockRef newTest;
            fixed (byte* p = S("test" + (i + 1)))
                newTest = (i + 1 < clauses.Length) ? AppendBasicBlock(currentFunc, (sbyte*)p) : defaultBlock;
            PositionBuilderAtEnd(builder, nextTest);
            BuildBr(builder, newTest);
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

    /// <summary>No-op: case clause lists are handled by switch statement visitors.</summary>
    public override object VisitExpressionCaseClauseList(MiniGoCompilerParser.ExpressionCaseClauseListContext context)
    {
        return null;
    }

    /// <summary>No-op: individual case clauses are handled by switch statement visitors.</summary>
    public override object VisitExpressionCaseClause(MiniGoCompilerParser.ExpressionCaseClauseContext context)
    {
        return null;
    }

    /// <summary>No-op: case switch labels are handled by switch statement visitors.</summary>
    public override object VisitCaseSwitch(MiniGoCompilerParser.CaseSwitchContext context)
    {
        return null;
    }

    /// <summary>No-op: default switch labels are handled by switch statement visitors.</summary>
    public override object VisitDefaultSwitch(MiniGoCompilerParser.DefaultSwitchContext context)
    {
        return null;
    }

    /// <summary>No-op: identifiers are resolved inline by their parent visitors.</summary>
    public override object VisitIdentifier(MiniGoCompilerParser.IdentifierContext context)
    {
        return null;
    }
}
