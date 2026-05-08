using System;
using System.Collections.Generic;
using Windows.UI;

namespace JitHub.WinUI.Views.Controls.CodeViewer;

/// <summary>
/// Defines how each language ID maps to either a WinUIEdit HighlightingLanguage or a
/// Lexilla lexer set directly via SCI_SETLEXERLANGUAGE.
/// </summary>
internal static class ScintillaLexerDatabase
{
    // ── Scintilla message constants ────────────────────────────────────────────
    public const int SciSetLexerLanguage = 4006; // SCI_SETLEXERLANGUAGE  lParam=const char* language
    public const int SciSetKeyWords      = 4005; // SCI_SETKEYWORDS       wParam=set#, lParam=const char*

    // ── Token kinds for colour mapping ────────────────────────────────────────
    public enum TokenKind
    {
        Default,
        Comment,
        String,
        Keyword,
        Keyword2,
        Number,
        Operator,
        Preprocessor,
        Type,
        Regex,
        Variable,
        Deleted,   // diff removed line
        Added,     // diff added line
        Header,    // diff header
        Section,   // ini section
        Heading,   // markdown heading
        Link,      // markdown link
        Code,      // markdown code
        Strong,    // markdown bold
        Emphasis,  // markdown italic
    }

    // ── VS Code Dark+ / Light+ colour scheme ──────────────────────────────────

    private static Color Hex(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    public static Color TokenColor(TokenKind kind, bool dark) => dark
        ? DarkColor(kind)
        : LightColor(kind);

    private static Color DarkColor(TokenKind kind) => kind switch
    {
        TokenKind.Comment     => Hex(0x6A, 0x99, 0x55),
        TokenKind.String      => Hex(0xCE, 0x91, 0x78),
        TokenKind.Keyword     => Hex(0x56, 0x9C, 0xD6),
        TokenKind.Keyword2    => Hex(0xC5, 0x86, 0xC0),
        TokenKind.Number      => Hex(0xB5, 0xCE, 0xA8),
        TokenKind.Operator    => Hex(0xD4, 0xD4, 0xD4),
        TokenKind.Preprocessor=> Hex(0x9B, 0x9B, 0x9B),
        TokenKind.Type        => Hex(0x4E, 0xC9, 0xB0),
        TokenKind.Regex       => Hex(0xD1, 0x69, 0x69),
        TokenKind.Variable    => Hex(0x9C, 0xDC, 0xFE),
        TokenKind.Deleted     => Hex(0xFF, 0x60, 0x60),
        TokenKind.Added       => Hex(0x80, 0xFF, 0x80),
        TokenKind.Header      => Hex(0x59, 0x99, 0xFF),
        TokenKind.Section     => Hex(0x4E, 0xC9, 0xB0),
        TokenKind.Heading     => Hex(0x56, 0x9C, 0xD6),
        TokenKind.Link        => Hex(0x9C, 0xDC, 0xFE),
        TokenKind.Code        => Hex(0xCE, 0x91, 0x78),
        TokenKind.Strong      => Hex(0xD4, 0xD4, 0xD4),
        TokenKind.Emphasis    => Hex(0xD4, 0xD4, 0xD4),
        _                     => Hex(0xD4, 0xD4, 0xD4),
    };

    private static Color LightColor(TokenKind kind) => kind switch
    {
        TokenKind.Comment     => Hex(0x00, 0x80, 0x00),
        TokenKind.String      => Hex(0xA3, 0x15, 0x15),
        TokenKind.Keyword     => Hex(0x00, 0x00, 0xFF),
        TokenKind.Keyword2    => Hex(0xAF, 0x00, 0xDB),
        TokenKind.Number      => Hex(0x09, 0x86, 0x58),
        TokenKind.Operator    => Hex(0x00, 0x00, 0x00),
        TokenKind.Preprocessor=> Hex(0xD3, 0x54, 0x00),
        TokenKind.Type        => Hex(0x26, 0x7F, 0x99),
        TokenKind.Regex       => Hex(0x81, 0x1F, 0x3F),
        TokenKind.Variable    => Hex(0x00, 0x10, 0x80),
        TokenKind.Deleted     => Hex(0x9F, 0x00, 0x00),
        TokenKind.Added       => Hex(0x00, 0x80, 0x40),
        TokenKind.Header      => Hex(0x00, 0x00, 0xCC),
        TokenKind.Section     => Hex(0x26, 0x7F, 0x99),
        TokenKind.Heading     => Hex(0x00, 0x00, 0xFF),
        TokenKind.Link        => Hex(0x00, 0x10, 0x80),
        TokenKind.Code        => Hex(0xA3, 0x15, 0x15),
        TokenKind.Strong      => Hex(0x00, 0x00, 0x00),
        TokenKind.Emphasis    => Hex(0x00, 0x00, 0x00),
        _                     => Hex(0x00, 0x00, 0x00),
    };

    // ── Lexilla language config (for languages handled via SCI_SETLEXERLANGUAGE) ─
    public sealed record LexillaConfig(
        string LexerName,
        string? Keywords0,
        string? Keywords1,
        (int StyleId, TokenKind Kind)[]? StyleMap)
    {
        public string? Keywords2 { get; init; }
    }

    // ── Tier 1: map our IDs to WinUIEdit HighlightingLanguage strings ──────────
    // WinUIEdit supports: cpp, csharp, javascript, json, html, xml, yaml, plaintext

    private static readonly Dictionary<string, string> WinUIEditMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Native WinUIEdit IDs (pass-through)
            ["cpp"]         = "cpp",
            ["csharp"]      = "csharp",
            ["javascript"]  = "javascript",
            ["json"]        = "json",
            ["json5"]       = "json",
            ["jsonc"]       = "json",
            ["html"]        = "html",
            ["xml"]         = "xml",
            ["xsd"]         = "xml",
            ["xslt"]        = "xml",
            ["yaml"]        = "yaml",
            ["plaintext"]   = "plaintext",
            ["text"]        = "plaintext",

            // C-like → cpp lexer (correct syntax tokenisation, good enough colors)
            ["c"]           = "cpp",
            ["objc"]        = "cpp",
            ["objectivec"]  = "cpp",
            ["c++"]         = "cpp",
            ["java"]        = "cpp",
            ["dart"]        = "cpp",
            ["hlsl"]        = "cpp",
            ["glsl"]        = "cpp",
            ["cuda"]        = "cpp",
            ["pawn"]        = "cpp",
            ["arduino"]     = "cpp",
            ["verilog"]     = "cpp",
            ["vhdl"]        = "cpp",
            ["d"]           = "cpp",
            ["nim"]         = "cpp",
            ["zig"]         = "cpp",
            ["odin"]        = "cpp",
            ["carbon"]      = "cpp",

            // JavaScript-like — handled via Lexilla cpp lexer with rich keywords
            // (jsx/tsx removed from WinUIEditMap so they fall through to LexillaMap)
            ["vue"]         = "html",
            ["svelte"]      = "html",
            ["handlebars"]  = "html",
            ["ejs"]         = "html",
        };

    // ── Tier 2: Lexilla-direct language configs ─────────────────────────────────

    // Style constant arrays use Lexilla/SciLexer.h values.
    // Each tuple: (scintilla style id, token kind for colour mapping)

