using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SourceGeneratorPlayground
{
    internal class Runner : IDisposable
    {
        private static readonly List<MetadataReference> s_references = GetReferences();

        private readonly AssemblyLoadContext _context;
        private readonly string _code;
        private readonly string _generator;

        public string ErrorText { get; private set; } = "";
        public string GeneratorOutput { get; private set; } = "";
        public string ProgramOutput { get; private set; } = "";

        public Runner(string code, string generator)
        {
            _code = code;
            _generator = generator;
            _context = new AssemblyLoadContext("GeneratorContext", true);
        }

        internal void Run()
        {
            this.ProgramOutput = "";
            this.GeneratorOutput = "";
            this.ErrorText = "";

            if (string.IsNullOrWhiteSpace(_code) || string.IsNullOrWhiteSpace(_generator))
            {
                this.ErrorText = "Need more input!";
                return;
            }

            SyntaxTree? generatorTree = CSharpSyntaxTree.ParseText(_generator, new CSharpParseOptions(kind: SourceCodeKind.Regular), "Generator.cs");

            var generatorCompilation = CSharpCompilation.Create("Generator", new[] { generatorTree }, s_references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            string? errors = GetErrors("Error(s) compiling generator:", generatorCompilation.GetDiagnostics());
            if (errors != null)
            {
                this.ErrorText = errors;
                return;
            }

            Assembly? generatorAssembly = GetAssembly(generatorCompilation, "generator", out errors);
            if (errors != null)
            {
                this.ErrorText = errors;
                return;
            }
            if (generatorAssembly == null)
            {
                this.ErrorText = "Unknown error emiting generator.";
                return;
            }

            var generatorInstances = generatorAssembly.GetTypes()
                .Where(t => !t.GetTypeInfo().IsInterface && !t.GetTypeInfo().IsAbstract && !t.GetTypeInfo().ContainsGenericParameters)
                .Where(t => typeof(ISourceGenerator).IsAssignableFrom(t))
                .Select(t => Activator.CreateInstance(t))
                .OfType<ISourceGenerator>()
                .ToImmutableArray();

            if (generatorInstances.Length == 0)
            {
                this.ErrorText = "Could not instantiate source generator. Types in assembly:" + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, (object)generatorAssembly.GetTypes());
                return;
            }

            SyntaxTree? codeTree = CSharpSyntaxTree.ParseText(_code, new CSharpParseOptions(kind: SourceCodeKind.Regular), "Program.cs");
            var codeCompilation = CSharpCompilation.Create("Program", new SyntaxTree[] { codeTree }, s_references, new CSharpCompilationOptions(OutputKind.ConsoleApplication));

            var driver = new CSharpGeneratorDriver(codeCompilation.SyntaxTrees[0].Options,
                                                   generatorInstances,
                                                   null!, // https://github.com/dotnet/roslyn/issues/46847
                                                   ImmutableArray<AdditionalText>.Empty);

            driver.RunFullGeneration(codeCompilation, out Compilation? outputCompilation, out ImmutableArray<Diagnostic> diagnostics);

            errors = GetErrors("Error(s) running generator:", diagnostics, false);
            if (errors != null)
            {
                this.ErrorText = errors;
                return;
            }

            var output = new StringBuilder();
            SyntaxTree[]? trees = outputCompilation.SyntaxTrees.Where(t => t != codeTree).OrderBy(t => t.FilePath).ToArray();
            foreach (SyntaxTree? tree in trees)
            {
                if (output.Length > 0)
                {
                    output.AppendLine().AppendLine();
                }

                if (trees.Length > 1)
                {
                    output.AppendLine(tree.FilePath);
                    output.AppendLine(new string('-', 50));
                }

                output.AppendLine(tree.WithRootAndOptions(tree.GetRoot().NormalizeWhitespace(), tree.Options).ToString());
            }
            if (output.Length == 0)
            {
                output.AppendLine("< No source generated >");
            }
            this.GeneratorOutput = output.ToString();

            errors = GetErrors("Error(s) compiling program:", outputCompilation.GetDiagnostics());
            if (errors != null)
            {
                this.ErrorText = errors;
                return;
            }

            Assembly? programAssembly = GetAssembly(outputCompilation, "program", out errors);
            if (errors != null)
            {
                this.ErrorText = errors;
                return;
            }
            if (programAssembly == null)
            {
                this.ErrorText = "Unknown error emiting program.";
                return;
            }

            ExecuteProgram(programAssembly);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "<Pending>")]
        private void ExecuteProgram(Assembly programAssembly)
        {
            Type? program = programAssembly.GetTypes().FirstOrDefault(t => t.Name == "Program");
            if (program == null)
            {
                this.ErrorText = "Error executing program:" + Environment.NewLine + Environment.NewLine + "Could not find type \"Program\" in program.";
                return;
            }

            MethodInfo? main = program.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (main == null)
            {
                this.ErrorText = "Error executing program:" + Environment.NewLine + Environment.NewLine + "Could not find static method \"Main\" in program.";
                return;
            }

            try
            {
                using var writer = new StringWriter();
                Console.SetOut(writer);

                int paramCount = main.GetParameters().Length;
                if (paramCount == 1)
                {
                    main.Invoke(null, new object?[] { null });
                }
                else if (paramCount == 0)
                {
                    main.Invoke(null, null);
                }
                else
                {
                    this.ErrorText = "Error executing program:" + Environment.NewLine + Environment.NewLine + "Method \"Main\" must have 0 or 1 parameters.";
                    return;
                }

                this.ProgramOutput = writer.ToString();

                if (string.IsNullOrEmpty(this.ProgramOutput))
                {
                    this.ProgramOutput = "< No program output >";
                }
            }
            catch (Exception ex)
            {
                this.ErrorText = "Error executing program:" + Environment.NewLine + Environment.NewLine + ex.ToString();
                return;
            }
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "<Pending>")]
        private Assembly? GetAssembly(Compilation generatorCompilation, string name, out string? errors)
        {
            try
            {
                using var generatorStream = new MemoryStream();
                Microsoft.CodeAnalysis.Emit.EmitResult? result = generatorCompilation.Emit(generatorStream);
                if (!result.Success)
                {
                    errors = GetErrors($"Error emiting {name}:", result.Diagnostics, false);
                    return null;
                }
                generatorStream.Seek(0, SeekOrigin.Begin);
                errors = null;
                return _context.LoadFromStream(generatorStream);
            }
            catch (Exception ex)
            {
                errors = ex.ToString();
                return null;
            }
        }

        private static string? GetErrors(string header, IEnumerable<Diagnostic> diagnostics, bool errorsOnly = true)
        {
            IEnumerable<Diagnostic>? errors = diagnostics.Where(d => !errorsOnly || d.Severity == DiagnosticSeverity.Error);

            if (!errors.Any())
            {
                return null;
            }

            return header + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, errors);
        }

        private static List<MetadataReference> GetReferences()
        {
            var references = new List<MetadataReference>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly? assembly in assemblies)
            {
                if (!assembly.IsDynamic)
                {
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
                }
            }

            return references;
        }

        public void Dispose()
        {
            _context?.Unload();
        }
    }
}
