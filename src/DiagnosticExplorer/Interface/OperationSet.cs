namespace DiagnosticExplorer;

public class OperationSet
{
    public OperationSet()
    {
        Operations = [];
    }

    public string Id { get; set; }

    public List<Operation> Operations { get; set; }
}
