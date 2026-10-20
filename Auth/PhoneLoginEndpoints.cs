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
/// They give both their email and that phone number, and both must belong to the same customer or contractor.
/// An email and phone that are both new sign up a new customer once the code is verified; an email or phone
/// already on file with something else is refused, so nobody can attach their phone to someone else's account.
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
            .WithSummary("Text a login code to a customer or contractor, or to a new customer signing up");
        phoneGroup.MapPost("/verify", VerifyAsync)
            .Produces<AccessTokenResponse>()
            .WithName("VerifyPhoneLogin")
            .WithSummary("Exchange the texted code for a bearer token");

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

        if (await MatchAsync(db, request.Email, phone, cancellationToken) == LoginMatch.Conflict)
        {
            return Conflict();
        }

        var failure = await SendCodeAsync(phone, twilio, logger);
        if (failure is not null)
        {
            return failure;
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

        var match = await MatchAsync(db, request.Email, phone, cancellationToken);
        if (match == LoginMatch.Conflict)
        {
            return Conflict();
        }

        return await SignInWithCodeAsync(
            phone, request.Email.Trim(), match == LoginMatch.New, request.Code, db, userManager, signInManager, twilio, loggerFactory.CreateLogger("PhoneLogin"), cancellationToken);
    }

    private enum LoginMatch
    {
        /// <summary>The phone belongs to a customer or contractor with this email.</summary>
        OnFile,

        /// <summary>Neither the email nor the phone is on file: a new customer signing up.</summary>
        New,

        /// <summary>The email or the phone is on file, but not together.</summary>
        Conflict,
    }

    /// <summary>How an email and phone relate to the customers and contractors on file (emails ignore case).</summary>
    private static async Task<LoginMatch> MatchAsync(AppDbContext db, string email, string phone, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        var onFile = await db.ContactPhones.AnyAsync(contactPhone =>
            contactPhone.Number == phone &&
            ((contactPhone.Customer != null && contactPhone.Customer.Email != null && contactPhone.Customer.Email.ToLower() == normalized) ||
             (contactPhone.Contractor != null && contactPhone.Contractor.Email != null && contactPhone.Contractor.Email.ToLower() == normalized)),
            cancellationToken);
        if (onFile)
        {
            return LoginMatch.OnFile;
        }

        var phoneTaken = await db.ContactPhones.AnyAsync(contactPhone => contactPhone.Number == phone, cancellationToken);
        var emailTaken =
            await db.Customers.AnyAsync(customer => customer.Email != null && customer.Email.ToLower() == normalized, cancellationToken) ||
            await db.Contractors.AnyAsync(contractor => contractor.Email != null && contractor.Email.ToLower() == normalized, cancellationToken);
        return phoneTaken || emailTaken ? LoginMatch.Conflict : LoginMatch.New;
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
        string email,
        bool isNewCustomer,
        string code,
        AppDbContext db,
        UserManager<AppUser> userManager,
        SignInManager<AppUser> signInManager,
        TwilioOptions twilio,
        ILogger logger,
        CancellationToken cancellationToken)
    {
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

        if (isNewCustomer)
        {
            // Self sign-up: the verified code proves they own the phone. Staff can add their name later.
            db.Customers.Add(new Customer
            {
                Name = email,
                Email = email,
                Phone = new ContactPhone { Number = phone }
            });
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("A new customer signed up by phone.");
        }

        var owners = await db.ContactPhones
            .Where(contactPhone => contactPhone.Number == phone)
            .Select(contactPhone => new { contactPhone.CustomerId, contactPhone.ContractorId })
            .ToListAsync(cancellationToken);
        if (owners.Count == 0)
        {
            return TypedResults.Unauthorized();
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
        // The email they signed in with, so the app can greet them by it instead of the phone number.
        user.Email = email;
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

    private static ProblemHttpResult Conflict() =>
        TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "This email or phone number is already on file with different details.",
            detail: "EmailPhoneMismatch");

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
    [EmailAddress]
    [MaxLength(256)]
    public required string Email { get; init; }

    [Required]
    [MaxLength(30)]
    public required string Phone { get; init; }
}

public sealed record PhoneLoginVerifyRequest
{
    [Required]
    [EmailAddress]
    [MaxLength(256)]
    public required string Email { get; init; }

    [Required]
    [MaxLength(30)]
    public required string Phone { get; init; }

    [Required]
    [StringLength(10, MinimumLength = 4)]
    public required string Code { get; init; }
}
