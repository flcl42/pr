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

While the dashboard runs, it also cleans stale GitHub notification threads for
tracked repositories only: closed, merged, draft, or inaccessible PR
notifications and closed or inaccessible issue notifications are marked as read.
Cleanup runs on startup and then once per hour. Press `C` in the dashboard to
run cleanup on demand.

## Settings

Settings are stored next to the executable in `.pr.yml`:

```yaml
requiredApprovals: 2
repositories:
  - https://github.com/OWNER/REPO
```
