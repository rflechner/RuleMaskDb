using Bogus;

namespace RuleMaskDb.Generators;

public class LastNameGenerator(Faker faker) : IDataGenerator
{
    public Task<object> GenerateValueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<object>(faker.Name.LastName());
    }
}
