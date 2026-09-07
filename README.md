# pr

Compact GitHub PR dashboard, opener, notification cleaner, local project
reviewer, and optional collaborative PR review watcher using Codex, Claude,
Kimi, DeepSeek through Deep Code, and OpenCode.

## Install

CLI release assets are unpacked, self-contained single-file executables. The
optional Windows desktop companion is a self-contained zip installed beside
the CLI. The app uses `GH_TOKEN` or `GITHUB_TOKEN` when set, otherwise it reads
the active token from `gh auth token`, so install and authenticate GitHub CLI
first with `gh auth login`. Automatic PR review also requires each enabled
`codex`, `claude`, `kimi`, `deepcode`, or `opencode` CLI on `PATH`; the reviewer
is disabled by default.

Linux, bash:

```bash
repo=flcl42/pr; arch="$(uname -m)"; asset=pr-linux-x64; case "$arch" in aarch64|arm64) asset=pr-linux-arm64;; esac; curl -fsSL "https://github.com/$repo/releases/latest/download/$asset" -o ./pr; chmod +x ./pr
```

macOS, zsh:

```zsh
repo=flcl42/pr; arch="$(uname -m)"; asset=pr-macos-arm64; [ "$arch" = "x86_64" ] && asset=pr-macos-x64; curl -fsSL "https://github.com/$repo/releases/latest/download/$asset" -o ./pr; chmod +x ./pr
```

Windows, PowerShell:

```powershell
$repo='flcl42/pr'; $zip=Join-Path $env:TEMP 'pr-ui-windows-x64.zip'; Invoke-WebRequest "https://github.com/$repo/releases/latest/download/pr-windows-x64.exe" -OutFile ".\pr.exe"; Invoke-WebRequest "https://github.com/$repo/releases/latest/download/pr-ui-windows-x64.zip" -OutFile $zip; Expand-Archive $zip ".\pr-ui" -Force; Remove-Item $zip
```

## Usage

```powershell
pr
pr https://github.com/OWNER/REPO
pr OWNER/REPO
pr 10843
pr OWNER/REPO#10843
pr https://github.com/OWNER/REPO/pull/10843
pr ui
pr --once
pr --cleanup-once
```

Running `pr` with no arguments starts the interactive PR dashboard. Passing a
repository URL or `OWNER/REPO` adds that repository to the tracked list and saves
it in `.pr.yml` next to the executable.

Only one interactive dashboard or maintenance command can run for a given
settings file. Starting the console or desktop dashboard takes over the active
instance that uses the same `.pr.yml`. Repository updates and one-shot PR open
commands exit independently and leave a running dashboard untouched.

If no repositories are tracked, the dashboard stays open with an empty list and
shows the add command instead of exiting.

### Desktop UI

`pr ui` starts an optional Windows MAUI companion using the same `.pr.yml` as
the CLI. It provides the grouped Reviewed, Top, and regular PR table; interactive
search; urgency breakdowns; repository addition; refresh and notification
cleanup; weekly stats; ignore and Top actions; manual review queueing; review
enablement and delivery controls; per-agent checkboxes plus model and effort
selectors; and a persistent activity journal. Refreshes, cleanup runs, and each
PR review are separate journal groups. Review groups show eligibility, worktree
preparation, every configured agent, the final activity check, and GitHub
publication as connected steps. The latest 30 operations are stored in
`journal.json` under `codexReview.dataDirectory`. PR agent, model, and effort
choices are saved to `.pr.yml`. Its Local review page saves project directories,
accepts typed paths or a native folder picker, provides per-run agent, model,
and effort choices, and shows the same connected-step timeline. PR titles
open GitHub, while PR numbers open the urgency calculation.
Reviewed titles start with the agents that completed successfully: `C` for
Codex, `c` for Claude, `K` for Kimi, `D` for DeepSeek, and `O` for OpenCode.
Failed agents are omitted, so `[CcK]` means those three stages completed.

The default solution deliberately excludes MAUI and needs only the .NET SDK:

