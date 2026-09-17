# checkout-service p95 latency incident — plan

Alert: checkout-service p95 latency spiked at 14:12 UTC.

## Triage checklist
1. Pull 30-minute `latency_p95_ms` metrics.
2. Inspect recent deployments for timing correlation.
3. Check current service health.
4. List downstream dependencies and health.
5. Inspect eight recent error logs.
6. Form an evidence-based hypothesis.
7. Propose remediation. Any restart or rollback is a production mutation and will only be invoked after explicit user approval ("go").

All harness telemetry must be described as DEMO DATA.
