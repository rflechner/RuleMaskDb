using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus.DataSets;

namespace RuleMaskDb.Generators;

internal static class BogusValueConverter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true };

    public static bool Supports(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum || type == typeof(string) || type == typeof(bool) || type == typeof(char)
            || type.IsPrimitive && type != typeof(IntPtr) && type != typeof(UIntPtr)
            || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset)
            || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan)
            || type == typeof(Guid) || type == typeof(CardType)
            || type.IsArray && Supports(type.GetElementType()!)
            || type.IsGenericType && new[] { typeof(List<>), typeof(IList<>), typeof(ICollection<>), typeof(IEnumerable<>) }
                .Contains(type.GetGenericTypeDefinition()) && Supports(type.GetGenericArguments()[0]);
    }

    public static object? Parse(string value, Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        type = nullable ?? type;
        // Preserve the existing gender mapping convention.
        if (nullable == typeof(Name.Gender) && value.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return null;
        if (type == typeof(string)) return value;
        if (type.IsEnum)
        {
            var name = Enum.GetNames(type).FirstOrDefault(n => n.Equals(value, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException($"Expected one of: {string.Join(", ", Enum.GetNames(type))}.");
            return Enum.Parse(type, name);
        }
        if (type == typeof(CardType))
        {
            var field = type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .FirstOrDefault(f => f.FieldType == type && f.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
                ?? throw new FormatException("Unknown credit card provider.");
            return field.GetValue(null);
        }
        if (type == typeof(DateTime)) return DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateTimeOffset)) return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (type == typeof(DateOnly)) return DateOnly.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(TimeOnly)) return TimeOnly.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(TimeSpan)) return TimeSpan.Parse(value, CultureInfo.InvariantCulture);
        if (type == typeof(Guid)) return Guid.Parse(value);
        if (type.IsArray || type.IsGenericType)
            return JsonSerializer.Deserialize(value, type, JsonOptions) ?? throw new FormatException("Expected a JSON array.");
        var result = Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        if (result is double d && !double.IsFinite(d) || result is float f && !float.IsFinite(f))
            throw new FormatException("Expected a finite number.");
        return result;
    }

    public static object ToDatabaseValue(object? value) => value switch
    {
        null => DBNull.Value,
        Enum => value.ToString()!,
        IPAddress or IPEndPoint or Version or Exception => value.ToString()!,
        char[] characters => new string(characters),
        char character => character.ToString(),
        byte[] => value,
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
        TimeOnly time => time.ToTimeSpan(),
        sbyte number => (short)number,
        ushort number => (int)number,
        uint number => (long)number,
        ulong number => (decimal)number,
        string or bool or byte or short or int or long or float or double or decimal
            or DateTime or DateTimeOffset or TimeSpan or Guid => value,
        _ => JsonSerializer.Serialize(value, value.GetType(), JsonOptions)
    };

    public static string TypeName(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return TypeName(inner) + "?";
        if (type.IsEnum) return string.Join("|", Enum.GetNames(type));
        if (type == typeof(CardType)) return "CardType";
        if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";
        if (type.IsGenericType) return TypeName(type.GetGenericArguments()[0]) + "[]";
        return type.Name;
    }
}
