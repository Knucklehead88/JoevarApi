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
builder.Services.AddSingleton<MondayCustomerCache>();
builder.Services.AddHttpClient<MondayClient>();
builder.Services.AddHostedService<MondayRefreshService>();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthPolicies.Staff, policy => policy.RequireRole(AppRoles.Admin, AppRoles.Manager, AppRoles.VirtualAssistant));

// Bearer-token login endpoints for the Vue frontend (register, login, refresh, ...).
builder.Services.AddIdentityApiEndpoints<AppUser>()
    .AddRoles<Microsoft.AspNetCore.Identity.IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>();

// The Vue frontend and the Shopify storefront (SMS opt-in form) call this API from the browser.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapGroup("/api/auth").WithTags("Auth").MapIdentityApi<AppUser>();
app.MapCustomerEndpoints();
app.MapProjectEndpoints();
app.MapContractorEndpoints();
app.MapSmsOptInEndpoints();
app.MapSmsInboundEndpoints();
app.MapMondayWebhookEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("HealthCheck");

app.Run();
