# Image key proof (batch 1, 2026-10-30; lead re-proof same day)

Single-call proof via `Gate.Eval/Vision/VisionImageProofTests.cs`
(exactly one HTTP POST, no retries).

- Endpoint used: `https://api.meta.ai/v1/images/generations`
  (`MetaApi:BaseUrl` or default + `/images/generations`; no `/v1` doubling).
- Model discovery: one `GET /models` call returned
  `muse-wonder-1.0, sam-3.1, muse-spark-1.3-contributor,
  muse-voice-transcribe-1.0, muse-spark-1.3, muse-image-1.0,
  muse-spark-1.2-contributor, muse-spark-1.2, muse-spark-1.1`.
  Image model pinned to `muse-image-1.0` (code default, overridable via
  `MetaApi:ImageModel` secret/env).
- Earlier result (`vision-b1-3`): HTTP 404 `model_not_found` because the
  test fell back to the chat model. Key already validated then (no 401/403).
- Lead re-proof (`lead-vision-proof-1`) with `muse-image-1.0`: PASS —
  HTTP 200 with an image payload (`b64_json`/`url` present), exactly one
  call. Endpoint plus model proven end to end.

No secret values are recorded here or in any log. The probe image used a
no-text connectivity prompt and was not stored; it is not a deliverable.
