#:package Microsoft.CodeAnalysis.CSharp.Workspaces@4.14.0
// Mechanical tidy-up steps for one source file; the rules are in documentation/tidy-up-progress.md.
// Usage: dotnet run tools/tidy/tidy.cs -- <file> <steps> [--dry]
//   steps, comma separated, run in the order given (suggested: ns,mods,else,bare,static,locals,braces,format):
//     ns      block namespace -> file-scoped namespace
//     mods    add the missing 'private' on members
//     else    drop an 'else' after a branch that always returns/throws/continues/breaks
//     bare    remove bare { } blocks inside a block
//     locals  move locals to their innermost scope, join declaration + assignment, inline 'out' variables,
//             declare for-loop counters in the for, delete unused locals
//     static  make instance methods that use no instance state static, and rewrite their call sites (same file only;
//             needs the whole OpenJPEG folder for the semantic model; TIDY_DIAG=1 prints compile errors of the target file)
//     braces  braces on loop bodies and on if/else chains
//     format  Roslyn formatter for each #if variant, trailing whitespace
//     report  list '== null' / '!= null' comparisons and using directives (changes nothing)
// The file is parsed with several #if symbol sets and only what all of them agree on is changed. BOM and line endings
// are kept (check with `file`: some shell tools rewrite them). Always build every target afterwards and run the checks
// in .claude/skills/verifying-jpx-decoder-changes.
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

var path = args[0];
var steps = args[1].Split(',');
bool dry = args.Contains("--dry");

byte[] raw = File.ReadAllBytes(path);
bool bom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
string text = new UTF8Encoding(false).GetString(raw, bom ? 3 : 0, raw.Length - (bom ? 3 : 0));
string eol = text.Contains("\r\n") ? "\r\n" : "\n";
string orig = text;

string[][] sets =
{
    new[] { "NET8_0_OR_GREATER", "NET7_0_OR_GREATER", "NETCOREAPP3_0_OR_GREATER", "DEBUG" },
    new[] { "NET8_0_OR_GREATER", "NET7_0_OR_GREATER", "NETCOREAPP3_0_OR_GREATER" },
    new[] { "NETCOREAPP3_0_OR_GREATER" },
    new[] { "NETSTANDARD2_1" },
    new string[0],
};

SyntaxTree Parse(string t, string[] s) =>
    CSharpSyntaxTree.ParseText(t, new CSharpParseOptions(LanguageVersion.CSharp12, preprocessorSymbols: s));

foreach (var step in steps)
{
    switch (step)
    {
        case "ns": text = Ns(text); break;
        case "mods": text = Mods(text); break;
        case "locals": text = Locals(text); break;
        case "braces": text = Braces(text); break;
        case "else": for (int pass = 0; pass < 10; pass++) { var next = RedundantElse(text); if (next == text) break; text = next; } break;
        case "bare": for (int pass = 0; pass < 10; pass++) { var next = BareBlocks(text); if (next == text) break; text = next; } break;
        case "static": text = StaticMethods(text); break;
        case "format": text = Format(text); break;
        case "report": Report(text); break;
        default: throw new Exception("unknown step " + step);
    }
}

if (text != orig && !dry)
{
    var bytes = new UTF8Encoding(false).GetBytes(text);
    using var fs = File.Create(path);
    if (bom) fs.Write(new byte[] { 0xEF, 0xBB, 0xBF });
    fs.Write(bytes);
    Console.WriteLine("written");
}
else Console.WriteLine(text != orig ? "changed (dry)" : "no change");
return 0;

// ---------------------------------------------------------------- helpers
string ApplyEdits(string t, List<Edit> edits)
{
    var sorted = edits.OrderBy(e => e.Start).ThenBy(e => e.End - e.Start == 0 ? 0 : 1).ThenBy(e => e.Order).ToList();
    var sb = new StringBuilder();
    int pos = 0;
    foreach (var e in sorted)
    {
        if (e.Start < pos) throw new Exception($"overlapping edit at {e.Start} (pos {pos}): {e.Text.Replace("\n", "\\n")}");
        sb.Append(t, pos, e.Start - pos);
        sb.Append(e.Text);
        pos = e.End;
    }
    sb.Append(t, pos, t.Length - pos);
    return sb.ToString();
}

int LineStart(string t, int pos) { int i = t.LastIndexOf('\n', Math.Max(0, pos - 1)); return pos == 0 ? 0 : i + 1; }
int PrevLineStart(string t, int ls) { if (ls <= 0) return -1; if (ls == 1) return 0; return t.LastIndexOf('\n', ls - 2) + 1; }
int LineEndIncl(string t, int pos) { int i = t.IndexOf('\n', pos); return i < 0 ? t.Length : i + 1; }
string LineText(string t, int ls) { int e = t.IndexOf('\n', ls); if (e < 0) e = t.Length; return t.Substring(ls, e - ls).TrimEnd('\r'); }
bool IsBlank(string s) => s.Trim().Length == 0;

// ---------------------------------------------------------------- ns
string Ns(string t)
{
    var root = Parse(t, sets[0]).GetRoot();
    var nss = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().ToList();
    if (nss.Count != 1 || nss[0] is not NamespaceDeclarationSyntax ns) { Console.WriteLine("ns: nothing to do / not a single block namespace"); return t; }
    if (ns.Parent is not CompilationUnitSyntax) return t;
    int open = ns.OpenBraceToken.Span.End;
    int close = ns.CloseBraceToken.SpanStart;
    string body = t.Substring(open, close - open);
    // drop the rest of the brace line and the indentation before the closing brace
    int nl = body.IndexOf('\n');
    body = body.Substring(nl + 1);
    int lastNl = body.LastIndexOf('\n');
    body = lastNl >= 0 ? body.Substring(0, lastNl + 1) : "";
    var lines = body.Split('\n');
    for (int i = 0; i < lines.Length; i++)
    {
        var l = lines[i];
        if (l.StartsWith("    ")) lines[i] = l.Substring(4);
        else if (l.StartsWith("\t")) lines[i] = l.Substring(1);
    }
    body = string.Join("\n", lines);
    string head = t.Substring(0, ns.SpanStart).TrimEnd() ;
    string name = ns.Name.ToString();
    string tail = t.Substring(ns.CloseBraceToken.Span.End).Trim();
    var res = head + eol + eol + "namespace " + name + ";" + eol + eol + body.TrimEnd('\r', '\n').Replace("\r", "").Replace("\n", eol) + eol;
    if (tail.Length > 0) res += tail + eol;
    if (head.Length == 0) res = res.TrimStart('\r', '\n');
    return res;
}

