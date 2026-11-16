namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// Proves the forbidden-contract scanner is not vacuous: it must find the
/// type nested in the generic argument of this protected member.
/// </summary>
public class ContractTypeReferencesFixture
{
    protected virtual IReadOnlyList<System.Text.StringBuilder> ForbiddenLeak => [];
}
