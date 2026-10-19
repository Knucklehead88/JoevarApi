using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Twilio;
using Twilio.Exceptions;
using Twilio.Rest.Verify.V2.Service;

/// <summary>Customers and contractors sign in with a code texted to the phone number on file (Twilio Verify).</summary>
public static class PhoneLoginEndpoints
{
    public static IEndpointRouteBuilder MapPhoneLoginEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth/phone")
            .WithTags("Auth")
            .RequireRateLimiting(AuthRateLimits.Policy);

        group.MapPost("/start", StartAsync)
            .WithName("StartPhoneLogin")
            .WithSummary("Text a login code to a customer or contractor");
        group.MapPost("/verify", VerifyAsync)
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
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Phone login is not configured.");
        }

        var phone = PhoneNumbers.Normalize(request.Phone);
        if (phone is null)
        {
            return InvalidPhone();
        }

        // Only numbers on file get a code. The response is the same either way, so numbers can't be probed.
        if (await db.ContactPhones.AnyAsync(contactPhone => contactPhone.Number == phone, cancellationToken))
        {
            try
            {
                TwilioClient.Init(twilio.AccountSid, twilio.AuthToken);
                await VerificationResource.CreateAsync(to: phone, channel: "sms", pathServiceSid: twilio.VerifyServiceSid);
            }
            catch (ApiException exception)
            {
                logger.LogError(exception, "Twilio Verify could not send a login code. ErrorCode={ErrorCode}", exception.Code);
                return TypedResults.Problem(statusCode: StatusCodes.Status502BadGateway, title: "The login code could not be sent.");
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
        var logger = loggerFactory.CreateLogger("PhoneLogin");
        var twilio = twilioOptions.Value;
        if (!IsConfigured(twilio))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Phone login is not configured.");
        }

        var phone = PhoneNumbers.Normalize(request.Phone);
        if (phone is null)
        {
            return InvalidPhone();
        }

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
            var check = await VerificationCheckResource.CreateAsync(to: phone, code: request.Code, pathServiceSid: twilio.VerifyServiceSid);
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
