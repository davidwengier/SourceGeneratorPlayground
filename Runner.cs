using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using MonoTouch.Dialog.Utilities;

#nullable enable

namespace SourceGeneratorPlayground
{
    internal class Runner : IRunner
    {
        private static readonly Dictionary<string, MetadataReference>? s_references = new();

        private static readonly LRUCache<string, ImmutableArray<ISourceGenerator>> s_sourceGeneratorCache = new(entryLimit: 10);

        private readonly string _baseUri;

        public string ErrorText { get; private set; } = "";
        public string? GeneratorOutput { get; private set; } = "";
        public string ProgramOutput { get; private set; } = "";

        public Runner(NavigationManager navigationManager)
        {
            _baseUri = navigationManager.BaseUri;
        }

        public async Task RunAsync(string code, string generator, CancellationToken cancellationToken)
        {
            await UpdateReferences(_baseUri);

            this.ProgramOutput = "";
            this.GeneratorOutput = "";
            this.ErrorText = "";

            if (!TryCompileGenerator(generator, out var errorCompilingGenerator, out var generatorInstances, cancellationToken))
            {
                this.ErrorText = errorCompilingGenerator;
                return;
            }

            if (!TryCompileUserCode(code, generatorInstances, out var errorCompilingUserCode, out var programAssembly, out var generatorOutput, cancellationToken))
            {
                this.ErrorText = errorCompilingUserCode;
                this.GeneratorOutput = generatorOutput;
                return;
            }

            this.GeneratorOutput = generatorOutput;
            var (success, output) = await TryExecuteProgramAsync(programAssembly);
            if (!success)
            {
                this.ErrorText = output;
            }
            else
            {
                this.ProgramOutput = output;
            }
        }

        private static bool TryCompileGenerator(
            string code, 
            [NotNullWhen(false)] out string? error, 
            [NotNullWhen(true)] out ImmutableArray<ISourceGenerator> generators, 
            CancellationToken cancellationToken)
        {
            error = default;
            generators = default;

            if (string.IsNullOrWhiteSpace(code))
            {
                error = "Need more input for the generator code!";
                return false;
            }

            var normalizedCode = CSharpSyntaxTree.ParseText(code).GetRoot().NormalizeWhitespace().ToFullString();
            var cacheHit = s_sourceGeneratorCache[normalizedCode];
            if (cacheHit != null)
            {
                generators = cacheHit;
                return true;
            }

            var generatorTree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(kind: SourceCodeKind.Regular), "Generator.cs", cancellationToken: cancellationToken);

            var generatorCompilation = CSharpCompilation.Create("Generator", new[] { generatorTree }, s_references.Values, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            error = GetErrors("Error(s) compiling generator:", generatorCompilation.GetDiagnostics(cancellationToken: cancellationToken));
            if (error != null)
            {
                return false;
            }

            var generatorAssembly = GetAssembly(generatorCompilation, "generator", out error, cancellationToken: cancellationToken);
            if (error != null)
            {
                return false;
            }
            if (generatorAssembly == null)
            {
                error = "Unknown error emitting generator.";
                return false;
            }


            generators = generatorAssembly.GetTypes()
                .Where(t => !t.GetTypeInfo().IsInterface && !t.GetTypeInfo().IsAbstract && !t.GetTypeInfo().ContainsGenericParameters)
                .Where(t => typeof(ISourceGenerator).IsAssignableFrom(t))
                .Select(t => Activator.CreateInstance(t))
                .OfType<ISourceGenerator>()
                .ToImmutableArray();

            if (generators.Length == 0)
            {
                error = "Could not instantiate source generator. Types in assembly:" + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, (object)generatorAssembly.GetTypes());
                return false;
            }

            s_sourceGeneratorCache[normalizedCode] = generators;
            return true;
        }

        private static bool TryCompileUserCode
            (string code, 
            ImmutableArray<ISourceGenerator> generators, 
            [NotNullWhen(false)] out string? error,
            [NotNullWhen(true)] out Assembly? programAssembly, 
            out string? generatorOutput, CancellationToken cancellationToken)
        {
            error = default;
            programAssembly = default;
            generatorOutput = default;

            if (string.IsNullOrWhiteSpace(code))
            {
                error = "Need more input for the user code!";
                return false;
            }

            var codeTree = CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(kind: SourceCodeKind.Regular), "Program.cs", cancellationToken: cancellationToken);
            var codeCompilation = CSharpCompilation.Create("Program", new SyntaxTree[] { codeTree }, s_references.Values, new CSharpCompilationOptions(OutputKind.ConsoleApplication));

            var driver = CSharpGeneratorDriver.Create(generators);

            driver.RunGeneratorsAndUpdateCompilation(codeCompilation, out Compilation? outputCompilation, out ImmutableArray<Diagnostic> diagnostics, cancellationToken: cancellationToken);

            error = GetErrors("Error(s) running generator:", diagnostics, false);
            if (error != null)
            {
                return false;
            }

