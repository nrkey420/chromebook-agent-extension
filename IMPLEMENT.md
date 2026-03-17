# Implementation Rules
- Read README.md and docs/architecture.md before coding.
- Make small scoped changes.
- Run tests after each milestone.
- Update docs with each functional change.
- Never commit secrets.
- Prefer straightforward implementations.
- Use Azure SQL views as Power BI contract layer.
- Raw Blob write path is highest priority.
- SQL is attribution/reporting system of record.
- Sentinel is security hunting sink.

## Milestones
M1 scaffold docs
M2 extension managed config/session lifecycle
M3 collector HMAC + blob writes
M4 SQL schema/write path
M5 Sentinel ingest
M6 infra/scripts
M7 end-to-end validation and docs polish
