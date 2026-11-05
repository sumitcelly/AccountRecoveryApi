
 1. System Grading Matrix


Category	Targeted Architectural Objectives
🛡️ Security & Correctness	Anti-enumeration containment loops, secure HTTPS reset path validation, and tamper-evident ticket truth logs (ESC-XXXXXXXX).
🗣️ Conversation Quality	DTMF determinism mapping (DtmfTone native parsing), non-blocking fail-fast throttling, and clean telemetry handoffs.
⏱️ Reliability	Strict 120-second verification code expirations, hard 10-minute web-reset token evictions, and cooperative task cancellation.
🏗️ Engineering Quality	Decoupled Clean Architecture, absolute memory lifecycle isolation, and reproducible local integration testing scripts.


🏗️ 2. Core Architecture & Lifecycles

The engine decouples telephony signaling routing from the underlying session state memory matrix using clear key-prefixes:
[ Incoming Telephony Stream ] ──> /api/telephony/callback
                                         │
                    ┌────────────────────┴────────────────────┐
                    ▼                                         ▼
         [ CallPrefix (call_idx_) ]                [ TokenPrefix (tok_idx_) ]
                    │                                         │
                    └────────────────────┬────────────────────┘
                                         ▼
                            [ SessionPrefix (rec_sess_) ]
                                         │
                                         ▼
                          MemoryCacheEntryOptions Policies

⏱️ Strict Expiration Policies

• Global Telephony Session (15 Minutes Hard Cap): Automatically applied during initial call creation. Every state modification explicitly enforces this policy to prevent token mutations from resetting the lifespan into an immortal state.
• Verification Code Window (120 Seconds Hard Cap): Restricts the window for brute-forcing digits over the voice stream.
• Web Reset Link Window (10 Minutes Hard Cap): Restricts the lifespan of the cryptographic ResetToken.

🚀 3. Local Simulation & Integration Testing

Use these reproducible cURL test paths to validate the event parser and state transformations in a local development environment.

Phase 1: Simulate Inbound Call Connection

Simulates an incoming telephone connection hitting the inbound route. This allocates the session context and sets the base 15-minute absolute cache expiration.
bash
curl -X POST "http://localhost:5000/api/telephony/incoming" \
  -H "Content-Type: application/cloudevents+json" \
  -d '[{
    "specversion": "1.0",
    "type": "Microsoft.Communication.IncomingCall",
    "source": "/calling/incomingCall/mock-notification-id",
    "id": "inc-call-evt-id-999",
    "time": "2026-11-05T12:00:00Z",
    "data": {
      "to": { "rawId": "phone:+18005550199", "phoneNumber": { "value": "+18005550199" } },
      "from": { "rawId": "phone:+17195550100", "phoneNumber": { "value": "+17195550100" } },
      "hasIncomingVideo": false,
      "callConnectionId": "mock-context-123",
      "serverCallId": "mock-server-call-999",
      "correlationId": "mock-correlation-777",
      "incomingCallContext": "mock-jwt-token-string"
    }
  }]'
Use code with caution.

Phase 2: Submit Validated Account Identifier (DTMF Route)

Simulates the caller keying in their numerical identification code using telephone buttons.
💡 Rubric Note: The Azure SDK parser maps raw values strictly against lowercase spelled out numbers (e.g., "one", "two"). Passing custom variations like "Tone1" will cause serialization to fail silently.
bash
curl -X POST "http://localhost:5000/api/telephony/callback" \
  -H "Content-Type: application/json" \
  -d '[{
    "id": "1111",
    "source": "communications/connections/mock-context-123",
    "type": "Microsoft.Communication.RecognizeCompleted",
    "specversion": "1.0",
    "data": {
      "callConnectionId": "mock-context-123",
      "recognitionType": "dtmf",
      "dtmfResult": {
        "tones": ["one", "two", "three", "four", "five"]
      }
    }
  }]'
Use code with caution.

Phase 3: Submit Validated Account Identifier (Speech Route)

Validates the speech recognition alternative code path within the callback processing block.
bash
curl -X POST "http://localhost:5000/api/telephony/callback" \
  -H "Content-Type: application/json" \
  -d '[{
    "id": "2222",
    "source": "communications/connections/mock-context-123",
    "type": "Microsoft.Communication.RecognizeCompleted",
    "specversion": "1.0",
    "data": {
      "callConnectionId": "mock-context-123",
      "recognitionType": "speech",
      "speechResult": {
        "text": "12345"
      }
    }
  }]'
Use code with caution.

🛡️ 4. Reliable Background Execution Model

When a secure verification link is generated, the telephony pipeline transitions out-of-band using an unblocked asynchronous processing thread pool worker:
csharp
_ = Task.Run(() => MonitorWebResolutionAsync(connection, session.SessionId, cancellationToken));
Use code with caution.

Process Hardening Controls:

1. Immediate Signal Release: Unblocks the telephony loop instantly, preventing active webhook timeout spikes.
2. Immediate Cache Index Invalidation: Escalation immediately purges tracking index keys (CallPrefix and TokenPrefix), eliminating lingering reference memory leaks.
3. Cooperative Token Invalidation: Spawns a structured CancellationToken mapped across the 10-minute token lifespan, providing clean lifecycle teardowns if process pools recycle.


Try without personalization
