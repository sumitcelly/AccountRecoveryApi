
 1. System Grading Matrix


Category	Targeted Architectural Objectives
🛡️ Security & Correctness	Anti-enumeration containment loops, secure HTTPS reset path validation, and tamper-evident ticket truth logs (ESC-XXXXXXXX).
🗣️ Conversation Quality	DTMF determinism mapping (DtmfTone native parsing), non-blocking fail-fast throttling, and clean telemetry handoffs.
⏱️ Reliability	120-second verification-code expiry, 10-minute reset-link expiry, and hosted background call monitoring.
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

• Telephony Session: Initially cached for 15 minutes. Some later state updates use the cache's default expiration, so this is not currently a guaranteed hard cap.
• Verification Code Window (120 Seconds Hard Cap): Restricts the window for brute-forcing digits over the voice stream.
• Web Reset Link Window (10 Minutes): The reset-token index expires after 10 minutes, and the reset endpoint also checks the session's link expiry.

🚀 3. Local Simulation & Integration Testing

Use these reproducible cURL test paths to validate the event parser and state transformations in a local development environment.

Phase 1: Simulate Inbound Call Connection

Simulates an incoming telephone connection hitting the inbound route. This allocates the session context with an initial 15-minute cache expiration.
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

🛡️ 4. Hosted Background Call Monitoring

The API registers `AccountRecoveryMonitorService` as a hosted background service and `RecoveryMonitorQueue` as a singleton. After a caller passes the verification-code check and the reset link is issued, the recovery engine enqueues that session for monitoring.

`RecoveryMonitorQueue` uses an in-process `ConcurrentQueue<string>`. The hosted service drains it and checks active sessions on a three-second timer:

1. **Successful reset:** When the session reaches `Completed` with a receipt, the worker plays a completion message to the caller, waits five seconds, and hangs up.
2. **Call disconnected:** When ACS reports the call is no longer connected, the worker stops monitoring that session. The normal call-disconnected callback is responsible for invalidating the session.
3. **Call-state check error:** The worker logs the error and retries on a later tick instead of treating a transient failure as a confirmed disconnect.
4. **Monitoring timeout:** After ten minutes or 200 polling iterations, the worker marks the session `Escalated`, removes its call and token index entries, and attempts to play an escalation message.

The monitor is queued only when verification completes with a non-null `CallConnection`. Offline/test paths that pass `null` do not enqueue monitoring.

**Operational limitations:** Both the queue and active-monitor registry are in memory. Restarting the process loses queued and active monitoring work; multiple API instances do not share monitoring state. This is suitable for a local/demo flow, not a durable production job system. For production, use shared persistent session storage and a durable/distributed queue, and make retries and call actions resilient to duplicate processing.
