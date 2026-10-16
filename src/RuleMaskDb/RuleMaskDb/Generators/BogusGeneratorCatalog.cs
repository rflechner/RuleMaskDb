using System.Reflection;
using System.Runtime.CompilerServices;
using Bogus;

namespace RuleMaskDb.Generators;

public sealed record BogusGeneratorDescription(string Name, string Parameters, string ReturnType);

/// <summary>Only data APIs from the installed Bogus assembly are exposed, never arbitrary CLR methods.</summary>
public static class BogusGeneratorCatalog
{
    internal sealed record Entry(string Name, Func<Faker, object> Target, MethodInfo? Method, FieldInfo[] Fields, bool Extension)
    {
        public ParameterInfo[] Parameters { get; } = Method?.GetParameters().Skip(Extension ? 1 : 0).ToArray() ?? [];
        public Type ReturnType => Method?.ReturnType ?? Fields[^1].FieldType;

        public object? Invoke(Faker faker, object?[] arguments)
        {
            var target = Target(faker);
            if (Method is not null)
                return Method.Invoke(Extension ? null : target, Extension ? [target, .. arguments] : arguments);
            object? value = target;
            foreach (var field in Fields) value = field.GetValue(value);
            return value;
        }
    }

    private static readonly Entry[] Entries = Discover().OrderBy(e => e.Name, StringComparer.Ordinal)
        .ThenBy(e => e.Parameters.Length).ThenBy(e => e.Method?.ToString(), StringComparer.Ordinal).ToArray();

    public static IReadOnlyList<BogusGeneratorDescription> Generators { get; } = Array.AsReadOnly(Entries
        .Select(e => new BogusGeneratorDescription(e.Name, string.Join(", ", e.Parameters.Select(p =>
            $"{p.Name}: {BogusValueConverter.TypeName(p.ParameterType)}{(p.IsOptional ? " (optional)" : "")}")),
            BogusValueConverter.TypeName(e.ReturnType))).Distinct().ToArray());

    internal static Entry[] Find(string name)
    {
        var matches = Entries.Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length > 0 ? matches
            : throw new InvalidOperationException($"Unknown Bogus generator: {name}. Run 'generators' to list supported names.");
    }

    private static IEnumerable<Entry> Discover()
    {
        var receivers = typeof(Faker).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => typeof(DataSet).IsAssignableFrom(p.PropertyType)
                || p.PropertyType == typeof(Randomizer) || p.PropertyType == typeof(Person)).ToArray();

        foreach (var receiver in receivers)
        {
            Func<Faker, object> target = faker => GetTarget(faker, receiver);
            foreach (var original in receiver.PropertyType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => !m.IsSpecialName && m.ReturnType != typeof(void)))
            {
                var method = original;
                if (method.IsGenericMethodDefinition)
                {
                    // Collection generators use strings; application-specific enum types are not executable YAML.
                    if (method.Name is "Enum" or "EnumValues") continue;
                    if (method.GetGenericArguments().Length != 1) continue;
                    method = method.MakeGenericMethod(typeof(string));
                }
                if (method.GetParameters().Any(p => !BogusValueConverter.Supports(p.ParameterType))) continue;
                yield return new Entry($"Bogus.{receiver.Name}.{method.Name}", target, method, [], false);
            }
            if (receiver.PropertyType == typeof(Person))
                foreach (var fields in PersonFields(typeof(Person), []))
                    yield return new Entry("Bogus.Person." + string.Join(".", fields.Select(f => f.Name)), target, null, fields, false);
        }

        foreach (var type in typeof(Faker).Assembly.GetTypes().Where(t => t.IsAbstract && t.IsSealed))
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.IsDefined(typeof(ExtensionAttribute)) && !m.ContainsGenericParameters && m.ReturnType != typeof(void)))
        {
            var parameters = method.GetParameters();
            var receiver = receivers.FirstOrDefault(p => p.PropertyType == parameters[0].ParameterType);
            if (receiver is null || parameters.Skip(1).Any(p => !BogusValueConverter.Supports(p.ParameterType))) continue;
            // Match the normal dataset API: Bogus.Finance.Iban, Bogus.Finance.Nino, etc.
            yield return new Entry($"Bogus.{receiver.Name}.{method.Name}", faker => GetTarget(faker, receiver), method, [], true);
        }
    }

    private static object GetTarget(Faker faker, PropertyInfo receiver)
        // Faker.Person is cached; create a fresh profile for each generated database value.
        => receiver.PropertyType == typeof(Person) ? new Person(faker.Locale) : receiver.GetValue(faker)!;

    private static IEnumerable<FieldInfo[]> PersonFields(Type type, FieldInfo[] parent)
    {
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            FieldInfo[] path = [.. parent, field];
            yield return path;
            if (field.FieldType.DeclaringType == typeof(Person))
                foreach (var nested in PersonFields(field.FieldType, path)) yield return nested;
        }
    }
}
