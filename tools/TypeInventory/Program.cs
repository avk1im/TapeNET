using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;


if (args.Length == 0)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  TypeInventory <rootPath> [--format text|json]");
    return;
}

string rootPath = args[0];
string format = "text";

for (int i = 1; i < args.Length - 1; i++)
{
    if (args[i] == "--format")
    {
        format = args[i + 1].ToLowerInvariant();
    }
}

if (!Directory.Exists(rootPath))
{
    Console.Error.WriteLine($"Directory not found: {rootPath}");
    return;
}

var files = Directory
    .EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories)
    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
    .ToList();

var results = new List<FileInventory>();

foreach (string file in files)
{
    try
    {
        string source = File.ReadAllText(file);

        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var root = syntaxTree.GetCompilationUnitRoot();

        var fileInventory = new FileInventory
        {
            File = Path.GetFullPath(file)
        };

        foreach (var node in root.DescendantNodes())
        {
            if (!IsTopLevelType(node))
                continue;

            switch (node)
            {
                case ClassDeclarationSyntax c:
                    fileInventory.Types.Add(CreateTypeInfo(
                        "class",
                        c.Identifier.Text,
                        c.TypeParameterList?.ToString(),
                        GetNamespace(c)));
                    break;

                case StructDeclarationSyntax s:
                    fileInventory.Types.Add(CreateTypeInfo(
                        "struct",
                        s.Identifier.Text,
                        s.TypeParameterList?.ToString(),
                        GetNamespace(s)));
                    break;

                case RecordDeclarationSyntax r:
                    fileInventory.Types.Add(CreateTypeInfo(
                        r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
                            ? "record struct"
                            : "record",
                        r.Identifier.Text,
                        r.TypeParameterList?.ToString(),
                        GetNamespace(r)));
                    break;

                case EnumDeclarationSyntax e:
                    fileInventory.Types.Add(CreateTypeInfo(
                        "enum",
                        e.Identifier.Text,
                        null,
                        GetNamespace(e)));
                    break;
            }
        }

        if (fileInventory.Types.Count > 0)
            results.Add(fileInventory);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error reading {file}: {ex.Message}");
    }
}

switch (format)
{
    case "json":
        Console.WriteLine(
            JsonSerializer.Serialize(
                results,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        break;

    default:
        WriteText(results);
        break;
}

static void WriteText(IEnumerable<FileInventory> files)
{
    foreach (var file in files)
    {
        Console.WriteLine(file.File);

        foreach (var type in file.Types)
        {
            if (!string.IsNullOrWhiteSpace(type.Namespace))
            {
                Console.WriteLine(
                    $"  {type.Kind}: {type.Name} [{type.Namespace}]");
            }
            else
            {
                Console.WriteLine(
                    $"  {type.Kind}: {type.Name}");
            }
        }

        Console.WriteLine();
    }
}

static bool IsTopLevelType(SyntaxNode node)
{
    if (node is not BaseTypeDeclarationSyntax)
        return false;

    return node.Parent is CompilationUnitSyntax
        || node.Parent is NamespaceDeclarationSyntax
        || node.Parent is FileScopedNamespaceDeclarationSyntax;
}

static string? GetNamespace(SyntaxNode node)
{
    for (SyntaxNode? current = node.Parent;
         current != null;
         current = current.Parent)
    {
        switch (current)
        {
            case NamespaceDeclarationSyntax ns:
                return ns.Name.ToString();

            case FileScopedNamespaceDeclarationSyntax fns:
                return fns.Name.ToString();
        }
    }

    return null;
}

static TypeInfo CreateTypeInfo(
    string kind,
    string name,
    string? genericSuffix,
    string? ns)
{
    return new TypeInfo
    {
        Kind = kind,
        Name = string.IsNullOrEmpty(genericSuffix)
            ? name
            : name + genericSuffix,
        Namespace = ns
    };
}

#pragma warning disable CA1050 // CA1050: Declare types in namespaces

public sealed class FileInventory
{
    public string File { get; set; } = string.Empty;

    public List<TypeInfo> Types { get; } = [];
}

public sealed class TypeInfo
{
    public string Kind { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? Namespace { get; set; }
}

#pragma warning restore CA1050
