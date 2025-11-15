using Bogus;

namespace RuleMaskDb.Generators;

public class CityGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        var value = _faker.Address.City();
        return Task.FromResult<object>(value);
    }
}