    private static readonly Dictionary<string, LexillaConfig> LexillaMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // ── Python ──────────────────────────────────────────────────────
            ["python"] = new("python",
                Keywords0: "False None True and as assert async await break class continue def del " +
                           "elif else except finally for from global if import in is lambda nonlocal " +
                           "not or pass raise return try while with yield",
                Keywords1: "ArithmeticError AssertionError AttributeError BaseException BlockingIOError " +
                           "BrokenPipeError BufferError BytesWarning ChildProcessError ConnectionAbortedError " +
                           "ConnectionError ConnectionRefusedError ConnectionResetError DeprecationWarning " +
                           "EOFError EnvironmentError Exception FileExistsError FileNotFoundError " +
                           "FloatingPointError FutureWarning GeneratorExit IOError ImportError ImportWarning " +
                           "IndentationError IndexError InterruptedError IsADirectoryError KeyError " +
                           "KeyboardInterrupt LookupError MemoryError ModuleNotFoundError NameError " +
                           "NotADirectoryError NotImplemented NotImplementedError OSError OverflowError " +
                           "PendingDeprecationWarning PermissionError ProcessLookupError RecursionError " +
                           "ReferenceError ResourceWarning RuntimeError RuntimeWarning StopAsyncIteration " +
                           "StopIteration SyntaxError SyntaxWarning SystemError SystemExit TabError " +
                           "TimeoutError TypeError UnboundLocalError UnicodeDecodeError UnicodeEncodeError " +
                           "UnicodeError UnicodeTranslateError UnicodeWarning UserWarning ValueError Warning " +
                           "ZeroDivisionError abs all any ascii bin bool breakpoint bytearray bytes callable " +
                           "chr classmethod compile complex copyright credits delattr dict dir divmod " +
                           "enumerate eval exec exit filter float format frozenset getattr globals hasattr " +
                           "hash help hex id input int isinstance issubclass iter len license list locals " +
                           "map max memoryview min next object oct open ord pow print property quit range " +
                           "repr reversed round set setattr slice sorted staticmethod str sum super tuple " +
                           "type vars zip",
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_P_COMMENTLINE
                    (2,  TokenKind.Number),      // SCE_P_NUMBER
                    (3,  TokenKind.String),      // SCE_P_STRING
                    (4,  TokenKind.String),      // SCE_P_CHARACTER
                    (5,  TokenKind.Keyword),     // SCE_P_WORD
                    (6,  TokenKind.String),      // SCE_P_TRIPLE
                    (7,  TokenKind.String),      // SCE_P_TRIPLEDOUBLE
                    (8,  TokenKind.Type),        // SCE_P_CLASSNAME
                    (9,  TokenKind.Type),        // SCE_P_DEFNAME
                    (10, TokenKind.Operator),    // SCE_P_OPERATOR
                    (12, TokenKind.Comment),     // SCE_P_COMMENTBLOCK
                    (13, TokenKind.String),      // SCE_P_STRINGEOL
                    (14, TokenKind.Keyword2),    // SCE_P_WORD2
                    (15, TokenKind.Preprocessor),// SCE_P_DECORATOR
                    (16, TokenKind.String),      // SCE_P_FSTRING
                    (17, TokenKind.String),      // SCE_P_FCHARACTER
                    (18, TokenKind.String),      // SCE_P_FTRIPLE
                    (19, TokenKind.String),      // SCE_P_FTRIPLEDOUBLE
                }),

            // ── Ruby ────────────────────────────────────────────────────────
            ["ruby"] = new("ruby",
                Keywords0: "BEGIN END __ENCODING__ __END__ __FILE__ __LINE__ alias and begin break case " +
                           "class def defined? do else elsif end ensure false for if in module next nil " +
                           "not or redo rescue retry return self super then true undef unless until when " +
                           "while yield",
                Keywords1: null,
                StyleMap: new[]
                {
                    (2,  TokenKind.Comment),     // SCE_RB_COMMENTLINE
                    (3,  TokenKind.Comment),     // SCE_RB_POD
                    (4,  TokenKind.Number),      // SCE_RB_NUMBER
                    (5,  TokenKind.Keyword),     // SCE_RB_WORD
                    (6,  TokenKind.String),      // SCE_RB_STRING
                    (7,  TokenKind.String),      // SCE_RB_CHARACTER
                    (8,  TokenKind.Type),        // SCE_RB_CLASSNAME
                    (9,  TokenKind.Type),        // SCE_RB_DEFNAME
                    (10, TokenKind.Operator),    // SCE_RB_OPERATOR
                    (12, TokenKind.Regex),       // SCE_RB_REGEX
                    (13, TokenKind.Variable),    // SCE_RB_GLOBAL
                    (14, TokenKind.String),      // SCE_RB_SYMBOL
                    (15, TokenKind.Type),        // SCE_RB_MODULE_NAME
                    (16, TokenKind.Variable),    // SCE_RB_INSTANCE_VAR
                    (17, TokenKind.Variable),    // SCE_RB_CLASS_VAR
                }),

            // ── Shell / Bash ─────────────────────────────────────────────────
            ["bash"] = new("bash",
                Keywords0: "break case continue do done elif else esac eval exec exit export fi for " +
                           "function if in local readonly return set shift source then trap until while",
                Keywords1: null,
                StyleMap: new[]
                {
                    (2,  TokenKind.Comment),     // SCE_SH_COMMENTLINE
                    (3,  TokenKind.Number),      // SCE_SH_NUMBER
                    (4,  TokenKind.Keyword),     // SCE_SH_WORD
                    (5,  TokenKind.String),      // SCE_SH_STRING
                    (6,  TokenKind.String),      // SCE_SH_CHARACTER
                    (7,  TokenKind.Operator),    // SCE_SH_OPERATOR
                    (9,  TokenKind.Variable),    // SCE_SH_SCALAR
                    (10, TokenKind.Variable),    // SCE_SH_PARAM
                    (11, TokenKind.String),      // SCE_SH_BACKTICKS
                    (12, TokenKind.String),      // SCE_SH_HERE_DELIM
                    (13, TokenKind.String),      // SCE_SH_HERE_Q
                }),

            // ── SQL ──────────────────────────────────────────────────────────
            ["sql"] = new("sql",
                Keywords0: "abort action add after all alter always analyze and as asc attach autoincrement " +
                           "before begin between by cascade case cast check collate column commit conflict " +
                           "constraint create cross current_date current_time current_timestamp database " +
                           "default deferrable deferred delete detach distinct drop each else end escape " +
                           "except exclusive exists explain fail for foreign from full glob group having " +
                           "if ignore immediate in index indexed initially inner insert instead intersect " +
                           "into is isnull join key left like limit match natural no not notnull null of " +
                           "offset on or order outer plan pragma primary query raise recursive references " +
                           "regexp reindex release rename replace restrict right rollback row savepoint " +
                           "select set table temp temporary then to transaction trigger union unique until " +
                           "update using vacuum values view virtual when where with without " +
                           "bigint binary bit blob boolean char character date datetime decimal double " +
                           "float integer mediumint nchar nvarchar numeric real smallint text tinyint " +
                           "unsigned varchar year",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_SQL_COMMENT
                    (2,  TokenKind.Comment),     // SCE_SQL_COMMENTLINE
                    (3,  TokenKind.Comment),     // SCE_SQL_COMMENTDOC
                    (4,  TokenKind.Number),      // SCE_SQL_NUMBER
                    (5,  TokenKind.Keyword),     // SCE_SQL_WORD
                    (6,  TokenKind.String),      // SCE_SQL_STRING
                    (7,  TokenKind.String),      // SCE_SQL_CHARACTER
                    (10, TokenKind.Operator),    // SCE_SQL_OPERATOR
                    (13, TokenKind.Comment),     // SCE_SQL_COMMENTLINEDOC
                    (15, TokenKind.Comment),     // SCE_SQL_COMMENTLINEDOC
                    (16, TokenKind.Keyword2),    // SCE_SQL_WORD2
                }),

            // ── CSS ──────────────────────────────────────────────────────────
            ["css"] = new("css",
                Keywords0: null,
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Type),        // SCE_CSS_TAG
                    (2,  TokenKind.Variable),    // SCE_CSS_CLASS
                    (3,  TokenKind.Keyword),     // SCE_CSS_PSEUDOCLASS
                    (4,  TokenKind.Keyword),     // SCE_CSS_UNKNOWN_PSEUDOCLASS
                    (5,  TokenKind.Operator),    // SCE_CSS_OPERATOR
                    (6,  TokenKind.Keyword),     // SCE_CSS_IDENTIFIER
                    (8,  TokenKind.String),      // SCE_CSS_VALUE
                    (9,  TokenKind.Comment),     // SCE_CSS_COMMENT
                    (10, TokenKind.Variable),    // SCE_CSS_ID
                    (11, TokenKind.Keyword2),    // SCE_CSS_IMPORTANT
                    (12, TokenKind.Preprocessor),// SCE_CSS_DIRECTIVE
                    (13, TokenKind.String),      // SCE_CSS_DOUBLESTRING
                    (14, TokenKind.String),      // SCE_CSS_SINGLESTRING
                    (22, TokenKind.Keyword),     // SCE_CSS_MEDIA
                    (23, TokenKind.Variable),    // SCE_CSS_VARIABLE
                }),

            // ── PowerShell ───────────────────────────────────────────────────
            ["powershell"] = new("powershell",
                Keywords0: "begin break catch class continue data define do dynamicparam else elseif end " +
                           "exit filter finally for foreach from function if in inlinescript parallel " +
                           "param process return sequence switch throw trap try until using var while",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_PS_COMMENT
                    (2,  TokenKind.String),      // SCE_PS_STRING
                    (3,  TokenKind.String),      // SCE_PS_CHARACTER
                    (4,  TokenKind.Number),      // SCE_PS_NUMBER
                    (5,  TokenKind.Variable),    // SCE_PS_VARIABLE
                    (6,  TokenKind.Operator),    // SCE_PS_OPERATOR
                    (8,  TokenKind.Keyword),     // SCE_PS_KEYWORD
                    (9,  TokenKind.Type),        // SCE_PS_CMDLET
                    (10, TokenKind.Keyword2),    // SCE_PS_ALIAS
                    (11, TokenKind.Type),        // SCE_PS_FUNCTION
                    (14, TokenKind.Comment),     // SCE_PS_COMMENTSTREAM
                    (15, TokenKind.String),      // SCE_PS_HERE_STRING
                    (16, TokenKind.String),      // SCE_PS_HERE_CHARACTER
                }),

            // ── Lua ──────────────────────────────────────────────────────────
            ["lua"] = new("lua",
                Keywords0: "and break do else elseif end false for function goto if in local nil not or " +
                           "repeat return then true until while",
                Keywords1: "assert collectgarbage dofile error _G getmetatable ipairs load loadfile next " +
                           "pairs pcall print rawequal rawget rawlen rawset require select setmetatable " +
                           "tonumber tostring type _VERSION xpcall",
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_LUA_COMMENT
                    (2,  TokenKind.Comment),     // SCE_LUA_COMMENTLINE
                    (3,  TokenKind.Comment),     // SCE_LUA_COMMENTDOC
                    (4,  TokenKind.Number),      // SCE_LUA_NUMBER
                    (5,  TokenKind.Keyword),     // SCE_LUA_WORD
                    (6,  TokenKind.String),      // SCE_LUA_STRING
                    (7,  TokenKind.String),      // SCE_LUA_CHARACTER
                    (8,  TokenKind.String),      // SCE_LUA_LITERALSTRING
                    (9,  TokenKind.Preprocessor),// SCE_LUA_PREPROCESSOR
                    (10, TokenKind.Operator),    // SCE_LUA_OPERATOR
                    (13, TokenKind.Keyword2),    // SCE_LUA_WORD2
                }),

            // ── R ────────────────────────────────────────────────────────────
            ["r"] = new("r",
                Keywords0: "if else repeat while function for in next break TRUE FALSE NULL Inf NaN NA " +
                           "NA_integer_ NA_real_ NA_complex_ NA_character_",
                Keywords1: null,
                StyleMap: new[]
                {
                    (2,  TokenKind.Comment),     // SCE_R_COMMENT
                    (3,  TokenKind.Number),      // SCE_R_NUMBER
                    (4,  TokenKind.String),      // SCE_R_STRING
                    (5,  TokenKind.String),      // SCE_R_STRING2
                    (6,  TokenKind.Keyword),     // SCE_R_KEYWORD
                    (7,  TokenKind.Operator),    // SCE_R_OPERATOR
                    (8,  TokenKind.Variable),    // SCE_R_IDENTIFIER
                }),

            // ── Visual Basic / VB.NET ────────────────────────────────────────
            ["vbnet"] = new("vb",
                Keywords0: "AddHandler AddressOf AndAlso Alias And As Boolean ByRef Byte ByVal Call Case " +
                           "Catch CBool CByte CChar CDate CDbl CDec Char CInt Class CLng CObj Const " +
                           "Continue CSByte CShort CSng CStr CType CUInt CULng CUShort Date Decimal " +
                           "Declare Default Delegate Dim DirectCast Do Double Each Else ElseIf End Enum " +
                           "Equals Error Event Exit False Finally For Friend Function Get GetType " +
                           "GetXMLNamespace Global GoSub GoTo Handles If Implements Imports In Inherits " +
                           "Integer Interface Is IsNot Let Lib Like Long Loop Me Mod Module MustInherit " +
                           "MustOverride MyBase MyClass Namespace Narrowing New Next Not Nothing " +
                           "NotInheritable NotOverridable Object Of On Operator Option Optional Or OrElse " +
                           "Out Overloads Overridable Overrides ParamArray Partial Private Property " +
                           "Protected Public RaiseEvent ReadOnly ReDim RemoveHandler Resume Return SByte " +
                           "Select Set Shadows Shared Short Single Static Step Stop String Structure Sub " +
                           "SyncLock Then Throw To True Try TryCast TypeOf UInteger ULong UShort Using " +
                           "Variant Wend When While Widening With WithEvents WriteOnly Xor",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_B_COMMENT
                    (2,  TokenKind.Number),      // SCE_B_NUMBER
                    (3,  TokenKind.Keyword),     // SCE_B_KEYWORD
                    (4,  TokenKind.String),      // SCE_B_STRING
                    (5,  TokenKind.Preprocessor),// SCE_B_PREPROCESSOR
                    (6,  TokenKind.Operator),    // SCE_B_OPERATOR
                    (9,  TokenKind.String),      // SCE_B_STRINGEOL
                    (10, TokenKind.Keyword2),    // SCE_B_KEYWORD2
                    (11, TokenKind.Type),        // SCE_B_KEYWORD3
                }),

            // ── Diff / Patch ─────────────────────────────────────────────────
            ["diff"] = new("diff",
                Keywords0: null,
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_DIFF_COMMENT
                    (2,  TokenKind.Preprocessor),// SCE_DIFF_COMMAND
                    (3,  TokenKind.Header),      // SCE_DIFF_HEADER
                    (4,  TokenKind.Type),        // SCE_DIFF_POSITION
                    (5,  TokenKind.Deleted),     // SCE_DIFF_DELETED
                    (6,  TokenKind.Added),       // SCE_DIFF_ADDED
                    (7,  TokenKind.Keyword),     // SCE_DIFF_CHANGED
                }),

            // ── Makefile ─────────────────────────────────────────────────────
            ["makefile"] = new("makefile",
                Keywords0: null,
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_MAKE_COMMENT
                    (2,  TokenKind.Preprocessor),// SCE_MAKE_PREPROCESSOR
                    (3,  TokenKind.Variable),    // SCE_MAKE_IDENTIFIER
                    (4,  TokenKind.Operator),    // SCE_MAKE_OPERATOR
                    (5,  TokenKind.Type),        // SCE_MAKE_TARGET
                }),

            // ── Dockerfile ───────────────────────────────────────────────────
            ["dockerfile"] = new("dockerfile",
                Keywords0: "ADD ARG CMD COPY ENTRYPOINT ENV EXPOSE FROM HEALTHCHECK LABEL MAINTAINER " +
                           "ONBUILD RUN SHELL STOPSIGNAL USER VOLUME WORKDIR",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_DOCKERFILE_COMMENT
                    (2,  TokenKind.Keyword),     // SCE_DOCKERFILE_INSTRUCTION
                    (4,  TokenKind.String),      // SCE_DOCKERFILE_STRING
                }),

            // ── TOML ─────────────────────────────────────────────────────────
            ["toml"] = new("toml",
                Keywords0: "true false inf nan",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_TOML_COMMENT
                    (2,  TokenKind.Variable),    // SCE_TOML_IDENTIFIER
                    (3,  TokenKind.Keyword),     // SCE_TOML_KEYWORD
                    (4,  TokenKind.Number),      // SCE_TOML_NUMBER
                    (5,  TokenKind.String),      // SCE_TOML_STRING
                    (6,  TokenKind.Keyword),     // SCE_TOML_BOOL
                    (7,  TokenKind.String),      // SCE_TOML_DATE
                    (10, TokenKind.Section),     // SCE_TOML_TABLE
                    (11, TokenKind.Type),        // SCE_TOML_KEY
                }),

            // ── INI / Properties ─────────────────────────────────────────────
            ["ini"] = new("props",
                Keywords0: null,
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_PROPS_COMMENT
                    (2,  TokenKind.Section),     // SCE_PROPS_SECTION
                    (3,  TokenKind.Operator),    // SCE_PROPS_ASSIGNMENT
                    (4,  TokenKind.String),      // SCE_PROPS_DEFVAL
                    (5,  TokenKind.Keyword),     // SCE_PROPS_KEY
                }),

            // ── Batch ────────────────────────────────────────────────────────
            ["batch"] = new("batch",
                Keywords0: "call cd chdir cls cmd copy date del dir do echo endlocal erase exit exist for " +
                           "ftype goto if md mkdir move path pause popd prompt pushd rd rem ren rename " +
                           "rmdir setlocal shift start time title type ver verify vol",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_BAT_COMMENT
                    (2,  TokenKind.Keyword),     // SCE_BAT_WORD
                    (3,  TokenKind.Type),        // SCE_BAT_LABEL
                    (4,  TokenKind.Preprocessor),// SCE_BAT_HIDE
                    (5,  TokenKind.Keyword2),    // SCE_BAT_COMMAND
                    (6,  TokenKind.Variable),    // SCE_BAT_IDENTIFIER
                    (7,  TokenKind.Operator),    // SCE_BAT_OPERATOR
                }),

            // ── Markdown ─────────────────────────────────────────────────────
            ["markdown"] = new("markdown",
                Keywords0: null,
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Strong),      // SCE_MARKDOWN_STRONG1
                    (2,  TokenKind.Strong),      // SCE_MARKDOWN_STRONG2
                    (3,  TokenKind.Emphasis),    // SCE_MARKDOWN_EM1
                    (4,  TokenKind.Emphasis),    // SCE_MARKDOWN_EM2
                    (5,  TokenKind.Heading),     // SCE_MARKDOWN_HEADER1
                    (6,  TokenKind.Heading),     // SCE_MARKDOWN_HEADER2
                    (7,  TokenKind.Heading),     // SCE_MARKDOWN_HEADER3
                    (8,  TokenKind.Heading),     // SCE_MARKDOWN_HEADER4
                    (9,  TokenKind.Heading),     // SCE_MARKDOWN_HEADER5
                    (10, TokenKind.Heading),     // SCE_MARKDOWN_HEADER6
                    (12, TokenKind.String),      // SCE_MARKDOWN_ULIST_ITEM
                    (13, TokenKind.String),      // SCE_MARKDOWN_OLIST_ITEM
                    (14, TokenKind.Comment),     // SCE_MARKDOWN_BLOCKQUOTE
                    (16, TokenKind.Operator),    // SCE_MARKDOWN_HRULE
                    (17, TokenKind.Link),        // SCE_MARKDOWN_LINK
                    (18, TokenKind.Code),        // SCE_MARKDOWN_CODE
                    (19, TokenKind.Code),        // SCE_MARKDOWN_CODE2
                    (20, TokenKind.Code),        // SCE_MARKDOWN_CODEBK
                }),

            // ── Perl ─────────────────────────────────────────────────────────
            ["perl"] = new("perl",
                Keywords0: "abs accept alarm and atan2 BEGIN bind binmode bless break caller chdir chmod " +
                           "chomp chop chown chr chroot close closedir cmp connect continue cos crypt " +
                           "dbmclose dbmopen defined delete die do dump each elsif END else eq eval exec " +
                           "exists exit exp fcntl fileno flock for foreach fork format given glob goto " +
                           "grep hex if index int ioctl join keys kill last lc lcfirst le length link " +
                           "listen local localtime lock log lstat lt map mkdir msgctl msgget msgsnd " +
                           "msgrcv my ne next no not oct open opendir or ord our pack package pipe pop " +
                           "pos print printf prototype push q qq qr qw qx rand read readdir readline " +
                           "readlink readpipe recv redo ref rename require reset return reverse rewinddir " +
                           "rindex rmdir say scalar seek seekdir select semctl semget semop send setpgrp " +
                           "setpriority setsockopt shift shmctl shmget shmread shmwrite shutdown sin " +
                           "sleep socket socketpair sort splice split sprintf sqrt srand stat study " +
                           "substr symlink syscall sysopen sysread sysseek system syswrite tell telldir " +
                           "tie tied time times truncate uc ucfirst umask undef unless unlink unpack " +
                           "unshift untie until use utime values vec wait waitpid wantarray warn when " +
                           "while write",
                Keywords1: null,
                StyleMap: new[]
                {
                    (2,  TokenKind.Comment),     // SCE_PL_COMMENTLINE
                    (4,  TokenKind.Number),      // SCE_PL_NUMBER
                    (5,  TokenKind.Keyword),     // SCE_PL_WORD
                    (6,  TokenKind.String),      // SCE_PL_STRING
                    (7,  TokenKind.String),      // SCE_PL_CHARACTER
                    (8,  TokenKind.Preprocessor),// SCE_PL_PREPROCESSOR
                    (10, TokenKind.Operator),    // SCE_PL_OPERATOR
                    (12, TokenKind.Regex),       // SCE_PL_REGEX
                    (17, TokenKind.Variable),    // SCE_PL_SCALAR
                    (18, TokenKind.Variable),    // SCE_PL_ARRAY
                    (19, TokenKind.Variable),    // SCE_PL_HASH
                    (20, TokenKind.Variable),    // SCE_PL_SYMBOLTABLE
                }),

            // ── Go (uses cpp lexer but with Go keywords for richer highlighting)
            ["go"] = new("cpp",
                Keywords0: "break case chan const continue default defer else fallthrough for func go " +
                           "goto if import interface map package range return select struct switch type var",
                Keywords1: "bool byte complex64 complex128 error float32 float64 int int8 int16 int32 " +
                           "int64 rune string uint uint8 uint16 uint32 uint64 uintptr",
                StyleMap: null),   // Colors handled by WinUIEdit cpp color scheme

            // ── Rust ─────────────────────────────────────────────────────────
            ["rust"] = new("cpp",
                Keywords0: "as async await break const continue crate dyn else enum extern false fn for " +
                           "if impl in let loop match mod move mut pub ref return self Self static struct " +
                           "super trait true type union unsafe use where while",
                Keywords1: "bool char f32 f64 i8 i16 i32 i64 i128 isize str u8 u16 u32 u64 u128 usize " +
                           "String Vec Option Result Box Arc Rc",
                StyleMap: null),

            // ── Swift ────────────────────────────────────────────────────────
            ["swift"] = new("cpp",
                Keywords0: "actor any as associatedtype associativity async await break case catch class " +
                           "continue convenience default defer deinit distributed do dynamic else enum " +
                           "extension fallthrough false fileprivate final for func get guard if import " +
                           "in indirect infix init inout internal is isolated lazy left let mutating " +
                           "nonisolated none nonmutating open operator optional override postfix " +
                           "precedencegroup prefix private protocol public repeat required rethrows " +
                           "return right self Self setter_access some static struct subscript super " +
                           "switch throw throws true try typealias unowned unsafe var weak where while",
                Keywords1: "Bool Character Double Float Int Int8 Int16 Int32 Int64 Optional String UInt " +
                           "UInt8 UInt16 UInt32 UInt64 Void Array Dictionary Set",
                StyleMap: null),

            // ── Kotlin ───────────────────────────────────────────────────────
            ["kotlin"] = new("cpp",
                Keywords0: "abstract actual annotation as break by catch class companion const constructor " +
                           "continue crossinline data do dynamic else enum expect external false final " +
                           "finally for fun get if import in infixline init inline inner interface " +
                           "internal is it lateinit noinline null object open operator out override " +
                           "package private protected public reified return sealed set super suspend " +
                           "tailrec this throw true try typealias typeof val var vararg when where while",
                Keywords1: "Boolean Byte Char Double Float Int Long Short String Unit Any Nothing",
                StyleMap: null),

            // ── Scala ────────────────────────────────────────────────────────
            ["scala"] = new("cpp",
                Keywords0: "abstract case catch class def do else extends false final finally for " +
                           "forSome if implicit import lazy match new null object override package " +
                           "private protected return sealed super this throw trait true try type val " +
                           "var while with yield",
                Keywords1: "AnyRef AnyVal Boolean Byte Char Double Float Int Long Nothing Null Short " +
                           "String Unit",
                StyleMap: null),

            // ── Java ─────────────────────────────────────────────────────────
            ["java"] = new("cpp",
                Keywords0: "abstract assert boolean break byte case catch char class const continue " +
                           "default do double else enum extends final finally float for goto if " +
                           "implements import instanceof int interface long native new package private " +
                           "protected public record return sealed short static strictfp super switch " +
                           "synchronized this throw throws transient try var void volatile while",
                Keywords1: "Boolean Byte Character Double Float Integer Long Object Short String Void",
                StyleMap: null),

            // ── TypeScript ───────────────────────────────────────────────────
            ["typescript"] = new("cpp",
                Keywords0: "abstract accessor any as asserts async at await bigint boolean break case " +
                           "catch class const constructor continue debugger declare default delete do " +
                           "else enum export extends false finally for from function get global if " +
                           "implements import in infer instanceof interface intrinsic is keyof let " +
                           "module namespace never new null number object of out override package " +
                           "private protected public readonly require return satisfies set static " +
                           "string super switch symbol this throw true try type typeof undefined " +
                           "unique unknown until using var void while with yield",
                Keywords1: null,
                StyleMap: null),

            // ── PHP ──────────────────────────────────────────────────────────
            ["php"] = new("cpp",
                Keywords0: "abstract and array as break callable case catch class clone const continue " +
                           "declare default do echo else elseif empty enddeclare endfor endforeach " +
                           "endif endswitch endwhile enum eval exit extends final finally fn for " +
                           "foreach function global goto if implements include include_once instanceof " +
                           "insteadof interface isset list match namespace new or print private " +
                           "protected public readonly require require_once return static switch throw " +
                           "trait try unset use var while xor yield",
                Keywords1: null,
                StyleMap: null),

            // ── Dart ─────────────────────────────────────────────────────────
            ["dart"] = new("cpp",
                Keywords0: "abstract as assert async await break case catch class const continue " +
                           "covariant default deferred do dynamic else enum export extends extension " +
                           "external factory false final finally for function get hide if implements " +
                           "import in interface is late library mixin new null of on operator part " +
                           "required rethrow return sealed set show static super switch sync this " +
                           "throw true try typedef var void when while with yield",
                Keywords1: "bool double dynamic int num object string void",
                StyleMap: null),

            // ── F# ───────────────────────────────────────────────────────────
            ["fsharp"] = new("cpp",
                Keywords0: "abstract and as assert async base begin class default delegate do done " +
                           "downcast downto elif else end exception extern false finally fixed for " +
                           "fun function global if in inherit inline interface internal lazy let " +
                           "match member module mutable namespace new not null of open or override " +
                           "private public rec return sig static struct then to true try type upcast " +
                           "use val void when while with yield",
                Keywords1: "bool byte char decimal double float float32 float64 int int8 int16 int32 " +
                           "int64 nativeint obj sbyte single string uint uint8 uint16 uint32 uint64 " +
                           "unativeint unit",
                StyleMap: null),

            // ── Elixir ───────────────────────────────────────────────────────
            ["elixir"] = new("cpp",
                Keywords0: "after alias and case catch cond def defcallback defdelegate defexception " +
                           "defguard defimpl defmacro defmacrop defmodule defoverridable defp " +
                           "defprotocol defstruct do else end fn for if import in not or quote raise " +
                           "receive require rescue try unless unquote unquote_splicing use when with",
                Keywords1: null,
                StyleMap: null),

            // ── Haskell ──────────────────────────────────────────────────────
            ["haskell"] = new("cpp",
                Keywords0: "case class data default deriving do else forall foreign hiding if import " +
                           "in infix infixl infixr instance let module newtype of qualified then type " +
                           "where",
                Keywords1: "Bool Char Double Float IO Int Integer Maybe Ordering String",
                StyleMap: null),

            // ── CMake ────────────────────────────────────────────────────────
            ["cmake"] = new("cmake",
                Keywords0: "add_compile_definitions add_compile_options add_custom_command " +
                           "add_custom_target add_definitions add_dependencies add_executable " +
                           "add_library add_subdirectory add_test cmake_minimum_required " +
                           "configure_file enable_language enable_testing execute_process " +
                           "file find_file find_library find_package find_path find_program " +
                           "foreach function get_cmake_property get_directory_property " +
                           "get_filename_component get_property get_source_file_property " +
                           "get_target_property if include include_directories install list " +
                           "macro math message option project set set_directory_properties " +
                           "set_property set_source_files_properties set_target_properties " +
                           "set_tests_properties string target_compile_definitions " +
                           "target_compile_features target_compile_options target_include_directories " +
                           "target_link_directories target_link_libraries target_link_options " +
                           "target_sources while",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1,  TokenKind.Comment),     // SCE_CMAKE_COMMENT
                    (2,  TokenKind.String),      // SCE_CMAKE_STRINGDQ
                    (3,  TokenKind.String),      // SCE_CMAKE_STRINGSQ
                    (4,  TokenKind.String),      // SCE_CMAKE_STRINGLQ
                    (5,  TokenKind.Variable),    // SCE_CMAKE_VARIABLE
                    (6,  TokenKind.Number),      // SCE_CMAKE_NUMBER
                    (8,  TokenKind.Keyword),     // SCE_CMAKE_WORD
                    (9,  TokenKind.Keyword2),    // SCE_CMAKE_COMMANDS_DEPRECATED
                    (12, TokenKind.Variable),    // SCE_CMAKE_VARIABLE2
                }),

            // ═════════════════════════════════════════════════════════════════
            // Additional broad language coverage (top GitHub languages)
            // ═════════════════════════════════════════════════════════════════

            // ── JavaScript: WinUIEdit uses cpp lexer; this entry feeds rich keywords via override ──
            ["javascript"] = new("cpp",
                Keywords0: "abstract arguments async await boolean break byte case catch char class const " +
                           "continue debugger default delete do double else enum eval export extends " +
                           "false final finally float for from function get goto if implements import in " +
                           "instanceof int interface let long native new null of package private protected " +
                           "public return set short static super switch synchronized this throw throws " +
                           "transient true try typeof undefined var void volatile while with yield",
                Keywords1: "Array ArrayBuffer Boolean Date Error Function Infinity JSON Map Math NaN Number " +
                           "Object Promise Proxy Reflect RegExp Set String Symbol WeakMap WeakSet console " +
                           "document window globalThis Intl URL URLSearchParams fetch require module exports",
                StyleMap: null),

            // ── JSX/TSX ──────────────────────────────────────────────────────
            ["jsx"] = new("cpp",
                Keywords0: "as async await break case catch class const continue debugger default delete " +
                           "do else enum export extends false finally for from function get if import " +
                           "in instanceof let new null of package private protected public return set " +
                           "static super switch this throw true try typeof undefined var void while with yield",
                Keywords1: "React Component Fragment useState useEffect useContext useReducer useCallback " +
                           "useMemo useRef useImperativeHandle useLayoutEffect useDebugValue useId " +
                           "useTransition useDeferredValue Array Object String Number Boolean Promise Map Set",
                StyleMap: null),
            ["tsx"] = new("cpp",
                Keywords0: "abstract any as asserts async await bigint boolean break case catch class const " +
                           "constructor continue debugger declare default delete do else enum export extends " +
                           "false finally for from function get if implements import in infer instanceof " +
                           "interface is keyof let module namespace never new null number object of override " +
                           "package private protected public readonly require return satisfies set static " +
                           "string super switch symbol this throw true try type typeof undefined unique " +
                           "unknown var void while with yield",
                Keywords1: "React Component Fragment useState useEffect useContext useReducer useCallback " +
                           "useMemo useRef Array Object String Number Boolean Promise Map Set Record Partial " +
                           "Required Readonly Pick Omit Exclude Extract NonNullable Parameters ReturnType",
                StyleMap: null),

            // ── HTML / XML keyword override (handled by WinUIEdit but we keep here for reference) ──

            // ── ASM / x86 / MASM / GAS / NASM ─────────────────────────────────
            ["asm"] = new("asm",
                Keywords0: "aaa aad aam aas adc add and call cbw clc cld cli cmc cmp cmpsb cmpsw cwd " +
                           "daa das dec div esc hlt idiv imul in inc int into iret ja jae jb jbe jc " +
                           "jcxz je jg jge jl jle jmp jna jnae jnb jnbe jnc jne jng jnge jnl jnle jno " +
                           "jnp jns jnz jo jp jpe jpo js jz lahf lds lea les lock lodsb lodsw loop " +
                           "loope loopne loopnz loopz mov movsb movsw mul neg nop not or out pop popf " +
                           "push pushf rcl rcr ret retf retn rol ror sahf sal sar sbb scasb scasw shl " +
                           "shr stc std sti stosb stosw sub test wait xchg xlat xor mov add sub mul div " +
                           "movzx movsx lea cdq cqo syscall ret leave int3",
                Keywords1: "ah al ax bh bl bp bx ch cl cs cx dh di dl ds dx es ip si sp ss " +
                           "eax ebx ecx edx ebp esp esi edi rax rbx rcx rdx rbp rsp rsi rdi r8 r9 " +
                           "r10 r11 r12 r13 r14 r15 xmm0 xmm1 xmm2 xmm3 xmm4 xmm5 xmm6 xmm7 ymm0 zmm0",
                StyleMap: new[]
                {
                    (1, TokenKind.Comment),
                    (2, TokenKind.Number),
                    (3, TokenKind.String),
                    (4, TokenKind.Operator),
                    (5, TokenKind.Variable),     // SCE_ASM_IDENTIFIER
                    (6, TokenKind.Keyword),      // SCE_ASM_CPUINSTRUCTION
                    (7, TokenKind.Keyword2),     // SCE_ASM_MATHINSTRUCTION
                    (8, TokenKind.Type),         // SCE_ASM_REGISTER
                    (9, TokenKind.Preprocessor), // SCE_ASM_DIRECTIVE
                    (10, TokenKind.Preprocessor),// SCE_ASM_DIRECTIVEOPERAND
                    (11, TokenKind.Comment),     // SCE_ASM_COMMENTBLOCK
                    (12, TokenKind.String),      // SCE_ASM_CHARACTER
                    (13, TokenKind.String),      // SCE_ASM_STRINGEOL
                    (14, TokenKind.Type),        // SCE_ASM_EXTINSTRUCTION
                })
                {
                    Keywords2 = "section text data bss global extern db dw dd dq resb resw resd resq " +
                                "equ times byte word dword qword ptr offset"
                },

            // ── Tcl ───────────────────────────────────────────────────────────
            ["tcl"] = new("tcl",
                Keywords0: "after append apply array auto_execok auto_import auto_load auto_mkindex " +
                           "auto_qualify auto_reset bgerror binary break case catch cd chan clock close " +
                           "concat continue coroutine dde dict encoding eof error eval exec exit expr " +
                           "fblocked fconfigure fcopy file fileevent flush for foreach format gets glob " +
                           "global history if incr info interp join lappend lassign lindex linsert list " +
                           "llength lmap load lpop lrange lremove lrepeat lreplace lreverse lsearch lset " +
                           "lsort namespace next nextto open package parray pid proc puts pwd read regexp " +
                           "regsub rename return scan seek set socket source split string subst switch " +
                           "tailcall tcl_endOfWord tell throw time timerate trace try unknown unload " +
                           "unset update uplevel upvar variable vwait while yield yieldto zlib",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1, TokenKind.Comment),
                    (2, TokenKind.Comment),
                    (3, TokenKind.Comment),
                    (4, TokenKind.Number),
                    (5, TokenKind.Keyword),
                    (6, TokenKind.String),
                    (7, TokenKind.String),
                    (8, TokenKind.String),
                    (9, TokenKind.Operator),
                    (10, TokenKind.Variable),
                    (11, TokenKind.Variable),
                }),

            // ── LaTeX ─────────────────────────────────────────────────────────
            ["latex"] = new("latex",
                Keywords0: null,
                Keywords1: null,
                StyleMap: new[]
                {
                    (1, TokenKind.Keyword),    // SCE_L_COMMAND
                    (2, TokenKind.Type),       // SCE_L_TAG
                    (3, TokenKind.Operator),   // SCE_L_MATH
                    (4, TokenKind.Comment),    // SCE_L_COMMENT
                    (5, TokenKind.Type),       // SCE_L_TAG2
                    (6, TokenKind.String),     // SCE_L_MATH2
                    (7, TokenKind.Comment),    // SCE_L_COMMENT2
                    (8, TokenKind.String),     // SCE_L_VERBATIM
                    (9, TokenKind.Number),     // SCE_L_SHORTCMD
                    (10, TokenKind.Operator),  // SCE_L_SPECIAL
                    (11, TokenKind.Code),      // SCE_L_CMDOPT
                    (12, TokenKind.String),    // SCE_L_ERROR
                }),

            // ── Vim script ────────────────────────────────────────────────────
            ["vim"] = new("vim",
                Keywords0: "if elseif else endif while endwhile for endfor function endfunction return " +
                           "let unlet const final lockvar unlockvar try catch finally endtry throw " +
                           "set setlocal setglobal map nmap vmap imap omap xmap smap cmap tmap " +
                           "noremap nnoremap vnoremap inoremap onoremap xnoremap snoremap cnoremap " +
                           "tnoremap autocmd augroup syntax highlight command call execute echo echom " +
                           "echoerr echohl source runtime packadd silent verbose redir delfunction " +
                           "abort range dict closure",
                Keywords1: null,
                StyleMap: null),

            // ── Pascal / Delphi ──────────────────────────────────────────────
            ["pascal"] = new("pascal",
                Keywords0: "absolute abstract and array as asm assembler at automated begin case cdecl " +
                           "class const constructor contains default deprecated destructor dispid " +
                           "dispinterface div do downto dynamic else end except export exports external " +
                           "far file final finalization finally for forward function generic goto if " +
                           "implementation implements in index inherited initialization inline interface " +
                           "is label library local message mod name near nil nodefault not object of on " +
                           "operator or out overload override package packed pascal platform private " +
                           "procedure program property protected public published raise read readonly " +
                           "record register reintroduce repeat requires resourcestring safecall sealed " +
                           "set shl shr static stdcall stored strict string then threadvar to try type " +
                           "unit unsafe until uses var varargs virtual while with write writeonly xor",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1, TokenKind.Comment),
                    (2, TokenKind.Comment),
                    (3, TokenKind.Comment),
                    (4, TokenKind.Preprocessor),
                    (5, TokenKind.Number),
                    (6, TokenKind.Number),
                    (7, TokenKind.Keyword),
                    (8, TokenKind.String),
                    (9, TokenKind.String),
                    (10, TokenKind.Operator),
                    (11, TokenKind.Variable),
                    (13, TokenKind.String),
                }),

            // ── Erlang ───────────────────────────────────────────────────────
            ["erlang"] = new("erlang",
                Keywords0: "after and andalso band begin bnot bor bsl bsr bxor case catch cond div end " +
                           "fun if let not of or orelse query receive rem try when xor",
                Keywords1: "atom binary boolean byte char float function integer iodata iolist list map " +
                           "maybe_improper_list mfa module no_return non_neg_integer none nonempty_list " +
                           "number pid port pos_integer reference string term timeout tuple",
                StyleMap: null),

            // ── Lisp / Clojure / Scheme / Racket ─────────────────────────────
            ["lisp"] = new("lisp",
                Keywords0: "and append apply assoc atom car case cdr cond cons defmacro defun defvar " +
                           "defparameter defconstant defstruct defclass defmethod defgeneric do dolist " +
                           "dotimes eq eql equal error eval flet funcall function if labels lambda let " +
                           "let* list loop macroexpand mapcar member multiple-value-bind nil not nth " +
                           "or otherwise package position progn quote return setf setq t the typecase " +
                           "unless unwind-protect values when",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1, TokenKind.Comment),
                    (2, TokenKind.Comment),
                    (3, TokenKind.Number),
                    (4, TokenKind.Keyword),
                    (5, TokenKind.Keyword2),
                    (6, TokenKind.String),
                    (7, TokenKind.String),
                    (10, TokenKind.Operator),
                    (11, TokenKind.Variable),
                }),
            ["clojure"] = new("lisp",
                Keywords0: "def defn defmacro defmulti defmethod defprotocol defrecord deftype let " +
                           "letfn fn if when when-not when-let if-let if-not cond condp case do " +
                           "doseq dotimes for loop recur try catch finally throw quote ns require " +
                           "import use refer in-ns and or not nil true false",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1, TokenKind.Comment),
                    (2, TokenKind.Comment),
                    (3, TokenKind.Number),
                    (4, TokenKind.Keyword),
                    (5, TokenKind.Keyword2),
                    (6, TokenKind.String),
                    (10, TokenKind.Operator),
                }),
            ["scheme"] = new("lisp",
                Keywords0: "and begin case cond define define-syntax delay do else if lambda let let* " +
                           "letrec or quasiquote quote set! syntax-rules unquote unquote-splicing when " +
                           "unless => library export import",
                Keywords1: null,
                StyleMap: new[]
                {
                    (1, TokenKind.Comment),
                    (2, TokenKind.Comment),
                    (3, TokenKind.Number),
                    (4, TokenKind.Keyword),
                    (6, TokenKind.String),
                    (10, TokenKind.Operator),
                }),

            // ── Julia (use cpp lexer for tokenisation, override keywords) ────
            ["julia"] = new("cpp",
                Keywords0: "abstract baremodule begin break catch ccall const continue do else elseif " +
                           "end export false finally for function global if import in isa let local " +
                           "macro module mutable primitive quote return struct true try type typealias " +
                           "using where while",
                Keywords1: "Int Int8 Int16 Int32 Int64 Int128 UInt UInt8 UInt16 UInt32 UInt64 UInt128 " +
                           "Float16 Float32 Float64 Bool Char String Symbol Array Vector Matrix Tuple " +
                           "NamedTuple Dict Set Nothing Missing Number Real Integer Signed Unsigned Any",
                StyleMap: null),

            // ── Nim ───────────────────────────────────────────────────────────
            ["nim"] = new("cpp",
                Keywords0: "addr and as asm bind block break case cast concept const continue converter " +
                           "defer discard distinct div do elif else end enum except export finally for " +
                           "from func generic if import in include interface is isnot iterator let " +
                           "macro method mixin mod nil not notin object of or out proc ptr raise ref " +
                           "return shl shr static template try tuple type using var when while xor yield",
                Keywords1: "int int8 int16 int32 int64 uint uint8 uint16 uint32 uint64 float float32 " +
                           "float64 bool char string cstring byte natural Positive seq array",
                StyleMap: null),

            // ── Crystal (Ruby-like) ──────────────────────────────────────────
            ["crystal"] = new("ruby",
                Keywords0: "abstract alias as asm begin break case class def do else elsif end ensure " +
                           "enum extend false for fun if in include instance_sizeof is_a lib macro " +
                           "module next nil of out pointerof private protected require rescue return " +
                           "select self sizeof struct super then true type typeof union unless until " +
                           "verbatim when while with yield",
                Keywords1: null,
                StyleMap: null),

            // ── Solidity (cpp-ish) ───────────────────────────────────────────
            ["solidity"] = new("cpp",
                Keywords0: "abstract address after alias anonymous apply as assembly assert auto break " +
                           "calldata case catch constant constructor continue contract copyof default " +
                           "define delete do else emit enum error event experimental external fallback " +
                           "false final for function hex if immutable implements import in indexed " +
                           "inline interface internal is let library macro mapping match memory " +
                           "modifier modifies new null of override partial payable pragma private " +
                           "promise public pure receive reference relocatable return returns sealed " +
                           "sizeof static storage struct super switch this throw true try type typedef " +
                           "typeof ufixed unchecked using var view virtual while",
                Keywords1: "address bool byte bytes bytes1 bytes2 bytes4 bytes8 bytes16 bytes32 fixed " +
                           "int int8 int16 int32 int64 int128 int256 string uint uint8 uint16 uint32 " +
                           "uint64 uint128 uint256 wei gwei ether",
                StyleMap: null),

            // ── GraphQL ───────────────────────────────────────────────────────
            ["graphql"] = new("cpp",
                Keywords0: "type interface union enum schema scalar directive input fragment query " +
                           "mutation subscription on extend implements true false null",
                Keywords1: "Int Float String Boolean ID",
                StyleMap: null),

            // ── Protobuf ──────────────────────────────────────────────────────
            ["protobuf"] = new("cpp",
                Keywords0: "syntax import option package message service rpc stream returns enum " +
                           "extend extensions oneof reserved repeated optional required map true false",
                Keywords1: "double float int32 int64 uint32 uint64 sint32 sint64 fixed32 fixed64 " +
                           "sfixed32 sfixed64 bool string bytes",
                StyleMap: null),

            // ── HCL / Terraform ──────────────────────────────────────────────
            ["hcl"] = new("cpp",
                Keywords0: "resource provider variable data output module locals terraform required_providers " +
                           "required_version backend provisioner connection lifecycle dynamic count for_each " +
                           "depends_on if else true false null",
                Keywords1: "string number bool list map set object tuple any",
                StyleMap: null),

            // ── Ada ───────────────────────────────────────────────────────────
            ["ada"] = new("ada",
                Keywords0: "abort abs abstract accept access aliased all and array at begin body case " +
                           "constant declare delay delta digits do else elsif end entry exception exit " +
                           "for function generic goto if in interface is limited loop mod new not null " +
                           "of or others out overriding package pragma private procedure protected " +
                           "raise range record rem renames requeue return reverse select separate some " +
                           "subtype synchronized tagged task terminate then type until use when while " +
                           "with xor",
                Keywords1: null,
                StyleMap: null),

            // ── Fortran ──────────────────────────────────────────────────────
            ["fortran"] = new("fortran",
                Keywords0: "allocatable allocate assign assignment associate asynchronous backspace " +
                           "block blockdata call case class close codimension common contains continue " +
                           "critical cycle data deallocate deferred dimension do double doubleprecision " +
                           "elemental else elseif elsewhere end endassociate endblock endblockdata enddo " +
                           "endenum endfile endforall endfunction endif endinterface endmodule " +
                           "endprogram endselect endsubmodule endsubroutine endtype endwhere entry enum " +
                           "enumerator equivalence error exit extends external final flush forall " +
                           "format function generic go goto if implicit import impure include inquire " +
                           "intent interface intrinsic kind len lock module namelist non_overridable " +
                           "none nopass nullify only open operator optional parameter pass pause " +
                           "pointer print private procedure program protected public pure read recursive " +
                           "result return rewind rewrite save select sequence stop submodule subroutine " +
                           "sync target then type unlock use value volatile wait where while write",
                Keywords1: "integer real complex logical character double precision",
                StyleMap: null),

            // ── OCaml ─────────────────────────────────────────────────────────
            ["caml"] = new("caml",
                Keywords0: "and as assert asr begin class constraint do done downto else end exception " +
                           "external false for fun function functor if in include inherit initializer " +
                           "land lazy let lor lsl lsr lxor match method mod module mutable new nonrec " +
                           "object of open or private rec sig struct then to true try type val virtual " +
                           "when while with",
                Keywords1: null,
                StyleMap: null),

            // ── Octave / Matlab ──────────────────────────────────────────────
            ["matlab"] = new("matlab",
                Keywords0: "break case catch classdef continue do else elseif end endfor endfunction " +
                           "endif endparfor endswitch endwhile error for function global if methods " +
                           "otherwise parfor persistent properties return spmd switch try while",
                Keywords1: null,
                StyleMap: null),
            ["octave"] = new("octave",
                Keywords0: "break case catch continue do else elseif end endfor endfunction endif " +
                           "endparfor endswitch endwhile error for function global if otherwise parfor " +
                           "persistent return switch try until while",
                Keywords1: null,
                StyleMap: null),

            // ── Smalltalk ─────────────────────────────────────────────────────
            ["smalltalk"] = new("smalltalk",
                Keywords0: "self super nil true false thisContext",
                Keywords1: null,
                StyleMap: null),

            // ── Verilog / SystemVerilog ──────────────────────────────────────
            ["verilog"] = new("verilog",
                Keywords0: "always and assign automatic begin buf bufif0 bufif1 case casex casez cell " +
                           "cmos config deassign default defparam design disable edge else end endcase " +
                           "endconfig endfunction endgenerate endmodule endprimitive endspecify endtable " +
                           "endtask event for force forever fork function generate genvar highz0 highz1 " +
                           "if ifnone incdir include initial inout input instance integer join large " +
                           "liblist library localparam macromodule medium module nand negedge nmos nor " +
                           "noshowcancelled not notif0 notif1 or output parameter pmos posedge primitive " +
                           "pull0 pull1 pulldown pullup pulsestyle_onevent pulsestyle_ondetect rcmos " +
                           "real realtime reg release repeat rnmos rpmos rtran rtranif0 rtranif1 scalared " +
                           "showcancelled signed small specify specparam strong0 strong1 supply0 supply1 " +
                           "table task time tran tranif0 tranif1 tri tri0 tri1 triand trior trireg unsigned " +
                           "use vectored wait wand weak0 weak1 while wire wor xnor xor",
                Keywords1: null,
                StyleMap: null),

            // ── VHDL ─────────────────────────────────────────────────────────
            ["vhdl"] = new("vhdl",
                Keywords0: "abs access after alias all and architecture array assert attribute begin " +
                           "block body buffer bus case component configuration constant disconnect " +
                           "downto else elsif end entity exit file for function generate generic group " +
                           "guarded if impure in inertial inout is label library linkage literal loop " +
                           "map mod nand new next nor not null of on open or others out package port " +
                           "postponed procedure process pure range record register reject rem report " +
                           "return rol ror select severity shared signal sla sll sra srl subtype then " +
                           "to transport type unaffected units until use variable wait when while with " +
                           "xnor xor",
                Keywords1: null,
                StyleMap: null),

            // ── Markdown duplicate alias path covered by aliases ──

            // ── Nginx config ──────────────────────────────────────────────────
            ["nginx"] = new("nginx",
                Keywords0: "http server location upstream events worker_processes worker_connections " +
                           "listen server_name root index include proxy_pass proxy_set_header " +
                           "fastcgi_pass try_files return rewrite if set ssl_certificate " +
                           "ssl_certificate_key error_log access_log gzip add_header expires",
                Keywords1: null,
                StyleMap: null),

            // ── ABAP ──────────────────────────────────────────────────────────
            ["abap"] = new("abap",
                Keywords0: null,
                Keywords1: null,
                StyleMap: null),

            // ── COBOL ─────────────────────────────────────────────────────────
            ["cobol"] = new("cobol",
                Keywords0: null,
                Keywords1: null,
                StyleMap: null),

            // ── ActionScript ──────────────────────────────────────────────────
            ["actionscript"] = new("cpp",
                Keywords0: "as break case catch class const continue default delete do dynamic else " +
                           "extends false final finally for function get if implements import in " +
                           "instanceof interface internal is namespace native new null override " +
                           "package private protected public return set static super switch this " +
                           "throw true try typeof use var void while with",
                Keywords1: null,
                StyleMap: null),

            // ── Groovy ────────────────────────────────────────────────────────
            ["groovy"] = new("cpp",
                Keywords0: "abstract as assert boolean break byte case catch char class const continue " +
                           "def default do double else enum extends false final finally float for " +
                           "goto if implements import in instanceof int interface long native new " +
                           "null package private protected public return short static strictfp super " +
                           "switch synchronized this throw throws trait transient true try void " +
                           "volatile while",
                Keywords1: null,
                StyleMap: null),

            // ── Gradle (groovy-based) ────────────────────────────────────────
            ["gradle"] = new("cpp",
                Keywords0: "apply plugins dependencies repositories implementation api compile " +
                           "testImplementation runtimeOnly compileOnly classpath buildscript task " +
                           "ext allprojects subprojects android sourceSets",
                Keywords1: null,
                StyleMap: null),

            // ── Razor / cshtml (use cpp for code blocks) ─────────────────────
            ["razor"] = new("cpp",
                Keywords0: "model using inject inherits page layout section RenderBody RenderSection " +
                           "Html ViewBag ViewData if else for foreach while switch case break continue " +
                           "return new var await async true false null",
                Keywords1: null,
                StyleMap: null),

            // ── Markdown reference is via WinUIEdit-style; covered above in markdown entry ──
        };

    // ── Aliases ───────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> LexillaAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["sh"]            = "bash",
            ["zsh"]           = "bash",
            ["ksh"]           = "bash",
            ["fish"]          = "bash",
            ["shellscript"]   = "bash",
            ["shell"]         = "bash",
            ["scss"]          = "css",
            ["less"]          = "css",
            ["sass"]          = "css",
            ["vb"]            = "vbnet",
            ["visualbasic"]   = "vbnet",
            ["patch"]         = "diff",
            ["properties"]    = "ini",
            ["cfg"]           = "ini",
            ["conf"]          = "ini",
            ["env"]           = "ini",
            ["dotenv"]        = "ini",
            ["editorconfig"]  = "ini",
            ["gitconfig"]     = "ini",
            ["cmd"]           = "batch",
            ["bat"]           = "batch",
            ["tex"]           = "latex",
            ["bibtex"]        = "latex",
            ["delphi"]        = "pascal",
            ["objectpascal"]  = "pascal",
            ["common-lisp"]   = "lisp",
            ["commonlisp"]    = "lisp",
            ["elisp"]         = "lisp",
            ["emacs-lisp"]    = "lisp",
            ["racket"]        = "scheme",
            ["clj"]           = "clojure",
            ["cljs"]          = "clojure",
            ["cljc"]          = "clojure",
            ["edn"]           = "clojure",
            ["systemverilog"] = "verilog",
            ["sv"]            = "verilog",
            ["matlab-octave"] = "octave",
            ["m"]             = "matlab",
            ["pl"]            = "perl",
            ["pm"]            = "perl",
            ["ps1"]           = "powershell",
            ["psm1"]          = "powershell",
            ["psd1"]          = "powershell",
            ["nu"]            = "bash",          // Nushell: closest available
            ["dockerfile"]    = "dockerfile",
            ["containerfile"] = "dockerfile",
            ["mk"]            = "makefile",
            ["gnumake"]       = "makefile",
            ["bazel"]         = "cmake",         // close enough for Starlark
            ["starlark"]      = "cmake",
            ["bzl"]           = "cmake",
            ["build"]         = "cmake",
            ["workspace"]     = "cmake",
            ["terraform"]     = "hcl",
            ["tf"]            = "hcl",
            ["tfvars"]        = "hcl",
            ["proto"]         = "protobuf",
            ["proto3"]        = "protobuf",
            ["proto2"]        = "protobuf",
            ["gql"]           = "graphql",
            ["sol"]           = "solidity",
            ["mat"]           = "matlab",
            ["nim"]           = "nim",
            ["zig"]           = "cpp",           // map zig → cpp lexer (close enough)
            ["odin"]          = "cpp",
            ["v"]             = "cpp",
            ["jl"]            = "julia",
            ["cr"]            = "crystal",
            ["ex"]            = "elixir",
            ["exs"]           = "elixir",
            ["erl"]           = "erlang",
            ["hrl"]           = "erlang",
            ["hs"]            = "haskell",
            ["lhs"]           = "haskell",
            ["ml"]            = "caml",
            ["mli"]           = "caml",
            ["fs"]            = "fsharp",
            ["fsi"]           = "fsharp",
            ["fsx"]           = "fsharp",
            ["nginxconf"]     = "nginx",
            ["razorcs"]       = "razor",
            ["cshtml"]        = "razor",
            ["vbhtml"]        = "razor",
            ["mips"]          = "asm",
            ["nasm"]          = "asm",
            ["gas"]           = "asm",
            ["masm"]          = "asm",
            ["x86asm"]        = "asm",
            ["assembly"]      = "asm",
            ["s"]             = "asm",
        };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the WinUIEdit HighlightingLanguage string for a given language ID,
    /// or null if the language should be handled via Lexilla directly.
    /// </summary>
    public static string? GetWinUIEditId(string languageId)
    {
        if (string.IsNullOrEmpty(languageId)) return "plaintext";

        // Resolve alias first
        string resolved = LexillaAliases.TryGetValue(languageId, out string? alias)
            ? alias
            : languageId;

        // Direct WinUIEdit mapping
        if (WinUIEditMap.TryGetValue(resolved, out string? winuiId))
            return winuiId;

        // Lexilla-direct languages that ultimately use cpp lexer return winuiId="cpp"
        if (LexillaMap.TryGetValue(resolved, out LexillaConfig? cfg) &&
            cfg.LexerName == "cpp")
        {
            return "cpp";  // WinUIEdit handles cpp colors
        }

        return null;  // Use Lexilla directly
    }

    /// <summary>
    /// Returns the Lexilla config for a given language ID,
    /// or null if not handled via Lexilla.
    /// </summary>
    public static LexillaConfig? GetLexillaConfig(string languageId)
    {
        if (string.IsNullOrEmpty(languageId)) return null;

        string resolved = LexillaAliases.TryGetValue(languageId, out string? alias)
            ? alias
            : languageId;

        return LexillaMap.TryGetValue(resolved, out LexillaConfig? cfg) ? cfg : null;
    }

    /// <summary>
    /// Returns the custom keyword sets for a WinUIEdit-mapped language
    /// (e.g., Java/Go/Rust mapped to "cpp" but needing different keywords).
    /// Returns null if no keyword override is needed.
    /// </summary>
    public static (string? Keywords0, string? Keywords1)? GetKeywordOverride(string languageId)
    {
        if (string.IsNullOrEmpty(languageId)) return null;

        string resolved = LexillaAliases.TryGetValue(languageId, out string? alias)
            ? alias
            : languageId;

        // Only return keyword overrides for languages that WinUIEdit handles as "cpp"
        // but that need different keywords (Java, Go, Rust, etc.)
        if (LexillaMap.TryGetValue(resolved, out LexillaConfig? cfg) &&
            cfg.LexerName == "cpp" &&
            (cfg.Keywords0 != null || cfg.Keywords1 != null))
        {
            return (cfg.Keywords0, cfg.Keywords1);
        }

        return null;
    }
}
