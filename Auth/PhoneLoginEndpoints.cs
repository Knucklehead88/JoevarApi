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
/// Customers and contractors sign in with a code texted to their phone (Twilio Verify): the code proves the phone, so
/// it signs in whoever has that number on file. A number that isn't on file signs up a new customer with the name
/// they gave.
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
        IOptions<TwilioOptions> twilioOptions,
        ILoggerFactory loggerFactory)
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

        return await SignInWithCodeAsync(
            phone, request.Name.Trim(), request.Code, db, userManager, signInManager, twilio, loggerFactory.CreateLogger("PhoneLogin"), cancellationToken);
    }

    internal static async Task<ProblemHttpResult?> SendCodeAsync(string phone, TwilioOptions twilio, ILogger logger)
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
        string name,
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

        var owners = await db.ContactPhones
            .Where(contactPhone => contactPhone.Number == phone)
            .Select(contactPhone => new { contactPhone.CustomerId, contactPhone.ContractorId })
            .ToListAsync(cancellationToken);
        if (owners.Count == 0)
        {
            // Self sign-up: the verified code proves they own the phone.
            var customer = new Customer { Name = name, Phone = new ContactPhone { Number = phone } };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("A new customer signed up by phone.");
            owners = [new { CustomerId = (Guid?)customer.Id, ContractorId = (Guid?)null }];
        }

        // One login per person: the one for this phone, else the one the customer/contractor already has (e.g.
        // created by a Google sign-in), else a new one named after the phone.
        var customerIds = owners.Where(owner => owner.CustomerId != null).Select(owner => owner.CustomerId).ToList();
        var contractorIds = owners.Where(owner => owner.ContractorId != null).Select(owner => owner.ContractorId).ToList();
        var user = await userManager.FindByNameAsync(phone)
            ?? await userManager.Users
                .Where(candidate => customerIds.Contains(candidate.CustomerId) || contractorIds.Contains(candidate.ContractorId))
                .OrderBy(candidate => candidate.UserName)
                .FirstOrDefaultAsync(cancellationToken);
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
        // The name they gave, unless the login already has one, so the app greets them by it, not by the number.
        user.DisplayName ??= name;
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

    internal static bool IsConfigured(TwilioOptions twilio) =>
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
    /// <summary>Used when the number isn't on file yet, to sign up a new customer.</summary>
    [Required]
    [MaxLength(200)]
    public required string Name { get; init; }

    [Required]
    [MaxLength(30)]
    public required string Phone { get; init; }
}

public sealed record PhoneLoginVerifyRequest
{
    /// <summary>Used when the number isn't on file yet, to sign up a new customer.</summary>
    [Required]
    [MaxLength(200)]
    public required string Name { get; init; }

    [Required]
    [MaxLength(30)]
    public required string Phone { get; init; }

    [Required]
    [StringLength(10, MinimumLength = 4)]
    public required string Code { get; init; }
}
