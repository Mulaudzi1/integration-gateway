using System.Text.Json.Nodes;

namespace IntegrationGateway.Domain;

/// <summary>Maps an inbound JSON payload to a connector's shape using simple "$.a.b[0].c" paths.</summary>
public static class PayloadMapper
{
    public static string Map(string payload, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return payload;
        var source = JsonNode.Parse(payload);
        var output = new JsonObject();
        foreach (var (field, path) in map)
            output[field] = Select(source, path)?.DeepClone();
        return output.ToJsonString();
    }

    public static JsonNode? Select(JsonNode? node, string path)
    {
        if (!path.StartsWith("$")) throw new ArgumentException($"Path must start with '$': {path}");
        foreach (var segment in path[1..].Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment;
            int? index = null;
            var bracket = segment.IndexOf('[');
            if (bracket >= 0)
            {
                name = segment[..bracket];
                index = int.Parse(segment[(bracket + 1)..segment.IndexOf(']')]);
            }
            if (name.Length > 0) node = node is JsonObject o && o.TryGetPropertyValue(name, out var child) ? child : null;
            if (index is { } i) node = node is JsonArray a && i < a.Count ? a[i] : null;
            if (node is null) return null;
        }
        return node;
    }
}
