# pr

Compact GitHub PR dashboard, opener, and notification cleaner.

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

If no repositories are tracked, the dashboard stays open with an empty list and
shows the add command instead of exiting.

Passing a PR number opens it in the default browser. Bare PR numbers work when a
single repository is tracked. With multiple repositories, use `OWNER/REPO#NUMBER`
or a full pull request URL to avoid ambiguity.

The dashboard resolves the authenticated `gh` username and excludes your own
PRs from the list. It polls GitHub every 3 minutes, so the list can lag by up to
3 minutes. New matching PRs that appear after the initial load trigger an
audible ding.

Titles are terminal hyperlinks in `--once` output. In the interactive
dashboard, click the title column to open a PR directly from the TUI.

The last column is an in-process mouse target for the ignore action; it does not
use an OS URL protocol handler. Startup removes the old `pr-ignore://` handler
if a previous build registered it. Press `I` for the same action from the
keyboard. Ignored PRs are hidden from the dashboard and saved in `.pr.yml`.
Press `Ctrl+I` to reveal ignored PRs that still need review; press `I` or click
`unignore` on a revealed ignored PR to unignore it.

Press `F1` to search PR titles in a filter row above the table. Filtering is
interactive, ignored PRs are included while search is active, and `Esc` cancels
the search.

While the dashboard runs, it also cleans stale GitHub notification threads for
tracked repositories only: closed, merged, draft, or inaccessible PR
notifications and closed or inaccessible issue notifications are marked as read.
Cleanup runs shortly after startup and then once per hour. Press `C` in the
dashboard to run cleanup on demand. Cleanup also checks ignored PRs and removes
them from the ignored list after they are closed or merged.

## Settings

Settings are stored next to the executable in `.pr.yml`:

```yaml
requiredApprovals: 2
repositories:
  - https://github.com/OWNER/REPO
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
    - copilot
```

The priority values are additive. `superHotThreshold` marks PR numbers red when
the score is above that value, `hotThreshold` marks PR numbers yellow at or
above that value, and lower scores stay green. Comment counts use distinct
human commenters after excluding the PR author and any `ignoredCommentAuthors`
pattern.

## License

MIT.
