# Nethermind Pull Request Review Context

Use this file as repository-specific context in addition to the current diff, PR description, nearby code, tests, and repository guidance. Report only concrete, actionable findings introduced by the PR. Do not turn these heuristics into speculative comments.

## Review Contract

1. Read the PR title, description, base branch, changed files, and complete diff.
2. Classify the affected area: consensus/spec, EVM, transaction pool, RPC, serialization/networking, state/trie, sync, configuration/init, tests, CI, packaging, or tooling.
3. Inspect nearby production code and tests before asserting that a pattern is wrong.
4. For protocol behavior, verify the relevant EIP, execution/consensus specification, or established Nethermind implementation pattern when locally available.
5. Run the narrowest useful tests or static checks when time permits. Never claim a test was run unless it was.
6. Report a finding only when it has a specific failure mode, broken invariant, compatibility risk, resource/lifetime problem, or meaningful maintenance cost.
7. Anchor every finding to an exact line in the PR diff. Immediately before returning output, verify each path, line, and side with `git diff --unified=3 origin/<base>...HEAD -- <path>`.
8. Use `RIGHT` only for added or context lines in the new file and `LEFT` only for deleted lines in the base file. Do not copy a line number from whole-file output without confirming that GitHub can anchor it in the diff.
9. Put cross-cutting or otherwise unanchorable observations in the summary, not in `findings`.

Avoid praise, restating the diff, broad redesign requests, formatting-only nits, and vague suggestions. Prefer a small number of high-confidence comments over a long speculative review.

## Finding Tone

Write comments as factual, collegial observations rather than instructions to the author.

- Use a neutral declarative title such as `Chunk-sizing rationale does not match the matrix`, not an imperative title such as `Update the chunk-sizing rationale to match the matrix`.
- Explain the current behavior, evidence, and impact before discussing a possible resolution.
- Avoid second-person language and direct commands such as `update`, `change`, `add`, or `remove`.
- Avoid prescriptive `must`, `should`, and `need to` wording. When a remedy adds value, describe the desired outcome or phrase it conditionally, such as `Wording that reflects the measured sizing would avoid this ambiguity.`

## Finding Quality

Every finding must explain:

- what input, state, fork, or execution path triggers the issue;
- what observable behavior is wrong;
- why the changed code causes it; and
- what outcome a correction or test would need to establish, when that clarification adds value.

Use severity consistently:

- `critical`: consensus divergence, fund/security risk, or broadly destructive behavior.
- `high`: likely production failure, invalid external behavior, or serious availability/resource risk.
- `medium`: real bug on a narrower path, compatibility regression, race, leak, or missing validation.
- `low`: concrete maintainability or test weakness likely to cause a future defect; not cosmetic style.

Do not report pre-existing issues unless the PR clearly makes them reachable or worse.

## Consensus, Forks, and Protocol Correctness

- Treat changes to block/transaction validation, gas accounting, receipts, state roots, withdrawals, blobs, Engine API, EVM execution, precompiles, sync, and trie/state access as consensus-sensitive until verified.
- Check every fork-dependent rule against the active spec provider and the exact activation condition. Modern forks are generally timestamp-activated; do not infer activation from transaction type ordering or hard-code the latest fork in shared logic.
- Separate consensus validation from TxPool policy and RPC convenience. `TxValidator` is used outside the mempool, including payload and sync validation.
- Exercise positive, negative, empty, maximum, overflow, malformed, and fork-disabled/enabled paths.
- For blob transactions, verify blob count, zero-blob behavior, versioned hashes/proofs, data gas, fee coverage, KZG failures, and both empty and non-empty block behavior.
- Distinguish `ChainId` transaction/signature semantics from `NetworkId` P2P compatibility.
- Compare compatibility-sensitive behavior with the relevant specification, execution-spec tests, Hive scenario, or established client behavior rather than accepting merely plausible behavior.
- Give constants that come from a spec a clear name and stable source reference.

## Serialization, Networking, and External Input

- Validate lengths, counts, offsets, remaining-byte arithmetic, integer bounds, and collection limits before allocating or reading.
- Do not trust P2P, RLP, SSZ, JSON, JSON-RPC, Engine API, or configuration input. Malformed data should produce the intended protocol/RPC error, not an incidental exception or unbounded allocation.
- RLP lists are not null. Invalid RLP must not be silently accepted unless an explicit behavior mode requires it.
- Keep consensus, mempool, and network transaction forms explicit when their encodings differ.
- Preserve field/key order wherever serialized bytes or hashes depend on it.
- When changing a model, inspect converters, copy constructors, serializers, cached responses, and source-generated JSON contexts.
- Prefer typed RPC results and intentional null/missing-field behavior. Preserve machine-readable error codes relied on by callers or test harnesses.
- Check buffer ownership, especially DotNetty `IByteBuffer` and pooled memory, at every handoff and error path.

