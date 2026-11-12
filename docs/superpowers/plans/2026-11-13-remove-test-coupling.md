# Remove test coupling implementation plan

**Goal:** Remove the four production/test dependencies confirmed in the review, preserving server features and diagnostic coverage.
**Architecture:** Runtime configuration selects storage credentials, databases, cache namespace and discovery root. Diagnostics default to disabled at both HTTP and stream boundaries; their opaque transport payload has no Ping/Pong dependency in the core frame. GM has its own entity ping messages. Test clients supply Profile requests and fixture values.
**Tech stack:** Existing .NET 10, Protobuf/gRPC, YAML, MongoDB, Redis, ZooKeeper and PowerShell tooling.
**Spec:** The four findings in the current conversation, authorized by “全部清除”. Existing local development endpoints remain environment configuration, not production defaults.
**Execution:** Native implementation in an isolated worktree from 9e41145; do not touch the concurrent DBMgr checkout, merge main or push.

- [x] Add regression for disabled diagnostics before implementation and observe failure.
- [x] Replace TestStorage with ServerSettings and StorageConnections. Select YAML via TGAME_SERVER_CONFIG; require explicit database/user/password-variable/cache/discovery values. Include a local environment YAML with diagnostics disabled. Launchers explicitly select it; the integration launcher separately enables diagnostics.
- [x] Give GM independent request/reply messages and routing; replace typed probe fields in Frame with opaque diagnostic payloads. Keep existing permissions, readiness, correlation and disconnect semantics.
- [x] Reject diagnostic traffic by default, including direct gRPC traffic; opt in explicitly in integration runs. Move Profile fixture values to tests and send actual ProfileRequest payloads from the test client. Server diagnostic bridge only validates and forwards supplied requests.
- [x] Pin the selected YAML and local credential-source marker per console run; verify reattach/recovery after parent configuration changes.
- [x] Update configuration, console launchers, protocol generation, tests and docs together. No duplicate source of environment values and no legacy credential fallback in server code.
- [x] Verify config validation, opt-in boundaries, GM without diagnostics, Profile ownership, solution build, console checks and the 8-process integration test. Scan production for old test constants and transport dependencies.
- [x] Review and commit only this task on codex/remove-test-coupling.

Review focus: disabled diagnostics cannot be bypassed on a peer stream; GM succeeds with diagnostics off; Profile mutation remains restricted to the owning entity; selected environment controls every storage/discovery namespace; existing console and integration launch paths still work.