// ---------------------------------------------------------------- mods
string Mods(string t)
{
    var edits = new Dictionary<int, Edit>();
    foreach (var s in sets)
    {
        var root = Parse(t, s).GetRoot();
        foreach (var td in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            if (td is InterfaceDeclarationSyntax) continue;
            foreach (var m in td.Members) Consider(m);
        }
    }
    void Consider(MemberDeclarationSyntax m)
    {
        SyntaxTokenList mods = m.Modifiers;
        if (mods.Any(x => x.IsKind(SyntaxKind.PublicKeyword) || x.IsKind(SyntaxKind.PrivateKeyword) || x.IsKind(SyntaxKind.ProtectedKeyword) || x.IsKind(SyntaxKind.InternalKeyword))) return;
        switch (m)
        {
            case MethodDeclarationSyntax md when md.ExplicitInterfaceSpecifier is not null: return;
            case PropertyDeclarationSyntax pd when pd.ExplicitInterfaceSpecifier is not null: return;
            case ConstructorDeclarationSyntax cd when cd.Modifiers.Any(SyntaxKind.StaticKeyword): return;
            case DestructorDeclarationSyntax: return;
            case OperatorDeclarationSyntax: return;
            case ConversionOperatorDeclarationSyntax: return;
            case MethodDeclarationSyntax md2 when md2.Modifiers.Any(SyntaxKind.PartialKeyword): return;
            case FieldDeclarationSyntax:
            case MethodDeclarationSyntax:
            case PropertyDeclarationSyntax:
            case EventFieldDeclarationSyntax:
            case EventDeclarationSyntax:
            case IndexerDeclarationSyntax:
            case ConstructorDeclarationSyntax:
            case DelegateDeclarationSyntax:
            case BaseTypeDeclarationSyntax:
                break;
            default: return;
        }
        int pos = mods.Count > 0 ? mods[0].SpanStart
            : m.AttributeLists.Count > 0 ? m.AttributeLists.Last().GetLastToken().GetNextToken().SpanStart
            : m.GetFirstToken().SpanStart;
        edits[pos] = new Edit(pos, pos, "private ", 0);
    }
    foreach (var e in edits.Values) Console.WriteLine($"mods: private at line {LineNo(t, e.Start)}: {LineText(t, LineStart(t, e.Start)).Trim()}");
    return ApplyEdits(t, edits.Values.ToList());
}

int LineNo(string t, int pos) { int n = 1; for (int i = 0; i < pos; i++) if (t[i] == '\n') n++; return n; }

// ---------------------------------------------------------------- static
string StaticMethods(string t)
{
    // semantic model of the whole codec (OpenJPEG folder) with the current text of the target file
    var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);
    while (dir is not null && dir.Name != "OpenJPEG") dir = dir.Parent;
    if (dir is null) { Console.WriteLine("static: file is not under an OpenJPEG folder"); return t; }
    string full = Path.GetFullPath(path);
    var opts = new CSharpParseOptions(LanguageVersion.CSharp12, preprocessorSymbols: sets[0]);
    var files = Directory.GetFiles(dir.FullName, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)).ToList();
    var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator).Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
    var trees = files.Select(f => CSharpSyntaxTree.ParseText(string.Equals(Path.GetFullPath(f), full, StringComparison.OrdinalIgnoreCase) ? t : File.ReadAllText(f), opts, path: Path.GetFullPath(f))).ToList();
    var global = CSharpSyntaxTree.ParseText("global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks; global using System.Net.Http;", opts);
    var comp = CSharpCompilation.Create("tidy", trees.Append(global), refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable));
    var tree = trees.First(x => string.Equals(x.FilePath, full, StringComparison.OrdinalIgnoreCase));
    var model = comp.GetSemanticModel(tree);
    if (Environment.GetEnvironmentVariable("TIDY_DIAG") == "1") foreach (var dg in model.GetDiagnostics().Where(x => x.Severity == DiagnosticSeverity.Error).Take(8)) Console.WriteLine("diag: " + dg.ToString());
    var edits = new List<Edit>();
    var converted = new HashSet<int>();
    foreach (var m in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
    {
        if (m.Modifiers.Any(x => x.IsKind(SyntaxKind.StaticKeyword) || x.IsKind(SyntaxKind.AbstractKeyword) || x.IsKind(SyntaxKind.VirtualKeyword) || x.IsKind(SyntaxKind.OverrideKeyword) || x.IsKind(SyntaxKind.ExternKeyword) || x.IsKind(SyntaxKind.PartialKeyword) || x.IsKind(SyntaxKind.NewKeyword) || x.IsKind(SyntaxKind.SealedKeyword) || x.IsKind(SyntaxKind.UnsafeKeyword) || x.IsKind(SyntaxKind.AsyncKeyword)))
            continue;
        if (m.ExplicitInterfaceSpecifier is not null || m.Parent is not TypeDeclarationSyntax td || td is InterfaceDeclarationSyntax) continue;
        if (model.GetDeclaredSymbol(m) is not IMethodSymbol sym) continue;
        var type = sym.ContainingType;
        // effectively public API stays as it is
        bool exposed = sym.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
        for (var ct = type; ct is not null && exposed; ct = ct.ContainingType)
            exposed = ct.DeclaredAccessibility is Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal;
        if (exposed) continue;
        if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Struct) continue;
        if (type.AllInterfaces.Any(i => i.GetMembers().OfType<IMethodSymbol>().Any(im => SymbolEqualityComparer.Default.Equals(type.FindImplementationForInterfaceMember(im), sym)))) continue;
        if (m.Body is null && m.ExpressionBody is null) continue;
        SyntaxNode body = (SyntaxNode?)m.Body ?? m.ExpressionBody!;
        bool usesThis = false;
        foreach (var n in body.DescendantNodesAndSelf())
        {
            if (n is ThisExpressionSyntax or BaseExpressionSyntax) { usesThis = true; break; }
            if (n is IdentifierNameSyntax id && !(id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id) && id.Parent is not MemberBindingExpressionSyntax && id.Parent is not NameColonSyntax)
            {
                var s = model.GetSymbolInfo(id).Symbol;
                if (s is { IsStatic: false } && s.Kind is SymbolKind.Field or SymbolKind.Property or SymbolKind.Method or SymbolKind.Event
                    && s.ContainingType is not null && (SymbolEqualityComparer.Default.Equals(s.ContainingType, type) || InheritsFrom(type, s.ContainingType)))
                { usesThis = true; break; }
            }
        }
        if (usesThis) continue;
        // call sites
        var site = new List<(SyntaxTree tree, IdentifierNameSyntax id)>();
        foreach (var tr in trees)
        {
            var mdl = tr == tree ? model : comp.GetSemanticModel(tr);
            foreach (var id in tr.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Where(i => i.Identifier.ValueText == sym.Name))
                if (SymbolEqualityComparer.Default.Equals(mdl.GetSymbolInfo(id).Symbol?.OriginalDefinition, sym.OriginalDefinition)) site.Add((tr, id));
        }
        if (site.Any(s => s.tree != tree)) { Console.WriteLine($"static: skipped {type.Name}.{sym.Name} (line {LineNo(t, m.SpanStart)}): called from another file"); continue; }
        var local = new List<Edit>();
        bool ok = true;
        foreach (var (_, id) in site)
        {
            if (id.Parent is MemberAccessExpressionSyntax ma2 && ma2.Name == id)
            {
                if (ma2.Expression is IdentifierNameSyntax or ThisExpressionSyntax && type.TypeParameters.Length == 0)
                    local.Add(new Edit(ma2.Expression.SpanStart, ma2.Expression.Span.End, type.Name, 0));
                else { ok = false; break; }
            }
        }
        if (!ok) { Console.WriteLine($"static: skipped {type.Name}.{sym.Name} (line {LineNo(t, m.SpanStart)}): called on an expression"); continue; }
        Console.WriteLine($"static: {type.Name}.{sym.Name} (line {LineNo(t, m.SpanStart)}, {site.Count} call sites)");
        edits.Add(new Edit(m.ReturnType.SpanStart, m.ReturnType.SpanStart, "static ", 0));
        edits.AddRange(local);
        converted.Add(m.SpanStart);
    }
    // two conversions can touch the same call-site receiver: keep distinct edits
    var distinct = edits.GroupBy(e => (e.Start, e.End, e.Text)).Select(g => g.First()).ToList();
    return ApplyEdits(t, distinct);
}