```powershell
dotnet build Pr.slnx -c Release
```

Build the desktop companion explicitly on Windows with the `maui-windows`
workload. Publish it into a `pr-ui` directory beside `pr.exe`, which is one of
the locations discovered by `pr ui`:

```powershell
dotnet workload install maui-windows
dotnet publish Pr.Maui/Pr.Maui.csproj -c Release -r win-x64 --self-contained true -p:WindowsPackageType=None -o C:\Programs\pr-ui
```

For a development output in another location, set `PR_UI_PATH` to the full
`pr-ui.exe` path. The launcher passes the CLI settings path through
`PR_SETTINGS_PATH`, so the companion never creates a separate configuration.

### Local directory review

Open **Local review** in the Windows desktop companion, add or select a project
directory, choose any combination of Codex, Claude, Kimi, Deep Code, and
OpenCode, select their models, and press **Review it**. Local selections apply
only to that run; they do not change the automatic PR review pipeline. The
selected agents run in order against a disposable Git
snapshot. For a Git repository root, the review scope is the branch diff against
the default branch plus staged, unstaged, and untracked changes. A non-Git
directory remains a complete-project review. Agents may modify and test the
snapshot, and it is reset to the original snapshot commit after every agent.
Different project directories can be reviewed concurrently; a second review of
the same directory is rejected until its current run finishes.

The selected source is not used as an agent worktree. The only file written to
it is `pr-review.md` in the selected top-level directory after the pipeline
finishes. That report contains the combined, deduplicated, severity-ordered
issues, agent status, and failures. An all-agent failure also produces a report
with the failure state. The previous `pr-review.md` is excluded from later
snapshots. Per-directory progress and the latest result are persisted in
`local-review-state.json` under `codexReview.dataDirectory`. Selecting a saved
directory restores that state after reopening the app; a run interrupted by an
app stop is shown as interrupted rather than silently discarded.

For a Git working tree, the snapshot includes tracked files and untracked files
that are not ignored by Git, while unchanged files are available only as review
context. For a non-Git directory, it skips conventional
VCS, dependency, build, coverage, and IDE-output directories. Symbolic links and
reparse points are not followed. Temporary worktrees use
`codexReview.workspaceDirectory`; prompts, results, logs, and the collaborative
ledger use `codexReview.dataDirectory`.

Local project context can use the selected directory name or its absolute path
as a key under `codexReview.contexts`. A `<directory-name>.md` file in
`contextDirectory` is the fallback. `maxFindings`, per-agent model, effort,
command, timeout, and sandbox are shared with PR reviews. The configured enabled
states provide the initial checkbox selection and can be
overridden for each local run. The watcher's top-level `enabled` switch is not
required for an explicitly started local review.

Passing a PR number opens it in the default browser. Bare PR numbers work when a
single repository is tracked. With multiple repositories, use `OWNER/REPO#NUMBER`
or a full pull request URL to avoid ambiguity.

The dashboard resolves the authenticated `gh` username and excludes your own
PRs from the list. It polls GitHub every 15 minutes, so the list can lag by up
to 15 minutes. Each refresh scans up to 1000 matching PRs per GitHub search
batch. New matching PRs that appear after the initial load trigger an audible
ding.

GitHub reads share one pooled HTTP/2 connection and are serialized with retries
for transient transport and server failures. Hotness activity is cached by each
PR's `updatedAt` value and recalculated locally, so an unchanged refresh normally
needs only the paged search request. Notification and ignored-PR status checks
are grouped into GraphQL batches of up to 50 instead of one request per item.

Titles are terminal hyperlinks in `--once` output. In the interactive
dashboard, click the title column to open a PR directly from the TUI.

Press `V` to enable or disable automatic agent review. The setting is persisted,
and the configured agent pipeline plus live reviewer state are shown in the
status area. On startup, directly
requested PRs created within the last four days are armed and begin the same
20-minute timer; older existing PRs are baselined without review. After that, a
newly discovered PR or newly added direct review request for the authenticated
user starts the timer while the PR remains open, non-draft, and requested from
that user. A later head change resets the timer. Your own and dashboard-ignored
PRs are skipped.