            var output = new StringBuilder();
            var trees = outputCompilation.SyntaxTrees.Where(t => t != codeTree).OrderBy(t => t.FilePath).ToArray();
            foreach (var tree in trees)
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

                output.AppendLine(tree.WithRootAndOptions(tree.GetRoot(cancellationToken: cancellationToken).NormalizeWhitespace(), tree.Options).ToString());
            }
            if (output.Length == 0)
            {
                output.AppendLine("< No source generated >");
            }
            generatorOutput = output.ToString();

            error = GetErrors("Error(s) compiling program:", outputCompilation.GetDiagnostics(cancellationToken: cancellationToken));
            if (error != null)
            {
                return false;
            }

            programAssembly = GetAssembly(outputCompilation, "program", out error, cancellationToken: cancellationToken);
            if (error != null)
            {
                return false;
            }
            if (programAssembly == null)
            {
                error = "Unknown error emitting program.";
                return false;
            }
            return true;
        }

        private static async Task<(bool Success, string Output)> TryExecuteProgramAsync(Assembly programAssembly)
        {
            string? error = default;
            string? output = default;
            var program = programAssembly.GetTypes().FirstOrDefault(t => t.Name == "Program");
            if (program == null)
            {
                error = "Error executing program:" + Environment.NewLine + Environment.NewLine + "Could not find type \"Program\" in program.";
                return (Success: false, Output: error);
            }

            var main = program.GetMethod("Main", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (main == null)
            {
                error = "Error executing program:" + Environment.NewLine + Environment.NewLine + "Could not find static method \"Main\" in program.";
                return (Success: false, Output: error);
            }

            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);

                int paramCount = main.GetParameters().Length;
                if (main.ReturnType == typeof(void))
                {
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
                        error = "Error executing program:" + Environment.NewLine + Environment.NewLine + "Method \"Main\" must have 0 or 1 parameters.";
                        return (Success: false, Output: error);
                    }
                }
                else if (main.ReturnType == typeof(Task))
                {
                    if (paramCount == 1)
                    {
                        var task = main.Invoke(null, new object?[] { null }) as Task;
                        await task!;
                    }
                    else if (paramCount == 0)
                    {
                        var task = main.Invoke(null, null) as Task;
                        await task!;
                    }
                    else
                    {
                        error = "Error executing program:" + Environment.NewLine + Environment.NewLine + "Method \"Main\" must have 0 or 1 parameters.";
                        return (Success: false, Output: error);
                    }
                }
                else
                {
                    error = "Error executing program:" + Environment.NewLine + Environment.NewLine + "Method \"Main\" must have either void or Task return type.";
                    return (Success: false, Output: error);
                }

                output = writer.ToString();

                if (string.IsNullOrEmpty(output))
                {
                    output = "< No program output >";
                }
            }
            catch (Exception ex)
            {
                error = writer.ToString() + "\n\nError executing program:" + Environment.NewLine + Environment.NewLine + ex.ToString();
                return (Success: false, Output: error);
            }
            return (Success: true, Output: output);
        }

        private static Assembly? GetAssembly(Compilation generatorCompilation, string name, out string? errors, CancellationToken cancellationToken)
        {
            try
            {
                using var generatorStream = new MemoryStream();
                var result = generatorCompilation.Emit(generatorStream, cancellationToken: cancellationToken);
                if (result == null)
                {
                    errors = "Failed to compile with unknown error";
                    return null;
                }
                if (!result.Success)
                {
                    errors = GetErrors($"Error emitting {name}:", result.Diagnostics, false);
                    return null;
                }
                generatorStream.Seek(0, SeekOrigin.Begin);
                errors = null;
                return Assembly.Load(generatorStream.ToArray());
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

        private static async Task<List<MetadataReference>> GetReferences(string baseUri)
        {
            Assembly[]? refs = AppDomain.CurrentDomain.GetAssemblies();
            var client = new HttpClient
            {
                BaseAddress = new Uri(baseUri)
            };

            var references = new List<MetadataReference>();

            foreach (Assembly? reference in refs.Where(x => !x.IsDynamic && !string.IsNullOrWhiteSpace(x.Location)))
            {
                Stream? stream = await client.GetStreamAsync($"_framework/_bin/{reference.Location}");
                references.Add(MetadataReference.CreateFromStream(stream));

            }

            return references;
        }

        private async Task UpdateReferences(string baseUri)
        {
            Assembly[]? refs = AppDomain.CurrentDomain.GetAssemblies();
            var client = new Lazy<HttpClient>(() => new HttpClient
            {
                BaseAddress = new Uri(baseUri)
            });

            foreach (Assembly? reference in refs.Where(x => !x.IsDynamic && !string.IsNullOrWhiteSpace(x.Location)))
            {
                if (!s_references.ContainsKey(reference.Location))
                {
                    Stream? stream = await client.Value.GetStreamAsync($"_framework/_bin/{reference.Location}");
                    s_references.Add(reference.Location, MetadataReference.CreateFromStream(stream));
                }

            }
        }
    }
}
