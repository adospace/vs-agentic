using System.IO;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace VsAgentic.VSExtension.IdeContext;

/// <summary>Where to look for a symbol; line and column are 1-based.</summary>
internal sealed class SymbolQuery
{
    public string? SymbolId { get; }
    public string? Name { get; }
    public string? File { get; }
    public int? Line { get; }
    public int? Column { get; }

    public SymbolQuery(string? symbolId, string? name, string? file, int? line, int? column)
    {
        SymbolId = symbolId;
        Name = name;
        File = file;
        Line = line;
        Column = column;
    }

    /// <summary>True when the query names nothing, which means the caret.</summary>
    public bool IsEmpty => SymbolId is null && Name is null && File is null;
}

/// <summary>
/// The symbol a query names, or why there is none. With a name that matches
/// several symbols, <see cref="Candidates"/> lists them and Symbol is null.
/// </summary>
internal sealed class SymbolResolution
{
    public ISymbol? Symbol { get; set; }
    public Project? Project { get; set; }

    /// <summary>For a position: the member or type the position is in.</summary>
    public ISymbol? Enclosing { get; set; }

    public IReadOnlyList<(ISymbol Symbol, Project Project)> Candidates { get; set; } = Array.Empty<(ISymbol, Project)>();
    public string? Note { get; set; }
}

/// <summary>
/// Looks up symbols, references and implementations in a Roslyn solution and
/// describes them for Claude. Knows nothing of Visual Studio, so that it can
/// be tried against a solution built in memory.
/// </summary>
internal static class RoslynSymbols
{
    private const int MaxCandidates = 20;
    private const int MaxDocumentationChars = 500;
    private const int MaxLineTextChars = 200;

    // "OrderService.SaveAsync(Order)": tells overloads apart without the
    // namespace noise of a fully qualified name.
    private static readonly SymbolDisplayFormat DisplayFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

    // "Shop.Orders.OrderService.SaveAsync": what a qualified name is matched against.
    private static readonly SymbolDisplayFormat QualifiedNameFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.CSharpErrorMessageFormat
        .WithMemberOptions(SymbolDisplayMemberOptions.None);

    /// <param name="currentContext">
    /// Picks the document of an open file in the project context the editor
    /// shows, for a file shared by several projects or target frameworks.
    /// </param>
    public static async Task<SymbolResolution> ResolveAsync(
        Solution solution, SymbolQuery query, Func<SourceTextContainer, DocumentId?>? currentContext, CancellationToken cancellationToken)
    {
        if (query.SymbolId is { } id)
            return await ResolveIdAsync(solution, id, cancellationToken).ConfigureAwait(false);

        if (query.File is { } file)
        {
            if (query.Line is not { } line)
                return new SymbolResolution { Note = "A file needs a line." };
            return await ResolvePositionAsync(solution, file, line, query.Column ?? 1, currentContext, cancellationToken)
                .ConfigureAwait(false);
        }

        if (query.Name is { } name)
            return await ResolveNameAsync(solution, name, cancellationToken).ConfigureAwait(false);

        return new SymbolResolution { Note = "Pass a symbolId, a name, or a file and line." };
    }

    /// <summary>
    /// An id Claude can pass back to name exactly this symbol, overload and
    /// all; null for locals and other symbols that have no declaration id.
    /// </summary>
    public static string? CreateId(ISymbol symbol, Project project)
    {
        var documentationId = DeclarationId(symbol);
        return documentationId is null ? null : documentationId + "|" + project.Id.Id.ToString("N");
    }

