using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiagnosticService.UnitTests;

internal sealed record DeploymentColumn(string Name, string Type);

/// <summary>Reads only the selected shapes emitted by the pinned, observed Bicep compiler.</summary>
internal sealed record LogAnalyticsDeploymentSchema(
    string TableName,
    string StreamName,
    IReadOnlyList<DeploymentColumn> Columns
)
{
    internal static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "retro-loganalytics.compiled.json");

    internal static LogAnalyticsDeploymentSchema Load() => Read(File.ReadAllBytes(FixturePath));

    internal static LogAnalyticsDeploymentSchema Read(byte[] bytes)
    {
        Require(bytes.Length is > 0 and <= 1024 * 1024, "compiled template envelope");
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        RejectDuplicateKeys(root);
        JsonElement variables = root.GetProperty("variables");
        JsonElement resources = root.GetProperty("resources");
        Require(resources.ValueKind == JsonValueKind.Array, "unsupported compiled resources shape");
        JsonElement table = OneResource(resources, "Microsoft.OperationalInsights/workspaces/tables");
        JsonElement rule = OneResource(resources, "Microsoft.Insights/dataCollectionRules");
        JsonElement tableSchema = table.GetProperty("properties").GetProperty("schema");
        string tableName = Text(tableSchema.GetProperty("name"), variables);
        string streamName = Text(variables.GetProperty("streamName"), variables);
        Require(streamName == "Custom-" + tableName, "table/output stream identity");
        string resourceName = table.GetProperty("name").GetString()!;
        Match resource = Match(
            resourceName,
            @"^\[format\('\{0\}/\{1\}', variables\('[A-Za-z_][A-Za-z_0-9]*'\), variables\('([A-Za-z_][A-Za-z_0-9]*)'\)\)\]$"
        );
        Require(
            resource.Success && LiteralVariable(variables, resource.Groups[1].Value) == tableName,
            "table resource/schema identity"
        );
        IReadOnlyList<DeploymentColumn> columns = ColumnArray(tableSchema.GetProperty("columns"), variables);
        JsonElement properties = rule.GetProperty("properties");
        JsonElement declarations = properties.GetProperty("streamDeclarations");
        Require(declarations.ValueKind == JsonValueKind.Object, "stream declarations shape");
        Dictionary<string, JsonElement> streams = new(StringComparer.Ordinal);
        foreach (JsonProperty declaration in declarations.EnumerateObject())
        {
            string name = StreamKey(declaration.Name, variables);
            Require(streams.TryAdd(name, declaration.Value), "duplicate resolved stream declaration");
        }
        Require(streams.Count == 1 && streams.ContainsKey(streamName), "selected stream declaration");
        IReadOnlyList<DeploymentColumn> streamColumns = ColumnArray(
            streams[streamName].GetProperty("columns"),
            variables
        );
        Require(columns.SequenceEqual(streamColumns), "compiled table and DCR column schemas differ");
        JsonElement flows = properties.GetProperty("dataFlows");
        Require(flows.ValueKind == JsonValueKind.Array && flows.GetArrayLength() == 1, "selected data flow");
        JsonElement flow = flows[0];
        JsonElement inputs = flow.GetProperty("streams");
        Require(
            inputs.ValueKind == JsonValueKind.Array
                && inputs.GetArrayLength() == 1
                && Text(inputs[0], variables) == streamName,
            "data flow stream binding"
        );
        Require(Text(flow.GetProperty("outputStream"), variables) == streamName, "data flow output binding");
        Require(flow.GetProperty("transformKql").GetString() == "source", "unsupported nonidentity transform");
        return new(tableName, streamName, columns);
    }

    private static JsonElement OneResource(JsonElement resources, string type)
    {
        JsonElement[] matches = resources
            .EnumerateArray()
            .Where(resource => resource.GetProperty("type").GetString() == type)
            .ToArray();
        Require(matches.Length == 1, "missing or duplicate selected resource: " + type);
        return matches[0];
    }

    private static IReadOnlyList<DeploymentColumn> ColumnArray(JsonElement value, JsonElement variables)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            Match reference = Match(value.GetString()!, @"^\[variables\('([A-Za-z_][A-Za-z_0-9]*)'\)\]$");
            Require(reference.Success, "unsupported selected column expression");
            value = variables.GetProperty(reference.Groups[1].Value);
        }
        Require(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0, "column array shape");
        List<DeploymentColumn> columns = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonElement item in value.EnumerateArray())
        {
            Require(
                item.ValueKind == JsonValueKind.Object && item.EnumerateObject().Count() == 2,
                "column object shape"
            );
            string name = item.GetProperty("name").GetString()!;
            string type = item.GetProperty("type").GetString()!;
            Require(!string.IsNullOrWhiteSpace(name) && names.Add(name), "empty or duplicate column name");
            Require(type is "datetime" or "int" or "string", "unsupported selected column type");
            columns.Add(new(name, type));
        }
        return columns;
    }

    private static string Text(JsonElement value, JsonElement variables)
    {
        Require(value.ValueKind == JsonValueKind.String, "selected string shape");
        string text = value.GetString()!;
        if (!text.StartsWith('['))
        {
            return text;
        }
        Match reference = Match(text, @"^\[variables\('([A-Za-z_][A-Za-z_0-9]*)'\)\]$");
        Require(reference.Success, "unsupported selected string expression");
        return LiteralVariable(variables, reference.Groups[1].Value);
    }

    private static string LiteralVariable(JsonElement variables, string name)
    {
        JsonElement value = variables.GetProperty(name);
        Require(
            value.ValueKind == JsonValueKind.String && !value.GetString()!.StartsWith('['),
            "nonliteral selected variable"
        );
        return value.GetString()!;
    }

    private static string StreamKey(string text, JsonElement variables)
    {
        Match format = Match(text, @"^\[format\('\{0\}', variables\('([A-Za-z_][A-Za-z_0-9]*)'\)\)\]$");
        if (format.Success)
        {
            return LiteralVariable(variables, format.Groups[1].Value);
        }
        Require(!text.StartsWith('['), "unsupported stream key expression");
        return text;
    }

    private static Match Match(string value, string pattern) =>
        Regex.Match(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty item in value.EnumerateObject())
            {
                Require(names.Add(item.Name), "duplicate compiled JSON key");
                RejectDuplicateKeys(item.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
            {
                RejectDuplicateKeys(item);
            }
        }
    }

    private static void Require(bool condition, string diagnostic)
    {
        if (!condition)
        {
            throw new InvalidDataException(diagnostic);
        }
    }
}