static bool InheritsFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
{
    for (var b = type.BaseType; b is not null; b = b.BaseType)
        if (SymbolEqualityComparer.Default.Equals(b, baseType)) return true;
    return false;
}

// ---------------------------------------------------------------- else / bare
static bool EndsWithJump(StatementSyntax s)
{
    if (s is BlockSyntax b) return b.Statements.Count > 0 && EndsWithJump(b.Statements.Last());
    return s is ReturnStatementSyntax or ThrowStatementSyntax or ContinueStatementSyntax or BreakStatementSyntax;
}

string DedentBody(string t, int from, int to)
{
    // text between two braces: drop the rest of the brace line and the last indentation, dedent by one level
    string body = t.Substring(from, to - from);
    int nl = body.IndexOf('\n');
    body = body.Substring(nl + 1);
    int lastNl = body.LastIndexOf('\n');
    body = lastNl >= 0 ? body.Substring(0, lastNl + 1) : "";
    var lines = body.Split('\n');
    for (int i = 0; i < lines.Length; i++)
        if (lines[i].StartsWith("    ")) lines[i] = lines[i].Substring(4);
    return string.Join("\n", lines).TrimEnd('\r', '\n');
}

// Names that a block declares in its own scope (locals, out variables, pattern variables), not those of nested blocks or lambdas.
static HashSet<string> OwnDeclaredNames(IEnumerable<StatementSyntax> statements)
{
    var names = new HashSet<string>();
    foreach (var st in statements)
    {
        if (st is LocalDeclarationStatementSyntax ld)
            foreach (var v in ld.Declaration.Variables) names.Add(v.Identifier.ValueText);
        // out var / pattern variables of expression, declaration, return and if statements leak into the block
        foreach (var n in st.DescendantNodes(x => x is not BlockSyntax && x is not AnonymousFunctionExpressionSyntax && x is not LocalFunctionStatementSyntax))
        {
            if (n is SingleVariableDesignationSyntax sv) names.Add(sv.Identifier.ValueText);
        }
    }
    return names;
}

// Removing a scope would let these names shadow a field / property / outer local for the rest of the parent block.
static bool ShadowsInParent(BlockSyntax parent, SyntaxNode removed, HashSet<string> names) =>
    names.Count > 0 && parent.DescendantNodes().OfType<IdentifierNameSyntax>()
        .Any(i => names.Contains(i.Identifier.ValueText) && IsRealRef(i) && !removed.Span.Contains(i.Span));

string RedundantElse(string t)
{
    var edits = new Dictionary<int, Edit>();
    foreach (var s in sets)
    {
        var root = Parse(t, s).GetRoot();
        foreach (var i in root.DescendantNodes().OfType<IfStatementSyntax>())
        {
            if (i.Else is null || i.Parent is not BlockSyntax || i.Else.Statement is not BlockSyntax eb || eb.Statements.Count == 0) continue;
            if (!EndsWithJump(i.Statement)) continue;
            if (ShadowsInParent((BlockSyntax)i.Parent, eb, OwnDeclaredNames(eb.Statements))) { Console.WriteLine($"else: skipped line {LineNo(t, i.Else.ElseKeyword.SpanStart)} (a local of the else block would shadow a name used in the parent block)"); continue; }
            if (!IsBlank(t.Substring(i.Statement.Span.End, i.Else.ElseKeyword.SpanStart - i.Statement.Span.End))) continue;
            if (!IsBlank(t.Substring(i.Else.ElseKeyword.Span.End, eb.OpenBraceToken.SpanStart - i.Else.ElseKeyword.Span.End))) continue;
            if (!IsBlank(t.Substring(eb.CloseBraceToken.Span.End, LineEndIncl(t, eb.CloseBraceToken.Span.End) - eb.CloseBraceToken.Span.End))) continue;
            string body = DedentBody(t, eb.OpenBraceToken.Span.End, eb.CloseBraceToken.SpanStart).Replace("\r", "").Replace("\n", eol);
            edits[i.Statement.Span.End] = new Edit(i.Statement.Span.End, eb.CloseBraceToken.Span.End, eol + eol + body, 0);
            Console.WriteLine($"else: line {LineNo(t, i.Else.ElseKeyword.SpanStart)}");
        }
    }
    // nested redundant elses overlap: keep the outer ones, the next pass takes the inner ones
    var kept = new List<Edit>();
    int until = -1;
    foreach (var ed in edits.Values.OrderBy(x => x.Start))
    {
        if (ed.Start < until) continue;
        kept.Add(ed);
        until = ed.End;
    }
    return ApplyEdits(t, kept);
}