    public static async Task<object> DescribeAsync(ISymbol symbol, Project project, CancellationToken cancellationToken)
    {
        var declarations = new List<object>();
        foreach (var location in symbol.Locations.Where(l => l.IsInSource))
        {
            var position = await DescribeLocationAsync(project.Solution, location, cancellationToken).ConfigureAwait(false);
            declarations.Add(new { position.file, position.line, position.column });
        }

        return new
        {
            symbolId = CreateId(symbol, project),
            name = symbol.Name,
            display = symbol.ToDisplayString(DisplayFormat),
            kind = KindName(symbol),
            containingType = symbol.ContainingType?.ToDisplayString(TypeFormat),
            containingNamespace = symbol.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null,
            type = ValueType(symbol)?.ToDisplayString(TypeFormat),
            returnType = symbol is IMethodSymbol { MethodKind: not MethodKind.Constructor and not MethodKind.StaticConstructor } method
                ? method.ReturnType.ToDisplayString(TypeFormat)
                : null,
            parameters = Parameters(symbol)?.Select(p => new { name = p.Name, type = p.Type.ToDisplayString(TypeFormat) }).ToList(),
            accessibility = symbol.DeclaredAccessibility == Accessibility.NotApplicable
                ? null
                : symbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            modifiers = Modifiers(symbol),
            project = project.Name,
            // A symbol from a referenced assembly has no source to open.
            assembly = symbol.Locations.Any(l => l.IsInSource) ? null : symbol.ContainingAssembly?.Name,
            declarations,
            documentation = Summary(symbol, cancellationToken),
        };
    }

    /// <summary>
    /// The references to a symbol, sorted by file and position, one per place
    /// in the source: a file compiled for several target frameworks is
    /// searched once per framework, but each place counts once.
    /// </summary>
    public static async Task<object> FindReferencesAsync(
        ISymbol symbol, Project project, int skip, int maxResults, CancellationToken cancellationToken)
    {
        var solution = project.Solution;
        var referenced = await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false);

        var places = new Dictionary<(string File, int Start), ReferenceLocation>();
        foreach (var location in referenced.SelectMany(r => r.Locations))
        {
            if (location.Location.SourceTree?.FilePath is not { } path)
                continue;
            var key = (path.ToUpperInvariant(), location.Location.SourceSpan.Start);
            if (!places.ContainsKey(key))
                places.Add(key, location);
        }

        var ordered = places
            .OrderBy(p => p.Value.Location.SourceTree!.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Key.Start)
            .Select(p => p.Value)
            .ToList();

        var references = new List<object>();
        foreach (var location in ordered.Skip(skip).Take(maxResults))
        {
            var position = await DescribeLocationAsync(solution, location.Location, cancellationToken).ConfigureAwait(false);
            references.Add(new
            {
                position.file,
                position.line,
                position.column,
                position.text,
                // A candidate is a place the compiler could not bind for sure,
                // e.g. a call with an argument of the wrong type.
                candidate = location.IsCandidateLocation ? true : (bool?)null,
                // E.g. a foreach that calls GetEnumerator, or a deconstruction.
                @implicit = location.IsImplicit ? true : (bool?)null,
            });
        }

        return new
        {
            capturedAt = DateTimeOffset.Now.ToString("o"),
            symbol = Brief(symbol, project),
            total = ordered.Count,
            files = ordered.Select(l => l.Location.SourceTree!.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            skipped = skip,
            returned = references.Count,
            truncated = skip + references.Count < ordered.Count,
            references,
        };
    }

