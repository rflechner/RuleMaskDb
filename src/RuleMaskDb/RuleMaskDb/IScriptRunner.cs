namespace RuleMaskDb;

public interface IScriptRunner
{
    Task RunAsync(ScriptSpecification script, CancellationToken cancellationToken = default);
    
    Task<bool> IsImpactedAsync(ScriptSpecification script, TableDescription table, FieldDescription field, CancellationToken cancellationToken = default);
}


public class ScriptRunner : IScriptRunner
{
    public Task RunAsync(ScriptSpecification script, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<bool> IsImpactedAsync(ScriptSpecification script, TableDescription table, FieldDescription field, CancellationToken cancellationToken = default)
    {
        foreach (var rule in script.Rules)
        {
            if (rule.Table != table.Name) continue;
            
            if (rule.Column == field.Path) return true;
        }
        
        return false;
    }
}