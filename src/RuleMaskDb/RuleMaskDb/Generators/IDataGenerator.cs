using RuleMaskDb.ScriptDom;

namespace RuleMaskDb.Generators;

public interface IDataGeneratorFactory
{
    IDataGenerator Create(GeneratorType generatorType);

    Sdk.IDataGenerator Create(string name)
    {
        var match = Enum.GetNames<GeneratorType>().FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is null) throw new InvalidOperationException($"Unknown generator: {name}");
        return Create(Enum.Parse<GeneratorType>(match));
    }
}

public class BogusDataGeneratorFactory : IDataGeneratorFactory
{
    public Sdk.IDataGenerator Create(string name)
    {
        if (name.StartsWith("Bogus.", StringComparison.OrdinalIgnoreCase))
        {
            var entries = BogusGeneratorCatalog.Find(name);
            return new CultureAwareGenerator(faker => new BogusMethodGenerator(faker, entries));
        }
        var match = Enum.GetNames<GeneratorType>().FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is null) throw new InvalidOperationException($"Unknown generator: {name}");
        return Create(Enum.Parse<GeneratorType>(match));
    }

    public IDataGenerator Create(GeneratorType generatorType)
    {
        if (!Enum.IsDefined(generatorType)) throw new ArgumentOutOfRangeException(nameof(generatorType), generatorType, null);
        return new CultureAwareGenerator(faker => CreateGenerator(generatorType, faker));
    }

    private static IDataGenerator CreateGenerator(GeneratorType generatorType, Bogus.Faker faker)
    {
        return generatorType switch
        {
            GeneratorType.FirstName => new FirstNameGenerator(faker),
            GeneratorType.LastName => new LastNameGenerator(faker),
            GeneratorType.Name => new NameGenerator(faker),
            GeneratorType.AddressLine => new AddressLineGenerator(faker),
            GeneratorType.City => new CityGenerator(faker),
            GeneratorType.State => new StateGenerator(faker),
            GeneratorType.ZipCode => new ZipCodeGenerator(faker),
            GeneratorType.PhoneNumber => new PhoneNumberGenerator(faker),
            GeneratorType.Email => new EmailGenerator(faker),
            GeneratorType.Age => new AgeGenerator(faker),
            GeneratorType.DateOfBirth => new DateOfBirthGenerator(faker),
            GeneratorType.CreditCardNumber => new CreditCardNumberGenerator(faker),
            GeneratorType.CreditCardExpirationDate => new CreditCardExpirationDateGenerator(faker),
            GeneratorType.CreditCardSecurityCode => new CreditCardSecurityCodeGenerator(faker),
            _ => throw new ArgumentOutOfRangeException(nameof(generatorType), generatorType, null)
        };
    }
}

public interface IDataGenerator : Sdk.IDataGenerator
{
}
