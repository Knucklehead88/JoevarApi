using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddValidation();

// Enums go over the wire as names ("Roofing"), which is what the Vue frontend works with.
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.Configure<TwilioOptions>(builder.Configuration.GetSection(TwilioOptions.SectionName));
builder.Services.Configure<MondayOptions>(builder.Configuration.GetSection(MondayOptions.SectionName));
builder.Services.Configure<AppleOptions>(builder.Configuration.GetSection(AppleOptions.SectionName));
builder.Services.Configure<GoogleOptions>(builder.Configuration.GetSection(GoogleOptions.SectionName));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddTransient<OfficeEmailSender>();
builder.Services.AddSingleton<MondayCustomerCache>();
builder.Services.AddHttpClient<MondayClient>();
builder.Services.AddHostedService<MondayRefreshService>();
builder.Services.AddSingleton<AppleTokenValidator>();
builder.Services.AddHttpClient<GoogleTokenValidator>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

// Staff must have an authenticator app enabled; customers and contractors sign in by phone code, passkey, Apple or Google.
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.Staff, policy => policy
        .RequireRole(AppRoles.Admin, AppRoles.Manager, AppRoles.VirtualAssistant)
        .RequireClaim(AppUserClaimsPrincipalFactory.TwoFactorEnabledClaim, "true"));

// Bearer-token login endpoints for the Vue frontend (login, refresh, 2FA, ...).
builder.Services.AddIdentityApiEndpoints<AppUser>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3)
    .AddRoles<IdentityRole>()
    .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>()
    .AddEntityFrameworkStores<AppDbContext>();

// Passkeys are bound to the frontend's domain (e.g. "joevar.com"), not the API host.
builder.Services.Configure<IdentityPasskeyOptions>(options =>
    options.ServerDomain = builder.Configuration["Passkeys:ServerDomain"]);

builder.Services.AddRateLimiter(options =>
{
    options.AddAuthRateLimits();
    options.AddContactRateLimit();
});

// The Vue frontend and the Shopify storefront (SMS opt-in form) call this API from the browser.
// Credentials are allowed because the passkey challenge travels in a cookie.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

if (args is ["create-admin", var adminUserName, var adminPassword])
{
    Environment.ExitCode = await AdminSetup.CreateAdminAsync(app.Services, adminUserName, adminPassword);
    return;
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Accounts are never self-registered: staff are created by an admin, customers and contractors on first phone login.
app.MapGroup("/api/auth")
    .WithTags("Auth")
    .AddEndpointFilter(async (context, next) =>
        context.HttpContext.Request.Path.Value?.EndsWith("/register", StringComparison.OrdinalIgnoreCase) == true
            ? Results.NotFound()
            : await next(context))
    .MapIdentityApi<AppUser>();
app.MapPhoneLoginEndpoints();
app.MapPasskeyEndpoints();
app.MapAppleLoginEndpoints();
app.MapGoogleLoginEndpoints();
app.MapAuthenticatorEndpoints();
app.MapCurrentUserEndpoints();
app.MapCustomerEndpoints();
app.MapProjectEndpoints();
app.MapContractorEndpoints();
app.MapContactEndpoints();
app.MapSmsOptInEndpoints();
app.MapSmsInboundEndpoints();
app.MapMondayWebhookEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("HealthCheck");

app.Run();
