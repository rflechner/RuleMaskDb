using Bogus;

namespace RuleMaskDb.Generators;

public class ZipCodeGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        var value = _faker.Address.ZipCode();
        return Task.FromResult<object>(value);
    }
}
