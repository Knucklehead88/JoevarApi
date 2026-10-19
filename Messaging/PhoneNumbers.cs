using Microsoft.AspNetCore.Http.HttpResults;

public static class PhoneNumbers
{
    /// <summary>Normalizes a phone number to E.164; 10-digit numbers are treated as US numbers.</summary>
    public static string? Normalize(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return null;
        }

        var digits = new string(phoneNumber.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 10)
        {
            digits = "1" + digits;
        }

        return digits.Length >= 8 ? "+" + digits : null;
    }

    /// <summary>
    /// Validates an optional phone from a request. Returns false with a validation problem when it is not a phone number.
    /// </summary>
    public static bool TryParseOptional(string? input, out string? normalized, out ValidationProblem? problem)
    {
        normalized = null;
        problem = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        normalized = Normalize(input);
        if (normalized is null)
        {
            problem = TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["phone"] = ["Phone must be a valid phone number, e.g. +17735550100."]
            });
            return false;
        }

        return true;
    }

    /// <summary>
    /// Points an owner at a new number. A changed number gets a new record, since consent belongs to a number.
    /// </summary>
    public static void Replace(AppDbContext db, ContactPhone? current, string? number, Action<ContactPhone?> assign)
    {
        if (current?.Number == number)
        {
            return;
        }

        if (current is not null)
        {
            db.ContactPhones.Remove(current);
        }

        assign(number is null ? null : new ContactPhone { Number = number });
    }
}
