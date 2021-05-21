using System.Threading.Tasks;

namespace SourceGeneratorPlayground
{
    internal interface IRunner
    {
        string ErrorText { get; }
        string GeneratorOutput { get; }
        string ProgramOutput { get; }

        Task RunAsync(string code, string generator);
    }
}