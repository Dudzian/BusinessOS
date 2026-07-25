using System.Text.RegularExpressions;

namespace BusinessOS.BuildingBlocks.Domain.Primitives;

public readonly record struct CurrencyCode
{
    private readonly string? value;

    public string Value => value ?? string.Empty;

    public CurrencyCode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!Regex.IsMatch(
                value,
                "^[A-Z]{3}$",
                RegexOptions.CultureInvariant))
        {
            throw new ArgumentException(
                "Currency code must use ISO 4217 format.",
                nameof(value));
        }

        this.value = value;
    }

    public static CurrencyCode Pln => new("PLN");

    public override string ToString() => Value;
}

public readonly record struct Money(decimal Amount, CurrencyCode Currency)
{
    public Money Add(Money other)
    {
        if (Currency != other.Currency)
        {
            throw new InvalidOperationException("Cannot add money in different currencies without conversion.");
        }

        return new Money(Amount + other.Amount, Currency);
    }

    public Money Round() => new(FinancialRounding.RoundMoney(Amount), Currency);
}

public static class FinancialRounding
{
    public static decimal RoundMoney(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal RoundPercentage(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}

public readonly record struct Percentage(decimal Value)
{
    public decimal Rounded => FinancialRounding.RoundPercentage(Value);
}

public readonly record struct Quantity(decimal Value);

public readonly record struct EmailAddress(string Value);

public readonly record struct TaxIdentificationNumber(string? Value)
{
    public bool IsValidForPoland
    {
        get
        {
            if (Value is not { Length: 10 })
            {
                return false;
            }

            ReadOnlySpan<int> weights = [6, 5, 7, 2, 3, 4, 5, 6, 7];
            var weightedSum = 0;
            var allDigitsIdentical = true;

            for (var index = 0; index < Value.Length; index++)
            {
                var character = Value[index];
                if (character is < '0' or > '9')
                {
                    return false;
                }

                allDigitsIdentical &= character == Value[0];
                if (index < weights.Length)
                {
                    weightedSum += (character - '0') * weights[index];
                }
            }

            if (allDigitsIdentical)
            {
                return false;
            }

            var checksum = weightedSum % 11;
            return checksum != 10 && checksum == Value[9] - '0';
        }
    }
}

public readonly record struct DateRange
{
    public DateOnly Start { get; }

    public DateOnly End { get; }

    public DateRange(DateOnly start, DateOnly end)
    {
        if (end < start)
        {
            throw new ArgumentException("End date cannot be before start date.", nameof(end));
        }

        Start = start;
        End = end;
    }
}

public readonly record struct EntityVersion(long Value)
{
    public EntityVersion Next() => new(Value + 1);
}
