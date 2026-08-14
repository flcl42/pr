# pr

Compact GitHub PR dashboard, opener, notification cleaner, and optional AI
review watcher using Codex, Claude, or Kimi.

## Install

Release assets are unpacked, self-contained single-file executables. The app
uses `GH_TOKEN` or `GITHUB_TOKEN` when set, otherwise it reads the active token
from `gh auth token`, so install and authenticate GitHub CLI first with
`gh auth login`. Automatic PR review also requires the configured `codex`,
`claude`, or `kimi` CLI on `PATH`; the reviewer is disabled by default.

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
$repo='flcl42/pr'; Invoke-WebRequest "https://github.com/$repo/releases/latest/download/pr-windows-x64.exe" -OutFile ".\pr.exe"
```

## Usage

```powershell
pr
pr https://github.com/OWNER/REPO
pr OWNER/REPO
pr 10843
pr OWNER/REPO#10843
pr https://github.com/OWNER/REPO/pull/10843
pr --once
pr --cleanup-once
```

Running `pr` with no arguments starts the interactive PR dashboard. Passing a
repository URL or `OWNER/REPO` adds that repository to the tracked list and saves
it in `.pr.yml` next to the executable.

Only one interactive dashboard or maintenance command can run from a given
executable path. Starting another one takes over the active instance. Repository
updates and one-shot PR open commands exit independently and leave a running
dashboard untouched.

If no repositories are tracked, the dashboard stays open with an empty list and
shows the add command instead of exiting.

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
and the configured agent/model plus live reviewer state are shown in the status
area. On startup, directly
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

The selected agent runs against a detached worktree at the exact PR head with
read-only permissions and a validated structured-output contract. Repository context can be stored inline under
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
outcome-focused. A clean result creates nothing by default.

Completed agent results appear in a section above Top PRs. Each row indicates
whether the result was sent, drafted, clean, or kept local. Clicking its title
opens the PR, acknowledges the item, and moves it back to Top or the
regular section. Review state and run artifacts live under `dataDirectory`.
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

Press `F1` to search PR titles in a filter row above the table. Filtering is
interactive, ignored PRs are included while search is active, and `Esc` cancels
the search.

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
    - copilot
codexReview:
  enabled: false
  pollIntervalSeconds: 60
  readyDelayMinutes: 20
  startupScanDays: 4
  eligibilityCheckSeconds: 15
  maxOpenPullRequests: 1000
  agent: codex
  model: gpt-5.6-sol
  sandbox: read-only
  ephemeral: true
  ignoreUserConfig: true
  reasoningEffort: max
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
of age. `dryRun: true` runs the selected agent and keeps local results without
posting to GitHub.

`postNoFindingsComment: true` creates a summary-only review for a clean result;
it follows the current `autoSubmit` delivery mode.

Set `agent` to `codex`, `claude`, or `kimi`. `model` is forwarded through the
selected CLI's `--model` flag. Codex defaults to the pinned `gpt-5.6-sol`; an
omitted Claude or Kimi model uses that CLI's default. Change or remove `model`
when switching agents. Codex uses the configured `sandbox`, Claude runs in
`plan` permission mode with editing tools disabled, and Kimi receives an
explicit read-only agent profile limited to `Read`, `Grep`, and `Glob` plus a
pre-generated PR diff. Kimi's JSONL response is validated against the same
review result contract before anything can be published. `reasoningEffort`,
ephemeral sessions, and ignored user configuration apply where supported by the
selected CLI. An optional `command` can point to a custom executable; built-in
agent names are treated as defaults and do not need an explicit command path.

`workspaceDirectory` is the root for repository clones and PR worktrees. The
directory is created when a review first needs it. It accepts an absolute path,
a path relative to `.pr.yml`, environment variables, or a `~/` home-relative
path. When omitted, it uses `dataDirectory` for backward compatibility.

Context lookup first checks the full case-insensitive repository slug, then the
short repository name for compatibility, then `contextDirectory`, and finally
an embedded context. Inline values use YAML literal blocks (`|-`), so Markdown,
comments, colons, and nested indentation are preserved.

## Release

Tagged commits build and publish these raw executable assets:

- `pr-linux-x64`
- `pr-linux-arm64`
- `pr-windows-x64.exe`
- `pr-windows-arm64.exe`
- `pr-macos-x64`
- `pr-macos-arm64`

Push a tag such as `v1.0.0` or `release/1.0.0` to create a GitHub release.

## License

MIT.
