using Bogus;

namespace RuleMaskDb.Generators;

public class AgeGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        var value = _faker.Random.Int(0, 100);
        return Task.FromResult<object>(value);
    }
}