Press `E` to force an immediate agent review of the selected PR. Manual queue
entries are persisted in `.pr-review/state.json` and run even while automatic
review is off. A forced review bypasses the timer, direct-review-request,
draft, own-PR, ignore, previous-review, approval, and commenter gates. It still
requires a tracked, open PR from an `OWNER`, `MEMBER`, or `COLLABORATOR`, and
cancels if the head commit or authenticated user changes. PRs from external
contributors are never queued, including through `E`. Forced reviews stage
only actionable findings; a clean result creates nothing. With automatic review
off, the worker resolves only manually queued PRs.

Press `A` to switch review delivery between drafts and automatic submission. The
choice is persisted as `codexReview.autoSubmit`; it affects both automatic and
manually forced reviews.

When the timer expires, the watcher counts current approvals and conversation,
inline, and non-empty review comments. It reviews only while both counts remain
below two distinct humans after excluding the PR author and configured bot
patterns. It checks again before publication. An external author association,
head change, draft transition,
closed PR, removed review request, user change, ignore action, or disabled
automatic watcher cancels stale automatic work. A manual queue entry remains
eligible when automatic review is off and is removed after completion, closure,
head change, account change, or explicit queue loss.

Enabled agents run sequentially against one detached worktree at the exact PR
head with permission to inspect, modify, and test code under a validated
structured-output contract. Edits are investigative only. After every agent,
including a failed, timed-out, or canceled agent, the coordinator force-checks
out the original PR head, removes untracked and ignored outputs, verifies the
worktree is clean, and restores the shared ledger before starting the next
agent. A reset failure stops the pipeline.
Every agent process starts in that worktree. Its absolute path and the separate
repository-cache path are written into the prompt, agent metadata, and initial
shared-ledger record, so repository commands and any tests allowed by the
configured tool policy have an unambiguous working directory. Repository
context can be stored inline under
`codexReview.contexts` in `.pr.yml`; use the full `OWNER/REPO` slug to avoid
collisions. Inline context takes precedence over `<repo>.md` next to the
executable and the embedded Nethermind fallback. Every finding is validated
against the local PR diff before a pooled GitHub API connection either creates a
server-side `PENDING` review or immediately submits a `COMMENT` review. If GitHub
reports a conflict with an existing pending review at the same head, missing
inline threads are appended without submitting or replacing that draft. A
pending review from an older head must be submitted or discarded first. The
review body is a short severity-and-files summary with no agent attribution or
review-state text. Finding titles describe the observed problem rather than
directing the author, and finding bodies keep remedies conditional and
outcome-focused. Before each stage, the agent reads `pr-<number>.review.jsonl`
from the worktree. The coordinator appends validated findings and agent failures
to that shared ledger, instructs later agents not to repeat the same underlying
defect, and publishes the accumulated findings once after the pipeline finishes.
An agent error advances to the next enabled agent; a job fails and retries only
when every enabled agent fails. A clean result creates nothing by default.

Completed agent results appear in a section above Top PRs. Each row indicates
whether the result was sent, drafted, clean, or kept local. Clicking its title
opens the PR, acknowledges the item, and moves it back to Top or the
regular section. Review state, per-agent prompts/results/logs, and a preserved
copy of the collaborative JSONL ledger live under `dataDirectory`.
Cached repositories and temporary worktrees live under `workspaceDirectory`,
which falls back to `dataDirectory` when it is omitted.

The last column is an in-process mouse target for the ignore action; it does not
use an OS URL protocol handler. Startup removes the old `pr-ignore://` handler
if a previous build registered it. Press `I` for the same action from the
keyboard. Ignored PRs are hidden from the dashboard and saved in `.pr.yml`.
Press `Ctrl+I` to reveal ignored PRs that still need review; press `I` or click
`unignore` on a revealed ignored PR to unignore it.

