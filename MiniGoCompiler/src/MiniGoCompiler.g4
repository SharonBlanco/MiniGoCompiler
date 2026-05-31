// MiniGoCompiler.g4
// Formato acomodado similar a AlphaCompiler.g4: lexer primero, parser después.

grammar MiniGoCompiler;

/** lexer **/
// keywords

PACKAGE        : 'package';
IF             : 'if';
ELSE           : 'else';
FOR            : 'for';
SWITCH         : 'switch';
CASE           : 'case';
DEFAULT        : 'default';
VAR            : 'var';
TYPE           : 'type';
FUNC           : 'func';
STRUCT         : 'struct';
APPEND         : 'append';
LEN            : 'len';
CAP            : 'cap';
PRINT          : 'print';
PRINTLN        : 'println';
RETURN         : 'return';
BREAK          : 'break';
CONTINUE       : 'continue';


// simbolos

SEMI           : ';';
COMMA          : ',';
DOT            : '.';
COLON          : ':';
EQUAL          : '=';
DECLARE        : ':=';

LEFTP          : '(';
RIGHTP         : ')';
LEFTB          : '[';
RIGHTB         : ']';
LEFTCB         : '{';
RIGHTCB        : '}';

DLESSEQ        : '<<=';
DMOREEQ        : '>>=';
ANDHATEQ       : '&^=';
ADDEQ          : '+=';
SUBEQ          : '-=';
ANDEQ          : '&=';
OREQ           : '|=';
MULEQ          : '*=';
DIVEQ          : '/=';
MODEQ          : '%=';
HATEQ          : '^=';

INC            : '++';
DEC            : '--';

DLESS          : '<<';
DMORE          : '>>';
ANDHAT         : '&^';
AND            : '&';
ADD            : '+';
SUB            : '-';
OR             : '|';
HAT            : '^';

EQEQ           : '==';
NOTEQ          : '!=';
LESSEQ         : '<=';
LESS           : '<';
MOREEQ         : '>=';
MORET          : '>';

DAND           : '&&';
DOR            : '||';

MUL            : '*';
DIV            : '/';
MOD            : '%';
NOT            : '!';


// other tokens

IDENTIFIER     : LETTER (LETTER | DIGIT)*;

INTLITERAL     : DECIMAL_LIT
               | BINARY_LIT
               | OCTAL_LIT
               | HEX_LIT;

FLOATLITERAL   : DECIMALS DOT DECIMALS? EXPONENT?
               | DECIMALS EXPONENT
               | DOT DECIMALS EXPONENT?;

RUNELITERAL    : '\'' (UNICODE_VALUE | BYTE_VALUE) '\'';

RAWSTRINGLITERAL
               : '`' ~'`'* '`';

INTERPRETEDSTRINGLITERAL
               : '"' (UNICODE_VALUE | BYTE_VALUE)* '"';

LINE_COMMENT   : '//' ~[\r\n]* -> channel(HIDDEN);
BLOCK_COMMENT  : '/*' .*? '*/' -> channel(HIDDEN);
WS             : [ \n\r\t]+ -> skip;

