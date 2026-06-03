namespace MiniGoCompiler.typechecker;
using Antlr4.Runtime;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Tabla de símbolos para el compilador MiniGo.
/// Gestiona identificadores (variables, tipos y funciones) organizados por niveles de alcance (scope),
/// utilizando una lista enlazada como estructura de almacenamiento con inserción al frente
/// para facilitar la búsqueda por proximidad de scope.
/// </summary>
public class SymbolsTable
{
    private LinkedList<Ident> table;
    protected int actualLevel;

    /// <summary>
    /// Obtiene el nivel de alcance (scope) actual de la tabla de símbolos.
    /// </summary>
    /// <returns>El nivel de scope actual, comenzando en -1 cuando no hay ningún scope abierto.</returns>
    public int GetActualLevel()
    {
        return this.actualLevel;
    }

    /// <summary>
    /// Representa la información de tipo de un identificador en el compilador MiniGo.
    /// Soporta tipos simples (int, float, string, etc.), arrays con tamaño fijo,
    /// slices dinámicos y structs con campos nombrados.
    /// </summary>
    public class TypeInfo {
        /// <summary>Categoría del tipo: "simple", "array", "slice" o "struct".</summary>
        private string category;
        /// <summary>Código del tipo simple: 0=int, 1=float, 2=string, etc.</summary>
        private int simpleType;
        /// <summary>Tamaño del array (aplica solo cuando category es "array").</summary>
        private int size;
        /// <summary>Tipo interno de los elementos (aplica para arrays y slices).</summary>
        private TypeInfo insideType;
        /// <summary>Mapa de campos para structs: nombre del campo → tipo asociado.</summary>
        private Dictionary<string, TypeInfo> fields;
        
        /// <summary>Obtiene la categoría del tipo ("simple", "array", "slice" o "struct").</summary>
        public string Category => category;
        /// <summary>Obtiene el código del tipo simple.</summary>
        public int SimpleType => simpleType;
        /// <summary>Obtiene el tamaño del array.</summary>
        public int Size => size;
        /// <summary>Obtiene el tipo interno de los elementos del array o slice.</summary>
        public TypeInfo InsideType => insideType;
        /// <summary>Obtiene el diccionario de campos del struct.</summary>
        public Dictionary<string, TypeInfo> Fields => fields;

    /// <summary>
    /// Crea una nueva instancia de TypeInfo con toda la información de tipo necesaria.
    /// </summary>
    /// <param name="category">Categoría del tipo: "simple", "array", "slice" o "struct".</param>
    /// <param name="simpleType">Código numérico del tipo simple (0=int, 1=float, 2=string).</param>
    /// <param name="size">Tamaño del array; se ignora para otras categorías.</param>
    /// <param name="insideType">Tipo interno para arrays/slices; null para tipos simples y structs.</param>
    /// <param name="fields">Diccionario de campos para structs; null para otras categorías.</param>
    public TypeInfo(string category, int simpleType, int size, TypeInfo insideType, Dictionary<string, TypeInfo> fields)
    {
        this.category = category;
        this.simpleType = simpleType;
        this.size = size;
        this.insideType = insideType;
        this.fields = fields;
    }
}
    
    
    

    /// <summary>
    /// Clase base abstracta que representa un identificador en la tabla de símbolos.
    /// Almacena el token léxico, la información de tipo, el nivel de scope
    /// y la referencia al nodo del árbol de parseo donde fue declarado.
    /// </summary>
    public abstract class Ident
    {
        private readonly IToken token;
        private readonly TypeInfo type;
        private readonly int level;
        private readonly ParserRuleContext decl;

        /// <summary>Obtiene el token léxico asociado al identificador.</summary>
        public IToken Token => token;

        /// <summary>Obtiene la información de tipo del identificador.</summary>
        public TypeInfo Type => type;

        /// <summary>Obtiene el nivel de scope en el que fue declarado el identificador.</summary>
        public int Level => level;

        /// <summary>Obtiene el contexto del árbol de parseo correspondiente a la declaración.</summary>
        public ParserRuleContext Decl => decl;