string BareBlocks(string t)
{
    var edits = new Dictionary<int, Edit>();
    foreach (var s in sets)
    {
        var root = Parse(t, s).GetRoot();
        foreach (var b in root.DescendantNodes().OfType<BlockSyntax>())
        {
            if (b.Parent is not BlockSyntax parent || b.Statements.Count == 0) continue;
            int os = LineStart(t, b.OpenBraceToken.SpanStart), cs = LineStart(t, b.CloseBraceToken.SpanStart);
            if (LineText(t, os).Trim() != "{" || LineText(t, cs).Trim() != "}") continue;
            if (b.GetLeadingTrivia().Any(x => x.IsKind(SyntaxKind.SingleLineCommentTrivia) || x.IsKind(SyntaxKind.MultiLineCommentTrivia))) continue;
            // names declared inside must not clash with locals of the parent block
            var inner = b.Statements.OfType<LocalDeclarationStatementSyntax>().SelectMany(x => x.Declaration.Variables).Select(v => v.Identifier.ValueText).ToHashSet();
            var outer = parent.DescendantNodes().OfType<VariableDeclaratorSyntax>().Where(v => v.Ancestors().OfType<BlockSyntax>().First() != b).Select(v => v.Identifier.ValueText);
            if (outer.Any(inner.Contains)) { Console.WriteLine($"bare: skipped line {LineNo(t, b.SpanStart)} (name clash)"); continue; }
            if (ShadowsInParent(parent, b, OwnDeclaredNames(b.Statements))) { Console.WriteLine($"bare: skipped line {LineNo(t, b.SpanStart)} (a local of the block would shadow a name used in the parent block)"); continue; }
            int end = LineEndIncl(t, b.CloseBraceToken.SpanStart);
            string body = DedentBody(t, b.OpenBraceToken.Span.End, b.CloseBraceToken.SpanStart).Replace("\r", "").Replace("\n", eol);
            edits[os] = new Edit(os, end, body + eol, 0);
            Console.WriteLine($"bare: line {LineNo(t, b.SpanStart)}");
        }
    }
    // nested bare blocks overlap: keep the outer ones, the next pass takes the inner ones
    var kept = new List<Edit>();
    int until = -1;
    foreach (var ed in edits.Values.OrderBy(x => x.Start))
    {
        if (ed.Start < until) continue;
        kept.Add(ed);
        until = ed.End;
    }
    return ApplyEdits(t, kept);
}

// ---------------------------------------------------------------- braces
string Braces(string t)
{
    var edits = new Dictionary<(int, string), Edit>();
    foreach (var s in sets)
    {
        var root = Parse(t, s).GetRoot();
        foreach (var n in root.DescendantNodes())
        {
            switch (n)
            {
                case ForStatementSyntax f: Wrap(f.Statement, f.CloseParenToken, f); break;
                case ForEachStatementSyntax f: Wrap(f.Statement, f.CloseParenToken, f); break;
                case WhileStatementSyntax f: Wrap(f.Statement, f.CloseParenToken, f); break;
                case IfStatementSyntax i:
                    {
                        // only the head of a chain decides; an else-if is visited as part of its chain
                        if (i.Parent is ElseClauseSyntax) break;
                        var chain = new List<IfStatementSyntax>();
                        for (var c = i; c != null; c = c.Else?.Statement as IfStatementSyntax) chain.Add(c);
                        bool hasElse = chain.Any(c => c.Else != null);
                        foreach (var c in chain)
                        {
                            if (hasElse || MultiLine(t, c.Statement)) Wrap(c.Statement, c.CloseParenToken, c);
                            if (c.Else != null && c.Else.Statement is not IfStatementSyntax && (hasElse || MultiLine(t, c.Else.Statement)))
                                Wrap(c.Else.Statement, c.Else.ElseKeyword, c.Else);
                        }
                        break;
                    }
            }
        }
    }
    void Wrap(StatementSyntax e, SyntaxToken prev, SyntaxNode header)
    {
        if (e is BlockSyntax) return;
        int prevLine = LineNo(t, prev.Span.End), eLine = LineNo(t, e.SpanStart);
        if (prevLine == eLine)
        {
            if (!IsBlank(t.Substring(prev.Span.End, e.SpanStart - prev.Span.End))) { Console.WriteLine($"braces: skipped line {eLine} (comment before body)"); return; }
            string ind = LeadingWs(t, header is ElseClauseSyntax ec2 ? ec2.ElseKeyword.SpanStart : header.SpanStart);
            edits[(prev.Span.End, "{")] = new Edit(prev.Span.End, e.SpanStart, eol + ind + "{" + eol + ind + "    ", 0);
            edits[(e.Span.End, "}" + ind.Length)] = new Edit(CloseAt(t, e.Span.End), CloseAt(t, e.Span.End), eol + ind + "}", 100000 - header.Ancestors().Count());
            return;
        }
        string indent = LeadingWs(t, header is ElseClauseSyntax ec ? ec.ElseKeyword.SpanStart : header.SpanStart);
        edits[(prev.Span.End, "{")] = new Edit(prev.Span.End, prev.Span.End, eol + indent + "{", 0);
        edits[(e.Span.End, "}" + indent.Length)] = new Edit(CloseAt(t, e.Span.End), CloseAt(t, e.Span.End), eol + indent + "}", 100000 - header.Ancestors().Count());
    }
    Console.WriteLine($"braces: {edits.Count / 2} bodies");
    return ApplyEdits(t, edits.Values.ToList());
}

