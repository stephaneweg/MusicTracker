---
name: aiclient-httpclient-per-call
description: "AIClient.SendAsync news up an HttpClient per call ON PURPOSE — do not \"fix\" it to a shared static"
metadata: 
  node_type: memory
  type: feedback
  originSessionId: b8c5c1ed-6ca1-420e-b017-e4bc69bb725b
---

The AI provider clients share `AIClient.SendAsync` (Engine/AI/AIClient.cs), which creates a **`new HttpClient { Timeout = 600s }` on every call** instead of one shared static client. This is deliberate — do NOT suggest or apply the usual .NET "one shared HttpClient" guidance here.

**Why:** the user hit bugs where a shared HttpClient got stuck reporting "this instance has already started one or more requests" after a previous crash mid-request; a fresh client per call sidesteps that stuck state. AI calls are occasional (arrangement generation), so socket churn is a non-issue.

**How to apply:** leave the per-call `new HttpClient` as-is in AIClient/SendAsync. If reviewing or refactoring the AI clients, don't flag it. Related: [[mistral-ai-arrangement]].
