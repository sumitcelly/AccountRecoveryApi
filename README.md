# Synthetic Account Recovery API

This .NET 8 sample demonstrates a recovery flow for synthetic accounts. It is an exercise, not a production identity-proofing system. The mock inbox simulates an independently accessed recovery channel; it does not establish real-world identity assurance.

## Local setup

The project uses SQLite for recovery state, mock inbox deliveries, synthetic credentials, and ASP.NET Core Identity users. The database is created and migrated at application startup. To run migrations manually:

```sh
dotnet tool restore
dotnet ef database update
```

Set a local inbox password using User Secrets; do not put it in `appsettings.json` or source control:

```sh
dotnet user-secrets init
dotnet user-secrets set "MockInbox:Username" "inbox-user"
dotnet user-secrets set "MockInbox:Password" "ChangeMe!2026"
dotnet user-secrets set "MockInbox:AccountId" "12345"
```

Then run the HTTPS launch profile:

```sh
dotnet dev-certs https --trust
dotnet run --launch-profile https
```

The HTTPS profile listens at `https://localhost:7208`. The reset API rejects non-HTTPS requests outside Development. Development allows HTTP for local tooling, so use the HTTPS URL for the browser and reset flow. Set `ConnectionStrings__RecoveryDatabase` to change the SQLite database location. `BaseUrl` must be set to the externally reachable HTTPS base URL for real ACS callbacks.

The configured inbox account is pre-enrolled for synthetic account `12345`. The sample also seeds synthetic account `67890`; it has no inbox login unless explicitly mapped in configuration/data.

For the PowerShell API smoke test, set `MOCK_INBOX_USERNAME` and `MOCK_INBOX_PASSWORD` in the shell. First complete the inbound call, account-ID, and verification-code steps with a configured ACS resource. Then run `./test.ps1`; it reads the authenticated inbox, prompts securely for a synthetic password, and verifies that a retry returns the original receipt. For the actual browser flow, use the inbox and reset pages instead.

## Exercise flow

1. The inbound call handler creates a persisted recovery session and asks for a five-digit synthetic account ID.
2. The issuer responds with the same voice message whether an account exists or not. For a known account, it persists a six-digit verification code to that account's mock inbox. Unknown accounts never get a delivery.
3. Open `https://localhost:7208/mock-inbox.html` in a separate browser context and sign in with the pre-enrolled inbox username/password. The inbox API derives account access from the authenticated Identity user; callers cannot choose an account ID in the request.
4. Enter the complete six-digit code by speech or keypad. Two distinct completed wrong-code events close the recovery attempt. Re-delivery of the same ACS event ID is ignored.
5. After verification, the issuer stores a high-entropy reset link in that same inbox. The voice prompts do not reveal its code or token.
6. Open the link and enter a new synthetic password privately in the browser form. The reset backend enforces the minimum-length policy, updates the synthetic credential hash in SQLite, and returns a persisted receipt. Repeating the same successful reset request returns the same receipt and does not apply another password change.
7. Asking for an agent or representative records a persisted `ESC-...` mock outcome and tells the caller that no human is connected. Verification exhaustion and expired links are also recorded as escalations.

The browser password necessarily reaches the reset backend over HTTPS. The application does not log password request bodies or recognized caller input. A spoken secret could still be present in raw telephony audio or an external speech service, so this sample asks users to use only synthetic data.

## Persistence and monitoring

- EF Core SQLite migrations persist sessions, verification-code attempt counts and expiry, mock inbox deliveries, synthetic credential hashes, Identity users, account-to-inbox mappings, and processed callback event IDs.
- Verification state transitions and reset completion run in database transactions. A concurrency version protects session updates; event IDs prevent duplicate callback delivery from counting as another attempt.
- Reset tokens are generated with 256 bits of randomness. Only a SHA-256 token hash is stored for validation; the raw link is held only as a mock inbox delivery, analogous to an email delivery.
- The monitor queries persisted `LinkIssued` sessions every three seconds, including after process restart. It tracks link expiry from the database, persists timeout escalation outcomes, and plays a completion/timeout message and hangs up when the call connection is still available.
- The SQLite database is a single-process demo store. The monitor can recover after a restart, but SQLite and periodic scans are not a substitute for a distributed production database/queue under multi-instance load.

## Useful routes

| Route | Access | Purpose |
|---|---|---|
| `POST /api/telephony/incoming` | ACS webhook | Handles Event Grid subscription validation and incoming calls |
| `POST /api/telephony/callback` | ACS webhook | Processes call recognition and disconnect events |
| `POST /api/mock/login` | Anonymous | Signs in a pre-enrolled mock inbox user |
| `GET /api/mock/inbox` | Authenticated inbox cookie | Reads only the signed-in user's inbox |
| `POST /api/mock/logout` | Authenticated inbox cookie | Signs out |
| `POST /api/recovery/reset-password` | Reset token | Updates the synthetic credential and returns the authoritative receipt |

The telephony webhook routes still need Azure request verification before public exposure. Do not deploy this demo with the mock ACS connection string, local SQLite, or an unauthenticated webhook configuration.
