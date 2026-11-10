using System.Security.Claims;
using System.ComponentModel.DataAnnotations;
using AccountRecoveryApi.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AccountRecoveryApi.Infrastructure;

public static class MockInboxEndpoints
{
    public static void MapMockInboxEndpoints(this WebApplication app)
    {
        app.MapPost("/api/mock/login", async (
            [FromBody] MockInboxLoginRequest request,
            HttpContext context,
            SignInManager<IdentityUser> signInManager) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { message = "Username and password are required." });
            }

            var result = await signInManager.PasswordSignInAsync(
                request.Username,
                request.Password,
                isPersistent: false,
                lockoutOnFailure: true);
            return result.Succeeded
                ? Results.Ok()
                : Results.Unauthorized();
        })
        .AllowAnonymous()
        .WithSummary("Sign in to the synthetic recovery inbox");

        app.MapPost("/api/mock/logout", async (SignInManager<IdentityUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithSummary("Sign out of the synthetic recovery inbox");

        app.MapGet("/api/mock/inbox", async (
            ClaimsPrincipal user,
            HttpContext context,
            IRecoverySessionEngine engine,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId is null)
            {
                return Results.Unauthorized();
            }

            var inbox = await engine.GetMockInboxForUserAsync(userId, cancellationToken);
            if (inbox is null)
            {
                return Results.Forbid();
            }

            return Results.Ok(new MockInboxResponse(
                inbox.LastSentCode,
                inbox.SecureResetLink,
                ToDateTimeOffset(inbox.CodeExpiresAt),
                ToDateTimeOffset(inbox.LinkExpiresAt)));
        })
        .RequireAuthorization()
        .WithSummary("Read the signed-in user's synthetic recovery inbox");
    }

    private static DateTimeOffset? ToDateTimeOffset(DateTime? value) =>
        value is null
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
}

/// <summary>Credentials for signing in to the synthetic recovery inbox.</summary>
public sealed record MockInboxLoginRequest
{
    [Required]
    public required string Username { get; init; }

    [Required]
    public required string Password { get; init; }
}

/// <summary>A recovery code and reset link delivered to the authenticated synthetic inbox.</summary>
public sealed record MockInboxResponse(
    string? VerificationCode,
    string? ResetLink,
    DateTimeOffset? VerificationCodeExpiresAt,
    DateTimeOffset? ResetLinkExpiresAt);
