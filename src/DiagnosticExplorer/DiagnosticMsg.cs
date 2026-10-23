namespace DiagnosticExplorer;

public class DiagnosticMsg
{
    public int Level { get; set; }

    public DateTime Date { get; set; }

    public string Machine { get; set; }

    public string Process { get; set; }

    public string User { get; set; }

    public string Category { get; set; }

    public string Message { get; set; }

    public string Environment { get; set; }

    public string MsgId { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 24);
}
