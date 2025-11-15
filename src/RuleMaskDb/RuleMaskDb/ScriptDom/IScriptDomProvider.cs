namespace RuleMaskDb.ScriptDom;

public interface IScriptDomProvider
{
    Task<ScriptSpecification> LoadScriptAsync();
}