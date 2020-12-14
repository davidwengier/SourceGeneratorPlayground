﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

#nullable enable

namespace SourceGeneratorPlayground
{
    public class Runner
    {
        private static List<MetadataReference>? s_references;

        private readonly string _code;
        private readonly string _generator;

        public string ErrorText { get; private set; } = "";
        public string GeneratorOutput { get; private set; } = "";
        public string ProgramOutput { get; private set; } = "";

        public Runner(string code, string generator)
        {
            _code = code;
            _generator = generator;
        }

        internal async Task Run(string baseUri)
        {
            if (s_references == null)
            {
                //s_references = await GetReferences(baseUri);
                s_references = await GetReferencesFromBootDoc(baseUri);
            }

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
                this.ErrorText = "Unknown error emitting generator.";
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

            var driver = CSharpGeneratorDriver.Create(generatorInstances);

            driver.RunGeneratorsAndUpdateCompilation(codeCompilation, out Compilation? outputCompilation, out ImmutableArray<Diagnostic> diagnostics);

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
                this.ErrorText = "Unknown error emitting program.";
                return;
            }

            ExecuteProgram(programAssembly);
        }

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

            using var writer = new StringWriter();
            try
            {
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
                this.ErrorText = writer.ToString() + "\n\nError executing program:" + Environment.NewLine + Environment.NewLine + ex.ToString();
                return;
            }
        }

        private Assembly? GetAssembly(Compilation generatorCompilation, string name, out string? errors)
        {
            try
            {
                using var generatorStream = new MemoryStream();
                Microsoft.CodeAnalysis.Emit.EmitResult? result = generatorCompilation.Emit(generatorStream);
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

        private async Task<List<MetadataReference>> GetReferences(string baseUri)
        {
            Assembly[]? refs = AppDomain.CurrentDomain.GetAssemblies();
            using var client = new HttpClient
            {
                BaseAddress = new Uri(baseUri)
            };

            using var bootResponse = await client.GetAsync("_framework/blazor.boot.json");
            bootResponse.EnsureSuccessStatusCode();
            using var boot = JsonDocument.Parse(await bootResponse.Content.ReadAsStringAsync());
            var assemblies = boot.RootElement.GetProperty("resources").GetProperty("assembly").EnumerateObject().Select(jp => jp.Name);

            var references = new List<MetadataReference>();
            try
            {

                foreach (Assembly reference in refs.Where(x => !x.IsDynamic))// && (!string.IsNullOrWhiteSpace(x.Location) || !string.IsNullOrWhiteSpace(x.CodeBase))))
                {
                    string path;
                    if (string.IsNullOrWhiteSpace(reference.Location))
                    {
                        //MetadataReference.CreateFromFile(reference.Location);
                        //MetadataReference.CreateFromImage(reference.)
                        try
                        {
                            //var uri = new Uri(reference.EscapedCodeBase);
                            //path = uri.LocalPath.Substring(1);
                            path = reference.GetName().Name!;
                            //references.Add(MetadataReference.Crea($"{baseUri}/_framework/{path}"));
                        }
                        catch (Exception e)
                        {
                            var str = e.ToString();

                            throw;
                        }
                    }
                    else
                    {
                        path = reference.Location;
                        //references.Add(MetadataReference.CreateFromFile(reference.Location));
                    }
                    //if (System.Diagnostics.Debugger.IsAttached)
                    //{
                    //    System.Diagnostics.Debugger.Log(1, "LOL", $"path: {path}");
                    //}
                    //Console.WriteLine($"path: {path}");
                    Stream? stream = await client.GetStreamAsync($"_framework/{path}.dll");
                    references.Add(MetadataReference.CreateFromStream(stream));

                }
            } catch (Exception e)
            {
                var str = e.ToString();
                throw;
            }

            return references;
        }

        private async Task<List<MetadataReference>> GetReferencesFromBootDoc(string baseUri)
        {
            Assembly[]? refs = AppDomain.CurrentDomain.GetAssemblies();
            using var client = new HttpClient
            {
                BaseAddress = new Uri(baseUri)
            };

            var references = new List<MetadataReference>();
            try
            {
            using var bootResponse = await client.GetAsync("_framework/blazor.boot.json");
            bootResponse.EnsureSuccessStatusCode();
            using var boot = JsonDocument.Parse(await bootResponse.Content.ReadAsStringAsync());
            var assemblies = boot.RootElement.GetProperty("resources").GetProperty("assembly").EnumerateObject().Select(jp => jp.Name);
                //.Concat(boot.RootElement.GetProperty("resources").GetProperty("lazyAssembly").EnumerateObject().Select(jp => jp.Name));

            try
            {
                foreach (var assemblyName in assemblies)
                {
                    Stream? stream = await client.GetStreamAsync($"_framework/{assemblyName}");
                    references.Add(MetadataReference.CreateFromStream(stream));

                }
            } catch (Exception e)
            {
                var str = e.ToString();
                throw;
            }
            } catch (Exception e)
            {
                var str = e.ToString();
                throw;
            }

            return references;
        }
    }
}