        /// <summary>
        /// Constructor base para un identificador.
        /// </summary>
        /// <param name="token">Token léxico del identificador.</param>
        /// <param name="type">Información de tipo asociada.</param>
        /// <param name="level">Nivel de scope donde se declara.</param>
        /// <param name="decl">Nodo del árbol de parseo de la declaración.</param>
        public Ident(IToken token, TypeInfo type, int level, ParserRuleContext decl)
        {
            this.token = token;
            this.type = type;
            this.level = level;
            this.decl = decl;
        }
    }

    /// <summary>
    /// Identificador de variable. Hereda de <see cref="Ident"/> sin agregar campos adicionales.
    /// </summary>
    public class VarIdent : Ident
    {
        /// <summary>
        /// Crea un identificador de variable.
        /// </summary>
        /// <param name="t">Token léxico de la variable.</param>
        /// <param name="tp">Información de tipo de la variable.</param>
        /// <param name="level">Nivel de scope.</param>
        /// <param name="d">Contexto de declaración en el árbol de parseo.</param>
        public VarIdent(IToken t, TypeInfo tp, int level, ParserRuleContext d) : base(t, tp, level, d)
        {
        }
        
    }

    /// <summary>
    /// Identificador de tipo definido por el usuario. Hereda de <see cref="Ident"/> sin agregar campos adicionales.
    /// </summary>
    public class TypeIdent : Ident
    {
        /// <summary>
        /// Crea un identificador de tipo.
        /// </summary>
        /// <param name="t">Token léxico del tipo.</param>
        /// <param name="tp">Información de tipo.</param>
        /// <param name="level">Nivel de scope.</param>
        /// <param name="d">Contexto de declaración en el árbol de parseo.</param>
        public TypeIdent(IToken t, TypeInfo tp, int level, ParserRuleContext d) : base(t, tp, level, d)
        {
        }
    }

    /// <summary>
    /// Identificador de función/método. Extiende <see cref="Ident"/> con una lista de tipos
    /// de parámetros para validación de llamadas.
    /// </summary>
    public class FunctionIdent : Ident
    {
        /// <summary>Lista ordenada de los tipos de los parámetros de la función.</summary>
        private readonly LinkedList<TypeInfo> parameters;

        /// <summary>Obtiene la lista de tipos de parámetros de la función.</summary>
        public LinkedList<TypeInfo> Parameters => parameters;

        /// <summary>
        /// Crea un identificador de función.
        /// </summary>
        /// <param name="t">Token léxico de la función.</param>
        /// <param name="tp">Tipo de retorno de la función.</param>
        /// <param name="level">Nivel de scope.</param>
        /// <param name="p">Lista de tipos de los parámetros formales.</param>
        /// <param name="d">Contexto de declaración en el árbol de parseo.</param>
        public FunctionIdent(IToken t, TypeInfo tp, int level, LinkedList<TypeInfo> p, ParserRuleContext d) : base(t, tp, level, d)
        {
            this.parameters = p;
        }
    }

    /// <summary>
    /// Inicializa una tabla de símbolos vacía con el nivel de scope en -1 (sin scope abierto).
    /// </summary>
    public SymbolsTable()
    {
        table = new LinkedList<Ident>();
        this.actualLevel = -1;
    }

    /// <summary>
    /// Inserta un identificador de variable en la tabla de símbolos en un nivel de scope específico.
    /// El identificador se inserta al inicio de la lista para priorizar la búsqueda por proximidad.
    /// </summary>
    /// <param name="id">Token léxico del identificador de la variable.</param>
    /// <param name="type">Información de tipo de la variable.</param>
    /// <param name="level">Nivel de scope en el que se declara.</param>
    /// <param name="d">Contexto de declaración en el árbol de parseo.</param>
    public void InsertVariableLevel(IToken id, TypeInfo type, int level, ParserRuleContext d)
    {
        Ident i = new VarIdent(id, type, level, d);
        table.AddFirst(i);
    }

