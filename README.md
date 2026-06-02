# pr

Compact GitHub PR dashboard, opener, and notification cleaner.

## Install

Release assets are unpacked, self-contained single-file executables. The app
still shells out to `gh`, so install and authenticate GitHub CLI first with
`gh auth login`.

Linux, bash:

```bash
repo=flcl42/pr; dir="$HOME/.local/bin"; arch="$(uname -m)"; asset=pr-linux-x64; case "$arch" in aarch64|arm64) asset=pr-linux-arm64;; esac; mkdir -p "$dir"; curl -fsSL "https://github.com/$repo/releases/latest/download/$asset" -o "$dir/pr"; chmod +x "$dir/pr"; grep -qxF 'export PATH="$HOME/.local/bin:$PATH"' "$HOME/.bashrc" || echo 'export PATH="$HOME/.local/bin:$PATH"' >> "$HOME/.bashrc"
```

macOS, zsh:

```zsh
repo=flcl42/pr; dir="$HOME/.local/bin"; arch="$(uname -m)"; asset=pr-macos-arm64; [ "$arch" = "x86_64" ] && asset=pr-macos-x64; mkdir -p "$dir"; curl -fsSL "https://github.com/$repo/releases/latest/download/$asset" -o "$dir/pr"; chmod +x "$dir/pr"; grep -qxF 'export PATH="$HOME/.local/bin:$PATH"' "$HOME/.zshrc" || echo 'export PATH="$HOME/.local/bin:$PATH"' >> "$HOME/.zshrc"
```

Windows, PowerShell:

```powershell
$repo='flcl42/pr'; $dir='C:\Programs'; New-Item -ItemType Directory -Force $dir | Out-Null; Invoke-WebRequest "https://github.com/$repo/releases/latest/download/pr-windows-x64.exe" -OutFile "$dir\pr.exe"; $p=[Environment]::GetEnvironmentVariable('Path','User'); if (($p -split ';') -notcontains $dir) { [Environment]::SetEnvironmentVariable('Path', ((@($p -split ';') + $dir | Where-Object { $_ }) -join ';'), 'User'); $env:Path += ";$dir" }
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
