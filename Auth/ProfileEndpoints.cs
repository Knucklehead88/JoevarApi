using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Verify.V2.Service;

/// <summary>
/// The signed-in user's own settings. Customers and contractors sign in with the email and phone on their customer or
/// contractor record, so changes are made there too, or they would lock themselves out. A new phone must be proven with
/// a texted code, since the phone is what signs them in.
/// </summary>
public static class ProfileEndpoints
{
    public static IEndpointRouteBuilder MapProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth/me")
            .WithTags("Auth")
            .RequireAuthorization();

        group.MapPut("/profile", UpdateProfileAsync)
            .WithName("UpdateProfile")
            .WithSummary("Change the signed-in user's name and email");
        group.MapPut("/preferences", UpdatePreferencesAsync)
            .WithName("UpdatePreferences")
            .WithSummary("Change the signed-in user's theme");
        group.MapPost("/phone/start", StartPhoneChangeAsync)
            .RequireRateLimiting(AuthRateLimits.Policy)
            .WithName("StartPhoneChange")
            .WithSummary("Text a code to the phone number the signed-in user wants to change to");
        group.MapPost("/phone/verify", VerifyPhoneChangeAsync)
            .RequireRateLimiting(AuthRateLimits.Policy)
            .WithName("VerifyPhoneChange")
            .WithSummary("Change the signed-in user's phone number with the texted code");

