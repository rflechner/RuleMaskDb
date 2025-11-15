using Microsoft.Extensions.DependencyInjection;
using Spectre.Console.Cli;

namespace RuleMaskDb.ConsoleApp.DependencyInjection;

internal class ServiceCollectionRegistar(ServiceCollection services) : ITypeRegistrar
{
    public void Register(Type service, Type implementation)
    {
        services.AddScoped(service, implementation);
    }

    public void RegisterInstance(Type service, object implementation)
    {
        services.AddSingleton(service, implementation);
    }

    public void RegisterLazy(Type service, Func<object> factory)
    {
        services.AddScoped(service, _ => factory());
    }

    public ITypeResolver Build()
    {
        return new ServiceCollectionTypeResolver(services.BuildServiceProvider());
    }
}