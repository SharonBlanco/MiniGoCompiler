# MiniGo Compiler

## Overview

MiniGo Compiler is an academic compiler project developed in C# using ANTLR4 and LLVMSharp. The project implements a reduced version of the Go programming language, called MiniGo, and translates valid MiniGo source code into LLVM Intermediate Representation (LLVM IR). The generated LLVM IR is then compiled, linked, executed, and displayed through a simple web-based IDE.

The purpose of this project is to demonstrate the complete compiler construction process, including lexical analysis, syntax analysis, semantic validation, intermediate code generation, native execution, and error reporting.

## Main Features

The compiler supports a substantial subset of MiniGo, including:

* Global and local variable declarations.
* Typed and inferred declarations.
* Multiple variable declarations.
* Primitive types: `int`, `float64`, `rune`, `bool`, and `string`.
* User-defined types.
* Struct declarations and field access.
* Fixed-size arrays.
* Slices with `append`, `len`, `cap`, and indexing.
* String indexing returning `rune`.
* Functions with parameters and return values.
* Procedures without return values.
* Arithmetic, relational, logical, unary, and bitwise expressions.
* Assignment and compound assignment operators.
* Increment and decrement operations.
* `if`, `else if`, and `else` statements.
* `for` loops.
* `switch` statements.
* Built-in output functions: `print` and `println`.
* LLVM IR generation.
* Native execution of compiled programs.
* Web-based code editor and output terminal.
* Error reporting for lexical, syntax, semantic, and code generation errors.

## Technologies Used

This project was built using the following technologies:

| Technology            | Purpose                                                |
| --------------------- | ------------------------------------------------------ |
| C#                    | Main implementation language                           |
| .NET                  | Runtime and application framework                      |
| ANTLR4                | Lexer and parser generation                            |
| LLVMSharp             | LLVM IR generation from C#                             |
| LLVM                  | Intermediate representation and native code generation |
| Clang                 | Linking generated object files                         |
| HTML, CSS, JavaScript | Web-based IDE interface                                |
| Monaco Editor         | Code editor component                                  |

## Project Structure

The project is organized around the main stages of a compiler.

```text
MiniGoCompiler/
├── MiniGoCompiler.g4              # ANTLR grammar for MiniGo
├── Program.cs                     # Application entry point
├── CompilerServer.cs              # Local web server and compile endpoint
├── MiniGoErrorListener.cs         # Lexer/parser error handling
├── SymbolsTable.cs                # Symbol table and type information
├── TypeErrorException.cs          # Semantic error exception class
├── MiniGoTypeChecker.cs           # Semantic analysis and type checking
├── MiniGoEncoder.cs               # LLVM IR code generation
├── index.html                     # Web-based IDE
├── Tests/                         # Compiler test files
└── generated/                     # ANTLR-generated parser and lexer files
```

## Compiler Pipeline

The compiler follows a multi-stage pipeline:

1. **Lexical Analysis**
   The source code is tokenized using the ANTLR-generated lexer.

2. **Syntax Analysis**
   The parser validates the program structure according to the MiniGo grammar.

3. **Semantic Analysis**
   The type checker validates declarations, scopes, types, function calls, returns, arrays, slices, structs, and other semantic rules.

4. **LLVM Code Generation**
   The encoder traverses the validated parse tree and emits LLVM IR instructions.

5. **Module Verification**
   The generated LLVM module is verified before native compilation.

6. **Object File Generation**
   LLVM emits a native object file.

7. **Linking**
   The object file is linked using Clang.

8. **Execution**
   The compiled binary is executed, and its output is captured and displayed in the IDE.

## Semantic Analysis

The type checker validates key language rules before code generation. It checks:

* Variable declarations and redeclarations.
* Scope rules and shadowing.
* Function declarations and calls.
* Parameter and argument compatibility.
* Return type correctness.
* Missing return statements in non-void functions.
* Expression type compatibility.
* Array, slice, and string indexing.
* Struct field access.
* Built-in functions such as `append`, `len`, and `cap`.

The semantic phase also attaches declaration information to identifier usages. This allows the LLVM encoder to resolve variables based on their real semantic declaration instead of relying only on the identifier name.

## LLVM Code Generation

The LLVM encoder generates intermediate representation for the supported MiniGo subset. It uses LLVMSharp to create instructions, basic blocks, functions, global variables, local variables, branches, calls, loads, stores, and composite data structures.

A key design improvement in the encoder is that variables are resolved using their semantic declaration. This prevents incorrect behavior when different variables share the same name in different scopes.

For example:

