using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Verify.V2.Service;

/// <summary>
/// Customers and contractors sign in with a code texted to the phone number on file (Twilio Verify).
/// They identify themselves by that phone number or by their email; either way the code goes to the phone.
/// </summary>
public static class PhoneLoginEndpoints
{
    public static IEndpointRouteBuilder MapPhoneLoginEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var phoneGroup = endpoints.MapGroup("/api/auth/phone")
            .WithTags("Auth")
            .RequireRateLimiting(AuthRateLimits.Policy);

        phoneGroup.MapPost("/start", StartAsync)
            .WithName("StartPhoneLogin")
            .WithSummary("Text a login code to a customer or contractor");
        phoneGroup.MapPost("/verify", VerifyAsync)
            .Produces<AccessTokenResponse>()
            .WithName("VerifyPhoneLogin")
            .WithSummary("Exchange the texted code for a bearer token");

        var emailGroup = endpoints.MapGroup("/api/auth/email")
            .WithTags("Auth")
            .RequireRateLimiting(AuthRateLimits.Policy);

        emailGroup.MapPost("/start", StartByEmailAsync)
            .WithName("StartEmailLogin")
            .WithSummary("Text a login code to the phone on file for a customer's or contractor's email");
        emailGroup.MapPost("/verify", VerifyByEmailAsync)
            .Produces<AccessTokenResponse>()
            .WithName("VerifyEmailLogin")
            .WithSummary("Exchange the code texted after an email login for a bearer token");