Press `T` to move the selected PR into or out of the top section. Top PRs are
saved in `.pr.yml`, stay otherwise normal, and are separated from the rest by a
single row when visible. Closed and merged entries are removed by the regular
cleanup pass.

Press `F1` to search PR titles, numbers, or authors in a filter row above the
table. Filtering is interactive, ignored PRs are included while search is
active, and `Esc` cancels the search.

Press `S` to open this week's tracked-repository stats for your GitHub user:
non-draft PRs you created that are still open or merged, and review submissions
you made. Press `B` or `Esc` to return to the dashboard.

While the dashboard runs, it also cleans stale GitHub notification threads for
tracked repositories only: closed, merged, draft, or inaccessible PR
notifications and closed or inaccessible issue notifications are marked as read.
Cleanup runs shortly after startup and then once per hour. Press `C` in the
dashboard to run cleanup on demand. Cleanup also checks ignored PRs and removes
them from the ignored list after they are closed or merged. The same batched
state check removes closed or merged PRs from the top list.

## Settings

Settings are stored next to the executable in `.pr.yml`:

```yaml
requiredApprovals: 2
repositories:
  - https://github.com/OWNER/REPO
localReviewDirectories:
  - C:\src\project
topPullRequests:
  - https://github.com/OWNER/REPO/pull/10844
ignoredPullRequests:
  - https://github.com/OWNER/REPO/pull/10843
priority:
  superHotThreshold: 40
  hotThreshold: 25
  noComments: 30
  oneCommenter: 15
  twoOrMoreCommenters: -30
  tenDaysNoReviews: 15
  tenDaysOneReview: 10
  reviewRequestedFromUser: 30
  fiveDaysNoReviews: 10
  fiveDaysOneReview: 5
  noReviewsNoComments: 10
  twentyDays: 35
  lessThanThreeHoursNoComments: -30
  ignoredCommentAuthors:
    - "[bot]"
    - bot
    - codex
    - claude
    - kimi
    - deepseek
    - deepcode
    - opencode
    - copilot
codexReview:
  enabled: false
  pollIntervalSeconds: 60
  readyDelayMinutes: 20
  startupScanDays: 4
  eligibilityCheckSeconds: 15
  maxOpenPullRequests: 1000
  agents:
    codex:
      enabled: true
      model: gpt-5.6-sol
      effort: max
    claude:
      enabled: false
      model: opus
      effort: max
    kimi:
      enabled: false
      model: kimi-code/k3
    deepseek:
      enabled: false
      model: deepseek-v4-pro
      effort: max
    opencode:
      enabled: false
  sandbox: workspace-write
  ephemeral: true
  ignoreUserConfig: true
  maxFindings: 25
  skipWhenApprovalCountAtLeast: 2
  skipWhenUniqueCommentersAtLeast: 2
  skipOwnPullRequests: true
  timeoutMinutes: 90
  failureRetryMinutes: 2
  postNoFindingsComment: false
  autoSubmit: false
  processExistingOnFirstRun: false
  dryRun: false
  dataDirectory: .pr-review
  workspaceDirectory: .pr-review
  contextDirectory: .
  contexts:
    "OWNER/REPO": |-
      Review this repository using its architecture and contribution rules.
      Treat generated files as outputs and trace findings to their source.
  ignoredAuthorPatterns:
    - "[bot]"
    - bot
    - codex
    - claude
    - kimi
    - deepseek
    - deepcode
    - opencode
    - copilot
```

The priority values are additive. `superHotThreshold` marks PR numbers red when
the score is above that value, `hotThreshold` marks PR numbers yellow at or
above that value, and lower scores stay green. Comment counts use distinct
human commenters and approvers after excluding the PR author and any
`ignoredCommentAuthors` pattern.

The review watcher state is intentionally separate from `.pr.yml` so frequent
poll updates do not rewrite user settings. `startupScanDays` controls the
startup catch-up window and `0` disables it. `processExistingOnFirstRun: true`
still opts into every already-requested PR after a fresh state file, regardless
of age. `dryRun: true` runs the enabled pipeline and keeps local results without
posting to GitHub.