```go
var x int = 10;

{
    var x int = 20;
    println(x);
};

println(x);
```

The compiler correctly prints:

```text
20
10
```

because each `x` is linked to its own declaration.

## Supported Data Structures

### Arrays

The compiler supports fixed-size arrays, including indexing, assignment, increment/decrement, and `len`/`cap`.

Example:

```go
var arr [3]int;
arr[0] = 10;
arr[1] = 20;
arr[2] = 30;

println(arr[0], arr[1], arr[2]);
println(len(arr));
println(cap(arr));
```

### Slices

Slices are represented internally as a structure containing:

```text
{ data pointer, length, capacity }
```

The compiler supports:

* Slice declaration.
* `append`.
* `len`.
* `cap`.
* Index reading.
* Index assignment.

Example:

```go
var sl []int;

sl = append(sl, 100);
sl = append(sl, 200);

println(sl[0]);
println(sl[1]);

sl[1] = 999;
println(sl[1]);
```

### Strings

Strings are represented as character pointers. Indexing a string returns a `rune`.

Example:

```go
var text string = "MiniGo";

println(text[0]);
println(text[1]);
println(text[2]);
```

Expected output:

```text
M
i
n
```

## Web IDE

The project includes a local web-based IDE. The interface allows the user to:

* Write MiniGo code.
* Load test files from the computer.
* Compile code.
* View syntax, semantic, and code generation errors.
* View generated LLVM IR.
* View program output.
* Cancel long-running requests.
* Expand the output terminal for better readability.

The backend includes execution timeout handling to prevent infinite loops from freezing the IDE.

## Error Handling

The compiler reports errors across multiple phases:

| Phase             | Example                                                |
| ----------------- | ------------------------------------------------------ |
| Lexer             | Invalid token                                          |
| Parser            | Invalid syntax                                         |
| Type checker      | Type mismatch, undeclared variable, missing return     |
| Code generation   | Unsupported backend feature or invalid LLVM generation |
| Runtime execution | Program timeout or non-zero exit code                  |

Code generation is intentionally conservative. If an unsupported feature or invalid backend state is detected, the compiler stops before verification, linking, or execution.

## Known Backend Limitations

MiniGo is a reduced language, and this project does not attempt to implement the full Go language. Some features may be recognized by the grammar or validated by the type checker but intentionally limited in LLVM generation.

Current limitations include:

* `break` and `continue` are recognized by syntax but are not part of the required LLVM subset.
* Full Go standard library support is not implemented.
* Imports are not supported.
* Interfaces are not supported.
* Concurrency features such as goroutines and channels are not supported.
* The compiler focuses on the MiniGo language subset defined for the project, not full Go compatibility.

## Example Program

```go
package main;

func suma(a int, b int) int {
    return a + b;
};

func main() {
    var x int = 10;
    var y int = 20;

    println(suma(x, y));

    var arr [3]int;
    arr[0] = 1;
    arr[1] = 2;
    arr[2] = 3;

    println(arr[0], arr[1], arr[2]);

    var sl []int;
    sl = append(sl, 100);
    sl = append(sl, 200);

    println(sl[0], sl[1]);

    var text string = "Go";
    println(text[0], text[1]);
};
```

Expected output:

```text
30
1 2 3
100 200
G o
```

## Testing

The project includes several test files designed to validate different compiler features:

```text
Tests/
├── features_test.txt
├── final_integration_test.txt
├── stress_test.txt
├── typechecker_error_test.txt
└── typechecker_valid_test.txt
```

The tests cover:

* Correct programs.
* Type checker validation.
* Scope handling.
* Function calls.
* Arrays.
* Slices.
* Strings.
* Structs.
* Control flow.
* LLVM code generation.
* Runtime output.

Large integration tests were used to identify and fix several important bugs, including:

* Incorrect handling of shadowed variables.
* Invalid LLVM generation for void function calls.
* Slice indexing issues.
* String indexing issues.
* Global string initialization.
* Infinite-loop execution handling.

## How to Run

Requirements:

* .NET SDK
* LLVM
* Clang
* ANTLR4 runtime for C#
* LLVMSharp

Run the compiler server:

```bash
dotnet run
```

Then open the local web IDE in the browser. The console will display the URL where the IDE is available.

## Project Purpose

This project demonstrates practical knowledge of compiler construction, including parsing, semantic analysis, symbol tables, type checking, intermediate representation, backend code generation, and execution. It also shows the ability to debug low-level compiler errors, validate language features with test programs, and build a usable interface for interacting with the compiler.

## Author

Sharon Blanco Piedra.
