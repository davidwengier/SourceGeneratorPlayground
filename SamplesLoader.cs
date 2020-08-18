using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SourceGeneratorPlayground
{
    public static class SamplesLoader
    {
        public static string[] Samples = GetSamples().ToArray();

        private static IEnumerable<string> GetSamples()
        {
            yield return " - None - ";
            foreach (string name in typeof(SamplesLoader).Assembly.GetManifestResourceNames())
            {
                if (name.StartsWith("SourceGeneratorPlayground.Samples") && name.EndsWith(".Generator.cs"))
                {
                    yield return name.Split(".")[2];
                }
            }
        }

        public static (string, string) LoadSample(int index)
        {
            string name = Samples[index];
            using var streamReader = new StreamReader(typeof(SamplesLoader).Assembly.GetManifestResourceStream("SourceGeneratorPlayground.Samples." + name + ".Program.cs")!);
            using var streamReader1 = new StreamReader(typeof(SamplesLoader).Assembly.GetManifestResourceStream("SourceGeneratorPlayground.Samples." + name + ".Generator.cs")!);

            return (streamReader.ReadToEnd(), streamReader1.ReadToEnd());
        }
    }
}