`postNoFindingsComment: true` creates a summary-only review only when every
enabled agent completed cleanly; it follows the current `autoSubmit` delivery
mode.

`agents` is an ordered map. Its file order is the review order. Every agent block
accepts `enabled`, `model`, `effort`, and an optional `command` override. Omit
`model` to use that CLI's configured default. Legacy top-level `agent`, `model`,
`reasoningEffort`, and `command` settings remain readable and are migrated to a
single enabled pipeline entry the next time settings are saved.

Codex receives `model` and `effort` through its CLI. The default
`workspace-write` policy uses Codex's automatically reviewed approval mode so
local inspection, temporary edits, and tests can run without prompts;
`danger-full-access` uses Codex's explicit sandbox bypass and should be selected
only for disposable, trusted worktrees. With `ignoreUserConfig: true`, Codex
also ignores external execution-policy rules. Claude receives both values,
runs in `auto` permission mode, and exposes only local read, search, shell,
edit, and write tools. Kimi receives `model`, but its CLI does not expose an
effort control; any configured Kimi effort is ignored. Its non-interactive
prompt mode applies Kimi's automatic permission policy implicitly, with an
explicit profile exposing `Read`, `Grep`, `Glob`, `Write`, `Edit`, and `Bash`,
plus a pre-generated scope manifest. DeepSeek runs through the `deepcode` CLI
in a cross-platform pseudo terminal because Deep Code requires a TTY. Deep Code
receives the model through `DEEPCODE_MODEL`; its effort may be `high` or `max`
and is passed through `DEEPCODE_REASONING_EFFORT`. A temporary project policy
allows reads, writes, deletion, tests, and Git inspection inside the worktree
while denying access outside it, network access, MCP, and Git-history mutation.
The original project policy is restored after the stage. OpenCode runs through
`opencode run --format json --auto`; `model` maps to `--model`, `effort` maps to
`--variant`, and omitted values use OpenCode's configured defaults. With
`ignoreUserConfig: true`, external OpenCode plugins are disabled through
`--pure` while its built-in local editing and shell tools remain available.
OpenCode data, cache, and state are kept under `workspaceDirectory/opencode`
instead of the system drive. Its own Git snapshots are disabled because the
review coordinator already creates and resets a disposable worktree for every
agent stage.

Each agent returns the same `summary` plus `findings` JSON object. The
coordinator validates the schema and diff anchors, removes exact repeated
findings, and appends accepted entries to the shared JSONL ledger.
`timeoutMinutes` applies separately to each agent. The worktree reset also runs
after timeouts and cancellation. Ephemeral sessions and ignored user
configuration apply where the selected CLI supports them.

`workspaceDirectory` is the root for repository clones and PR worktrees. The
directory is created when a review first needs it. It accepts an absolute path,
a path relative to `.pr.yml`, environment variables, or a `~/` home-relative
path. Cached clones use `<workspaceDirectory>/repositories/<owner>-<repo>`;
agents run in `<workspaceDirectory>/worktrees/<owner>-<repo>-pr-<number>`.
When omitted, it uses `dataDirectory` for backward compatibility.

Context lookup first checks the full case-insensitive repository slug, then the
short repository name for compatibility, then `contextDirectory`, and finally
an embedded context. Inline values use YAML literal blocks (`|-`), so Markdown,
comments, colons, and nested indentation are preserved.

## Release

Tagged commits build and publish these release assets:

- `pr-linux-x64`
- `pr-linux-arm64`
- `pr-windows-x64.exe`
- `pr-windows-arm64.exe`
- `pr-macos-x64`
- `pr-macos-arm64`
- `pr-ui-windows-x64.zip`
- `pr-ui-windows-arm64.zip`

Push a tag such as `v1.0.0` or `release/1.0.0` to create a GitHub release.

## License

MIT.
