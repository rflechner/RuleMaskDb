using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace RuleMaskDb.ConsoleApp.DependencyInjection;

internal class ServiceCollectionTypeResolver(ServiceProvider serviceProvider) : ITypeResolver
{
    public object? Resolve(Type? type)
    {
        return serviceProvider.GetService(type ?? throw new ArgumentNullException(nameof(type)));
    }
}