        return endpoints;
    }

    private static async Task<Results<NoContent, ValidationProblem, UnauthorizedHttpResult>> UpdateProfileAsync(
        UpdateProfileRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
        if (email is null && (user.CustomerId is not null || user.ContractorId is not null))
        {
            return FieldError("email", "Your email is required: you sign in with it.");
        }

        if (email is not null && await EmailIsTakenAsync(db, user, email, cancellationToken))
        {
            return FieldError("email", "This email is already on file for someone else.");
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        if (!string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            user.EmailConfirmed = false;
        }

        user.DisplayName = displayName;
        user.Email = email;
        await userManager.UpdateAsync(user);

        if (await db.Customers.FirstOrDefaultAsync(customer => customer.Id == user.CustomerId, cancellationToken) is { } linkedCustomer)
        {
            linkedCustomer.Email = email;
            linkedCustomer.Name = displayName ?? linkedCustomer.Name;
        }

        if (await db.Contractors.FirstOrDefaultAsync(contractor => contractor.Id == user.ContractorId, cancellationToken) is { } linkedContractor)
        {
            linkedContractor.Email = email;
            linkedContractor.Name = displayName ?? linkedContractor.Name;
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, UnauthorizedHttpResult>> UpdatePreferencesAsync(
        UpdatePreferencesRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        user.SystemTheme = request.SystemTheme ?? user.SystemTheme;
        user.DarkTheme = request.DarkTheme ?? user.DarkTheme;
        await userManager.UpdateAsync(user);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Accepted, ValidationProblem, UnauthorizedHttpResult, ProblemHttpResult>> StartPhoneChangeAsync(
        StartPhoneChangeRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        AppDbContext db,
        IOptions<TwilioOptions> twilioOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var phone = PhoneNumbers.Normalize(request.Phone);
        if (phone is null)
        {
            return FieldError("phone", "Enter a valid phone number, e.g. +17735550100.");
        }

        if (await PhoneIsTakenAsync(db, user, phone, cancellationToken))
        {
            return FieldError("phone", "This phone number is already on file for someone else.");
        }

        var twilio = twilioOptions.Value;
        if (!PhoneLoginEndpoints.IsConfigured(twilio))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Text messages are not configured.");
        }

        var failure = await PhoneLoginEndpoints.SendCodeAsync(phone, twilio, loggerFactory.CreateLogger("Profile"));
        return failure is null ? TypedResults.Accepted((string?)null) : failure;
    }

    private static async Task<Results<NoContent, ValidationProblem, UnauthorizedHttpResult, ProblemHttpResult>> VerifyPhoneChangeAsync(
        VerifyPhoneChangeRequest request,
        ClaimsPrincipal principal,
        UserManager<AppUser> userManager,
        AppDbContext db,
        IOptions<TwilioOptions> twilioOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(principal);
        if (user is null)
        {
            return TypedResults.Unauthorized();
        }

        var phone = PhoneNumbers.Normalize(request.Phone);
        if (phone is null)
        {
            return FieldError("phone", "Enter a valid phone number, e.g. +17735550100.");
        }

        if (await PhoneIsTakenAsync(db, user, phone, cancellationToken))
        {
            return FieldError("phone", "This phone number is already on file for someone else.");
        }

        var twilio = twilioOptions.Value;
        if (!PhoneLoginEndpoints.IsConfigured(twilio))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Text messages are not configured.");
        }

        try
        {
            TwilioClient.Init(twilio.AccountSid, twilio.AuthToken);
            var check = await VerificationCheckResource.CreateAsync(to: phone, code: request.Code, pathServiceSid: twilio.VerifyServiceSid);
            if (check.Status != "approved")
            {
                return FieldError("code", "That code didn't work. Check it, or send a new one.");
            }
        }
        catch (ApiException exception) when (exception.Status == StatusCodes.Status404NotFound)
        {
            return FieldError("code", "That code has expired. Send a new one.");
        }
        catch (ApiException exception)
        {
            loggerFactory.CreateLogger("Profile").LogError(exception, "Twilio Verify could not check a phone change code. ErrorCode={ErrorCode}", exception.Code);
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The code could not be checked.");
        }

        var contactPhone = await db.ContactPhones.FirstOrDefaultAsync(
            candidate => (user.CustomerId != null && candidate.CustomerId == user.CustomerId) ||
                         (user.ContractorId != null && candidate.ContractorId == user.ContractorId),
            cancellationToken);
        if (contactPhone is not null && contactPhone.Number != phone)
        {
            // Consent was given for the old number: keep the records as proof, but not as consent for the new one.
            await db.SmsConsents
                .Where(consent => consent.ContactPhoneId == contactPhone.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(consent => consent.ContactPhoneId, (Guid?)null), cancellationToken);
            contactPhone.Number = phone;
        }
        else if (contactPhone is null && user.CustomerId is not null)
        {
            db.ContactPhones.Add(new ContactPhone { Number = phone, CustomerId = user.CustomerId });
        }
        else if (contactPhone is null && user.ContractorId is not null)
        {
            db.ContactPhones.Add(new ContactPhone { Number = phone, ContractorId = user.ContractorId });
        }

        await db.SaveChangesAsync(cancellationToken);

        // Phone sign-ins name the login after the number; keep them together.
        if (user.UserName == user.PhoneNumber)
        {
            user.UserName = phone;
        }

        user.PhoneNumber = phone;
        user.PhoneNumberConfirmed = true;
        await userManager.UpdateAsync(user);
        return TypedResults.NoContent();
    }

    private static async Task<bool> EmailIsTakenAsync(AppDbContext db, AppUser user, string email, CancellationToken cancellationToken)
    {
        var lower = email.ToLowerInvariant();
        var upper = email.ToUpperInvariant();
        return await db.Users.AnyAsync(other => other.Id != user.Id && other.NormalizedEmail == upper, cancellationToken)
            || await db.Customers.AnyAsync(customer => customer.Id != user.CustomerId && customer.Email != null && customer.Email.ToLower() == lower, cancellationToken)
            || await db.Contractors.AnyAsync(contractor => contractor.Id != user.ContractorId && contractor.Email != null && contractor.Email.ToLower() == lower, cancellationToken);
    }

    private static async Task<bool> PhoneIsTakenAsync(AppDbContext db, AppUser user, string phone, CancellationToken cancellationToken) =>
        await db.Users.AnyAsync(other => other.Id != user.Id && other.PhoneNumber == phone, cancellationToken)
        || await db.ContactPhones.AnyAsync(
            contactPhone => contactPhone.Number == phone &&
                (user.CustomerId == null || contactPhone.CustomerId != user.CustomerId) &&
                (user.ContractorId == null || contactPhone.ContractorId != user.ContractorId),
            cancellationToken);

    private static ValidationProblem FieldError(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

public sealed record UpdateProfileRequest
{
    [MaxLength(200)]
    public string? DisplayName { get; init; }

    [EmailAddress]
    [MaxLength(256)]
    public string? Email { get; init; }
}

/// <summary>Only the values given change.</summary>
public sealed record UpdatePreferencesRequest
{
    /// <summary>Follow the device's light/dark setting.</summary>
    public bool? SystemTheme { get; init; }

    /// <summary>The theme used when not following the device.</summary>
    public bool? DarkTheme { get; init; }
}

public sealed record StartPhoneChangeRequest
{
    [Required]
    [MaxLength(30)]
    public required string Phone { get; init; }
}

public sealed record VerifyPhoneChangeRequest
{
    [Required]
    [MaxLength(30)]
    public required string Phone { get; init; }

    [Required]
    [MaxLength(10)]
    public required string Code { get; init; }
}
