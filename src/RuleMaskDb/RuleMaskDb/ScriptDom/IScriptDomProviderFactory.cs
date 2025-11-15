namespace RuleMaskDb.ScriptDom;

public interface IScriptDomProviderFactory
{
    Task<IScriptDomProvider> CreateAsync(Uri uri);
}