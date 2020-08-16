using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace SourceGenerator
{
    [Generator]
    public class DISourceGenerator : ISourceGenerator
    {
        public void Initialize(InitializationContext context)
        {
        }

        public void Execute(SourceGeneratorContext context)
        {
            Compilation? compilation = context.Compilation;

            string stub = @"
namespace DI
{ 
    public static class ServiceLocator
    {
        public static T GetService<T>()
        {
            return default;
        }
    }
}
";

            var options = (compilation as CSharpCompilation)?.SyntaxTrees[0].Options as CSharpParseOptions;
            compilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(SourceText.From(stub, Encoding.UTF8), options));

            ImmutableArray<Diagnostic> diags = compilation.GetDiagnostics();

            var sourceBuilder = new StringBuilder();

            var services = new List<Service>();

            INamedTypeSymbol? serviceLocatorClass = compilation.GetTypeByMetadataName("DI.ServiceLocator")!;
            INamedTypeSymbol? iEnumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1")!.ConstructUnboundGenericType();
            INamedTypeSymbol? listOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!;

            var knownTypes = new KnownTypes(iEnumerableOfT, listOfT);

            foreach (SyntaxTree? tree in compilation.SyntaxTrees)
            {
                SemanticModel? semanticModel = compilation.GetSemanticModel(tree);
                IEnumerable<INamedTypeSymbol>? typesToCreate = from i in tree.GetRoot().DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                                                               let symbol = semanticModel.GetSymbolInfo(i).Symbol as IMethodSymbol
                                                               where symbol != null
                                                               where SymbolEqualityComparer.Default.Equals(symbol.ContainingType, serviceLocatorClass)
                                                               select symbol.ReturnType as INamedTypeSymbol;

                foreach (INamedTypeSymbol? typeToCreate in typesToCreate)
                {
                    Generate(context, typeToCreate, compilation, services, knownTypes);
                }
            }

            sourceBuilder.AppendLine(@"
using System;

namespace DI
{ 
    public static class ServiceLocator
    {");
            var fields = new List<Service>();
            GenerateFields(sourceBuilder, services, fields, services.Count > 1);

            sourceBuilder.AppendLine(@"
        public static T GetService<T>()
        {");

            foreach (Service? service in services)
            {
                if (service != services.Last())
                {
                    sourceBuilder.AppendLine("if (typeof(T) == typeof(" + service.Type + "))");
                    sourceBuilder.AppendLine("{");
                }
                sourceBuilder.AppendLine($"    return (T)(object){GetTypeConstruction(service, service.IsTransient ? new List<Service>() : fields, services.Count > 1)};");
                if (service != services.Last())
                {
                    sourceBuilder.AppendLine("}");
                }
            }

            if (services.Count == 0)
            {
                sourceBuilder.AppendLine("throw new System.InvalidOperationException(\"This code is unreachable.\");");
            }
            sourceBuilder.AppendLine(@"
        }
    }
}");

            context.AddSource("ServiceLocator.cs", SourceText.From(sourceBuilder.ToString(), Encoding.UTF8));
        }

        private static void GenerateFields(StringBuilder sourceBuilder, List<Service> services, List<Service> fields, bool lazy)
        {
            foreach (Service? service in services)
            {
                GenerateFields(sourceBuilder, service.ConstructorArguments, fields, lazy);
                if (!service.IsTransient)
                {
                    if (fields.Any(f => SymbolEqualityComparer.Default.Equals(f.ImplementationType, service.ImplementationType)))
                    {
                        continue;
                    }
                    service.VariableName = GetVariableName(service, fields);
                    sourceBuilder.Append($"private static ");
                    if (lazy)
                    {
                        sourceBuilder.Append("Lazy<");
                    }
                    sourceBuilder.Append(service.Type);
                    if (lazy)
                    {
                        sourceBuilder.Append(">");
                    }
                    sourceBuilder.AppendLine($" {service.VariableName} = {GetTypeConstruction(service, fields, lazy)};");
                    fields.Add(service);
                }
            }
        }

        private static string GetTypeConstruction(Service service, List<Service> fields, bool lazy)
        {
            var sb = new StringBuilder();

            Service? field = fields.FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.ImplementationType, service.ImplementationType));
            if (field != null)
            {
                sb.Append(field.VariableName);
                if (lazy)
                {
                    sb.Append(".Value");
                }
            }
            else
            {
                if (lazy)
                {
                    sb.Append("new Lazy<");
                    sb.Append(service.Type);
                    sb.Append(">(() => ");
                }
                sb.Append("new ");
                sb.Append(service.ImplementationType);
                sb.Append('(');
                if (service.UseCollectionInitializer)
                {
                    sb.Append(')');
                    sb.Append('{');
                }
                bool first = true;
                foreach (Service? arg in service.ConstructorArguments)
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    sb.Append(GetTypeConstruction(arg, fields, lazy));
                    first = false;
                }
                if (service.UseCollectionInitializer)
                {
                    sb.Append('}');
                }
                else
                {
                    sb.Append(')');
                }
                if (lazy)
                {
                    sb.Append(")");
                }
            }
            return sb.ToString();
        }

        private static string GetVariableName(Service service, List<Service> fields)
        {
            string typeName = service.ImplementationType.ToString().Replace("<", "").Replace(">", "").Replace("?", "");

            string[] parts = typeName.Split('.');
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                string? candidate = string.Join("", parts.Skip(i));
                candidate = "_" + char.ToLowerInvariant(candidate[0]) + candidate.Substring(1);
                if (!fields.Any(f => string.Equals(f.VariableName, candidate, StringComparison.Ordinal)))
                {
                    typeName = candidate;
                    break;
                }
            }
            return typeName;
        }

        private static void Generate(SourceGeneratorContext context, INamedTypeSymbol typeToCreate, Compilation compilation, List<Service> services, KnownTypes knownTypes)
        {
            typeToCreate = (INamedTypeSymbol)typeToCreate.WithNullableAnnotation(default);

            if (services.Any(s => SymbolEqualityComparer.Default.Equals(s.Type, typeToCreate)))
            {
                return;
            }

            if (typeToCreate.IsGenericType && SymbolEqualityComparer.Default.Equals(typeToCreate.ConstructUnboundGenericType(), knownTypes.IEnumerableOfT))
            {
                ITypeSymbol? typeToFind = typeToCreate.TypeArguments[0];
                IEnumerable<INamedTypeSymbol>? types = FindImplementations(typeToFind, compilation);

                INamedTypeSymbol? list = knownTypes.ListOfT.Construct(typeToFind);

                var listService = new Service(typeToCreate);
                services.Add(listService);
                listService.ImplementationType = list;
                listService.UseCollectionInitializer = true;

                foreach (INamedTypeSymbol? thingy in types)
                {
                    Generate(context, thingy, compilation, listService.ConstructorArguments, knownTypes);
                }
            }
            else
            {
                INamedTypeSymbol? realType = typeToCreate.IsAbstract ? FindImplementation(typeToCreate, compilation) : typeToCreate;

                if (realType == null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("DIGEN001", "Type not found", $"Could not find an implementation of '{typeToCreate}'.", "DI.ServiceLocator", DiagnosticSeverity.Error, true), Location.None));
                }

                var service = new Service(typeToCreate);
                services.Add(service);
                service.ImplementationType = realType;

                IMethodSymbol? constructor = realType?.Constructors.FirstOrDefault();
                if (constructor != null)
                {
                    foreach (IParameterSymbol? parametr in constructor.Parameters)
                    {
                        if (parametr.Type is INamedTypeSymbol paramType)
                        {
                            Generate(context, paramType, compilation, service.ConstructorArguments, knownTypes);
                        }
                    }
                }
            }
        }

        private static INamedTypeSymbol? FindImplementation(ITypeSymbol typeToCreate, Compilation compilation)
        {
            return FindImplementations(typeToCreate, compilation).FirstOrDefault();
        }

        private static IEnumerable<INamedTypeSymbol> FindImplementations(ITypeSymbol typeToFind, Compilation compilation)
        {
            foreach (INamedTypeSymbol? x in GetAllTypes(compilation.GlobalNamespace.GetNamespaceMembers()))
            {
                if (!x.IsAbstract && x.Interfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, typeToFind)))
                {
                    yield return x;
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> GetAllTypes(IEnumerable<INamespaceSymbol> namespaces)
        {
            foreach (INamespaceSymbol? ns in namespaces)
            {
                foreach (INamedTypeSymbol? t in ns.GetTypeMembers())
                {
                    yield return t;
                }

                foreach (INamedTypeSymbol? subType in GetAllTypes(ns.GetNamespaceMembers()))
                {
                    yield return subType;
                }
            }
        }

        private class KnownTypes
        {
            public INamedTypeSymbol IEnumerableOfT;
            public INamedTypeSymbol ListOfT;

            public KnownTypes(INamedTypeSymbol iEnumerableOfT, INamedTypeSymbol listOfT)
            {
                IEnumerableOfT = iEnumerableOfT;
                ListOfT = listOfT;
            }
        }

        private class Service
        {
            public Service(INamedTypeSymbol typeToCreate)
            {
                this.Type = typeToCreate;
            }

            public INamedTypeSymbol Type { get; set; }
            public INamedTypeSymbol ImplementationType { get; internal set; } = null!;
            public List<Service> ConstructorArguments { get; internal set; } = new List<Service>();
            public bool IsTransient { get; internal set; }
            public bool UseCollectionInitializer { get; internal set; }
            public string? VariableName { get; internal set; }
        }
    }
}
