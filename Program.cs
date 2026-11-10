using System.Text.Json;
using AccountRecoveryApi.Domain;
using AccountRecoveryApi.Infrastructure;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("RecoveryDatabase")
    ?? "Data Source=account-recovery.db";
builder.Services.AddDbContext<RecoveryDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddIdentityCore<IdentityUser>(options =>
    {
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<RecoveryDbContext>()
    .AddSignInManager();
builder.Services
    .AddAuthentication(IdentityConstants.ApplicationScheme)
    .AddCookie(IdentityConstants.ApplicationScheme, options =>
    {
        options.Cookie.Name = "AccountRecovery.Inbox";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddScoped<IRecoverySessionEngine, RecoverySessionEngine>();
builder.Services.AddHostedService<AccountRecoveryMonitorService>();

var acsConnectionString = builder.Configuration.GetConnectionString("AzureCommunicationServices")
    ?? "endpoint=https://azure.com;accesskey=ZXhlY3V0aW9uTmV0d29ya1NhbmRib3hNb2NrS2V5VmFsdWUzMg==";
builder.Services.AddSingleton(new CallAutomationClient(acsConnectionString));

var app = builder.Build();

await InitializeDatabaseAsync(app.Services, app.Configuration);

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();
app.MapMockInboxEndpoints();

app.MapPost("/api/telephony/incoming", async (
    [FromBody] JsonElement[] jsonEvents,
    [FromServices] CallAutomationClient client,
    [FromServices] IRecoverySessionEngine engine,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    foreach (var jsonEvent in jsonEvents)
    {
        if (!jsonEvent.TryGetProperty("eventType", out var eventTypeProp))
        {
            continue;
        }

        var eventType = eventTypeProp.GetString();
        if (eventType == "Microsoft.EventGrid.SubscriptionValidationEvent")
        {
            var validationCode = jsonEvent.GetProperty("data").GetProperty("validationCode").GetString();
            return Results.Ok(new { validationResponse = validationCode });
        }

        if (eventType != "Microsoft.Communication.IncomingCall")
        {
            continue;
        }

        var incomingCallContext = jsonEvent.GetProperty("data")
            .GetProperty("incomingCallContext").GetString();
        if (string.IsNullOrWhiteSpace(incomingCallContext))
        {
            return Results.BadRequest();
        }

        try
        {
            var callbackUri = new Uri(
                $"{app.Configuration["BaseUrl"] ?? "https://localhost:7208"}/api/telephony/callback");
            var answerResult = await client.AnswerCallAsync(incomingCallContext, callbackUri, cancellationToken);
            var connectionId = answerResult.Value.CallConnection.CallConnectionId;
            await engine.CreateSessionAsync(connectionId, cancellationToken);
            await PromptForAccountIdentifierAsync(client.GetCallConnection(connectionId), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unable to initialize the incoming recovery call.");
            return Results.Problem(
                title: "Unable to initialize call",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    return Results.Ok();
});

app.MapPost("/api/telephony/callback", async (
    [FromBody] JsonElement[] cloudEvents,
    [FromServices] CallAutomationClient client,
    [FromServices] IRecoverySessionEngine engine,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        if (!cloudEvent.TryGetProperty("id", out var idProperty) ||
            string.IsNullOrWhiteSpace(idProperty.GetString()))
        {
            logger.LogWarning("Ignored a call event without an event ID.");
            continue;
        }

        var callEvent = CallAutomationEventParser.Parse(
            BinaryData.FromString(cloudEvent.GetRawText()));
        if (callEvent is null)
        {
            continue;
        }

        var session = await engine.GetByCallIdAsync(callEvent.CallConnectionId, cancellationToken);
        if (session is null)
        {
            continue;
        }

        if (callEvent is RecognizeCompleted recognizeCompleted)
        {
            var input = recognizeCompleted.RecognizeResult switch
            {
                DtmfResult dtmfResult => string.Concat(
                    dtmfResult.Tones
                        .Where(tone => tone != DtmfTone.Pound)
                        .Select(tone => tone.ToString().Replace("Tone", ""))),
                SpeechResult speechResult => speechResult.Speech,
                _ => string.Empty
            };

            CallConnection? connection = null;
            try
            {
                connection = client.GetCallConnection(callEvent.CallConnectionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not obtain call connection for callback.");
            }

            await engine.ProcessCallInputAsync(
                session.SessionId,
                input,
                idProperty.GetString()!,
                connection,
                cancellationToken);
        }
        else if (callEvent is CallDisconnected)
        {
            await engine.MarkCallDisconnectedAsync(session.SessionId, cancellationToken);
        }
        else if (callEvent is RecognizeFailed or PlayFailed)
        {
            try
            {
                var connection = client.GetCallConnection(callEvent.CallConnectionId);
                await PromptForRecoveryInputAsync(connection, session.State, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Unable to restart recognition for the recovery call.");
            }
        }
    }

    return Results.Ok();
});

app.MapPost("/api/recovery/reset-password", async (
    [FromBody] ResetRequest request,
    HttpContext context,
    [FromServices] IRecoverySessionEngine engine,
    CancellationToken cancellationToken) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (!context.Request.IsHttps && !app.Environment.IsDevelopment())
    {
        return Results.BadRequest(new
        {
            code = "ERR_HTTPS_REQUIRED",
            description = "Submit the reset request over HTTPS."
        });
    }

    if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
    {
        return Results.BadRequest(new
        {
            code = "ERR_REQUEST_INVALID",
            description = "A reset token and new password are required."
        });
    }

    var result = await engine.ExecuteAuthoritativeResetAsync(
        request.Token,
        request.NewPassword,
        cancellationToken);
    if (!result.Success)
    {
        return Results.Json(
            new { code = result.ErrorCode, description = result.ErrorDescription },
            statusCode: StatusCodes.Status400BadRequest);
    }

    return Results.Ok(new { receipt = result.ReceiptId, message = "Synthetic credential reset completed." });
})
.WithSummary("Reset the synthetic account credential with a verified single-use link");

app.Run();

static async Task InitializeDatabaseAsync(IServiceProvider services, IConfiguration configuration)
{
    await using var scope = services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<RecoveryDbContext>();
    await db.Database.MigrateAsync();

    var accounts = new[]
    {
        new SyntheticAccount { AccountId = "12345", DisplayName = "Test Account Alpha" },
        new SyntheticAccount { AccountId = "67890", DisplayName = "Test Account Beta" }
    };
    foreach (var account in accounts)
    {
        if (!await db.SyntheticAccounts.AnyAsync(item => item.AccountId == account.AccountId))
        {
            db.SyntheticAccounts.Add(account);
        }
    }
    await db.SaveChangesAsync();

    var username = configuration["MockInbox:Username"] ?? "inbox-user";
    var password = configuration["MockInbox:Password"];
    var accountId = configuration["MockInbox:AccountId"] ?? "12345";
    if (string.IsNullOrWhiteSpace(password))
    {
        throw new InvalidOperationException(
            "Configure MockInbox:Password with user-secrets or an environment variable before starting the API.");
    }
    if (!await db.SyntheticAccounts.AnyAsync(account => account.AccountId == accountId))
    {
        throw new InvalidOperationException("MockInbox:AccountId must identify a seeded synthetic account.");
    }

    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    var user = await userManager.FindByNameAsync(username);
    if (user is null)
    {
        user = new IdentityUser { UserName = username, EmailConfirmed = true };
        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            var errors = string.Join("; ", createResult.Errors.Select(error => error.Code));
            throw new InvalidOperationException($"Unable to create the configured mock inbox user: {errors}");
        }
    }

    var inboxMapping = await db.InboxAccounts.SingleOrDefaultAsync(item => item.UserId == user.Id);
    if (inboxMapping is null)
    {
        db.InboxAccounts.Add(new InboxAccount { UserId = user.Id, AccountId = accountId });
    }
    else
    {
        inboxMapping.AccountId = accountId;
    }

    await db.SaveChangesAsync();
}

static async Task PromptForAccountIdentifierAsync(
    CallConnection connection,
    CancellationToken cancellationToken) =>
    await PromptForRecoveryInputAsync(connection, SessionState.AwaitingId, cancellationToken);

static async Task PromptForRecoveryInputAsync(
    CallConnection connection,
    SessionState state,
    CancellationToken cancellationToken)
{
    if (state is not (SessionState.AwaitingId or SessionState.AwaitingCode))
    {
        return;
    }

    var awaitingAccountId = state == SessionState.AwaitingId;
    var prompt = new TextSource(awaitingAccountId
        ? "Please enter your five-digit synthetic account identifier."
        : "Please enter the complete six-digit verification code.")
    {
        VoiceName = "en-US-NancyNeural"
    };

    var recognizeOptions = new CallMediaRecognizeSpeechOrDtmfOptions(
        targetParticipant: CommunicationIdentifier.FromRawId(connection.CallConnectionId),
        maxTonesToCollect: awaitingAccountId ? 5 : 6)
    {
        InitialSilenceTimeout = TimeSpan.FromSeconds(10),
        InterToneTimeout = TimeSpan.FromSeconds(4),
        EndSilenceTimeout = TimeSpan.FromSeconds(2),
        SpeechLanguage = "en-US",
        Prompt = prompt,
        InterruptPrompt = true,
        StopTones = new[] { DtmfTone.Pound }
    };

    await connection.GetCallMedia().StartRecognizingAsync(recognizeOptions, cancellationToken);
}

/// <summary>Token and new synthetic password submitted by the reset form.</summary>
public sealed record ResetRequest(string Token, string NewPassword);
