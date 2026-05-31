namespace MiniGoCompiler.typechecker;
using Antlr4.Runtime;
using System.Collections.Generic;
using System.Linq;

public class SymbolsTable
{
    private LinkedList<Ident> table;
    protected int actualLevel;

    public int GetActualLevel()
    {
        return this.actualLevel;
    }

    public class TypeInfo {
        private string category;        // "simple", "array", "slice", "struct"
        private int simpleType;         // para tipos simples: 0=int, 1=float, 2=string...
        private int size;               // para arrays: el tamaño
        private TypeInfo insideType;    // para arrays/slices: el tipo de adentro
        private Dictionary<string, TypeInfo> fields;  // para structs: nombre → tipo
        
        public string Category => category;
        public int SimpleType => simpleType;
        public int Size => size;
        public TypeInfo InsideType => insideType;
        public Dictionary<string, TypeInfo> Fields => fields;

    public TypeInfo(string category, int simpleType, int size, TypeInfo insideType, Dictionary<string, TypeInfo> fields)
    {
        this.category = category;
        this.simpleType = simpleType;
        this.size = size;
        this.insideType = insideType;
        this.fields = fields;
    }
}
    
    
    

    public abstract class Ident
    {
        private readonly IToken token;
        private readonly TypeInfo type;
        private readonly int level;
        private readonly ParserRuleContext decl;

        public IToken Token => token;

        public TypeInfo Type => type;

        public int Level => level;

        public ParserRuleContext Decl => decl;

        public Ident(IToken token, TypeInfo type, int level, ParserRuleContext decl)
        {
            this.token = token;
            this.type = type;
            this.level = level;
            this.decl = decl;
        }
    }

    public class VarIdent : Ident
    {
        public VarIdent(IToken t, TypeInfo tp, int level, ParserRuleContext d) : base(t, tp, level, d)
        {
        }
        
    }

    public class TypeIdent : Ident
    {
        public TypeIdent(IToken t, TypeInfo tp, int level, ParserRuleContext d) : base(t, tp, level, d)
        {
        }
    }

    public class FunctionIdent : Ident
    {
        private readonly LinkedList<TypeInfo> parameters;

        public LinkedList<TypeInfo> Parameters => parameters;

        public FunctionIdent(IToken t, TypeInfo tp, int level, LinkedList<TypeInfo> p, ParserRuleContext d) : base(t, tp, level, d)
        {
            this.parameters = p;
        }
    }

    public SymbolsTable()
    {
        table = new LinkedList<Ident>();
        this.actualLevel = -1;
    }

    public void InsertVariableLevel(IToken id, TypeInfo type, int level, ParserRuleContext d)
    {
        Ident i = new VarIdent(id, type, level, d);
        table.AddFirst(i);
    }

    public void InsertTypeLevel(IToken id, TypeInfo type, int level, ParserRuleContext d)
    {
        Ident i = new TypeIdent(id, type, level, d);
        table.AddFirst(i);
    }

    public void InsertMethod(IToken id, TypeInfo type, int level, LinkedList<TypeInfo> p, ParserRuleContext d)
    {
        Ident i = new FunctionIdent(id, type, level, p, d);
        table.AddFirst(i);
    }

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

    public void OpenScope()
    {
        actualLevel++;
    }

    public void CloseScope()
    {
        table = new LinkedList<Ident>(table.Where(ident => ident.Level != this.actualLevel));
        actualLevel--;
    }

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