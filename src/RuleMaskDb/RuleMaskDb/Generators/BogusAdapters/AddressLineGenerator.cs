using Bogus;

namespace RuleMaskDb.Generators;

public class AddressLineGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        var value = _faker.Address.StreetAddress();
        return Task.FromResult<object>(value);
    }
}