## State, Concurrency, and Resource Lifetime

- Establish one clear owner and release path for every disposable buffer/message, stream, database handle, timer, cancellation source, channel, pooled collection, and background task.
- Put acquired resources inside the `try`/`using` scope that protects them. Check cancellation and exceptional paths for leaks and double disposal.
- Await background work during shutdown when disposal must guarantee completion; use async disposal when required.
- Treat cancellation using the matching token as normal shutdown, not an error.
- Challenge shared mutable state in RPC handlers, sync, peer pools, caches, schedulers, and parallel tests. Retention limits are not concurrency limits.
- Avoid blocking waits (`.Result`, `.Wait()`) in asynchronous or thread-pool-sensitive paths.
- Ensure background maintenance, compaction, or retries cannot starve block processing or grow without a bound.

## Performance and Allocation

- Review block processing, EVM, transaction validation/sorting, RLP/SSZ, networking, trie/state access, and RPC serialization as hot paths.
- Look for avoidable `ToArray`, `ToList`, LINQ/iterator chains, repeated decoding/spec lookups, temporary collections, delegate/interface dispatch, and whole-file buffering inside repeated paths.
- Prefer spans/memory, direct buffer writes, cached immutable data, bounded pooling, pre-sized collections, and simple readable loops when they measurably reduce work.
- Do not introduce pooling when ownership becomes ambiguous, or `stackalloc` using an untrusted input length.
- Avoid expensive concurrent collection properties in hot paths when they acquire all locks.
- Ask for a benchmark when a PR claims a performance win or makes a non-obvious hot-path tradeoff.

## Architecture, API, and Configuration

- Prefer dependency injection and existing extension points over widening a core interface or adding a parallel abstraction for one consumer.
- Keep responsibilities with their owner: protocol-version behavior in that protocol, retry policy in the retry component, sending in the requestor, and validation in the appropriate layer.
- Avoid changing public defaults, plugin startup behavior, interfaces, RPC shape, or configuration semantics without explicit compatibility analysis.
- New configuration values need correct defaults, units, nullability, documentation, and tests. Do not expose a knob unless the behavior is intentionally user-facing.
- Keep initialization dependencies explicit instead of relying on constructor timing, static access, or incidental startup order.
- Reuse existing serializers and helpers; override only the behavior that differs.

## Tests and Verification

- Every bug fix should have a regression test that fails for the original defect and asserts the important outcome.
- Keep or replace existing coverage deliberately. Deleted test cases need a clear reason.
- Prefer focused parameterization (`TestCase`, `Values`, or `TestCaseSource`) over duplicate methods, and split tests that assert unrelated behaviors.
- Include serializer round trips and invalid encodings for RLP, SSZ, JSON, headers, receipts, transactions, and hash/CRC-sensitive output.
- Cover empty/null/default, maximum/boundary, overflow, malformed, cancellation, disposal, concurrency, and fork-off/fork-on cases introduced by the change.
- Use production-like wiring and existing Nethermind test helpers where available.
- Prefer NUnit assertions in newly touched tests.
- For externally observable protocol changes, consider execution-spec/EEST or Hive coverage in addition to unit tests.
- Use deterministic inputs; seed randomness deliberately.

## Common Nethermind Traps

- Transaction-type numeric ordering is not a capability model. Use explicit capability checks or sets.
- TxPool retry paths must not endlessly re-request and reject the same invalid or underpriced transaction.
- Engine API payload versions should remain statically clear and share centralized validation without blurring version-specific fields.
- Bootnode/discovery changes should parse structured enode/URI data instead of using string surgery.
- Chain-spec activation schedules should stay sorted/cached and carry all required values, such as both target and maximum blob counts.
- RLP decoder registration should use stable instances where available and fail clearly on invalid override behavior.
- Logging in hot paths must guard expensive formatting and retain enough bounded context to diagnose the rejected rule.
- Generated, package, Docker, and workflow changes should avoid unrelated churn and explain regeneration, cache, artifact, and cleanup behavior.

## Final Pass

Before returning findings, check the diff for accidental files, deleted tests, debug code, unrelated dependency/lock changes, stale comments, unused imports, unsafe magic numbers, mismatched type/file names, misspelled config text, and claimed behavior not covered by the shown implementation.

If no actionable issue survives verification, return no findings. Do not invent a comment to make the review look productive.