fragment LETTER           : [a-zA-Z_];
fragment DIGIT            : [0-9];
fragment DECIMAL_LIT      : '0' | [1-9] ('_'? DIGIT)*;
fragment BINARY_LIT       : '0' [bB] '_'? [01] ('_'? [01])*;
fragment OCTAL_LIT        : '0' [oO]? '_'? [0-7] ('_'? [0-7])*;
fragment HEX_LIT          : '0' [xX] '_'? [0-9a-fA-F] ('_'? [0-9a-fA-F])*;
fragment DECIMALS         : DIGIT ('_'? DIGIT)*;
fragment EXPONENT         : [eE] [+-]? DECIMALS;
fragment UNICODE_VALUE    : ~[\r\n'"\\] | ESCAPED_VALUE;
fragment BYTE_VALUE       : OCTAL_BYTE_VALUE | HEX_BYTE_VALUE;
fragment OCTAL_BYTE_VALUE : '\\' [0-7] [0-7] [0-7];
fragment HEX_BYTE_VALUE   : '\\' 'x' [0-9a-fA-F] [0-9a-fA-F];
fragment ESCAPED_VALUE    : '\\' [abfnrtv\\'"`];


/******** parser ********/

/** parser **/

root                  : PACKAGE IDENTIFIER SEMI topDeclarationList EOF;

topDeclarationList    : (variableDecl | typeDecl | funcDecl)*;

variableDecl          : VAR singleVarDecl SEMI
                      | VAR LEFTP innerVarDecls? RIGHTP SEMI;

innerVarDecls         : (singleVarDecl SEMI)+;

singleVarDecl
locals [ParserRuleContext decl = null]    
                      : identifierList declType EQUAL expressionList       #typedVarDecl
                      | identifierList EQUAL expressionList                #inferredVarDecl
                      | singleVarDeclNoExps                                #noExpressionVarDecl;


singleVarDeclNoExps   
locals [ParserRuleContext decl = null] 
                      : identifierList declType;

typeDecl              : TYPE singleTypeDecl SEMI
                      | TYPE LEFTP innerTypeDecls? RIGHTP SEMI;

innerTypeDecls        : (singleTypeDecl SEMI)+;

singleTypeDecl        : IDENTIFIER declType;

funcDecl              
                      locals [ParserRuleContext decl = null]  
                      : funcFrontDecl block SEMI;

funcFrontDecl         : FUNC IDENTIFIER LEFTP funcArgDecls? RIGHTP declType?;

funcArgDecls          : singleVarDeclNoExps (COMMA singleVarDeclNoExps)*;

declType              : LEFTP declType RIGHTP                              #groupDeclType
                      | identifier                                         #typeDenoterDeclType
                      | sliceDeclType                                      #sliceTypeDecl
                      | arrayDeclType                                      #arrayTypeDecl
                      | structDeclType                                     #structTypeDecl;

sliceDeclType         : LEFTB RIGHTB declType;

arrayDeclType         : LEFTB INTLITERAL RIGHTB declType;

structDeclType        : STRUCT LEFTCB structMemDecls? RIGHTCB;

structMemDecls        : (singleVarDeclNoExps SEMI)+;

identifierList        : IDENTIFIER (COMMA IDENTIFIER)*;

expressionList        : expression (COMMA expression)*;

expression            : primaryExpression                                  #primaryExpr
                      | ADD expression                                     #unaryAddExpr
                      | SUB expression                                     #unarySubExpr
                      | NOT expression                                     #unaryNotExpr
                      | HAT expression                                     #unaryHatExpr
                      | expression (MUL | DIV | MOD | DLESS | DMORE | AND | ANDHAT) expression   #mulExpr
                      | expression (ADD | SUB | OR | HAT) expression                             #addExpr
                      | expression (EQEQ | NOTEQ | LESS | LESSEQ | MORET | MOREEQ) expression    #relExpr
                      | expression DAND expression                                               #andExpr
                      | expression DOR expression                                                #orExpr;

primaryExpression     : operand                                            #operandPrimaryExpr
                      | primaryExpression selector                         #selectorPrimaryExpr
                      | primaryExpression index                            #indexPrimaryExpr
                      | primaryExpression arguments                        #argumentsPrimaryExpr
                      | appendExpression                                   #appendPrimaryExpr
                      | lengthExpression                                   #lengthPrimaryExpr
                      | capExpression                                      #capPrimaryExpr;
                                                                            



operand               : literal                                            #literalOperand
                      | identifier                                         #idOperand
                      | LEFTP expression RIGHTP                            #groupOperand;

literal               : INTLITERAL                                         #intLiteral
                      | FLOATLITERAL                                       #floatLiteral
                      | RUNELITERAL                                        #runeLiteral
                      | RAWSTRINGLITERAL                                   #rawStringLiteral
                      | INTERPRETEDSTRINGLITERAL                           #interpretedStringLiteral;

index                 : LEFTB expression RIGHTB;

arguments             : LEFTP expressionList? RIGHTP;

selector              : DOT IDENTIFIER;

appendExpression      : APPEND LEFTP expression COMMA expression RIGHTP;

lengthExpression      : LEN LEFTP expression RIGHTP;

capExpression         : CAP LEFTP expression RIGHTP;

statementList         : statement*;

block                 : LEFTCB statementList RIGHTCB;

statement             : PRINT LEFTP expressionList? RIGHTP SEMI            #printStatement
                      | PRINTLN LEFTP expressionList? RIGHTP SEMI          #printlnStatement
                      | RETURN expression? SEMI                            #returnStatement
                      | BREAK SEMI                                         #breakStatement
                      | CONTINUE SEMI                                      #continueStatement
                      | simpleStatement SEMI                               #simpleStmtStatement
                      | block SEMI                                         #blockStatement
                      | switchStmt SEMI                                    #switchStatement
                      | ifStatement SEMI                                   #ifStmtStatement
                      | loop SEMI                                          #loopStatement
                      | typeDecl                                           #typeDeclStatement
                      | variableDecl                                       #variableDeclStatement;

simpleStatement       :                                                    #emptySimpleStatement
                      | expression (INC | DEC)?                            #expressionSimpleStatement
                      | assignmentStatement                                #assignmentSimpleStatement
                      | expressionList DECLARE expressionList              #declareSimpleStatement;

assignmentStatement   : expressionList EQUAL expressionList                #equalAssignment
                      | expression ADDEQ expression                        #addAssignment
                      | expression ANDEQ expression                        #andAssignment
                      | expression SUBEQ expression                        #subAssignment
                      | expression OREQ expression                         #orAssignment
                      | expression MULEQ expression                        #mulAssignment
                      | expression HATEQ expression                        #hatAssignment
                      | expression DLESSEQ expression                      #dlessAssignment
                      | expression DMOREEQ expression                      #dmoreAssignment
                      | expression ANDHATEQ expression                     #andHatAssignment
                      | expression MODEQ expression                        #modAssignment
                      | expression DIVEQ expression                        #divAssignment;

ifStatement           : IF expression block                                #normalIfStatement
                      | IF expression block ELSE ifStatement               #elseIfStatement
                      | IF expression block ELSE block                     #elseBlockIfStatement
                      | IF simpleStatement SEMI expression block           #simpleIfStatement
                      | IF simpleStatement SEMI expression block ELSE ifStatement #simpleElseIfStatement
                      | IF simpleStatement SEMI expression block ELSE block       #simpleElseBlockIfStatement;

loop                  : FOR block                                          #infiniteLoop
                      | FOR expression block                               #conditionLoop
                      | FOR simpleStatement SEMI expression SEMI simpleStatement block #completeForLoop
                      | FOR simpleStatement SEMI SEMI simpleStatement block            #noConditionForLoop;

switchStmt            : SWITCH simpleStatement SEMI expression LEFTCB expressionCaseClauseList RIGHTCB #simpleExpressionSwitch
                      | SWITCH expression LEFTCB expressionCaseClauseList RIGHTCB                      #expressionSwitch
                      | SWITCH simpleStatement SEMI LEFTCB expressionCaseClauseList RIGHTCB            #simpleSwitch
                      | SWITCH LEFTCB expressionCaseClauseList RIGHTCB                                 #emptySwitch;

expressionCaseClauseList
                      : expressionCaseClause*;

expressionCaseClause  : expressionSwitchCase COLON statementList;

expressionSwitchCase  : CASE expressionList                                #caseSwitch
                      | DEFAULT                                            #defaultSwitch;
                      
identifier                
locals [ParserRuleContext decl = null]  
                      
                          : IDENTIFIER;
           
                   