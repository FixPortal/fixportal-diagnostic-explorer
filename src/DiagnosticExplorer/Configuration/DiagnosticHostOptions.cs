namespace DiagnosticExplorer;

public enum DiagnosticHostType
{
    Remote,
    SelfHost,
}

public sealed class DiagnosticHostOptions
{
    public DiagnosticHostType Type { get; set; }

    public string Url { get; set; }
}
