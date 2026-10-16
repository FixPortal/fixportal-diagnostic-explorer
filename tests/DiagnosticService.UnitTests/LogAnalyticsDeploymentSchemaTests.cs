using System.Text;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Xunit;

namespace DiagnosticService.UnitTests;

public sealed class LogAnalyticsDeploymentSchemaTests
{
    [Fact]
    public void Actual_generated_variables_and_supported_literals_resolve_to_the_same_schema()
    {
        byte[] original = File.ReadAllBytes(LogAnalyticsDeploymentSchema.FixturePath);
        LogAnalyticsDeploymentSchema expected = LogAnalyticsDeploymentSchema.Read(original);
        JsonNode root = JsonNode.Parse(original)!;
        JsonNode columns = root["variables"]!["retroColumns"]!;
        Resource(root, "Microsoft.OperationalInsights/workspaces/tables")["properties"]!["schema"]!["columns"] =
            columns.DeepClone();
        JsonObject declarations = Resource(root, "Microsoft.Insights/dataCollectionRules")["properties"]![
            "streamDeclarations"
        ]!.AsObject();
        declarations.Clear();
        declarations[expected.StreamName] = new JsonObject { ["columns"] = columns.DeepClone() };
        LogAnalyticsDeploymentSchema actual = LogAnalyticsDeploymentSchema.Read(
            Encoding.UTF8.GetBytes(root.ToJsonString())
        );
        actual.TableName.Should().Be(expected.TableName);
        actual.StreamName.Should().Be(expected.StreamName);
        actual.Columns.Should().Equal(expected.Columns);
    }

    [Theory]
    [InlineData("missing-table", "*selected resource*")]
    [InlineData("duplicate-table", "*selected resource*")]
    [InlineData("missing-rule", "*selected resource*")]
    [InlineData("duplicate-rule", "*selected resource*")]
    [InlineData("duplicate-column", "*duplicate column*")]
    [InlineData("unknown-column-shape", "*column object shape*")]
    [InlineData("unsupported-expression", "*column expression*")]
    [InlineData("table-only-fork", "*table and DCR column schemas differ*")]
    [InlineData("wrong-output-stream", "*output binding*")]
    [InlineData("duplicate-resolved-stream", "*duplicate resolved stream*")]
    public void Invalid_or_unsupported_selected_shapes_fail_closed(string mutation, string diagnostic)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllBytes(LogAnalyticsDeploymentSchema.FixturePath))!;
        JsonArray resources = root["resources"]!.AsArray();
        JsonNode table = Resource(root, "Microsoft.OperationalInsights/workspaces/tables");
        JsonNode rule = Resource(root, "Microsoft.Insights/dataCollectionRules");
        JsonArray columns = root["variables"]!["retroColumns"]!.AsArray();
        switch (mutation)
        {
            case "missing-table":
                resources.Remove(table);
                break;
            case "duplicate-table":
                resources.Add(table.DeepClone());
                break;
            case "missing-rule":
                resources.Remove(rule);
                break;
            case "duplicate-rule":
                resources.Add(rule.DeepClone());
                break;
            case "duplicate-column":
                columns.Add(columns[0]!.DeepClone());
                break;
            case "unknown-column-shape":
                columns[0]!["unexpected"] = true;
                break;
            case "unsupported-expression":
                table["properties"]!["schema"]!["columns"] = "[concat('unsupported', 'expression')]";
                break;
            case "table-only-fork":
                JsonArray fork = columns.DeepClone().AsArray();
                fork[0]!["name"] = "forked-column";
                table["properties"]!["schema"]!["columns"] = fork;
                break;
            case "wrong-output-stream":
                rule["properties"]!["dataFlows"]![0]!["outputStream"] = "Custom-Unrelated";
                break;
            case "duplicate-resolved-stream":
                JsonObject declarations = rule["properties"]!["streamDeclarations"]!.AsObject();
                declarations["Custom-DiagRetro_CL"] = new JsonObject { ["columns"] = columns.DeepClone() };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }
        Action read = () => LogAnalyticsDeploymentSchema.Read(Encoding.UTF8.GetBytes(root.ToJsonString()));
        read.Should().Throw<InvalidDataException>().WithMessage(diagnostic);
    }

    [Fact]
    public void Duplicate_compiled_json_keys_are_rejected_before_schema_selection()
    {
        string original = File.ReadAllText(LogAnalyticsDeploymentSchema.FixturePath);
        string duplicate = original.Replace(
            "\"contentVersion\":",
            "\"contentVersion\": \"duplicate\", \"contentVersion\":",
            StringComparison.Ordinal
        );
        duplicate.Should().NotBe(original);
        Action read = () => LogAnalyticsDeploymentSchema.Read(Encoding.UTF8.GetBytes(duplicate));
        read.Should().Throw<InvalidDataException>().WithMessage("*duplicate compiled JSON key*");
    }

    private static JsonNode Resource(JsonNode root, string type) =>
        root["resources"]!.AsArray().Single(resource => resource!["type"]!.GetValue<string>() == type)!;
}
