using System.Reflection;
using DiagnosticExplorer.Util;

namespace DiagnosticExplorer;

public class Operation
{
    public Operation() { }

    public Operation(MethodInfo methodInfo)
        : this()
    {
        MethodInfo = methodInfo;
        Signature = methodInfo.Name;

        Parameters = methodInfo
            .GetParameters()
            .Select(x => new OperationParameter(x.Name, TypeUtil.GetFriendlyTypeName(x.ParameterType)))
            .ToList();

        var paramTypes = Parameters.Select(x => x.Type).ToArray();
        Signature = string.Format("{0}({1})", methodInfo.Name, string.Join(", ", paramTypes));
        ReturnType = TypeUtil.GetFriendlyTypeName(methodInfo.ReturnType);
    }

    public string ReturnType { get; set; }

    public string Signature { get; set; }

    public string Description { get; set; }

    public List<OperationParameter> Parameters { get; set; }

    internal MethodInfo MethodInfo { get; private set; }
}