bool MultiLine(string t, SyntaxNode n) => LineNo(t, n.SpanStart) != LineNo(t, n.Span.End);
string LeadingWs(string t, int pos) { int ls = LineStart(t, pos); int i = ls; while (i < t.Length && (t[i] == ' ' || t[i] == '\t')) i++; return t.Substring(ls, i - ls); }
int CloseAt(string t, int pos)
{
    int le = t.IndexOf('\n', pos);
    if (le < 0) le = t.Length;
    var rest = t.Substring(pos, le - pos);
    if (rest.TrimStart(' ', '\t').StartsWith("//")) return le - (rest.EndsWith("\r") ? 1 : 0);
    return pos;
}

// ---------------------------------------------------------------- format
string Format(string t)
{
    using var ws = new AdhocWorkspace();
    foreach (var s in sets)
    {
        var root = Parse(t, s).GetRoot();
        t = Formatter.Format(root, ws).ToFullString();
    }
    var sb = new StringBuilder();
    foreach (var l in t.Replace("\r\n", "\n").Split('\n')) sb.Append(l.TrimEnd(' ', '\t')).Append('\n');
    var r = sb.ToString().TrimEnd('\n') + "\n";
    return r.Replace("\n", eol);
}

// ---------------------------------------------------------------- report
void Report(string t)
{
    var root = Parse(t, sets[0]).GetRoot();
    foreach (var b in root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        if ((b.IsKind(SyntaxKind.EqualsExpression) || b.IsKind(SyntaxKind.NotEqualsExpression)) &&
            (b.Right.IsKind(SyntaxKind.NullLiteralExpression) || b.Left.IsKind(SyntaxKind.NullLiteralExpression)))
            Console.WriteLine($"null-compare line {LineNo(t, b.SpanStart)}: {b}");
    foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        Console.WriteLine($"using line {LineNo(t, u.SpanStart)}: {u}");
}

// ---------------------------------------------------------------- locals
string Locals(string t)
{
    for (int pass = 1; pass <= 8; pass++)
    {
        var (nt, n) = LocalsPass(t);
        Console.WriteLine($"locals pass {pass}: {n} moved");
        if (nt == t) break;
        t = nt;
        if (n == 0) break;
    }
    return t;
}

(string, int) LocalsPass(string t)
{
    // per declarator key: per set outcome
    var outcomes = new Dictionary<int, List<Outcome>>();
    var exists = new Dictionary<int, int>();
    foreach (var s in sets)
    {
        var tree = Parse(t, s);
        var root = tree.GetRoot();
        foreach (var block in root.DescendantNodes().OfType<BlockSyntax>())
        {
            foreach (var st in block.Statements.OfType<LocalDeclarationStatementSyntax>())
            {
                foreach (var d in st.Declaration.Variables)
                {
                    int key = d.SpanStart;
                    exists[key] = exists.GetValueOrDefault(key) + 1;
                    var o = Analyse(t, block, st, d);
                    if (!outcomes.TryGetValue(key, out var l)) outcomes[key] = l = new();
                    l.Add(o);
                }
            }
        }
    }

    var accepted = new List<Outcome>();
    foreach (var (key, list) in outcomes)
    {
        var plans = list.Where(o => o.Plan is not null).ToList();
        if (plans.Count == 0)
        {
            var why = list.Where(o => o.Why != null && o.Why != "no refs").Select(o => o.Why).Distinct().ToList();
            if (why.Count > 0 && why.All(w => w.StartsWith("note:"))) Console.WriteLine($"locals skipped line {LineNo(t, key)}: {string.Join("; ", why)}");
            continue;
        }
        var first = plans[0];
        bool ok = list.All(o => o.Plan is null ? o.Why == "no refs" : o.Plan.Id == first.Plan!.Id);
        if (!ok) { Console.WriteLine($"locals skipped line {LineNo(t, key)}: sets disagree"); continue; }
        accepted.Add(first);
    }

    // a for-loop conversion needs all of its variables
    foreach (var g in accepted.Where(o => o.Plan!.ForKey != 0).GroupBy(o => o.Plan!.ForKey).ToList())
        if (g.Count() != g.First().Plan!.ForCount)
        {
            Console.WriteLine($"locals skipped for at line {LineNo(t, g.Key)}: not all variables accepted");
            foreach (var o in g) accepted.Remove(o);
        }
    // build edits
    var edits = new List<Edit>();
    int order = 0;
    var byStmt = accepted.GroupBy(o => o.Plan!.StmtStart).ToList();
    foreach (var g in byStmt.OrderBy(g => g.Key))
    {
        var items = g.OrderBy(o => o.Plan!.DeclPos).ToList();
        var p0 = items[0].Plan!;
        bool wholeStmt = items.Count == p0.DeclaratorCount;
        if (!wholeStmt && items.Any(i => i.Plan!.DeclaratorIndex == 0)) { Console.WriteLine($"locals skipped line {LineNo(t, p0.StmtStart)}: first declarator of a partial multi-declaration"); continue; }
        // removal
        if (wholeStmt) edits.Add(new Edit(p0.RemoveStart, p0.RemoveEnd, "", order++));
        else foreach (var i in items) edits.Add(new Edit(i.Plan!.DeclRemoveStart, i.Plan.DeclRemoveEnd, "", order++));
        foreach (var i in items)
        {
            var p = i.Plan!;
            if (p.Delete) { Console.WriteLine($"locals: line {LineNo(t, p.DeclPos)} {p.Name} unused, deleted"); continue; }
            Console.WriteLine($"locals: line {LineNo(t, p.DeclPos)} {p.Name} -> line {LineNo(t, p.InsertPos)}{(p.Merge ? " (merged)" : "")}");
            if (p.Merge)
            {
                if (p.ForKey == 0 || p.ForFirst) edits.Add(new Edit(p.MergePos, p.MergePos, p.TypeText + " ", order++));
                if (p.ExtraTypePos != null) foreach (var xp in p.ExtraTypePos) edits.Add(new Edit(xp, xp, p.TypeText + " ", order++));
            }
            else
            {
                string stmtText = wholeStmt && items.Count == 1 ? p.MovedText : p.NewDeclText;
                edits.Add(new Edit(p.InsertPos, p.InsertPos, stmtText, order++));
            }
        }
    }
    try { return (ApplyEdits(t, edits), accepted.Count); }
    catch (Exception ex) { Console.WriteLine("locals: edit conflict, " + ex.Message); return (t, 0); }
}

Outcome Analyse(string t, BlockSyntax b0, LocalDeclarationStatementSyntax st, VariableDeclaratorSyntax d)
{
    if (st.Modifiers.Count > 0 || st.UsingKeyword.RawKind != 0 || st.Declaration.Type is RefTypeSyntax) return Outcome.No("skip");
    if (d.Initializer?.Value.DescendantNodesAndSelf().Any(n => n is StackAllocArrayCreationExpressionSyntax or ImplicitStackAllocArrayCreationExpressionSyntax) == true) return Outcome.No("stackalloc");
    string name = d.Identifier.ValueText;
    var refs = FindRefs(b0, st, name);
    // references from sibling declarators
    foreach (var other in st.Declaration.Variables)
        if (other != d && other.Initializer is not null && other.Initializer.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.ValueText == name))
            return Outcome.No("note: used by sibling declarator");
    if (refs.Count == 0)
    {
        // unused local: delete it when the initialiser has no effect
        if (d.Initializer is not null && d.Initializer.Value.DescendantNodesAndSelf().Any(n => n is InvocationExpressionSyntax or ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or AssignmentExpressionSyntax or AwaitExpressionSyntax or ElementAccessExpressionSyntax || n.IsKind(SyntaxKind.PreIncrementExpression) || n.IsKind(SyntaxKind.PostIncrementExpression) || n.IsKind(SyntaxKind.PreDecrementExpression) || n.IsKind(SyntaxKind.PostDecrementExpression))) return Outcome.No("note: unused local with a side-effecting initialiser");
        var rm = Removal(t, b0, st, d);
        if (rm is null) return Outcome.No("note: unused local, declaration shares its line / has comments");
        rm.Id = "delete"; rm.Delete = true;
        return new Outcome(rm, null);
    }
    if (refs.Any(r => r.Ancestors().TakeWhile(a => a != b0).Any(a => a is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)))
        return Outcome.No("note: captured by lambda/local function");

    // the same local used by several separate statements, each of them starting with "v = expr;" in its own block
    // (or being a "for (v = expr; ...)" that contains all its uses): declare it where each of them starts
    {
        List<int>? SplitPositions(BlockSyntax scope, List<IdentifierNameSyntax> rs)
        {
            var res = new List<int>();
            foreach (var g in rs.GroupBy(r => scope.Statements.First(s => s.Span.Contains(r.SpanStart))))
            {
                var gr = g.ToList();
                var stmt = g.Key;
                var first = gr.OrderBy(r => r.SpanStart).First();
                if (stmt is ExpressionStatementSyntax ges && scope != b0 && ges.Expression is AssignmentExpressionSyntax ga && ga.IsKind(SyntaxKind.SimpleAssignmentExpression)
                    && ga.Left == first && !SelfRef(ga.Right, name))
                    res.Add(first.SpanStart);
                else if (stmt is ForStatementSyntax gf && gf.Declaration is null && gf.Initializers.Count == 1 && gf.Initializers[0] is AssignmentExpressionSyntax fa2
                         && fa2.IsKind(SyntaxKind.SimpleAssignmentExpression) && fa2.Left == first && !SelfRef(fa2.Right, name) && gr.All(r => gf.Span.Contains(r.Span)))
                    res.Add(first.SpanStart);
                else
                {
                    var anc2 = gr[0].AncestorsAndSelf().ToList();
                    foreach (var r in gr.Skip(1)) { var hs = new HashSet<SyntaxNode>(r.AncestorsAndSelf()); anc2 = anc2.Where(hs.Contains).ToList(); }
                    var blk = anc2.First().AncestorsAndSelf().OfType<BlockSyntax>().First();
                    if (blk == scope || !stmt.Span.Contains(blk.Span)) return null;
                    var sub = SplitPositions(blk, gr);
                    if (sub is null) return null;
                    res.AddRange(sub);
                }
            }
            return res;
        }

        if (st.Declaration.Type.ToString() != "var" && (d.Initializer is null || IsPure(d.Initializer.Value)))
        {
            var poss = SplitPositions(b0, refs);
            if (poss is { Count: >= 1 })
            {
                var rm = Removal(t, b0, st, d);
                if (rm is null) return Outcome.No("note: split variable, declaration shares its line / has comments");
                poss.Sort();
                rm.Id = "split:" + string.Join(",", poss);
                rm.Merge = true; rm.MergePos = poss[0]; rm.ExtraTypePos = poss.Skip(1).ToList();
                rm.TypeText = st.Declaration.Type.ToString();
                return new Outcome(rm, null);
            }
        }
    }

    // for (i = 0, j = 1; ...) with locals only used inside that for  ->  for (int i = 0, j = 1; ...)
    var firstRef0 = refs.OrderBy(r => r.SpanStart).First();
    var S0 = b0.Statements.First(s => s.Span.Contains(firstRef0.SpanStart));
    if (S0 is ForStatementSyntax fs && fs.Declaration is null && fs.Initializers.Count > 0 && refs.All(r => fs.Span.Contains(r.Span)))
    {
        bool good = fs.Initializers.All(e => e is AssignmentExpressionSyntax a && a.IsKind(SyntaxKind.SimpleAssignmentExpression) && a.Left is IdentifierNameSyntax);
        string typeText0 = st.Declaration.Type.ToString();
        if (good)
            foreach (AssignmentExpressionSyntax a in fs.Initializers)
            {
                string n2 = ((IdentifierNameSyntax)a.Left).Identifier.ValueText;
                LocalDeclarationStatementSyntax? ds = null; VariableDeclaratorSyntax? dd = null;
                foreach (var cand in b0.Statements.TakeWhile(s => s != fs).OfType<LocalDeclarationStatementSyntax>())
                {
                    var v = cand.Declaration.Variables.FirstOrDefault(x => x.Identifier.ValueText == n2);
                    if (v != null) { ds = cand; dd = v; }
                }
                if (ds is null || ds.Modifiers.Count > 0 || ds.Declaration.Type.ToString() != typeText0 || (dd!.Initializer is not null && !IsPure(dd.Initializer.Value))) { good = false; break; }
                var r2 = FindRefs(b0, ds, n2);
                var f2 = r2.OrderBy(r => r.SpanStart).FirstOrDefault();
                if (f2 is null || f2 != a.Left || !r2.All(r => fs.Span.Contains(r.Span))) { good = false; break; }
                if (SelfRef(a.Right, n2)) { good = false; break; }
            }
        if (good && d.Initializer is not null && !IsPure(d.Initializer.Value)) good = false;
        if (good && firstRef0.Parent is AssignmentExpressionSyntax fa && fa.Left == firstRef0 && fs.Initializers.Contains(fa))
        {
            var info = Removal(t, b0, st, d);
            if (info is null) return Outcome.No("note: for-loop variable, declaration shares its line / has directive");
            var fp = info;
            fp.Id = "for:" + fs.SpanStart;
            fp.Merge = true;
            fp.ForKey = fs.SpanStart; fp.ForCount = fs.Initializers.Count;
            fp.ForFirst = ((IdentifierNameSyntax)((AssignmentExpressionSyntax)fs.Initializers[0]).Left) == firstRef0;
            fp.MergePos = ((AssignmentExpressionSyntax)fs.Initializers[0]).Left.SpanStart;
            fp.InsertPos = fs.SpanStart;
            fp.TypeText = typeText0;
            return new Outcome(fp, null);
        }
    }

    // lowest common ancestor
    var anc = refs[0].AncestorsAndSelf().ToList();
    foreach (var r in refs.Skip(1)) { var set = new HashSet<SyntaxNode>(r.AncestorsAndSelf()); anc = anc.Where(set.Contains).ToList(); }
    var lca = anc.First();
    var ib = lca.AncestorsAndSelf().OfType<BlockSyntax>().First();
    if (!b0.Span.Contains(ib.Span)) return Outcome.No("note: already innermost (or loop header)");
    var firstRef = refs.OrderBy(r => r.SpanStart).First();
    var S = ib.Statements.First(s => s.Span.Contains(firstRef.SpanStart));

    bool inLoop = ib.AncestorsAndSelf().TakeWhile(a => a != b0).Any(a => a is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);
    string? init = d.Initializer?.Value.ToString();
    bool pureInit = d.Initializer is null || IsPure(d.Initializer.Value);

    // is the first reference the left side of a plain assignment statement S ?
    bool writeFirst = S is ExpressionStatementSyntax es && es.Expression is AssignmentExpressionSyntax asg && asg.IsKind(SyntaxKind.SimpleAssignmentExpression)
        && asg.Left is IdentifierNameSyntax li && li == firstRef
        && !SelfRef(asg.Right, name);

    bool outFirst = firstRef.Parent is ArgumentSyntax oa && oa.RefKindKeyword.IsKind(SyntaxKind.OutKeyword) && (S is ExpressionStatementSyntax || S is LocalDeclarationStatementSyntax);
    if (inLoop && d.Initializer is not null && !((writeFirst || outFirst) && pureInit)) return Outcome.No("note: initialiser would be re-evaluated per iteration");
    if (!pureInit) return Outcome.No("note: non-trivial initialiser");

    // positions
    int sLineStart = LineStart(t, S.SpanStart);
    if (!IsBlank(t.Substring(sLineStart, S.SpanStart - sLineStart))) return Outcome.No("note: target not at line start");
    int insertPos = sLineStart;
    while (insertPos > 0)
    {
        int prevStart = PrevLineStart(t, insertPos);
        var pl = LineText(t, prevStart).Trim();
        if (pl.StartsWith("#")) return Outcome.No("note: directive above target");
        if (pl.StartsWith("//")) insertPos = prevStart; else break;
    }
    string indent = t.Substring(sLineStart, S.SpanStart - sLineStart);

    var vars = st.Declaration.Variables;
    int idx = vars.IndexOf(d);
    // the declaration statement lines
    int stLineStart = LineStart(t, st.SpanStart);
    bool aloneStart = IsBlank(t.Substring(stLineStart, st.SpanStart - stLineStart));
    int stEndLine = LineEndIncl(t, Math.Max(st.Span.End - 1, st.SpanStart));
    string afterOnLine = t.Substring(st.Span.End, stEndLine - st.Span.End).Trim();
    bool aloneEnd = afterOnLine.Length == 0 || afterOnLine.StartsWith("//");
    bool hasTrailingComment = afterOnLine.StartsWith("//");
    if (!aloneStart || !aloneEnd) return Outcome.No("note: declaration shares its line");
    // leading comment lines moved with a single declaration
    int removeStart = stLineStart;
    bool hasLeadingComment = false;
    while (removeStart > 0)
    {
        int prevStart = PrevLineStart(t, removeStart);
        var pl = LineText(t, prevStart).Trim();
        if (pl.StartsWith("#")) { return Outcome.No("note: directive above declaration"); }
        if (pl.StartsWith("//")) { removeStart = prevStart; hasLeadingComment = true; } else break;
    }
    int removeEnd = stEndLine;
    // blank-line collapse
    {
        string prevLine = removeStart > 0 ? LineText(t, PrevLineStart(t, removeStart)) : "";
        string nextLine = removeEnd < t.Length ? LineText(t, removeEnd) : "x";
        if ((IsBlank(prevLine) || prevLine.TrimEnd().EndsWith("{")) && IsBlank(nextLine) && removeEnd < t.Length) removeEnd = LineEndIncl(t, removeEnd);
    }
    if (vars.Count > 1 && (hasLeadingComment || hasTrailingComment)) return Outcome.No("note: multi declaration with comments");
    string origIndent = t.Substring(stLineStart, st.SpanStart - stLineStart);
    var movedLines = t.Substring(removeStart, stEndLine - removeStart).Replace("\r", "").TrimEnd('\n').Split('\n');
    string moved = string.Concat(movedLines.Select(l => { var x = l.StartsWith(origIndent) ? l.Substring(origIndent.Length) : l.TrimStart(); return (x.Length == 0 ? "" : indent + x) + eol; }));
    string typeText = st.Declaration.Type.ToString();
    string newDecl = indent + typeText + " " + d.ToString() + ";" + eol;

    bool canMerge = (writeFirst || outFirst) && pureInit && !typeText.Equals("var") && !hasTrailingComment && !hasLeadingComment;
    if (ib == b0 && !canMerge) return Outcome.No("note: already innermost (or loop header)");
    int declRemoveStart, declRemoveEnd;
    if (idx == 0) { declRemoveStart = d.SpanStart; declRemoveEnd = vars.Count > 1 ? vars[1].SpanStart : d.Span.End; }
    else { declRemoveStart = vars[idx - 1].Span.End; declRemoveEnd = d.Span.End; }

    var plan = new Plan
    {
        Id = $"{insertPos}:{canMerge}:{(canMerge ? firstRef.SpanStart : 0)}",
        Name = name, DeclPos = d.SpanStart, StmtStart = st.SpanStart, DeclaratorCount = vars.Count, DeclaratorIndex = idx,
        InsertPos = canMerge ? sLineStart : insertPos, Merge = canMerge, MergePos = firstRef.SpanStart, TypeText = typeText,
        RemoveStart = removeStart, RemoveEnd = removeEnd, MovedText = moved, NewDeclText = newDecl,
        DeclRemoveStart = declRemoveStart, DeclRemoveEnd = declRemoveEnd,
    };
    return new Outcome(plan, null);
}

