using YamlDotNet.RepresentationModel;

namespace BlenderLauncher.Core.Localisation;

/// <summary>
/// Loads one upstream translation file (<c>resources/localization/&lt;namespace&gt;.&lt;locale&gt;.yml</c>) into
/// a flat map of dotted keys to strings, for example <c>prog.progress</c>.
/// Uses YamlDotNet's representation model (<see cref="YamlStream"/>) only: it builds nodes without reflection,
/// unlike the serialiser, so it is safe under Native AOT. Scalars stay strings, so quoted keys such as
/// <c>"yes"</c> are never turned into booleans.
/// </summary>
public static class TranslationFileLoader
{
    public static Dictionary<string, string> Load(TextReader reader)
    {
        var stream = new YamlStream();
        stream.Load(reader);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (stream.Documents.Count > 0 && stream.Documents[0].RootNode is YamlMappingNode root)
        {
            Flatten(root, prefix: "", result);
        }

        return result;
    }

    private static void Flatten(YamlMappingNode mapping, string prefix, Dictionary<string, string> result)
    {
        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = prefix + ((YamlScalarNode)keyNode).Value;
            switch (valueNode)
            {
                case YamlMappingNode child:
                    Flatten(child, key + ".", result);
                    break;
                case YamlScalarNode scalar:
                    result[key] = scalar.Value ?? "";
                    break;
                default:
                    throw new InvalidDataException($"Unsupported YAML node for '{key}': {valueNode.NodeType}");
            }
        }
    }
}
