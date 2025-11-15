using Bogus;

namespace RuleMaskDb.Generators;

public class StateGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        var value = _faker.Address.StateAbbr();
        return Task.FromResult<object>(value);
    }
}
