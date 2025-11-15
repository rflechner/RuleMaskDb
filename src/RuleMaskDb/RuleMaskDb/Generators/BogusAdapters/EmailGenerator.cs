using Bogus;

namespace RuleMaskDb.Generators;

public class EmailGenerator(Faker faker) : IDataGenerator
{
    private readonly Faker _faker = faker;

    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        var value = _faker.Internet.Email();
        return Task.FromResult<object>(value);
    }
}