    public static async Task<object> FindImplementationsAsync(
        ISymbol symbol, Project project, int maxResults, CancellationToken cancellationToken)
    {
        var solution = project.Solution;
        IEnumerable<ISymbol> found;
        string? note = null;

        // As Go To Implementation: an interface and its members are
        // implemented, an abstract class is derived from, and an abstract or
        // virtual member is overridden.
        if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface } || symbol.ContainingType?.TypeKind == TypeKind.Interface)
        {
            found = await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else if (symbol is INamedTypeSymbol { TypeKind: TypeKind.Class, IsAbstract: true } abstractClass)
        {
            found = await SymbolFinder.FindDerivedClassesAsync(abstractClass, solution, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else if (symbol is not INamedTypeSymbol && (symbol.IsAbstract || symbol.IsVirtual || symbol.IsOverride))
        {
            found = await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            found = Array.Empty<ISymbol>();
            note = "Only interfaces and their members, abstract classes, and abstract, virtual or override members have implementations.";
        }

        // The same implementation found once per target framework.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = found
            .Where(s => s.Locations.Any(l => l.IsInSource))
            .Where(s => seen.Add(DeclarationId(s) ?? s.ToDisplayString(DisplayFormat)))
            .ToList();

        var implementations = new List<object>();
        foreach (var implementation in unique.Take(maxResults))
        {
            var location = implementation.Locations.First(l => l.IsInSource);
            var position = await DescribeLocationAsync(solution, location, cancellationToken).ConfigureAwait(false);
            implementations.Add(new
            {
                symbolId = ProjectOf(solution, location) is { } owner ? CreateId(implementation, owner) : null,
                display = implementation.ToDisplayString(DisplayFormat),
                kind = KindName(implementation),
                position.file,
                position.line,
                position.column,
            });
        }

        return new
        {
            capturedAt = DateTimeOffset.Now.ToString("o"),
            symbol = Brief(symbol, project),
            total = unique.Count,
            returned = implementations.Count,
            truncated = unique.Count > implementations.Count,
            note,
            implementations,
        };
    }

    public static object Brief(ISymbol symbol, Project project) => new
    {
        symbolId = CreateId(symbol, project),
        display = symbol.ToDisplayString(DisplayFormat),
        kind = KindName(symbol),
    };

    public static object Candidates(SymbolResolution resolution) => new
    {
        note = "The name matches several symbols. Pass the symbolId of the one you mean.",
        candidates = resolution.Candidates.Select(c => new
        {
            symbolId = CreateId(c.Symbol, c.Project),
            display = c.Symbol.ToDisplayString(DisplayFormat),
            kind = KindName(c.Symbol),
            file = c.Symbol.Locations.FirstOrDefault(l => l.IsInSource)?.SourceTree?.FilePath,
            line = c.Symbol.Locations.FirstOrDefault(l => l.IsInSource) is { } l
                ? l.GetLineSpan().StartLinePosition.Line + 1
                : (int?)null,
        }).ToList(),
    };

    public static string Display(ISymbol symbol) => symbol.ToDisplayString(DisplayFormat);

    public static string KindName(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol type => type.TypeKind switch
        {
            TypeKind.Class => type.IsRecord ? "record" : "class",
            TypeKind.Struct => type.IsRecord ? "record struct" : "struct",
            TypeKind.Interface => "interface",
            TypeKind.Enum => "enum",
            TypeKind.Delegate => "delegate",
            TypeKind.Module => "module",
            _ => "type",
        },
        IMethodSymbol method => method.MethodKind switch
        {
            MethodKind.Constructor or MethodKind.StaticConstructor => "constructor",
            MethodKind.UserDefinedOperator or MethodKind.Conversion => "operator",
            MethodKind.LocalFunction => "local function",
            MethodKind.PropertyGet or MethodKind.PropertySet => "accessor",
            _ => method.IsExtensionMethod ? "extension method" : "method",
        },
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IFieldSymbol { IsConst: true } => "constant",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "enum member",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        ILocalSymbol => "local",
        IParameterSymbol => "parameter",
        ITypeParameterSymbol => "type parameter",
        INamespaceSymbol => "namespace",
        _ => symbol.Kind.ToString().ToLowerInvariant(),
    };

    private static async Task<SymbolResolution> ResolveIdAsync(Solution solution, string id, CancellationToken cancellationToken)
    {
        var separator = id.LastIndexOf('|');
        var documentationId = separator < 0 ? id : id.Substring(0, separator);
        Guid.TryParse(separator < 0 ? null : id.Substring(separator + 1), out var projectGuid);

        // The project the id came from first; after a reload its id may be
        // gone, and the symbol is then looked for everywhere.
        var projects = solution.Projects
            .OrderBy(p => p.Id.Id == projectGuid ? 0 : 1)
            .Where(p => p.SupportsCompilation);
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is not null &&
                DocumentationCommentId.GetFirstSymbolForDeclarationId(documentationId, compilation) is { } symbol)
            {
                return new SymbolResolution { Symbol = symbol, Project = project };
            }
        }

        return new SymbolResolution { Note = "No symbol with this id in the solution; the code may have changed. Look it up again." };
    }

    private static async Task<SymbolResolution> ResolvePositionAsync(
        Solution solution, string file, int line, int column,
        Func<SourceTextContainer, DocumentId?>? currentContext, CancellationToken cancellationToken)
    {
        string path;
        try { path = Path.GetFullPath(file); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return new SymbolResolution { Note = $"Not a valid file path: {file}" };
        }

        var ids = solution.GetDocumentIdsWithFilePath(path);
        if (ids.IsEmpty)
            return new SymbolResolution { Note = "The file is not a C# or Visual Basic document of a project in the open solution." };

        // A file shared by several projects or target frameworks has one
        // document per project; the editor shows one of them. An open file's
        // text belongs to the editor's buffer, which knows which.
        var document = solution.GetDocument(ids[0])!;
        var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        if (ids.Length > 1 && currentContext?.Invoke(text.Container) is { } current && ids.Contains(current))
            document = solution.GetDocument(current)!;
        var project = document.Project;

        if (line < 1 || line > text.Lines.Count)
            return new SymbolResolution { Note = $"The file has {text.Lines.Count} lines." };
        var textLine = text.Lines[line - 1];
        var position = textLine.Start + Math.Max(0, Math.Min(column - 1, textLine.Span.Length));

        var symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false);

        // A caret just after a name, as after typing it.
        if (symbol is null && position > textLine.Start)
            symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, position - 1, cancellationToken).ConfigureAwait(false);

        var model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        var enclosing = model?.GetEnclosingSymbol(position, cancellationToken);

        // Inside a lambda or local function the enclosing symbol is that; the
        // member it is written in says more.
        while (enclosing is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction or MethodKind.LocalFunction })
            enclosing = enclosing.ContainingSymbol;
        if (enclosing is INamespaceSymbol)
            enclosing = null;

        return new SymbolResolution
        {
            Symbol = symbol,
            Project = project,
            Enclosing = enclosing,
            Note = symbol is null ? "No symbol at this position." : null,
        };
    }

    private static async Task<SymbolResolution> ResolveNameAsync(Solution solution, string name, CancellationToken cancellationToken)
    {
        // "SaveAsync(Order)" names an overload by its parameter types; the
        // lookup itself is by the simple name.
        var parameters = name.IndexOf('(') is var open and >= 0 ? name.Substring(open) : null;
        var qualified = (parameters is null ? name : name.Substring(0, name.IndexOf('('))).Trim();
        var simple = qualified.Substring(qualified.LastIndexOf('.') + 1);
        if (simple.Length == 0)
            return new SymbolResolution { Note = "Pass the symbol's name, e.g. OrderService.SaveAsync." };

        var matches = new List<(ISymbol Symbol, Project Project)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in solution.Projects)
        {
            var declarations = await SymbolFinder.FindSourceDeclarationsAsync(
                project, simple, ignoreCase: false, SymbolFilter.TypeAndMember, cancellationToken).ConfigureAwait(false);
            foreach (var symbol in declarations)
            {
                var qualifiedName = symbol.ToDisplayString(QualifiedNameFormat);
                if (!qualifiedName.Equals(qualified, StringComparison.Ordinal) &&
                    !qualifiedName.EndsWith("." + qualified, StringComparison.Ordinal))
                {
                    continue;
                }

                if (parameters is not null && !ParametersMatch(symbol, parameters))
                    continue;

                // The same declaration once per target framework.
                if (seen.Add(DeclarationId(symbol) ?? qualifiedName))
                    matches.Add((symbol, project));
            }
        }

        return matches.Count switch
        {
            0 => new SymbolResolution { Note = $"No type or member named {name} is declared in the solution's source." },
            1 => new SymbolResolution { Symbol = matches[0].Symbol, Project = matches[0].Project },
            _ => new SymbolResolution { Candidates = matches.Take(MaxCandidates).ToList() },
        };
    }

    private static async Task<(string? file, int line, int column, string? text)> DescribeLocationAsync(
        Solution solution, Location location, CancellationToken cancellationToken)
    {
        var span = location.GetLineSpan();
        string? text = null;
        if (location.SourceTree is { } tree)
        {
            var source = await tree.GetTextAsync(cancellationToken).ConfigureAwait(false);
            var line = span.StartLinePosition.Line;
            if (line < source.Lines.Count)
            {
                text = source.Lines[line].ToString().Trim();
                if (text.Length > MaxLineTextChars)
                    text = text.Substring(0, MaxLineTextChars) + "…";
            }
        }

        return (span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1, text);
    }

    /// <summary>
    /// "(Order, int)" against the symbol's parameter types, each written
    /// either qualified or not: Order and Shop.Orders.Order both match.
    /// </summary>
    private static bool ParametersMatch(ISymbol symbol, string parameters)
    {
        if (Parameters(symbol)?.ToList() is not { } actual)
            return false;

        var inner = parameters.Trim().TrimStart('(').TrimEnd(')');
        var wanted = inner.Trim().Length == 0
            ? Array.Empty<string>()
            : inner.Split(',').Select(p => NormalizeSpaces(p).Replace(" ", "")).ToArray();
        if (wanted.Length != actual.Count)
            return false;

        return wanted.Zip(actual, (w, p) =>
            w == p.Type.ToDisplayString(TypeFormat).Replace(" ", "") ||
            w == p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat).Replace(" ", "") ||
            w == p.Type.Name).All(match => match);
    }

    private static Project? ProjectOf(Solution solution, Location location)
        => location.SourceTree is { } tree ? solution.GetDocument(tree)?.Project : null;

    private static string? DeclarationId(ISymbol symbol)
    {
        // Locals, parameters and the like have none.
        if (symbol is not (INamespaceSymbol or INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol) ||
            symbol is IMethodSymbol { MethodKind: MethodKind.LocalFunction or MethodKind.AnonymousFunction })
        {
            return null;
        }

        try { return DocumentationCommentId.CreateDeclarationId(symbol); }
        catch (ArgumentException) { return null; }
    }

    private static ITypeSymbol? ValueType(ISymbol symbol) => symbol switch
    {
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        IEventSymbol @event => @event.Type,
        ILocalSymbol local => local.Type,
        IParameterSymbol parameter => parameter.Type,
        _ => null,
    };

    private static IEnumerable<IParameterSymbol>? Parameters(ISymbol symbol) => symbol switch
    {
        IMethodSymbol method => method.Parameters,
        IPropertySymbol { IsIndexer: true } indexer => indexer.Parameters,
        INamedTypeSymbol { DelegateInvokeMethod: { } invoke } => invoke.Parameters,
        _ => null,
    };

    private static List<string>? Modifiers(ISymbol symbol)
    {
        var modifiers = new List<string>();
        if (symbol.IsStatic && symbol is not INamespaceSymbol) modifiers.Add("static");
        if (symbol.IsAbstract && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Interface }) modifiers.Add("abstract");
        if (symbol.IsVirtual) modifiers.Add("virtual");
        if (symbol.IsOverride) modifiers.Add("override");
        if (symbol.IsSealed && symbol is not INamedTypeSymbol { TypeKind: TypeKind.Struct or TypeKind.Enum or TypeKind.Delegate }) modifiers.Add("sealed");
        if (symbol.IsExtern) modifiers.Add("extern");
        if (symbol is IMethodSymbol { IsAsync: true }) modifiers.Add("async");
        if (symbol is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true }) modifiers.Add("readonly");
        if (symbol is IFieldSymbol { IsConst: true }) modifiers.Add("const");
        return modifiers.Count == 0 ? null : modifiers;
    }

    /// <summary>The summary of the XML documentation, as plain text.</summary>
    private static string? Summary(ISymbol symbol, CancellationToken cancellationToken)
    {
        var xml = symbol.GetDocumentationCommentXml(cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        try
        {
            var summary = XElement.Parse(xml).Element("summary");
            if (summary is null)
                return null;

            // <see cref="T:Foo.Bar"/> reads as Bar.
            foreach (var reference in summary.Descendants().Where(e => e.Name == "see" || e.Name == "paramref" || e.Name == "typeparamref").ToList())
            {
                var target = (string?)reference.Attribute("cref") ?? (string?)reference.Attribute("name") ?? (string?)reference.Attribute("langword") ?? "";
                reference.ReplaceWith(target.Substring(target.IndexOf(':') + 1).Split('.').Last());
            }

            var text = NormalizeSpaces(summary.Value).Trim();
            return text.Length == 0 ? null
                : text.Length > MaxDocumentationChars ? text.Substring(0, MaxDocumentationChars) + "…"
                : text;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static string NormalizeSpaces(string text)
        => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
