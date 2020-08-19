using System;
using System.Collections.Generic;
using System.Linq;
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
        private const bool SimplifyFieldNames = true;
        private const bool UseLazyWhenMultipleServices = true;

        private const string ServiceLocatorStub = @"
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
        private const string TransientAttribute = @"
using System;

namespace DI
{
    [AttributeUsage(AttributeTargets.Interface)]
    public class TransientAttribute : Attribute
    {
    }
}
";

        public void Initialize(InitializationContext context)
        {
        }

        public void Execute(SourceGeneratorContext context)
        {
            Compilation? compilation = context.Compilation;

            compilation = GenerateHelperClasses(context);

            INamedTypeSymbol? serviceLocatorClass = compilation.GetTypeByMetadataName("DI.ServiceLocator")!;
            INamedTypeSymbol? transientAttribute = compilation.GetTypeByMetadataName("DI.TransientAttribute")!;

            INamedTypeSymbol? iEnumerableOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1")!.ConstructUnboundGenericType();
            INamedTypeSymbol? listOfT = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!;

            var knownTypes = new KnownTypes(iEnumerableOfT, listOfT, transientAttribute);

            var services = new List<Service>();
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
                    CollectServices(context, typeToCreate, compilation, services, knownTypes);
                }
            }

            GenerateServiceLocator(context, services);
        }

        private static Compilation GenerateHelperClasses(SourceGeneratorContext context)
        {
            var compilation = context.Compilation;

            var options = (compilation as CSharpCompilation)?.SyntaxTrees[0].Options as CSharpParseOptions;
            var tempCompilation = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(SourceText.From(ServiceLocatorStub, Encoding.UTF8), options))
                                             .AddSyntaxTrees(CSharpSyntaxTree.ParseText(SourceText.From(TransientAttribute, Encoding.UTF8), options));

            context.AddSource("TransientAttribute.cs", SourceText.From(TransientAttribute, Encoding.UTF8));

            return tempCompilation;
        }

        private static void GenerateServiceLocator(SourceGeneratorContext context, List<Service> services)
        {
            var sourceBuilder = new StringBuilder();

            bool generateLazies = UseLazyWhenMultipleServices && services.Count > 1;

            sourceBuilder.AppendLine(@"
using System;

namespace DI
{ 
    public static class ServiceLocator
    {");
            var fields = new List<Service>();
            GenerateFields(sourceBuilder, services, fields, generateLazies);

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
                sourceBuilder.AppendLine($"    return (T)(object){GetTypeConstruction(service, service.IsTransient ? new List<Service>() : fields, !service.IsTransient && generateLazies)};");
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
                    if (SimplifyFieldNames)
                    {
                        break;
                    }
                }
            }
            return typeName;
        }

        private static void CollectServices(SourceGeneratorContext context, INamedTypeSymbol typeToCreate, Compilation compilation, List<Service> services, KnownTypes knownTypes)
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
                    CollectServices(context, thingy, compilation, listService.ConstructorArguments, knownTypes);
                }
            }
            else
            {
                INamedTypeSymbol? realType = typeToCreate.IsAbstract ? FindImplementation(typeToCreate, compilation) : typeToCreate;

                if (realType == null)
                {
                    context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("DIGEN001", "Type not found", $"Could not find an implementation of '{typeToCreate}'.", "DI.ServiceLocator", DiagnosticSeverity.Error, true), Location.None));
                    return;
                }

                var service = new Service(typeToCreate);
                services.Add(service);
                service.ImplementationType = realType;
                service.IsTransient = typeToCreate.GetAttributes().Any(c => SymbolEqualityComparer.Default.Equals(c.AttributeClass, knownTypes.TransientAttribute));

                IMethodSymbol? constructor = realType?.Constructors.FirstOrDefault();
                if (constructor != null)
                {
                    foreach (IParameterSymbol? parametr in constructor.Parameters)
                    {
                        if (parametr.Type is INamedTypeSymbol paramType)
                        {
                            CollectServices(context, paramType, compilation, service.ConstructorArguments, knownTypes);
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
            public INamedTypeSymbol TransientAttribute;

            public KnownTypes(INamedTypeSymbol iEnumerableOfT, INamedTypeSymbol listOfT, INamedTypeSymbol transientAttribute)
            {
                IEnumerableOfT = iEnumerableOfT;
                ListOfT = listOfT;
                TransientAttribute = transientAttribute;
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