    /// <summary>
    /// Inserta un identificador de tipo definido por el usuario en la tabla de símbolos.
    /// </summary>
    /// <param name="id">Token léxico del identificador del tipo.</param>
    /// <param name="type">Información del tipo que se está definiendo.</param>
    /// <param name="level">Nivel de scope en el que se declara.</param>
    /// <param name="d">Contexto de declaración en el árbol de parseo.</param>
    public void InsertTypeLevel(IToken id, TypeInfo type, int level, ParserRuleContext d)
    {
        Ident i = new TypeIdent(id, type, level, d);
        table.AddFirst(i);
    }

    /// <summary>
    /// Inserta un identificador de función/método en la tabla de símbolos,
    /// incluyendo la lista de tipos de sus parámetros formales.
    /// </summary>
    /// <param name="id">Token léxico del nombre de la función.</param>
    /// <param name="type">Tipo de retorno de la función.</param>
    /// <param name="level">Nivel de scope en el que se declara.</param>
    /// <param name="p">Lista de tipos de los parámetros formales.</param>
    /// <param name="d">Contexto de declaración en el árbol de parseo.</param>
    public void InsertMethod(IToken id, TypeInfo type, int level, LinkedList<TypeInfo> p, ParserRuleContext d)
    {
        Ident i = new FunctionIdent(id, type, level, p, d);
        table.AddFirst(i);
    }

    /// <summary>
    /// Busca un identificador por nombre en toda la tabla de símbolos,
    /// recorriendo desde el scope más interno hacia el más externo.
    /// Retorna la primera coincidencia encontrada (la más cercana al scope actual).
    /// </summary>
    /// <param name="nombre">Nombre del identificador a buscar.</param>
    /// <returns>El identificador encontrado, o null si no existe en ningún scope.</returns>
    public Ident? Search(string nombre)
    {
        Ident? temp = null;
        foreach (Ident id in table)
        {
            if (id.Token.Text.Equals(nombre))
            {
                temp = id;
                break;
            }
        }
        return temp;
    }

    /// <summary>
    /// Busca un identificador por nombre exclusivamente en el nivel de scope actual.
    /// Útil para detectar declaraciones duplicadas dentro del mismo bloque.
    /// </summary>
    /// <param name="name">Nombre del identificador a buscar.</param>
    /// <returns>El identificador si existe en el scope actual, o null en caso contrario.</returns>
    public Ident? SearchActualLevel(string name)
    {
        Ident? temp = null;
        int tempNivel = actualLevel;
        foreach (Ident id in table)
        {
            if (tempNivel == id.Level)
            {
                if (id.Token.Text.Equals(name))
                    temp = id;
            }
            else
                break;
        }
        return temp;
    }

    /// <summary>
    /// Abre un nuevo nivel de scope, incrementando el nivel actual.
    /// Se invoca al entrar en un bloque (función, if, for, etc.).
    /// </summary>
    public void OpenScope()
    {
        actualLevel++;
    }

    /// <summary>
    /// Cierra el scope actual: elimina todos los identificadores declarados en el nivel actual
    /// y decrementa el nivel de scope. Se invoca al salir de un bloque.
    /// </summary>
    public void CloseScope()
    {
        table = new LinkedList<Ident>(table.Where(ident => ident.Level != this.actualLevel));
        actualLevel--;
    }

    /// <summary>
    /// Imprime el contenido completo de la tabla de símbolos en consola.
    /// Muestra el nombre, nivel de scope y tipo de cada identificador registrado.
    /// Útil para depuración durante el desarrollo del compilador.
    /// </summary>
    public void Print()
    {
        Console.WriteLine("----- INICIO TABLA ------");
        for (int i = 0; i < table.Count(); i++)
        {
            IToken s = table.ElementAt(i).Token;
            Console.WriteLine("Nombre: " + s.Text + " - " + table.ElementAt(i).Level + " - " + table.ElementAt(i).Type);
        }
        Console.WriteLine("----- FIN TABLA ------");
    }
}