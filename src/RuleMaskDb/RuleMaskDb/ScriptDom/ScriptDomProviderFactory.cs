using RuleMaskDb.Yaml;

namespace RuleMaskDb.ScriptDom;

public class ScriptDomProviderFactory : IScriptDomProviderFactory
{
    public async Task<IScriptDomProvider> CreateAsync(Uri uri)
    {
        if (uri.Scheme == "file" && uri.LocalPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return new YamlScriptDomProvider(uri.LocalPath);
        }
        
        throw new NotSupportedException($"Unsupported URI scheme: {uri.Scheme}");
    }
}