Plan? Removal(string t, BlockSyntax b0, LocalDeclarationStatementSyntax st, VariableDeclaratorSyntax d)
{
    int stLineStart = LineStart(t, st.SpanStart);
    if (!IsBlank(t.Substring(stLineStart, st.SpanStart - stLineStart))) return null;
    int stEndLine = LineEndIncl(t, Math.Max(st.Span.End - 1, st.SpanStart));
    string after = t.Substring(st.Span.End, stEndLine - st.Span.End).Trim();
    if (after.Length > 0 && !after.StartsWith("//")) return null;
    var vars = st.Declaration.Variables;
    int idx = vars.IndexOf(d);
    int removeStart = stLineStart;
    bool lead = false;
    while (removeStart > 0)
    {
        int prevStart = PrevLineStart(t, removeStart);
        var pl = LineText(t, prevStart).Trim();
        if (pl.StartsWith("#")) return null;
        break; // leading comment lines stay where they are
    }
    if (vars.Count > 1 && (lead || after.StartsWith("//"))) return null;
    if (vars.Count == 1 && (lead || after.StartsWith("//"))) return null; // keep it simple: comments stay with a manual move
    int removeEnd = stEndLine;
    string prevLine = removeStart > 0 ? LineText(t, PrevLineStart(t, removeStart)) : "";
    string nextLine = removeEnd < t.Length ? LineText(t, removeEnd) : "x";
    if ((IsBlank(prevLine) || prevLine.TrimEnd().EndsWith("{")) && IsBlank(nextLine) && removeEnd < t.Length) removeEnd = LineEndIncl(t, removeEnd);
    int drs, dre;
    if (idx == 0) { drs = d.SpanStart; dre = vars.Count > 1 ? vars[1].SpanStart : d.Span.End; }
    else { drs = vars[idx - 1].Span.End; dre = d.Span.End; }
    return new Plan { Name = d.Identifier.ValueText, DeclPos = d.SpanStart, StmtStart = st.SpanStart, DeclaratorCount = vars.Count, DeclaratorIndex = idx, RemoveStart = removeStart, RemoveEnd = removeEnd, DeclRemoveStart = drs, DeclRemoveEnd = dre };
}

