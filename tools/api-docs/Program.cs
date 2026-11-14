using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

return ApiDocument.Run(args);

internal static partial class ApiDocument
{
    private static readonly CSharpParseOptions ParseOptions = CSharpParseOptions.Default
        .WithLanguageVersion(LanguageVersion.Preview).WithDocumentationMode(DocumentationMode.Diagnose);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, NewLine = "\n" };

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is not ("--write" or "--check" or "--audit" or "--verify-examples" or "--html" or "--json"))
                throw new ArgumentException("用法：--write | --check | --audit [报告路径] | --verify-examples <Core.dll> | --html <输出.html> | --json <输出.json>；可附加 --root <Core目录>。");
            int rootOption = Array.IndexOf(args, "--root");
            string root = rootOption >= 0 ? Path.GetFullPath(args[rootOption + 1]) : FindCoreRoot();
            if (args[0] == "--verify-examples") return VerifyExamples(root, args[1]);
            var scan = Scan(root);
            ValidatePresentation(scan);
            IndexReferenceLabels(scan);
            if (args[0] == "--audit")
            {
                string report = args.Length > 1 && args[1] != "--root" ? args[1] : Path.Combine(root, "../../.artifacts/api-documentation/audit.json");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report))!);
                File.WriteAllText(report, JsonSerializer.Serialize(new
                {
                    files = scan.Files, types = scan.Types.Count, members = scan.Types.Sum(t => t.Members.Count),
                    errors = scan.Errors,
                    typesIndex = scan.Types.Select(t => new { id = t.Symbol.GetDocumentationCommentId(), t.Name, t.Path, title = TypeDisplayName(t), role = PresentationFor(t).Role, paths = NavigationPaths(t) }),
                    entries = scan.Types.SelectMany(t => t.Members.Select(m => new { type = t.Name, id = m.Id, m.Name, m.Path, m.Line, m.Kind, m.Signature, title = MethodDisplayName(t, m), paths = MemberNavigationPaths(t, m) }))
                }, JsonOptions));
                Console.WriteLine($"API 材料：{scan.Files} 文件，{scan.Types.Count} 类型，{scan.Types.Sum(t => t.Members.Count)} 成员，{scan.Errors.Count} 项待处理。");
                return scan.Errors.Count == 0 ? 0 : 1;
            }
            if (scan.Errors.Count != 0)
                throw new InvalidOperationException("接口说明不完整或格式有误：\n" + string.Join("\n", scan.Errors.Take(30))
                    + (scan.Errors.Count > 30 ? $"\n另有 {scan.Errors.Count - 30} 项；使用 --audit 查看完整清单。" : ""));
            _ = HtmlDesigns(scan);
            string path = DocumentationFile(root, "API.md");
            string original = File.ReadAllText(path).Replace("\r\n", "\n");
            string version = XDocument.Load(Path.Combine(root, "ExHyperV.Core.csproj")).Descendants("Version").First().Value;
            var source = ReadSourceIdentity(root);
            // A tracked document cannot embed its own commit ID: every documentation commit would invalidate it.
            // Keep the content digest here; HTML and the export manifest record the publishing revision.
            string provenance = $"<!-- source-sha256: {source.ContentSha256} -->\n\n";
            string guide = File.ReadAllText(DocumentationFile(root, "guide.md")).Replace("\r\n", "\n");
            string generated = ReplaceBlock(guide, "api-reference", provenance + Render(scan, version));
            generated = RenderExampleBlocks(generated, root);
            string helpPath = DocumentationFile(root, "FieldHelp.json");
            string help = JsonSerializer.Serialize(BuildFieldHelp(scan), JsonOptions) + "\n";
            string modelPath = DocumentationFile(root, "API.json");
            string model = JsonSerializer.Serialize(BuildDocumentData(scan, version, generated,
                new SourceIdentity(null, source.ContentSha256)), JsonOptions) + "\n";
            if (args[0] == "--json")
            {
                if (args.Length < 2 || !args[1].EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--json 需要指定 JSON 输出文件。");
                string destination = Path.GetFullPath(args[1]);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, model, new UTF8Encoding(false));
                Console.WriteLine($"API 元数据已生成：{scan.Types.Count} 类型，{scan.Types.Sum(t => t.Members.Count)} 成员。");
                return 0;
            }
            if (args[0] == "--html")
            {
                if (args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("--html 需要指定输出 HTML 文件。");
                if (generated != original || !File.Exists(modelPath) || File.ReadAllText(modelPath).Replace("\r\n", "\n") != model || !File.Exists(helpPath) || File.ReadAllText(helpPath).Replace("\r\n", "\n") != help)
                    throw new InvalidOperationException("API 文档产物已过期，请运行 --write。");
                string destination = Path.GetFullPath(args[1]);
                if (!destination.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("网页输出必须使用 .html 扩展名。");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllText(destination, RenderHtml(scan, version, generated, source), new UTF8Encoding(false));
                Console.WriteLine($"API 网页已生成：{scan.Types.Count} 类型，{scan.Types.Sum(t => t.Members.Count)} 成员。");
                return 0;
            }
            if (args[0] == "--write")
            {
                File.WriteAllText(path, generated, new UTF8Encoding(false));
                File.WriteAllText(modelPath, model, new UTF8Encoding(false));
                File.WriteAllText(helpPath, help, new UTF8Encoding(false));
            }
            else if (!File.Exists(helpPath) || File.ReadAllText(helpPath).Replace("\r\n", "\n") != help || generated != original || !File.Exists(modelPath) || File.ReadAllText(modelPath).Replace("\r\n", "\n") != model) throw new InvalidOperationException("API.md 已过期。运行 --write，检查文档差异后与源码一起保存。");
            Console.WriteLine($"API 文档{(args[0] == "--write" ? "已生成" : "同步检查通过")}：{scan.Types.Count} 类型，{scan.Types.Sum(t => t.Members.Count)} 成员；示例不执行。");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    private static IReadOnlyDictionary<string, string> BuildFieldHelp(ScanResult scan)
    {
        var members = scan.Types.SelectMany(type => type.Members).ToDictionary(member => member.Id);
        return members.Values.Where(member => member.Id.StartsWith("P:", StringComparison.Ordinal))
            .OrderBy(member => member.Id, StringComparer.Ordinal).ToDictionary(member => member.Id, member =>
            {
                var source = PresentationFor(member).HelpSource is { } id ? members[id] : member;
                return Regex.Replace(Inline(source.Doc.Element("fielddoc")?.Element("meaning")
                    ?? source.Doc.Element("summary")), @"\s+", " ").Trim();
            });
    }

    private static string RepositoryRoot(string core) => Path.GetFullPath(Path.Combine(core, "../.."));
    private static string DocumentationFile(string core, string name) => Path.Combine(RepositoryRoot(core), "doc/api", name);

    private static string FindCoreRoot()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var current = new DirectoryInfo(start); current != null; current = current.Parent)
            {
                if (File.Exists(Path.Combine(current.FullName, "ExHyperV.Core.csproj"))) return current.FullName;
                string nested = Path.Combine(current.FullName, "src/ExHyperV.Core");
                if (File.Exists(Path.Combine(nested, "ExHyperV.Core.csproj"))) return nested;
            }
        throw new DirectoryNotFoundException("找不到 ExHyperV.Core.csproj；请传入 --root。");
    }

    private static ScanResult Scan(string root)
    {
        var files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories).Where(path =>
            !Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Intersect(new[] { "bin", "obj", "external", "Documentation" }, StringComparer.OrdinalIgnoreCase).Any()
            && !path.EndsWith("Resources.Designer.cs", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToArray();
        var trees = files.Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions, path)).ToArray();
        var globals = CSharpSyntaxTree.ParseText("global using System; global using System.IO; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;", ParseOptions);
        var compilation = CSharpCompilation.Create("CoreDocumentation", trees.Append(globals), PlatformReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
        var errors = new List<string>();
        var types = new Dictionary<string, TypeEntry>(StringComparer.Ordinal);
        var items = new List<(ISymbol Symbol, SyntaxNode Node, XElement Doc, string Path)>();
        foreach (var tree in trees)
        {
            string path = Path.GetRelativePath(root, tree.FilePath).Replace('\\', '/');
            errors.AddRange(tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error || d.Id == "CS1570")
                .Select(d => $"{path}:{d.Location.GetLineSpan().StartLinePosition.Line + 1}: {d.GetMessage(CultureInfo.InvariantCulture)}"));
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                ISymbol? symbol = node switch
                {
                    BaseTypeDeclarationSyntax or DelegateDeclarationSyntax or MethodDeclarationSyntax or ConstructorDeclarationSyntax
                        or PropertyDeclarationSyntax or IndexerDeclarationSyntax or EventDeclarationSyntax or EnumMemberDeclarationSyntax
                        or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => model.GetDeclaredSymbol(node),
                    VariableDeclaratorSyntax v when v.Parent?.Parent is BaseFieldDeclarationSyntax => model.GetDeclaredSymbol(v),
                    _ => null
                };
                if (symbol == null) continue;
                SyntaxNode commentNode = node is VariableDeclaratorSyntax ? node.Parent!.Parent! : node;
                var doc = ReadDoc(commentNode, path, errors);
                if (symbol is INamedTypeSymbol type && Visible(type))
                {
                    string name = type.ToDisplayString();
                    if (!types.TryGetValue(name, out var entry)) types.Add(name, new(name, path, Line(node), TypeSignature(node), doc, type));
                    else if (!HasSummary(entry.Doc) && HasSummary(doc)) entry.Doc = doc;
                }
                else if (Visible(symbol)) items.Add((symbol, node, doc, path));
                if (symbol.ContainingType != null && Visible(symbol.ContainingType))
                {
                    if (node is VariableDeclaratorSyntax field && commentNode is FieldDeclarationSyntax declaration
                        && HasAttribute(declaration.AttributeLists, "ObservableProperty"))
                    {
                        string fieldName = field.Identifier.ValueText;
                        string stem = fieldName.StartsWith("m_", StringComparison.Ordinal) ? fieldName[2..] : fieldName.TrimStart('_');
                        if (stem.Length == 0) { errors.Add($"{path}:{Line(node)} 无法确定生成属性名。"); continue; }
                        string name = char.ToUpperInvariant(stem[0]) + stem[1..];
                        string owner = symbol.ContainingType.ToDisplayString();
                        items.Add((symbol, node, new XElement(doc), path));
                        // Marker stays internal to the generator; it is never written into source comments.
                        items[^1].Doc.SetAttributeValue("generatedProperty", name);
                        items[^1].Doc.SetAttributeValue("owner", owner);
                        items[^1].Doc.SetAttributeValue("propertyType", declaration.Declaration.Type.ToString());
                        if (field.Initializer != null) items[^1].Doc.SetAttributeValue("initializer", field.Initializer.ToString());
                    }
                    if (node is MethodDeclarationSyntax method && HasAttribute(method.AttributeLists, "RelayCommand"))
                    {
                        if (method.ParameterList.Parameters.Count != 0 || method.ReturnType.ToString() != "void")
                            errors.Add($"{path}:{Line(node)} RelayCommand 形态变化，需要更新生成器，不能静默漏掉公开命令。");
                        else
                        {
                            items.Add((symbol, node, new XElement(doc), path));
                            items[^1].Doc.SetAttributeValue("generatedCommand", method.Identifier.ValueText + "Command");
                        }
                    }
                }
            }
        }
        foreach (var item in items)
        {
            string owner = item.Symbol.ContainingType!.ToDisplayString();
            if (!types.TryGetValue(owner, out var type)) throw new InvalidOperationException($"缺少公开类型：{owner}");
            bool generatedProperty = item.Doc.Attribute("generatedProperty") != null;
            bool generatedCommand = item.Doc.Attribute("generatedCommand") != null;
            string name = (string?)item.Doc.Attribute("generatedProperty") ?? (string?)item.Doc.Attribute("generatedCommand") ?? item.Symbol.Name;
            string kind = generatedProperty ? "生成属性" : generatedCommand ? "生成命令" : item.Symbol switch
            {
                IMethodSymbol { MethodKind: MethodKind.Constructor } => "构造器", IMethodSymbol => "方法",
                IPropertySymbol => "属性", IEventSymbol => "事件", IFieldSymbol f when f.ContainingType.TypeKind == TypeKind.Enum => "枚举值", _ => "字段"
            };
            string signature = generatedProperty ? $"public {item.Doc.Attribute("propertyType")!.Value} {name} {{ get; set; }}"
                : generatedCommand ? $"public IRelayCommand {name} {{ get; }}" : MemberSignature(item.Node);
            if (generatedProperty && item.Doc.Attribute("initializer") is XAttribute initializer) signature += " " + initializer.Value + ";";
            if (item.Node is EnumMemberDeclarationSyntax { EqualsValue: null } && item.Symbol is IFieldSymbol { HasConstantValue: true } enumField)
                signature += " = " + Convert.ToString(enumField.ConstantValue, CultureInfo.InvariantCulture);
            string id = generatedProperty || generatedCommand ? $"P:{owner}.{name}" : item.Symbol.GetDocumentationCommentId()!;
            var parameters = generatedProperty || generatedCommand ? [] : Parameters(item.Symbol);
            var generics = item.Symbol is IMethodSymbol methodSymbol ? methodSymbol.TypeParameters.Select(p => p.Name).ToArray() : [];
            bool returns = !generatedCommand && !generatedProperty && item.Symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.UserDefinedOperator or MethodKind.Conversion, ReturnsVoid: false } m
                && m.ReturnType.ToDisplayString() is not "System.Threading.Tasks.Task" and not "System.Threading.Tasks.ValueTask";
            Validate(item.Doc, parameters, generics, returns, $"{item.Path}:{Line(item.Node)} {owner}.{name}", errors);
            type.Members.Add(new(id, name, kind, signature, item.Path, Line(item.Node), item.Doc));
        }
        foreach (var type in types.Values)
        {
            var declarations = type.Symbol.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).ToArray();
            var primary = declarations.OfType<TypeDeclarationSyntax>().FirstOrDefault(d => d.ParameterList != null);
            var parameters = primary?.ParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToArray()
                ?? (type.Symbol.TypeKind == TypeKind.Delegate ? type.Symbol.DelegateInvokeMethod!.Parameters.Select(p => p.Name).ToArray() : []);
            Validate(type.Doc, parameters, type.Symbol.TypeParameters.Select(p => p.Name).ToArray(), false, $"{type.Path}:{type.Line} {type.Name}", errors);
            if (primary?.ParameterList != null)
            {
                type.Members.Add(new($"C:{type.Name}", type.Symbol.Name, "主构造器",
                    $"public {type.Symbol.Name}{primary.ParameterList.WithoutTrivia().NormalizeWhitespace()};", type.Path, type.Line,
                    new XElement("root", type.Doc.Elements("param").Select(p => new XElement(p)))));
                if (primary is RecordDeclarationSyntax)
                    foreach (var parameter in primary.ParameterList.Parameters)
                    {
                        string name = parameter.Identifier.ValueText;
                        if (type.Members.Any(m => m.Name == name && m.Kind == "属性")) continue;
                        // A positional parameter may reuse a base record's property without declaring a new one.
                        if (!type.Symbol.GetMembers(name).OfType<IPropertySymbol>().Any(p => SymbolEqualityComparer.Default.Equals(p.ContainingType, type.Symbol))) continue;
                        var description = type.Doc.Elements("param").FirstOrDefault(p => (string?)p.Attribute("name") == name);
                        type.Members.Add(new($"P:{type.Name}.{name}", name, "record 属性",
                            $"public {parameter.Type} {name} {{ get; {(type.Symbol.IsReadOnly || type.Symbol.TypeKind == TypeKind.Class ? "init" : "set")}; }}",
                            type.Path, Line(parameter), new XElement("root",
                                new XElement("summary", description?.Nodes().Where(node => node is not XElement element || element.Name.LocalName != "fielddoc")),
                                description?.Elements("fielddoc").Select(element => new XElement(element)))));
                    }
            }
            type.Members.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
            if (type.Members.GroupBy(m => m.Id).Any(g => g.Count() > 1)) errors.Add($"{type.Name}: 发现重复成员标识，需核对 partial 或生成成员。");
            try { _ = NavigationPaths(type); }
            catch (InvalidOperationException error) { errors.Add(error.Message); }
        }
        var result = new ScanResult(files.Length, types.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToList(), errors);
        ValidateModificationDocumentation(result);
        ValidateHostEntries(result);
        return result;
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> lists, string name) => lists.SelectMany(l => l.Attributes)
        .Any(a => a.Name.ToString().Split('.').Last() is var n && (n == name || n == name + "Attribute"));
    private static bool Visible(ISymbol symbol)
    {
        if (symbol.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) return false;
        for (var type = symbol.ContainingType; type != null; type = type.ContainingType)
            if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedOrInternal)) return false;
        return true;
    }
    private static int Line(SyntaxNode node) => node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
    private static string[] Parameters(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.Parameters.Select(p => p.Name).ToArray(), IPropertySymbol p => p.Parameters.Select(x => x.Name).ToArray(), _ => []
    };
    private static XElement ReadDoc(SyntaxNode node, string path, List<string> errors)
    {
        var blocks = node.GetLeadingTrivia().Select(t => t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>();
        string text = string.Join("\n", blocks.Select(b => string.Concat(b.Content.Select(c => c.ToFullString()))));
        text = Regex.Replace(text, @"(?m)^\s*/// ?", "").Trim();
        try { return XElement.Parse("<root>" + text + "</root>", LoadOptions.PreserveWhitespace); }
        catch (Exception e) { errors.Add($"{path}:{Line(node)} XML 格式：{e.Message}"); return new("root"); }
    }
    private static bool HasSummary(XElement doc) => !string.IsNullOrWhiteSpace(doc.Element("summary")?.Value);
    private static void Validate(XElement doc, string[] parameters, string[] generics, bool returns, string label, List<string> errors)
    {
        ValidateFieldDocumentation(doc, label, errors);
        if (!HasSummary(doc)) errors.Add(label + " 缺少 summary。");
        CheckTags("param", parameters); CheckTags("typeparam", generics);
        if (returns && string.IsNullOrWhiteSpace(doc.Element("returns")?.Value)) errors.Add(label + " 缺少 returns。");
        foreach (var exception in doc.Elements("exception"))
            if (string.IsNullOrWhiteSpace((string?)exception.Attribute("cref")) || string.IsNullOrWhiteSpace(exception.Value)) errors.Add(label + " exception 不完整。");
        foreach (var prose in doc.Elements().Where(e => e.Name.LocalName is "summary" or "remarks" or "param" or "typeparam" or "returns" or "value" or "exception" or "fielddoc"))
        {
            string narrative = string.Concat(prose.DescendantNodes().OfType<XText>()
                .Where(text => !text.Ancestors().Any(e => e.Name.LocalName is "code" or "c" or "see" or "paramref" or "typeparamref"))
                .Select(text => text.Value));
            if (Regex.IsMatch(narrative, "不代表完成|不能把属性存在|不承诺|尚未验证|具体用途待确认|具体布局含义待补充"))
                errors.Add(label + " 含冗余验证说明；未知机制使用：相关机制暂不明确。");
            // API identifiers and code stay verbatim; standalone explanatory sentences use Chinese.
            if (Regex.IsMatch(narrative, @"[A-Za-z]{2,}\s+[A-Za-z]{2,}") && !Regex.IsMatch(narrative, @"[\p{IsCJKUnifiedIdeographs}\p{IsCJKUnifiedIdeographsExtensionA}]"))
                errors.Add(label + $" {prose.Name.LocalName} 仍有未统一为中文的说明。");
        }
        void CheckTags(string tag, string[] names)
        {
            var entries = doc.Elements(tag).ToArray();
            foreach (string name in names)
                if (entries.Count(e => (string?)e.Attribute("name") == name && !string.IsNullOrWhiteSpace(e.Value)) != 1) errors.Add(label + $" 缺少或重复 {tag} {name}。");
            foreach (var entry in entries)
                if (!names.Contains((string?)entry.Attribute("name"))) errors.Add(label + $" {tag} 引用了不存在的参数 {entry.Attribute("name")?.Value}。");
        }
    }

    private static string TypeSignature(SyntaxNode node) => node switch
    {
        TypeDeclarationSyntax t => string.Join(" ", t.Modifiers.Select(m => m.Text)) + " " +
            (t is RecordDeclarationSyntax r ? "record" + (r.ClassOrStructKeyword.Text.Length > 0 ? " " + r.ClassOrStructKeyword.Text : "") : t.Keyword.Text)
            + " " + t.Identifier.Text + t.TypeParameterList + t.ParameterList + (t.BaseList == null ? "" : " " + t.BaseList) +
            (t.ConstraintClauses.Count == 0 ? "" : " " + string.Join(" ", t.ConstraintClauses)),
        EnumDeclarationSyntax e => string.Join(" ", e.Modifiers.Select(m => m.Text)) + " enum " + e.Identifier.Text + (e.BaseList == null ? "" : " " + e.BaseList),
        DelegateDeclarationSyntax d => d.WithAttributeLists(default).WithoutTrivia().NormalizeWhitespace().ToFullString(),
        _ => throw new InvalidOperationException("未知类型声明。")
    };
    private static string MemberSignature(SyntaxNode node)
    {
        var semicolon = SyntaxFactory.Token(SyntaxKind.SemicolonToken);
        SyntaxNode? signature = node switch
        {
            MethodDeclarationSyntax m => m.WithAttributeLists(default).WithBody(null).WithExpressionBody(null).WithSemicolonToken(semicolon),
            ConstructorDeclarationSyntax c => c.WithAttributeLists(default).WithInitializer(null).WithBody(null).WithExpressionBody(null).WithSemicolonToken(semicolon),
            OperatorDeclarationSyntax o => o.WithAttributeLists(default).WithBody(null).WithExpressionBody(null).WithSemicolonToken(semicolon),
            ConversionOperatorDeclarationSyntax c => c.WithAttributeLists(default).WithBody(null).WithExpressionBody(null).WithSemicolonToken(semicolon),
            _ => null
        };
        if (signature != null) return signature.WithoutTrivia().NormalizeWhitespace().ToFullString();
        if (node is BasePropertyDeclarationSyntax property)
        {
            string prefix = string.Join(" ", property.Modifiers.Select(m => m.Text)) + (property is EventDeclarationSyntax ? " event" : "") + " " + property.Type + " ";
            string name = property switch { PropertyDeclarationSyntax p => p.Identifier.Text, IndexerDeclarationSyntax i => "this" + i.ParameterList, EventDeclarationSyntax e => e.Identifier.Text, _ => "" };
            var accessors = property.AccessorList?.Accessors.Where(a => !a.Modifiers.Any(SyntaxKind.PrivateKeyword)
                    && (!a.Modifiers.Any(SyntaxKind.InternalKeyword) || a.Modifiers.Any(SyntaxKind.ProtectedKeyword)))
                .Select(a => string.Join(" ", a.Modifiers.Select(m => m.Text).Append(a.Keyword.Text)) + ";");
            return prefix + name + " { " + (accessors == null ? "get;" : string.Join(" ", accessors)) + " }"
                + (property is PropertyDeclarationSyntax { Initializer: not null } initialized ? " " + initialized.Initializer + ";" : "");
        }
        if (node is VariableDeclaratorSyntax variable && variable.Parent?.Parent is BaseFieldDeclarationSyntax field)
            return string.Join(" ", field.Modifiers.Select(m => m.Text)) + (field is EventFieldDeclarationSyntax ? " event" : "") + " "
                + field.Declaration.Type + " " + variable.Identifier.Text
                + (field.Modifiers.Any(SyntaxKind.ConstKeyword) && variable.Initializer != null ? " " + variable.Initializer : "") + ";";
        if (node is EnumMemberDeclarationSyntax enumValue) return enumValue.Identifier.Text + (enumValue.EqualsValue == null ? "" : " " + enumValue.EqualsValue);
        throw new InvalidOperationException("未知成员声明：" + node.Kind());
    }

    private static string Render(ScanResult scan, string version)
    {
        var text = new StringBuilder();
        text.AppendLine($"程序集：`ExHyperV.Core.dll` · 版本：`{version}`。").AppendLine();
        text.AppendLine("本参考收录公开及受保护的自有成员，包括生成属性、命令和 record 主构造器。继承成员、record 编译器基础方法、隐式默认构造器及资源文案访问器不重复列出；`DEBUG` 专用成员不属于发布接口。").AppendLine();
        text.AppendLine("### 类型导航").AppendLine();
        string? navigationRoot = null, navigationDomain = null;
        foreach (var path in DocumentationCategoriesFor(scan).Where(path => path[0] != "使用说明"))
        {
            if (navigationRoot != path[0])
            {
                navigationRoot = path[0]; navigationDomain = null;
                text.AppendLine($"#### {navigationRoot}").AppendLine();
            }
            if (navigationDomain != path[1])
            {
                navigationDomain = path[1];
                text.AppendLine($"##### {navigationDomain}").AppendLine();
            }
            if (path.Length > 2) text.AppendLine($"**{string.Join(" / ", path.Skip(2))}**").AppendLine();
            if (path[0] == "业务层面" && path.Length == 2)
                foreach (var section in scan.Types.SelectMany(type => type.Doc.Elements("businessdoc"))
                    .Where(section => (string?)section.Attribute("category") == path[1]))
                    text.AppendLine(Inline(section)).AppendLine();
            var categoryTypes = scan.Types.Where(type => NavigationPaths(type).Select(DocumentationPath).Any(item => NavigationKey(item) == NavigationKey(path))).ToArray();
            var metadataKeys = categoryTypes.SelectMany(type => type.Members.Where(member => member.Doc.Element("metadatakey") != null)
                .Select(member => (Key: MetadataKeyValue(type, member), Note: member.Doc.Element("metadatakey")!))).ToArray();
            if (metadataKeys.Length != 0)
            {
                text.AppendLine("**ExHyperV 已使用的键**").AppendLine();
                text.AppendLine("| 键名 | 用途 | 保存内容 |").AppendLine("| --- | --- | --- |");
                foreach (var item in metadataKeys)
                    text.AppendLine($"| {CodeCell(item.Key)} | {Cell((string?)item.Note.Attribute("purpose") ?? "")} | {Cell(item.Note.Value.Trim())} |");
                text.AppendLine();
            }
            if (categoryTypes.Length == 0)
            {
                if (!DocumentationCategoriesFor(scan).Any(item => NavigationKey(item).StartsWith(NavigationKey(path) + " / ", StringComparison.Ordinal)))
                    text.AppendLine("本分类暂未归入接口。").AppendLine();
                continue;
            }
            text.AppendLine("| 类型 | 命名空间 | 简介 |");
            text.AppendLine("| --- | --- | --- |");
            foreach (var type in categoryTypes)
                text.AppendLine($"| [{CodeCell(ShortTypeName(type))}](#{Anchor(type.Name)}) | {CodeCell(NamespaceName(type))} | {Brief(type.Doc)} |");
            text.AppendLine();
        }
        foreach (var type in scan.Types)
        {
            string label = TypeLabel(type.Symbol);
            text.AppendLine($"<a id=\"{Anchor(type.Name)}\"></a>\n\n### `{ShortTypeName(type)}` {label}\n");
            text.AppendLine(Inline(type.Doc.Element("summary"))).AppendLine();
            text.AppendLine("| 项目 | 内容 |");
            text.AppendLine("| --- | --- |");
            text.AppendLine($"| 类型 | {label} |");
            text.AppendLine($"| 命名空间 | {CodeCell(NamespaceName(type))} |");
            text.AppendLine("| 程序集 | `ExHyperV.Core.dll` |");
            text.AppendLine($"| 功能归属 | {string.Join("；", NavigationPaths(type).Select(DocumentationPath).Select(NavigationKey).Distinct())} |");
            text.AppendLine($"| 源文件 | {CodeCell(type.Path)} |");
            text.AppendLine();

            var memberGroups = type.Members.GroupBy(MemberCategory).OrderBy(g => g.Key.Order).ToArray();
            if (memberGroups.Length > 0)
            {
                text.AppendLine("#### 成员概览").AppendLine();
                foreach (var group in memberGroups)
                {
                    text.AppendLine($"**{group.Key.Title}**").AppendLine();
                    text.AppendLine("| 成员 | 说明 |");
                    text.AppendLine("| --- | --- |");
                    foreach (var member in group)
                    {
                        var tokens = SyntaxFactory.ParseTokens(member.Signature).ToArray();
                        string signature = tokens.Any(token => token.Text.Contains('\n')) ? member.Name
                            : string.Concat(tokens.Select(token => Regex.Replace(token.LeadingTrivia.ToFullString(), @"\s+", " ")
                                + token.Text + Regex.Replace(token.TrailingTrivia.ToFullString(), @"\s+", " "))).Trim().TrimEnd(';');
                        text.AppendLine($"| [{CodeCell(signature)}](#{Anchor(member.Id)}) | {Brief(member.Doc)} |");
                    }
                    text.AppendLine();
                }
            }

            text.AppendLine("#### 详细说明").AppendLine();
            text.AppendLine("```csharp\n" + type.Signature.Trim() + "\n```\n");
            AppendDoc(text, type.Doc, includeSummary: false);
            foreach (var group in memberGroups)
            {
                text.AppendLine($"#### {group.Key.Title}说明").AppendLine();
                foreach (var member in group)
                {
                    string memberName = MethodDisplayName(type, member);
                    text.AppendLine($"<a id=\"{Anchor(member.Id)}\"></a>\n\n##### {memberName} · {member.Kind}\n");
                    text.AppendLine("```csharp\n" + member.Signature.Trim() + "\n```\n");
                    if (NavigationPaths(type).Length > 1)
                        text.AppendLine("功能归属：" + string.Join("；", MemberNavigationPaths(type, member).Select(DocumentationPath).Select(NavigationKey).Distinct()) + "。").AppendLine();
                    AppendDoc(text, MemberDescription(type, member));
                    AppendFieldDocumentation(text, type, member);
                }
            }
        }
        return text.ToString().TrimEnd();

        static string NamespaceName(TypeEntry type) => type.Symbol.ContainingNamespace.IsGlobalNamespace
            ? "（全局）" : type.Symbol.ContainingNamespace.ToDisplayString();
        static string ShortTypeName(TypeEntry type)
        {
            string prefix = type.Symbol.ContainingNamespace.ToDisplayString() + ".";
            return type.Name.StartsWith(prefix, StringComparison.Ordinal) ? type.Name[prefix.Length..] : type.Name;
        }
        static string TypeLabel(INamedTypeSymbol type) => type.TypeKind switch
        {
            TypeKind.Enum => "枚举", TypeKind.Interface => "接口", TypeKind.Delegate => "委托",
            TypeKind.Struct => type.IsRecord ? "record 结构" : "结构",
            _ => type.IsRecord ? "record 类" : "类"
        };
        static (int Order, string Title) MemberCategory(MemberEntry member) => member.Kind switch
        {
            "构造器" or "主构造器" => (0, "构造函数"),
            "属性" or "生成属性" or "record 属性" => (1, "属性"),
            "生成命令" => (2, "命令"),
            "事件" => (3, "事件"),
            "方法" when Regex.IsMatch(member.Signature, @"\bprotected\b") => (6, "受保护方法"),
            "方法" when Regex.IsMatch(member.Signature, @"\bstatic\b") => (4, "静态方法"),
            "方法" => (5, "方法"),
            "字段" => (7, "字段与常量"),
            "枚举值" => (8, "枚举值"),
            _ => (9, "其他成员")
        };
        static string CodeCell(string value)
        {
            int run = Regex.Matches(value, "`+").Select(match => match.Length).DefaultIfEmpty(0).Max();
            string fence = new('`', run + 1);
            return Cell(fence + " " + value + " " + fence);
        }
        static string Brief(XElement doc)
        {
            string value = Regex.Replace(doc.Element("summary")?.Value ?? "", @"\s+", " ").Trim();
            int sentence = value.IndexOf('。');
            if (sentence >= 0) value = value[..(sentence + 1)];
            if (value.Length > 120) value = value[..117] + "…";
            string escaped = System.Net.WebUtility.HtmlEncode(value).Replace("\\", "\\\\")
                .Replace("`", "\\`").Replace("[", "\\[").Replace("]", "\\]")
                .Replace("*", "\\*").Replace("_", "\\_");
            return Cell(escaped);
        }
    }
    private static string Anchor(string id) => "api-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16].ToLowerInvariant();
    private static void AppendDoc(StringBuilder output, XElement doc, bool includeSummary = true)
    {
        if (includeSummary && doc.Element("summary") is XElement summary)
            output.AppendLine(Inline(summary)).AppendLine();
        if (doc.Element("instanceentry") is XElement entry)
        {
            output.AppendLine("调用顺序：").AppendLine();
            int index = 1;
            foreach (var step in entry.Elements("step")) output.AppendLine($"{index++}. {Inline(step)}");
            output.AppendLine();
        }
        foreach (string tag in new[] { "typeparam", "param" })
        {
            var values = doc.Elements(tag).ToArray();
            if (values.Length == 0) continue;
            output.AppendLine(tag == "param" ? "| 参数 | 说明 |" : "| 类型参数 | 说明 |");
            output.AppendLine("| --- | --- |");
            foreach (var value in values) output.AppendLine($"| `{value.Attribute("name")?.Value}` | {Cell(Inline(value))} |");
            output.AppendLine();
        }
        if (doc.Element("returns") is XElement returns) output.AppendLine("返回：" + Inline(returns)).AppendLine();
        if (doc.Element("value") is XElement valueDoc) output.AppendLine("属性值：" + Inline(valueDoc)).AppendLine();
        if (doc.Element("remarks") is XElement remarks) output.AppendLine("调用约定：" + Inline(remarks)).AppendLine();
        foreach (var exception in doc.Elements("exception")) output.AppendLine($"- 异常 `{ReferenceLabel(exception.Attribute("cref")?.Value ?? "")}`：{Inline(exception)}");
        if (doc.Elements("exception").Any()) output.AppendLine();
        foreach (var example in doc.Elements("example")) output.AppendLine("示例：\n\n" + Inline(example)).AppendLine();
    }
    private static string Inline(XElement? element) => element == null ? "" : string.Concat(element.Nodes().Select(NodeText)).Trim();
    private static string NodeText(XNode node) => node switch
    {
        XText text when string.IsNullOrWhiteSpace(text.Value) && text.Parent?.Elements("para").Any() == true => "",
        XText text => System.Net.WebUtility.HtmlEncode(Regex.Replace(text.Value, @"\s+", " ")),
        XElement e when e.Name.LocalName is "c" or "paramref" or "typeparamref" => "`" + (e.Attribute("name")?.Value ?? e.Value.Trim()) + "`",
        XElement e when e.Name.LocalName == "see" => e.Attribute("href") is XAttribute href ? $"[{(e.Value.Length > 0 ? e.Value : href.Value)}]({href.Value})" : "`" + (e.Value.Length > 0 ? e.Value : ReferenceLabel(e.Attribute("cref")?.Value ?? e.Attribute("langword")?.Value ?? "")) + "`",
        XElement e when e.Name.LocalName == "code" => "\n\n```csharp\n" + Dedent(e.Value) + "\n```\n\n",
        XElement e when e.Name.LocalName == "para" => "\n\n" + Inline(e) + "\n\n",
        XElement e when e.Name.LocalName == "list" => "\n\n" + string.Join("\n", e.Elements("item").Select(item => "- " + Inline(item))) + "\n\n",
        XElement e when e.Name.LocalName == "fielddoc" => "",
        XElement e => Inline(e), _ => ""
    };
    private static string Dedent(string value)
    {
        string[] lines = value.Replace("\r\n", "\n").Trim('\r', '\n').Split('\n');
        int indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : "")).TrimEnd();
    }
    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\n", "<br>");
    private static string ReplaceBlock(string document, string name, string content)
    {
        string begin = $"<!-- {name}:start -->", end = $"<!-- {name}:end -->";
        int from = document.IndexOf(begin, StringComparison.Ordinal), to = document.IndexOf(end, StringComparison.Ordinal);
        if (from < 0 || to <= from || document.LastIndexOf(begin, StringComparison.Ordinal) != from || document.LastIndexOf(end, StringComparison.Ordinal) != to)
            throw new InvalidOperationException($"API.md 必须恰好包含一对 {name} 标记。");
        return document[..(from + begin.Length)] + "\n\n" + content.Replace("\r\n", "\n").Trim() + "\n\n" + document[to..];
    }
    private static string RenderExampleBlocks(string document, string root)
    {
        string source = File.ReadAllText(DocumentationFile(root, "UsageExamples.cs")).Replace("\r\n", "\n");
        var examples = Regex.Matches(source, @"(?ms)^[ \t]*// example:(?<key>[\w-]+):start\n(?<body>.*?)^[ \t]*// example:\k<key>:end[ \t]*$");
        if (examples.Count == 0 || Regex.Matches(source, @"(?m)^[ \t]*// example:[\w-]+:(?:start|end)[ \t]*$").Count != examples.Count * 2)
            throw new InvalidOperationException("调用示例的开始和结束标记不匹配。");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match example in examples)
        {
            string key = example.Groups["key"].Value, body = example.Groups["body"].Value;
            if (!keys.Add(key)) throw new InvalidOperationException("调用示例标记重复：" + key);
            var title = Regex.Matches(body, @"(?m)^[ \t]*// title: (.+)$");
            var description = Regex.Matches(body, @"(?m)^[ \t]*// description: (.+)$");
            if (title.Count != 1 || description.Count != 1)
                throw new InvalidOperationException("调用示例需要一份标题和用途说明：" + key);
            string code = Regex.Replace(body, @"(?m)^[ \t]*// (?:title|description): .*\n?", "");
            document = ReplaceBlock(document, "api-example-" + key,
                "### " + title[0].Groups[1].Value.Trim() + "\n\n" + description[0].Groups[1].Value.Trim()
                + "\n\n```csharp\n" + Dedent(code) + "\n```\n");
        }
        foreach (Match block in Regex.Matches(document, @"<!-- api-example-([\w-]+):start -->"))
            if (!keys.Contains(block.Groups[1].Value))
                throw new InvalidOperationException("文档引用了不存在的调用示例：" + block.Groups[1].Value);
        return document;
    }
    private static IEnumerable<PortableExecutableReference> PlatformReferences() => ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
    private static int VerifyExamples(string root, string assembly)
    {
        assembly = Path.GetFullPath(assembly);
        if (!File.Exists(assembly)) throw new FileNotFoundException("请提供已构建的 Core DLL。", assembly);
        var paths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .GroupBy(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        // Use the compiled product's dependencies so generated properties and public contracts are checked too.
        foreach (var path in Directory.GetFiles(Path.GetDirectoryName(assembly)!, "*.dll")) paths[Path.GetFileName(path)] = path;
        var references = new List<MetadataReference>();
        foreach (var path in paths.Values)
        {
            try { references.Add(MetadataReference.CreateFromFile(path)); } catch (BadImageFormatException) { }
        }
        var scan = Scan(root);
        if (scan.Errors.Count != 0) throw new InvalidOperationException("接口材料检查未通过：\n" + string.Join("\n", scan.Errors.Take(30)));
        var examples = new List<(string Label, string Source)>
        {
            ("调用示例", File.ReadAllText(DocumentationFile(root, "UsageExamples.cs")))
        };
        foreach (var type in scan.Types)
        {
            AddExamples(type.Name, type.Doc);
            foreach (var member in type.Members) AddExamples(type.Name + "." + member.Name, member.Doc);
        }
        var globals = CSharpSyntaxTree.ParseText("global using System; global using System.Linq; global using System.Collections.Generic; global using System.Threading; global using System.Threading.Tasks;", ParseOptions);
        foreach (var example in examples)
        {
            var source = CSharpSyntaxTree.ParseText(example.Source, ParseOptions);
            var compilation = CSharpCompilation.Create("CoreApiExamples", new[] { source, globals }, references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
            using var output = new MemoryStream();
            var result = compilation.Emit(output);
            if (!result.Success) throw new InvalidOperationException(example.Label + "：\n" + string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }
        Console.WriteLine($"调用示例及 {examples.Count - 1} 个接口内示例编译通过，未加载或执行 Core。");
        return 0;
        void AddExamples(string label, XElement doc)
        {
            foreach (var code in doc.Elements("example").SelectMany(e => e.Descendants("code")))
                examples.Add((label, Dedent(code.Value)));
        }
    }
    private sealed record ScanResult(int Files, List<TypeEntry> Types, List<string> Errors);
    private sealed class TypeEntry(string name, string path, int line, string signature, XElement doc, INamedTypeSymbol symbol)
    {
        public string Name { get; } = name; public string Path { get; } = path; public int Line { get; } = line;
        public string Signature { get; } = signature; public XElement Doc { get; set; } = doc;
        public INamedTypeSymbol Symbol { get; } = symbol; public List<MemberEntry> Members { get; } = [];
    }
    private sealed record MemberEntry(string Id, string Name, string Kind, string Signature, string Path, int Line, XElement Doc);
}
