using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using FixPortal.CodeStyle.ArchRules;

namespace DiagnosticExplorer.UnitTests;

public class ArchitectureTests
{
    private static readonly Architecture Architecture = new ArchLoader()
        .LoadAssemblies(typeof(DiagnosticManager).Assembly, typeof(DiagnosticHostingService).Assembly)
        .Build();

    [Fact]
    public void Interfaces_must_have_I_prefix()
    {
        FixPortalArchRules.InterfacesMustHaveIPrefix().Check(Architecture);
    }

    [Fact]
    public void Exception_types_must_inherit_from_Exception()
    {
        FixPortalArchRules.ExceptionsMustInheritFromException().Check(Architecture);
    }

    [Fact]
    public void Namespace_slices_must_be_free_of_cycles()
    {
        FixPortalArchRules.NamespaceSlicesMustBeFreeOfCycles("DiagnosticExplorer.(*)").Check(Architecture);
    }
}
