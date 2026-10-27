using System.Reflection;

namespace VoiceAgent.Harness;

/// <summary>
/// Prints the sherpa-onnx offline recognizer surface (NeMo / Parakeet / transducer). The harness
/// has no compile reference to the package, so everything goes through reflection.
/// </summary>
public static class AsrApiProbe
{
    public static int Describe()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "sherpa-onnx")
            ?? Assembly.Load("sherpa-onnx");

        var types = assembly.GetTypes()
            .Where(t => t.IsPublic && (
                t.Name.StartsWith("OfflineRecognizer") ||
                t.Name.StartsWith("OfflineStream") ||
                t.Name.StartsWith("OfflineModelConfig") ||
                t.Name.Contains("Nemo") ||
                t.Name.Contains("Parakeet") ||
                t.Name.Equals("OfflineTransducerModelConfig")))
            .OrderBy(t => t.Name);

        foreach (var type in types)
        {
            Console.WriteLine(type.FullName);

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"  field {field.FieldType.Name} {field.Name}");

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                Console.WriteLine($"  prop  {property.PropertyType.Name} {property.Name}");

            if (!type.Name.EndsWith("Config", StringComparison.Ordinal))
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                             .Where(m => !m.IsSpecialName))
                {
                    var args = string.Join(", ", method.GetParameters()
                        .Select(p => $"{p.ParameterType.Name} {p.Name}"));
                    Console.WriteLine($"  method {method.ReturnType.Name} {method.Name}({args})");
                }
            }
        }

        return 0;
    }
}
