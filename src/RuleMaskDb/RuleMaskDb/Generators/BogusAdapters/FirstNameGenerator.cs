using Bogus;
using RuleMaskDb.Sdk;

namespace RuleMaskDb.Generators;

public class FirstNameGenerator(Faker faker) : IDataGenerator, IContextualDataGenerator
{
    public void ValidateParameters(IReadOnlyDictionary<string, IReadOnlyCollection<string>> possibleValues)
    {
        foreach (var (name, values) in possibleValues)
        {
            if (name != "gender") throw new InvalidOperationException($"Unknown FirstName parameter: {name}");
            foreach (var value in values)
            {
                var gender = new GenerationContext(new Dictionary<string, string> { [name] = value })
                    .GetEnum("gender", Gender.Unknown);
                EnsureSupported(gender);
            }
        }
    }

    private void EnsureSupported(Gender gender)
    {
        if (gender != Gender.Unknown && !faker.Name.SupportsGenderFirstNames)
            throw new InvalidOperationException("The selected Bogus locale does not support gender-specific first names.");
    }

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
        => GenerateValueAsync(GenerationContext.Empty, cancellationToken);

    public Task<object> GenerateValueAsync(GenerationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gender = context.GetEnum("gender", Gender.Unknown);
        EnsureSupported(gender);
        Bogus.DataSets.Name.Gender? bogusGender = gender switch
        {
            Gender.Male => Bogus.DataSets.Name.Gender.Male,
            Gender.Female => Bogus.DataSets.Name.Gender.Female,
            _ => null
        };
        return Task.FromResult<object>(faker.Name.FirstName(bogusGender));
    }
}