        return endpoints;
    }

    private static async Task<Results<Accepted, ValidationProblem, ProblemHttpResult>> StartAsync(
        PhoneLoginStartRequest request,
        AppDbContext db,
        IOptions<TwilioOptions> twilioOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("PhoneLogin");
        var twilio = twilioOptions.Value;
        if (!IsConfigured(twilio))
        {
            logger.LogError("Twilio Verify settings are not configured.");
            return NotConfigured();
        }

        var phone = PhoneNumbers.Normalize(request.Phone);
        if (phone is null)
        {
            return InvalidPhone();
        }

        // Only numbers on file get a code. The response is the same either way, so numbers can't be probed.
        if (await db.ContactPhones.AnyAsync(contactPhone => contactPhone.Number == phone, cancellationToken))
        {
            var failure = await SendCodeAsync(phone, twilio, logger);
            if (failure is not null)
            {
                return failure;
            }
        }

        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<Accepted, ValidationProblem, ProblemHttpResult>> StartByEmailAsync(
        EmailLoginStartRequest request,
        AppDbContext db,
        IOptions<TwilioOptions> twilioOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("PhoneLogin");
        var twilio = twilioOptions.Value;
        if (!IsConfigured(twilio))
        {
            logger.LogError("Twilio Verify settings are not configured.");
            return NotConfigured();
        }

        // Same response whether or not the email is on file, and the phone number is never revealed.
        var phone = await FindPhoneByEmailAsync(db, request.Email, logger, cancellationToken);
        if (phone is not null)
        {
            var failure = await SendCodeAsync(phone, twilio, logger);
            if (failure is not null)
            {
                return failure;
            }
        }

        return TypedResults.Accepted((string?)null);
    }

    private static async Task<Results<EmptyHttpResult, ValidationProblem, UnauthorizedHttpResult, ProblemHttpResult>> VerifyAsync(
        PhoneLoginVerifyRequest request,
        AppDbContext db,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        IOptions<TwilioOptions> twilioOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var twilio = twilioOptions.Value;
        if (!IsConfigured(twilio))
        {
            return NotConfigured();
        }

        var phone = PhoneNumbers.Normalize(request.Phone);
        if (phone is null)
        {
            return InvalidPhone();
        }

        return await SignInWithCodeAsync(
            phone, request.Code, db, userManager, signInManager, twilio, loggerFactory.CreateLogger("PhoneLogin"), cancellationToken);
    }

    private static async Task<Results<EmptyHttpResult, ValidationProblem, UnauthorizedHttpResult, ProblemHttpResult>> VerifyByEmailAsync(
        EmailLoginVerifyRequest request,
        AppDbContext db,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        IOptions<TwilioOptions> twilioOptions,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("PhoneLogin");
        var twilio = twilioOptions.Value;
        if (!IsConfigured(twilio))
        {
            return NotConfigured();
        }

        var phone = await FindPhoneByEmailAsync(db, request.Email, logger, cancellationToken);
        if (phone is null)
        {
            return TypedResults.Unauthorized();
        }

        return await SignInWithCodeAsync(phone, request.Code, db, userManager, signInManager, twilio, logger, cancellationToken);
    }

    /// <summary>
    /// The phone on file for a customer's or contractor's email. Emails aren't unique, so an email that leads to
    /// more than one phone number can't be used to sign in (the person can still sign in with their number).
    /// </summary>
    private static async Task<string?> FindPhoneByEmailAsync(AppDbContext db, string email, ILogger logger, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var numbers = await db.ContactPhones
            .Where(contactPhone =>
                (contactPhone.Customer != null && contactPhone.Customer.Email != null && contactPhone.Customer.Email.ToLower() == normalized) ||
                (contactPhone.Contractor != null && contactPhone.Contractor.Email != null && contactPhone.Contractor.Email.ToLower() == normalized))
            .Select(contactPhone => contactPhone.Number)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (numbers.Count > 1)
        {
            logger.LogWarning("Email login refused: the email belongs to {Count} different phone numbers.", numbers.Count);
        }

        return numbers.Count == 1 ? numbers[0] : null;
    }

    private static async Task<ProblemHttpResult?> SendCodeAsync(string phone, TwilioOptions twilio, ILogger logger)
    {
        try
        {
            TwilioClient.Init(twilio.AccountSid, twilio.AuthToken);
            await VerificationResource.CreateAsync(to: phone, channel: "sms", pathServiceSid: twilio.VerifyServiceSid);
            return null;
        }
        catch (ApiException exception)
        {
            logger.LogError(exception, "Twilio Verify could not send a login code. ErrorCode={ErrorCode}", exception.Code);
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The login code could not be sent.");
        }
    }

    private static async Task<Results<EmptyHttpResult, ValidationProblem, UnauthorizedHttpResult, ProblemHttpResult>> SignInWithCodeAsync(
        string phone,
        string code,
        AppDbContext db,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        TwilioOptions twilio,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var owners = await db.ContactPhones
            .Where(contactPhone => contactPhone.Number == phone)
            .Select(contactPhone => new { contactPhone.CustomerId, contactPhone.ContractorId })
            .ToListAsync(cancellationToken);
        if (owners.Count == 0)
        {
            return TypedResults.Unauthorized();
        }

        try
        {
            TwilioClient.Init(twilio.AccountSid, twilio.AuthToken);
            var check = await VerificationCheckResource.CreateAsync(to: phone, code: code, pathServiceSid: twilio.VerifyServiceSid);
            if (check.Status != "approved")
            {
                return TypedResults.Unauthorized();
            }
        }
        catch (ApiException exception) when (exception.Status == StatusCodes.Status404NotFound)
        {
            // No pending code for this number: it expired, was already used, or was never sent.
            return TypedResults.Unauthorized();
        }
        catch (ApiException exception)
        {
            logger.LogError(exception, "Twilio Verify could not check a login code. ErrorCode={ErrorCode}", exception.Code);
            return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The login code could not be checked.");
        }

        // One login per phone number, linked to the customer and/or contractor who owns it.
        var user = await userManager.FindByNameAsync(phone);
        if (user is null)
        {
            user = new AppUser { UserName = phone };
            var created = await userManager.CreateAsync(user);
            if (!created.Succeeded)
            {
                logger.LogError("Could not create a login for a verified phone: {Errors}", string.Join("; ", created.Errors.Select(error => error.Description)));
                return TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "The login could not be created.");
            }
        }

        user.PhoneNumber = phone;
        user.PhoneNumberConfirmed = true;
        foreach (var owner in owners)
        {
            if (owner.CustomerId is not null)
            {
                user.CustomerId = owner.CustomerId;
                await EnsureRoleAsync(userManager, user, AppRoles.Customer);
            }

            if (owner.ContractorId is not null)
            {
                user.ContractorId = owner.ContractorId;
                await EnsureRoleAsync(userManager, user, AppRoles.Contractor);
            }
        }

        await userManager.UpdateAsync(user);

        // The bearer scheme writes the access/refresh token response, the same shape as /api/auth/login.
        signInManager.AuthenticationScheme = IdentityConstants.BearerScheme;
        await signInManager.SignInAsync(user, isPersistent: false, authenticationMethod: "otp");
        return TypedResults.Empty;
    }

    private static bool IsConfigured(TwilioOptions twilio) =>
        !string.IsNullOrWhiteSpace(twilio.AccountSid) &&
        !string.IsNullOrWhiteSpace(twilio.AuthToken) &&
        !string.IsNullOrWhiteSpace(twilio.VerifyServiceSid);

    private static ProblemHttpResult NotConfigured() =>
        TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Phone login is not configured.");

    private static async Task EnsureRoleAsync(UserManager<AppUser> userManager, AppUser user, string role)
    {
        if (!await userManager.IsInRoleAsync(user, role))
        {
            await userManager.AddToRoleAsync(user, role);
        }
    }

    private static ValidationProblem InvalidPhone() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["phone"] = ["Phone must be a valid phone number, e.g. +17735550100."]
        });
}

public sealed record PhoneLoginStartRequest
{
    [Required]
    [MaxLength(30)]
    public required string Phone { get; init; }
}

public sealed record PhoneLoginVerifyRequest
{
    [Required]
    [MaxLength(30)]
    public required string Phone { get; init; }

    [Required]
    [StringLength(10, MinimumLength = 4)]
    public required string Code { get; init; }
}

public sealed record EmailLoginStartRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public required string Email { get; init; }
}

public sealed record EmailLoginVerifyRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public required string Email { get; init; }

    [Required]
    [StringLength(10, MinimumLength = 4)]
    public required string Code { get; init; }
}