static List<IdentifierNameSyntax> FindRefs(BlockSyntax b0, StatementSyntax st, string name)
{
    var refs = new List<IdentifierNameSyntax>();
    foreach (var s in b0.Statements.SkipWhile(s => s != st).Skip(1))
        foreach (var id in s.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (id.Identifier.ValueText != name) continue;
            if (id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id) continue;
            if (id.Parent is NameColonSyntax) continue;
            if (id.Parent is MemberBindingExpressionSyntax) continue;
            if (id.Parent is AssignmentExpressionSyntax ae && ae.Left == id && ae.Parent is InitializerExpressionSyntax ie && ie.IsKind(SyntaxKind.ObjectInitializerExpression)) continue;
            if (id.Parent is QualifiedNameSyntax qn && qn.Right == id) continue;
            refs.Add(id);
        }
    return refs;
}


static bool IsRealRef(IdentifierNameSyntax id) =>
    !(id.Parent is MemberAccessExpressionSyntax ma && ma.Name == id) && id.Parent is not NameColonSyntax && id.Parent is not MemberBindingExpressionSyntax &&
    !(id.Parent is AssignmentExpressionSyntax ae && ae.Left == id && ae.Parent is InitializerExpressionSyntax ie && ie.IsKind(SyntaxKind.ObjectInitializerExpression)) &&
    !(id.Parent is QualifiedNameSyntax qn && qn.Right == id);

static bool SelfRef(SyntaxNode n, string name) => n.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.ValueText == name && IsRealRef(i));

static bool IsPure(ExpressionSyntax e) => e switch
{
    LiteralExpressionSyntax => true,
    DefaultExpressionSyntax => true,
    PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax } => true,
    CastExpressionSyntax { Expression: LiteralExpressionSyntax } => true,
    CastExpressionSyntax { Expression: PrefixUnaryExpressionSyntax { Operand: LiteralExpressionSyntax } } => true,
    _ => false,
};

record Edit(int Start, int End, string Text, int Order);
class Plan
{
    public string Id = "", Name = "", TypeText = "", MovedText = "", NewDeclText = "";
    public int DeclPos, StmtStart, DeclaratorCount, DeclaratorIndex, InsertPos, MergePos, RemoveStart, RemoveEnd, DeclRemoveStart, DeclRemoveEnd;
    public bool Merge, ForFirst, Delete;
    public List<int>? ExtraTypePos;
    public int ForKey, ForCount;
}
record Outcome(Plan? Plan, string? Why)
{
    public static Outcome No(string why) => new(null, why);
}
