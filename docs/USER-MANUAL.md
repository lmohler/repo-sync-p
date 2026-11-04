# Repo Sync v3 - User Manual

Repo Sync v3 (version 3.1.0) is a Windows app for everyday git work on GitHub and GitLab, plus an SVN round trip
for repos that still live in SVN. You can see every repo at once, commit, sync, branch, review merge requests /
pull requests and watch pipelines / GitHub Actions without typing git commands. Everything runs through your own
`git.exe` (your hooks, credential manager and global config apply), and every git command the app runs is shown in
the Activity panel.

This manual is organised by task. Each section explains what a feature does, then walks through an example.
On GitHub, the app says *pull request* where this manual says *merge request (MR)*, and *Actions* / *workflow run*
where it says *Pipelines* / *pipeline*.

---

## Contents

1. [Getting started](#1-getting-started)
2. [The main window](#2-the-main-window)
3. [The repo's Overview tab](#3-the-repos-overview-tab)
4. [Profiles and identity](#4-profiles-and-identity)
5. [Daily loop: commit, pull, push](#5-daily-loop-commit-pull-push)
6. [Staging part of a file (lines and hunks)](#6-staging-part-of-a-file-lines-and-hunks)
7. [Branches](#7-branches)
8. [History, search and the branch graph](#8-history-search-and-the-branch-graph)
9. [Rewriting commits (interactive rebase)](#9-rewriting-commits-interactive-rebase)
10. [Undo and recovery](#10-undo-and-recovery)
11. [Conflicts](#11-conflicts)
12. [Merge requests / pull requests](#12-merge-requests--pull-requests)
13. [Pipelines / GitHub Actions](#13-pipelines--github-actions)
14. [Stashes and tags](#14-stashes-and-tags)
15. [Worktrees](#15-worktrees)
16. [SVN round trip](#16-svn-round-trip)
17. [Working with many repos at once](#17-working-with-many-repos-at-once)
18. [Repo health, remotes and submodules](#18-repo-health-remotes-and-submodules)
19. [Command palette and keyboard shortcuts](#19-command-palette-and-keyboard-shortcuts)
20. [Background fetch, notifications and the tray](#20-background-fetch-notifications-and-the-tray)
21. [Settings reference](#21-settings-reference)
22. [Safety rules](#22-safety-rules)
23. [What's in this folder, and the files the app keeps](#23-whats-in-this-folder-and-the-files-the-app-keeps)
24. [Troubleshooting](#24-troubleshooting)

---

## 1. Getting started

### Install and run

- **Published copy:** run `repo-sync-v3.exe`. It is one self-contained file (no .NET install needed). The app keeps
  `settings.json`, `tokens.json` and its other files next to the exe, so the whole folder is portable (see
  [section 23](#23-whats-in-this-folder-and-the-files-the-app-keeps)). Download it from the GitHub Releases page.
- **From source** (.NET 8 SDK, Windows):

| Command | Does |
|---|---|
| `dotnet build RepoSyncV3.slnx` | Debug build to `RepoSyncV3\bin\Debug\net8.0-windows`. |
| `dotnet run --project RepoSyncV3` | Builds and runs the Debug copy. |
| `dotnet publish RepoSyncV3 -c Release -o dist` | Publishes the self-contained `repo-sync-v3.exe` to `dist`. |

Windows can't replace a running exe, so close the app (tray icon > Exit) before publishing over it. A publish never
touches the settings, tokens, feed or cache files next to the exe.

The Debug copy keeps its own `settings.json` and `tokens.json` in `bin\Debug\net8.0-windows`, separate
from the published copy. Don't run both against the same repos at once.

### First-time setup

Until you've added a token, set a commit identity and added a repo, a **Get started** checklist sits at the top of
the Home page. Each step has a button (**Accounts...**, **Profiles...**, **Clone...**, **Add...**, **Scan...**) and
ticks itself off. Close it with the X if you don't need it.

1. **Add a token for each server.** Click the **key** button (Accounts) at the top right.
   1. Pick **GitHub**, or pick **GitLab** and enter the server (for example `gitlab.example.com`).
   2. Click **Create a token**. The token page opens with the right scopes already ticked (GitHub: `repo`,
      `workflow`, `read:org`; GitLab: `api`).
   3. Paste the token and click **Test & save**.

   One token per server is used by every profile. Tokens are encrypted for your Windows account. The **SAVED** list
   shows each server with a **Remove** button.
2. **Set up your profiles.** Open **Settings > Profiles** (or the gear next to the profile picker). For each profile
   (you start with **Personal** and **Work**) enter:
   - **COMMIT AS (NAME / EMAIL)**: the identity to commit with. Empty means no identity check for that profile.
   - **CLONE INTO**: the folder new clones go into.
   - **BACKGROUND FETCH EVERY (MINUTES, 0 = OFF)**: default 10.
3. **Add repos** with **Add repo** in the toolbar. The menu has four items:
   - **Clone from GitHub / GitLab...**: pick an account and a project from the list (with a filter box and a reload
     button), or paste a **CLONE URL**. Tick **SSH** for an SSH URL. Pick the **PROFILE** and **CLONE INTO**
     folder; the window shows *Will clone into ... and commit as \<email\>*. The folder you clone into becomes that
     profile's clone folder, and the new clone gets the profile identity in its local git config. With no clone
     folder set, it uses `%USERPROFILE%\code\<profile>`.
   - **Add existing folder(s)...**: pick one or more folders that are already repos. A folder that isn't a repo
     gives the banner *Not a git repository*.
   - **Scan a folder for repos...**: finds every repo under a folder and lists them (REPO / PATH, all ticked, with
     **All** / **None**). Repos already in any profile are left out; if nothing is new you see *No new repositories
     found*.
   - **Add SVN repo...**: see [section 16](#16-svn-round-trip).

   With **All profiles** loaded and more than one profile, Add and Scan ask *Which profile?*. Otherwise new repos
   go into the loaded profile. Linked worktrees of a repo you add, scan or load are added as cards automatically.

> **Example: first morning setup**
> 1. Accounts > GitLab > `gitlab.example.com` > Create a token > paste > Test & save.
> 2. Settings > Profiles > Work > commit as `Jane Doe` / `jane.doe@example.com`.
> 3. Pick **Work** in the profile picker, then Add repo > Scan a folder for repos > `C:\Users\<you>\code\work`.
>    Those repos appear as cards in Work.

### Moving to a new computer

1. **On the old computer**, open **Settings > Export / migrate** and click **Export repos and settings...**. The
   file (default `Desktop\repo-sync-export-<MACHINE>-<yyyyMMdd-HHmm>.json`) holds every repo (folder, remote URLs,
   branches, profile, identity), your app settings, your global git config, the servers you have tokens for and the
   names of your SSH keys and `~/.ssh/config` host aliases. **Show file** appears once it's saved.
2. The tab then lists work that exists only on this computer: uncommitted files, stashes, unpushed commits,
   local-only branches, repos with no remote, missing folders and SVN working copies. Push or commit that work first.
3. **On the new computer**, open **Settings > Export / migrate > Import from a file...** and pick the file. (This
   closes Settings and drops unsaved changes there.)
   - **Repositories (N to clone, M to add)**: repos that aren't here yet are cloned, and folders that already exist
     are added. Columns: Repo, **Folder on this computer** (editable), **What happens**, **Clone from**. Paths in
     your old user folder are moved to your new one. Rows can also say *Already in the list*, *Folder exists but
     isn't a git repo* or *No remote - copy the folder by hand*. **All** / **None** tick every row.
   - **Settings (N differ)**: only settings that differ are listed, grouped as *App settings* and *Global git
     config*, with this computer's value next to the file's. Tick **Take** to use the file's value; unticked rows
     keep this computer's. **Keep all mine** / **Take all from file** set every row. Some rows can't be taken
     (*secret - not in the file*, *only on this computer - kept*, *not used by this version*).
4. Click **Import** and confirm the summary. The banner says *Import done* or *Import finished with problems*.

After a clone, the other remotes and push URLs, the branch you had checked out and the repo's own identity are set
up again; each repo's display name, pin, watch state and profile come back too. The file doesn't hold tokens, SSH
keys or passwords (passwords in URLs and secret-looking git config values are masked as `***`). The import window
lists the servers to add tokens for under Accounts and the SSH keys to copy into `~/.ssh`. Window size, the last
open repo, quick-command history and the loaded profile are not migrated. Worktrees and SVN working copies aren't
cloned: add worktrees again from the main repo's Worktrees tab, and set SVN repos up again with Add SVN repo.

---

## 2. The main window

```
+-----------------------------------------------------------------------------------+
| Repo Sync  [Work] 2 need you  1 failing | Add repo v  Refresh  Fetch/Pull/Commit/ |
|  (logo = Home)                          | Push all | key  gear  light/dark  ?  log|
+---------------------+-------------------------------------------------------------+
| [Profile v] (gear)  | <- Home  repo name  state   path  fetched 2m ago            |
| Filter repos ...    | commits as Name <email>   [Work · GitLab]  Folder Editor   |
| All Pinned Needs me | Web  More                                                   |
| ...  (chips+counts) | Fetch Pull(3) Push Pull&push | Start branch Switch Update  |
| +-----------------+ | from main  Create MR  Finish branch                         |
| |AB repo-a  2 to  | |-------------------------------------------------------------|
| |   push  main  o | | Overview | Changes | Branches | History | MRs | Pipelines...|
| +-----------------+ |                    tab content                              |
+---------------------+-------------------------------------------------------------+
| ACTIVITY  (every git command)   Clear  Hide   git [quick command] Run Terminal    |
+-----------------------------------------------------------------------------------+
| Background fetch every 10 min        key hints           F1 all shortcuts         |
+-----------------------------------------------------------------------------------+
```

The window title reads *Repo Sync \<version\> - \<repo\> (\<profile\>)*. The window size and maximized state are
remembered, and each profile remembers its last open repo and reopens it when you switch back.

### Top bar and toolbar

- **Repo Sync / GitHub + GitLab** (the logo, top left) opens the Home page.
- The **profile pill**, then either *Nothing needs you* or clickable counters *N need you*, *N failing* and
  *N wrong identity*. Clicking a counter filters the repo list.
- Toolbar: **Add repo** (menu, see [section 1](#first-time-setup)), **Refresh** (F5), **Fetch all** (Shift+F5),
  **Pull all**, **Commit all**, **Push all**, then **Accounts** (key), **Settings** (gear), **Light / dark**
  (Ctrl+Alt+T), **Keyboard shortcuts** (? / F1) and **Show / hide the activity log** (Ctrl+Shift+L).

### Repo list (sidebar)

- **Profile picker** at the top, with a gear that opens Settings > Profiles. See [section 4](#4-profiles-and-identity).
- **Filter box** (Ctrl+F): matches repo name, branch, path, MR title and server project path. To search *inside*
  repos use Ctrl+Shift+F (see [Home tools](#home-page)).
- **Filter chips**, each with a count: **All**, **Pinned**, **Needs me**, **Changes**, **To push**, **On a branch**,
  **Failing**, and **Wrong identity** (only shown when its count is above 0).
- An empty profile shows *No repos in \<profile\> yet* with **Clone**, **Add a folder** and **Scan a folder** buttons.

### Repo cards

Each card has a monogram avatar (color from the repo name, or your own under Repo settings > AVATAR COLOR), a pin
icon when pinned, an eye icon when watched (included in background fetch; it is only an indicator), the name, a
colored stripe, a state label and a profile dot (its tooltip shows *profile · server*). A progress bar runs while
the repo is busy.

| Stripe | State label | Means |
|---|---|---|
| Green | up to date | Nothing to do |
| Blue | N to push / not published | You have commits to push, or the branch isn't on the server yet |
| Amber | N change(s) | You have uncommitted changes |
| Purple | N to pull | The server has commits to pull |
| Orange | diverged (A / B) | Both sides have new commits |
| Red | N conflict(s), merge in progress, rebase in progress, error, folder not found | Something stopped, or the folder is gone |
| Grey | loading... | First status read is still running |

Badges on the card show the branch, push / pull / change counts, the branch's MR (for example `!12 ready`), the
latest pipeline, an **SVN** badge on SVN repos and their work folders, a purple **worktree of X** badge on linked
worktrees, and a red **identity** badge when the repo would commit under the wrong email.

**Right-click a card** for:
- **Fetch selected**, **Pull selected (fast-forward only)**, **Commit selected...**, **Push selected** (act on every
  selected card; see [section 17](#17-working-with-many-repos-at-once))
- **Pin / Unpin**, **Watch / Unwatch (background fetch)**, **Edit .gitignore...**
- **Open in File Explorer**, **Open in Terminal**, **Open in Editor**, **Open on Web**
- **Move to profile** (with several cards selected: **Move N repos to profile**), **Repo settings...**,
  **Remove from RepoSync** (the folder is kept)

Pin, Watch, Open, Repo settings and Remove act only on the card you right-clicked.

### Home page

Open it with the logo, the **Home** button in the repo header, **Alt+Home**, or by having no repo selected. It covers
the loaded profile and lists:
- **Needs you on this PC**: repos with conflicts, changes, unpushed commits or an unpublished branch
- failing or running checks
- your open MRs
- MRs waiting for your review (ones not updated for 3+ days are marked **waiting Nd** and listed first)
- **Tidy up**: stashes older than 14 days, and local branches whose server branch was deleted
- **My commits, last 14 days**: a bar chart for the 12 busiest repos (hover over a bar to see the day). It counts
  commits on all branches by each repo's git email.

Click any line to open that repo on the right tab. MR and pipeline rows also have an **Open on the web** button. The
**Filter** box narrows every list; *updated X ago* shows when the MR lists were last read. When there's nothing to do,
an **All clear** card says so. A note under the lists flags servers with no token (*No token for \<host\> - add one
under Accounts (key button).*) or projects that didn't answer.

The Home toolbar has **Refresh** (re-reads the MR / PR lists), **Overview**, **Fetch all**, **Pull all**,
**Commit all**, **Push all**, **Clean up branches**, **Search**, **What I did**, **Clone** and **Accounts**.

- **Search** (Ctrl+Shift+F) searches every repo in the profile. Tick what to look in: **Commits** (message, hash or
  author, latest 5000 commits of every branch), **Branches** (local and server), **Merge requests** (open and merged;
  needs the token) and **Code** (`git grep` over tracked text files; unticked by default; *Match case* and *Regex*
  apply to Code only). Results are grouped by repo in Kind / Result / Details / Date columns, up to 100 per kind per
  repo (200 for code). Double-click a result, or press Enter, to jump to it: the repo opens on History / Branches /
  Merge requests with that item selected, and a code hit opens the file at that line (VS Code jumps to the line). The
  window can stay open while you work.
- **What I did** lists your commits on every local branch since the last workday (Friday on a Monday), today, or the
  last 7 days, grouped by repo. It matches each repo's commit email and the profile email and leaves out merge
  commits. **Copy** puts it on the clipboard for a standup or timesheet.

### Overview and the What changed feed

**Overview** in the Home toolbar (or **Ctrl+Alt+H**, which toggles between Overview and Home) shows every repo in the
profile as one table: Repo, Profile, Server, Branch, To push, To pull, Changes, Pipeline, Merge request and Status.
Click a column header to sort. **Group by** picks None, Profile (the default), Server or Status, and is remembered.
Double-click a row, or press Enter, to open that repo. The toolbar has **Refresh**, **Fetch all** and the
background-fetch interval.

The **What changed** panel beside it lists what each refresh found: new commits on the server (*3 new commit(s) on
main in web-portal*), pipelines finishing, and MRs opened, approved, merged, closed or getting conflicts. Every refresh
fills it: F5, Fetch all, Pull all, Push all and the background fetch. The Overview button shows how many lines are
new. **Clear** empties it. The newest 200 lines are kept in `feed.json` next to the exe.

**Fast start**: each repo's last known status is saved to `status-cache.json` after every refresh and on exit. On the
next start the cards show it at once (a small clock icon in the Overview marks it as stale) while the real refresh
runs.

**Compact density** (Settings > General) makes cards and table rows smaller everywhere.

### Repo header

With a repo open, the header shows:
- **Home**, the repo name and state, its path and *fetched X ago*
- *commits as \<name\> \<email\>*, with a red **wrong for this profile** pill when it doesn't match the profile
- the **profile · server** pill: click it to open Repo settings (profile and git identity)
- **Folder**, **Editor** and a web button labelled with the server name (for example *GitLab*)
- **More**, a popup with the less-used actions:

| Section | Items |
|---|---|
| IN THE LIST | **Pin / unpin**, **Watch / unwatch**, **Repo settings...** |
| TOOLS | **Tags** (Alt+8), **Worktrees** (Alt+9), **Edit .gitignore...**, **Remotes and submodules...**, **Repo health...** |
| UNDO AND RECOVERY | **Undo history...**, **Recover (reflog)...** |
| | **Remove from list** |

While an action runs, the header shows what it's doing (for example *Push: Writing objects: 42%*) and a **Cancel**
link (Esc).

**Edit .gitignore...** opens the file in a small editor (it's created if missing), with an **Open in editor** button.
It asks before throwing away unsaved changes.

### Action bar

**Fetch**, **Pull** (shows *Pull (N)* when behind), **Push** (*Push (N)* when ahead, *Publish* when the branch isn't on
the server yet), **Pull & push**, **Start branch**, **Switch** (opens the Branches tab), **Update from \<default\>
(N)**, **Create MR** / **Create PR** and **Finish branch**. The button that matches the repo's next step (see
[section 3](#3-the-repos-overview-tab)) is highlighted.

After an action, a banner shows the result. When the action moved a branch it has an **Undo** link (Ctrl+Z); the X
dismisses it.

### Tabs

**Overview**, **Changes**, **Branches**, **History**, **Merge requests**, **Pipelines**, **Stashes**, **Tags**,
**Worktrees**, **SVN** (Alt+1 ... Alt+9, Alt+0 for SVN). Merge requests and Pipelines only appear for repos on GitHub
or GitLab. Stashes, Tags and Worktrees only appear when there's something in them (Tags and Worktrees are always in
**More**). The SVN tab only appears on SVN repos and their work folders.

### Activity panel and status bar

The **Activity** panel lists every git command the app runs, with up to 40 lines of output each (the last 3000 lines
are kept, in memory only). **Clear** empties it, **Hide** hides it (Ctrl+Shift+L brings it back). Below it is the
quick git command box (see [section 19](#quick-git-command-ctrl)), with **Run** and **Terminal** (opens PowerShell /
Windows Terminal in the repo).

The status bar shows the background-fetch interval (*Background fetch every N min*, *off*, or *per profile*), key
hints, **F1 all shortcuts**, and a red **N errors - show** link when errors reached a hidden Activity panel.

---

## 3. The repo's Overview tab

The first tab (Alt+1) sums up one repo and suggests what to do next.

### Next step

A colored banner with one suggestion and one button. The first rule that applies wins:

| Banner | Button |
|---|---|
| Folder not found | Remove from list |
| Git reported an error | Refresh |
| Resolve N conflicted file(s) | Resolve conflicts (opens Changes) |
| Merge / Rebase in progress - ready to finish | Continue |
| Use your \<profile\> identity here | Use \<email\> |
| Your branch and the server's have both moved | Pull & push |
| Pull N new commit(s) | Pull |
| !N was merged - finish the branch | Finish branch |
| Commit N change(s) | Review & commit |
| Publish this branch | Publish |
| Push N commit(s) | Push |
| The pipeline failed | Show failure |
| \<default\> has N new commit(s) | Update from \<default\> |
| Open a merge request | Create merge request |
| !N is ready to merge | Open merge request |
| Pipeline running / Checks running | Watch |
| Up to date on \<branch\> | Start a branch |
| All good | (none) |

### Cards

- **BRANCH**: branch, tracking, *N to push* / *N to pull*, *fetched X ago*.
- **WORKING TREE**: uncommitted changes (*X staged · Y not staged (Z new)*), stashes put aside, any merge or rebase
  in progress; **Review changes** and **Stashes** buttons.
- **COMPARED WITH origin/\<default\>**: *N your commits* / *N new on \<default\>* (or *This is the default branch.*), with
  **Update now** and **Compare on \<server\>**.
- **MERGE REQUEST**: readiness, title, merge status and branches, with **Open on \<server\>** and a button to the MR
  tab. With no MR: *No merge request for this branch.* and **Create merge request**.
- **LATEST PIPELINE ON THIS BRANCH** (GitHub: *LATEST CHECKS*): status, id, commit, age and the failed jobs
  (allowed-to-fail jobs left out), with **Jobs & logs** and **Open on \<server\>**.
- **RECENT COMMITS**: the last 6, with a **not pushed** pill on unpushed ones, and a **Full history** link.

---

## 4. Profiles and identity

Every repo belongs to one profile. You start with **Personal** and **Work**; add, rename, recolor, reorder or remove
profiles under **Settings > Profiles**. The order sets Ctrl+1..9. Removing a profile moves its repos to the first
other profile in the list (it asks first if the profile has repos); nothing on disk is touched.

The profile picker loads **only that profile's repos**: the list, header counts, bulk actions, Home page, file
watching and background fetch all cover just those. Other profiles aren't touched until you pick them. **All
profiles** loads everything, grouped by profile. Shortcuts: Ctrl+0 = All profiles, Ctrl+1..9 = the profiles in order.

Move a repo to another profile with right-click > **Move to profile**, Repo settings > **PROFILE**, or the profile ·
server pill in the header.

### Repo settings

Open with **More > Repo settings...**, right-click > **Repo settings...**, or the profile · server pill.

| Field | Does |
|---|---|
| NAME IN THE LIST | Display name on the card |
| AVATAR COLOR | `#RRGGBB` or one of 10 swatches; empty = automatic |
| PROFILE | Which profile the repo belongs to |
| COMMITS IN THIS REPO ARE AUTHORED AS | *Leave it as it is*, *Use the \<profile\> identity*, *Use my global git identity (remove this repo's own)*, or *Use this name and email for this repo* |
| BEFORE EVERY PUSH, RUN | A pre-push check (for example `dotnet test`); see [section 22](#22-safety-rules) |
| Keep this branch up to date automatically | Fast-forward after a fetch when it's safe; see [section 20](#20-background-fetch-notifications-and-the-tray) |

### Identity check

Settings > Profiles > **WRONG IDENTITY ON COMMIT / PUSH** compares the repo's git email with its profile's *commit
as* email:

- **Stop and ask (recommended)** (default): before a commit or push, a dialog offers:
  - *Use \<email\> for this repo, then \<commit/push\>*
  - *This is really a \<profile\> repo - move it to \<profile\>, then ...* (only when another profile uses that email)
  - *\<Commit/Push\> as \<email\> anyway* (asks again)
  - Cancel
- **Warn, then go ahead**: shows a banner and continues.
- **Don't check**: skips the check and hides the identity badges, counts and chip.

**Pull & push** (and the Overview's *Pull & push* step) doesn't run the identity check.

> **Example: committing to a work repo with your personal email**
> The card shows a red `identity` badge, and the Changes tab shows a red bar with a **Fix** button.
> Click **Fix**. The app sets the repo's local `user.email` (and `user.name`, if the profile has one) to the Work
> identity, and the badge disappears.

---

## 5. Daily loop: commit, pull, push

### The Changes tab

The left side has three lists:
- **CONFLICTS (n)**: shown whenever a file is conflicted (after a merge, rebase, cherry-pick, revert, or stash
  apply / pop)
- **CHANGES (n)**: unstaged changes, with **Discard all** and **Stage all** (Ctrl+Shift+A)
- **STAGED (n)**: with **Unstage all**

The right side shows the diff for the selected file, headed by its name, a status pill (*Modified*, *Untracked (new
file)*, *Renamed from X*, ...), a *staged* pill and its folder.

Hovering a file row shows buttons for **Open in editor**, **Discard changes** (unstaged files), and **Stage** /
**Unstage** (double-click works too).

| Action | How |
|---|---|
| Stage / unstage a file | Double-click it, its row button, or right-click > **Stage** / **Unstage** |
| Stage / unstage everything | **Stage all** / **Unstage all** |
| Discard a file's unstaged changes | Row button or right-click > **Discard...** (CHANGES only; asks first; new files are deleted) |
| Discard everything unstaged | **Discard all** (asks first) |
| Ignore a file | CHANGES right-click > **Add to .gitignore** > *Ignore this exact file*, *Ignore this file type (\*.ext)* or *Ignore this folder* |
| File history / blame | Right-click > **View file history...** / **Git blame...** |
| Open it | Right-click > **Open in editor** / **Show in Explorer** |
| Commit | Type a message, then **Commit** or **Ctrl+Enter** (in the message box) |
| Commit and push | **Commit & push** or **Ctrl+Shift+Enter** (in the message box) |
| Fill in a message | **Suggest** writes one from the changed file names; **Template** fills in git's `commit.template` (only shown when the repo has one) |
| Add to the last commit | Tick **Amend last**. With an empty message box it fills in the last commit's subject. Only amend commits you haven't pushed; the app doesn't stop you. |
| Hide whitespace changes | Tick **Ignore whitespace** (`git diff -w`). Also applies to History and Stashes. |

The hint under the buttons says what **Commit** will do: *Commits the N staged file(s).*, *Nothing staged - commits
all N change(s).* (it runs `git add -A` first) or *No changes to commit.*

Under the message box, **Subject n/50** counts the first line: amber past 50 characters (*a bit long*), red past 72.
It also reminds you to leave line 2 blank before the body.

Also on the Changes tab: **Force push**, **Undo last** and **Recover...** (see below and
[section 10](#10-undo-and-recovery)).

### Folders (tree view)

Tick **Folders** in the CHANGES header to group the file lists by folder. It's one saved setting, shared by the
Changes, History and SVN tabs.

- Folders come first (alphabetical), then files, at every level. A chain of folders that each hold one folder shows
  as one row (`src/app/views`). A folder row shows a chevron, its name and how many files are under it.
- Files are indented under their folder, and the folder text is dropped from the file row.
- Fold a folder with its chevron, by double-clicking it, or select it and press Left (collapse), Right (expand),
  Enter or Space (toggle). Folded folders stay folded when the list refreshes.
- Hovering a folder row shows **Stage this folder** and **Discard every change in this folder** (CHANGES) or
  **Unstage this folder** (STAGED). Right-click > Stage, Unstage, Discard..., *Ignore this folder* (adds
  `/<folder>/`) and Show in Explorer also work on a folder. File-only items (history, blame, open) do nothing there.
- The CONFLICTS list always stays flat.

### The diff

- Changed words inside a changed line are highlighted, and code files get light syntax colors (C#, JS/TS, Java,
  Kotlin, Go, Rust, C/C++, Swift, Dart, CSS, JSON, PowerShell, Python, shell / YAML / TOML / Ruby, XML-style files
  such as XAML / HTML / csproj, and SQL).
- **F7** / **Alt+Down** jumps to the next block of changes, **Shift+F7** / **Alt+Up** to the previous one. This works
  in every diff in the app.
- **Split diff** shows old and new side by side (saved, shared with History). Each side scrolls sideways on its own;
  vertical scroll and selection stay in sync. Line staging works in the normal (unified) view only, so untick Split
  diff to stage lines.
- **Open in window** opens the diff in its own resizable window (with its own Split diff box). It's a snapshot and
  doesn't update.
- Very large diffs are cut off after 6000 lines. New files over 5 MB and binary files aren't shown.

### Ticket prefix

When the branch name contains a ticket key, the commit message gets it in front. Untick **Prefix PROJ-123** (next to
*Amend last*) to leave it off. It stays off until you tick it again or open another repo.

| Branch | Prefix |
|---|---|
| `feature/PROJ-123-fix-login` | `PROJ-123 ` |
| `proj-42_cleanup` | `PROJ-42 ` |
| `release-2024`, `hotfix-3`, `main` | none |

Words that look like versions, not tickets, are skipped: release, hotfix, version, build, rc, v, sprint, phase, step,
part, temp, wip, test, backup. The prefix isn't added if the message already contains the key.

> **Example: commit on a ticket branch**
> Branch: `feature/PROJ-311-timeout`. Message typed: `Raise the API timeout to 60s`.
> The commit is saved as `PROJ-311 Raise the API timeout to 60s`.

### Pull and push

The action bar has **Fetch** (Ctrl+Shift+E), **Pull** (Ctrl+Shift+P), **Push** (Ctrl+Shift+U) and **Pull & push**
(Ctrl+Shift+Y):
- **Pull** fast-forwards when it can, otherwise rebases your local commits on top of the server's. Uncommitted work,
  new files included, is stashed during the pull and put back afterwards. If the rebase stops on a conflict, the
  stash stays in the Stashes list until you've finished. Pull refuses while files are conflicted.
- **Push** publishes the branch the first time (`push -u`); the button then reads **Publish**.
- **Force push** (Changes tab) uses `--force-with-lease`. Use it after amending or rebasing. It first shows *What will
  this do*: the server commits that would be overwritten (the button turns red) and your commits that replace them.

### Large-file guard

Before a push (Push, Commit & push, Pull & push, Push all, Force push, and the push before creating an MR), the app
checks the files you're pushing:
- **25 MB or more:** a warning in the Activity log (*Large file in this push: ...*).
- **Over 100 MB:** the push is stopped (*N file(s) are over 100 MB - the server will reject this push.*), with a hint
  to use Git LFS or remove the file. Files tracked by LFS pass.

> **Example: you accidentally committed a 140 MB `dump.sql`**
> The push stops with *"1 file(s) are over 100 MB"*. To fix it:
> 1. **Undo last**. The commit is taken back and its changes stay staged.
> 2. In STAGED, right-click `dump.sql` > **Unstage**. It moves to CHANGES as a new file.
> 3. Right-click it > Add to .gitignore > **Ignore this exact file**.
> 4. Commit again and push.

---

## 6. Staging part of a file (lines and hunks)

Use this when one file has two unrelated changes and you want two clean commits.

1. Select the file in **CHANGES**. Its diff appears on the right (unified view, *Split diff* off).
2. Select lines in the diff. Click, then Shift-click or Ctrl-click to add more.
   - Select a **`@@` hunk header** to take the whole hunk, or press **H** (*Select whole hunk*) to add every changed
     line of the hunk(s) your selection touches.
3. Choose an action:
   - **S**, the **Stage lines** button, or right-click > *Stage / unstage selected lines (S)*: stages those lines.
   - **D**, **Discard lines**, or right-click > *Discard selected lines (D)*: throws those lines away (asks first;
     unstaged files only).

To **unstage** part of a staged file, select it in **STAGED**, select lines, then press **U**, **S** (S toggles:
stage on an unstaged file, unstage on a staged one) or click **Unstage lines**. Your file keeps the lines.

Notes:
- **Ignore whitespace** must be off; the app tells you if it isn't.
- Context lines and `\ No newline` lines are handled for you; selecting them does nothing.
- New (untracked) files work too: pick lines by line number. There's no `@@` header in a new file's view, so use
  click / Shift-click instead of H.

> **Example: two changes in `api.cs`**
> `api.cs` has a bug fix at line 40 and a logging tweak at line 210.
> 1. Select `api.cs`, click the `@@ -38,6 +38,7 @@` header, and press **S**.
> 2. Commit: `Fix null check in GetUser`.
> 3. `api.cs` still shows the logging change. Stage it and commit: `More detailed request logging`.

---

## 7. Branches

### The Branches tab

**On this PC** (left) has **New** (Start branch, Ctrl+Shift+N), **Switch**, **Rename**, **Delete**, **Merge into
current**, **Rebase onto** and **Clean up (N)**. Columns: a check on the current branch, Branch, Push / pull (pills,
or a check when it's the same as the server), Tracking and Last commit. Pills mark branches that are **merged** or
**gone on server**. Double-click a branch to switch to it; right-click for *Switch to branch*, *Merge into current
branch* and *Rebase current branch onto this*.

**On GitLab / On GitHub** (right) lists the server's branches, with a *Filter by name or author* box, **Check out**,
**Merge into current**, **Rebase onto**, and buttons to open a branch on the web or **Delete on the server**. A
**local** pill marks branches you already have. Double-click to check one out as a local branch.

- **Rename** is local only. **Delete** asks first, and force-deletes an unmerged branch after a warning.
- **Merge**, **Rebase onto**, **Update from \<default\>**, **Finish branch**, **Revert** and **Cherry-pick** refuse
  while you have uncommitted changes. Commit or stash first.
- **Rebase onto** first shows *What will this do*: the commits that will be rewritten, and whether any are already on
  the server.
- **Clean up** deletes branches that are merged or gone from the server, after you tick which (Home > **Clean up
  branches**, Ctrl+Alt+X, does it for every repo).

### Start branch

**Start branch** (action bar, Ctrl+Shift+N) opens *Start a feature branch*:
- **WHAT ARE YOU WORKING ON?**: for example `PROJ-500 export`.
- **BRANCH NAME PATTERN**: default `feature/{name}-{user}` (saved). The name is lower-cased with spaces as dashes, so
  the example becomes `feature/proj-500-export-<user>`.
- **START FROM**: `origin/<default>` *(latest from the server - usual choice)*, or the branch you're on. A warning
  shows when your uncommitted changes will come along.
- **Publish to GitLab / GitHub right away (push -u)**: ticked by default on GitHub / GitLab repos.

**Create branch** fetches first and refuses a name that already exists. The branch is created with `--no-track`, so
its first push can never land on `main`.

In a repo with **no remote** (for example an SVN working copy's git or one of its worktrees), Start branch offers the
branch you're on and your other local branches, and the branch stays local.

### Keeping up and finishing

- **Update from \<default\> (N)** (Ctrl+Shift+M) fetches and **merges** the latest default branch into yours. To rebase
  instead, use Branches > right-click `origin/main` > *Rebase current branch onto this*.
- **Finish branch** (Ctrl+Shift+X), after the MR is merged: fetches, switches to the default branch, fast-forwards
  it, deletes the local branch and deletes the server branch if it's still there. It refuses unless the work is in
  the default branch or the server says the MR was merged.

> **Example: the full feature loop**
> 1. **Start branch** > `PROJ-500 export` > Create branch. You're on `feature/proj-500-export-<you>`, already published.
> 2. Work, commit, **Push**.
> 3. **Create MR** (action bar). With one commit the title is its subject; with several, the title comes from the
>    branch name and the commit subjects go into the description.
> 4. A reviewer approves, then you **Merge** on the Merge requests tab.
> 5. **Finish branch**. You're back on `main` and up to date, and the feature branch is gone locally and on the
>    server.

---

## 8. History, search and the branch graph

The History tab lists commits for **This branch**, **Default branch** or **All branches**. It loads 300 commits;
**Load more** adds 300. A dot marks commits that aren't pushed yet.

The toolbar has **Copy hash**, a button that opens the commit on the server (disabled for unpushed commits), **Branch
here**, **Cherry-pick** (disabled in *This branch*, since the commit is already there), **Revert**, **Tag** and
**Rewrite...**. Right-click a commit for *Copy hash*, *Create branch here...*, *Cherry-pick commit*, *Revert commit*,
*Reset branch to this commit* > Soft / Mixed / Hard, and *Create tag here...*. **Reset** first shows *What will this
do*: commits taken off, commits already on the server, and (for hard) uncommitted changes that would be lost.

### Graph and ref labels

The **Graph** column draws branch and merge lines. Each line keeps its own color when it shifts lanes (10 colors that
repeat; up to 14 lanes are drawn). Merge commits are hollow rings.

Labels before the commit message show branches and tags in the commit's line color: a local branch is solid, the
checked-out branch is bold with a check mark, a server branch is outlined with a cloud icon, and a tag is neutral
with a tag icon. **Click a branch label** to show only that branch. A chip says *Showing only \<branch\>*; **Show all**
(or changing the scope) goes back.

### Search modes

Pick a mode in the dropdown next to the search box:

| Mode | Finds | When |
|---|---|---|
| **Filter list** | Loaded commits by message, author, hash or branch name | As you type |
| **Search message** | Commits whose message contains the text (`git log -i --grep`) | Press Enter |
| **Search code** | Commits that **added or removed** the text (`git log -S`) | Press Enter |
| **Search file name** | Commits that touched a file whose name contains the text (case-insensitive) | Press Enter |

Enter searches cover the whole history of the selected scope (or the *Showing only* branch), up to 500 results. The
graph is hidden while the list is filtered or searched, because gaps would make it wrong. Clear the box to bring it
back.

> **Example: "who removed `RetryPolicy`?"**
> Scope **All branches**, mode **Search code**, type `RetryPolicy`, press Enter. You get the commits that added it and
> the commit that removed it. Select the removal commit to see its diff, then **Revert** it if you want it back.

> **Example: "everything that touched the installer script"**
> Mode **Search file name**, type `installer`, press Enter.

### Commit details

Select a commit to see its full message, author, date and hash, and its files (**FILES (N)**, with the same
**Folders** tree view as the Changes tab). With one file, it's selected for you; otherwise the whole commit's diff
shows until you pick a file. The diff has **Open in window**, **Split diff** and **Ignore whitespace**. Right-click a
file for **View file history...** (follows renames, last 100 commits), **Git blame...** (#, Commit, Author, Date,
Code; whitespace changes ignored) and **Open in editor**.

---

## 9. Rewriting commits (interactive rebase)

Open it with **History > Rewrite...**, or *Interactive rebase...* in the command palette. It's disabled while the app
is busy, during a merge or rebase, or on a detached HEAD.

The *Rewrite commits* window lists your branch's commits, **oldest first**:
- On a feature branch, every commit since it left the default branch.
- On the default branch, your unpushed commits.
- When you have no commits of your own (up to date, or a new branch), the last 20.

For each commit, pick an action:

| Action | Effect |
|---|---|
| **pick** | Keep as is. If you edit the message, it's reworded. |
| **reword** | Keep it, with the text in **Message (edit for reword)** as its whole new message |
| **squash** | Melt into the commit above, keeping both messages |
| **fixup** | Melt into the commit above, dropping this message |
| **drop** | Remove the commit |

Use **Move up** and **Move down** to reorder. Click **Rewrite**; *What will this do* then lists the rewritten and
dropped commits (the button turns red when commits are dropped or already on the server). Click **Rewrite** again to
run it. A plan that changes nothing does nothing.

The rules:
- Uncommitted work is stashed during the rewrite and put back afterwards.
- If a step conflicts, the rewrite stops. Resolve it on the Changes tab, then **Continue** or **Abort**.
- If the commits were already pushed, a yellow warning appears. Afterwards you'll need **Force push**.
- History that contains merge commits is refused. So is squash / fixup on the first kept commit, and dropping
  everything (use Reset on the History tab).
- The rewrite can be undone from **Undo history** or **Recover** ([section 10](#10-undo-and-recovery)).

> **Example: clean up before review**
> Branch commits, oldest first:
> ```
> pick   a1b2c3d  Add export button
> pick   d4e5f6a  wip
> pick   0badf00  fix typo
> pick   c0ffee1  Export to CSV
> ```
> Set `wip` and `fix typo` to **fixup**, and reword the last one to `Export orders to CSV`. Click **Rewrite**:
> ```
> a1b2c3d  Add export button        (unchanged, keeps its hash)
> 9e8d7c6  Export orders to CSV     (new hash)
> ```
> Then **Force push**.

---

## 10. Undo and recovery

| Situation | Use |
|---|---|
| The app's last action (commit, merge, rebase, start branch, reset...) was a mistake | **Undo** in the result banner, or **Ctrl+Z** |
| An earlier action from this session | **More > Undo history...** |
| Last commit is wrong and **not pushed** | Changes > **Undo last** (its changes stay staged) |
| A commit is already pushed | History > **Revert** (adds a commit that undoes it) |
| A file's edits should go | Right-click > **Discard...** |
| A few lines should go | Select them in the diff, then **D** |
| "I reset / rebased / amended and lost commits" | **More > Recover (reflog)...** |

### Undo the last action

Before every action that moves a branch, the app notes where HEAD and each local branch pointed. Afterwards the
result banner has an **Undo** link (also **Ctrl+Z**, or *Undo ...* in the command palette). It runs straight away,
without asking:
- New commits on top (commit, cherry-pick, revert) are taken back with their changes **staged**, like *Undo last*.
- A merge, rebase or reset is moved back with `reset --keep`, so uncommitted work is never overwritten.
- A branch the action created is deleted if it has no commits of its own; deleted or moved branches come back.
- Undo refuses when a commit it would drop is already on the server. Use Revert there.

Only branches are restored. Stashes, tags and the server are left alone.

### Undo history

**More > Undo history...** (or *Undo history...* in the palette, or **App undo history...** in the Recover window) lists every git action the app ran in
this session, newest first (up to 300; it's cleared when the app closes), including ones that failed part-way. The
columns are When, Repo, Action and **What moved** (for example *main: 1a2b3c4d -> 5e6f7a8b; switched main ->
feature/x*, *created x*, *No branch moved*, *Failed - nothing moved*, *Undone*), with an **Undo** button per row.
Filters: **Only this repo** (on by default) and **Only actions that moved a branch**. **Reflog (recover)...** opens
Recover for the selected repo. The window can stay open; new actions appear at the top.

Undo from this window asks first, and warns (the button turns red) when branches moved since then (those later moves
are taken back too) or when you have uncommitted changes. It refuses when the row is already undone, nothing moved,
or a merge or rebase is in progress. The undo is recorded too, so you can undo it again.

### Cancel

While an action runs, the repo header shows **Cancel** (or press **Esc**, or *Cancel the running action* in the
palette). git, or a running pre-push check, is stopped. If a merge or rebase was left open, the Overview's next step
offers **Continue**, and the Changes tab has **Continue** / **Abort**.

### Recover (reflog)

**Recover** (*Undo / recover (reflog)*) lists up to 150 places HEAD has been: commits, resets, rebases, amends and
checkouts, newest first (Hash, Ref, What happened, When). The entry just before the latest one is selected for you.
Pick the entry just *before* the mistake, then:
- **Go back here** (or double-click): moves the branch back with `git reset --keep`, so your uncommitted changes are
  kept. It asks first, and is undoable the same way.
- **Branch from here...**: creates a new branch at that point (default name `recovered`) and leaves the current branch
  alone.

> **Example: a hard reset went one commit too far**
> Click **More > Recover (reflog)...**. The list shows:
> ```
> HEAD@{0}  reset: moving to HEAD~2
> HEAD@{1}  commit: Add report filters
> ```
> `HEAD@{1}` is already selected. Click **Go back here**. The commit is back.

---

## 11. Conflicts

When a pull, merge, rebase, cherry-pick, revert or stash apply / pop conflicts, a **CONFLICTS** list appears on the
Changes tab. For a merge, rebase, cherry-pick or revert, a red bar also appears at the top with **Continue** and
**Abort** (*Resolve each conflicted file below ..., then Continue. Abort puts everything back.*). A stash conflict has
no bar: resolve the files, and the stash is kept (on Pop too) until it applies cleanly.

For each conflicted file:
- **Resolve...**: opens the conflict editor (*Resolve conflicts*). Each conflict (*CONFLICT 1*, *2*, ...) shows
  **MINE** and **THEIRS** side by side; pick **Mine**, **Theirs**, **Mine, then theirs** or **Theirs, then mine**.
  **RESULT (EDITABLE)** below updates as you pick, and you can edit it by hand (picking again after an edit asks
  before rebuilding it). **Save & mark resolved** writes the file (keeping its line endings and BOM) and stages it;
  it asks if conflict markers are left. *Mine* is always the branch you're on, also during a rebase. A file deleted
  on one side, or with no conflict markers (binary), tells you to use Keep mine / Take theirs.
- **Keep mine** / **Take theirs**: use one side's version of the whole file, and stage it.
- **Edit**: open it in your editor, fix the `<<<<<<<` markers, then click **Mark resolved**.

When the list is empty, click **Continue** (a merge, cherry-pick or revert commits; a rebase moves to the next
commit, and tells you if that one conflicts too). **Abort** asks first, then puts everything back the way it was.

---

## 12. Merge requests / pull requests

The tab is called **Merge requests** on GitLab and **Pull requests** on GitHub, and only appears for repos on those
servers. Pick **Mine**, **To review**, **All open** (default) or **Merged**. The toolbar has **Refresh**, **New for
this branch** (Ctrl+Shift+R) and **All on the web**. Your branch's own MR is selected for you.

Select one to see its readiness, merge status, checks, comment count and description. The buttons are:
- **Merge**, with **Squash** and **Delete source branch** (on by default). Disabled for drafts and conflicts. After
  merging your own branch, the banner suggests **Finish branch**.
- **Review...**, **Approve**, **Draft / ready**
- **Check out**: fetch and switch to its branch locally
- **Open**: open it on the web

The first time you create, approve, merge or comment on a server, the app asks you to confirm (*first use*).

On GitHub, *Mine* and *To review* look at the 50 most recently updated open PRs, and *Merged* lists closed PRs that
were merged.

### Review window

**Review...** fetches both branches and opens the MR inside the app, with three panes:
1. **Files** changed, computed by local git from the fetched branches (no API calls). GitHub forks use
   `pull/<n>/head`.
2. **Diff** of the selected file.
3. **Comments**, oldest first (GitLab system notes hidden). Line comments show `path:line`. Type in the box and click
   **Post comment** (or Ctrl+Enter). **Double-click a diff line** to quote it (`path:line` and the code) into the box,
   then add your comment. It's posted as a regular comment that points at the line.

**Open on the web** and **Close** are at the bottom.

### Creating an MR / PR

**Create MR** (action bar), **New for this branch**, or Ctrl+Shift+R opens a dialog with **TITLE**, **DESCRIPTION**
(with a **Template** picker), **MERGE INTO**, **Draft (not ready for review yet)**, **Delete the branch when merged**,
**Squash commits when merged** (both GitLab only, remembered) and **Open it in the browser afterwards**.

- It needs a token for the server. It pushes or publishes the branch first if needed, and jumps to the existing MR if
  one is already open.
- Templates come from `.gitlab/merge_request_templates/*.md`, `.github/PULL_REQUEST_TEMPLATE/*.md`, and
  `pull_request_template.md` in `.github`, `docs` or the root. A template called *Default* (GitLab) or
  *pull_request_template* (GitHub) is picked automatically.
- When Settings > **TICKET LINK** is set (for example `https://app.clickup.com/t/{ticket}`), a `Ticket:` link for the
  branch's ticket is added to the description.

> **Example: reviewing a colleague's MR**
> 1. Merge requests > **To review** > select `!48 Add audit log`.
> 2. **Review...** Walk the files and read the earlier comments.
> 3. Type `Looks good - one question: should AuditEntry be immutable?` and press Ctrl+Enter.
> 4. Close the window and click **Approve**.

---

## 13. Pipelines / GitHub Actions

The tab is called **Pipelines** on GitLab and **Actions** on GitHub. It lists the last 30 runs for the branch, or for
every branch with **All branches**: Status, Workflow, Run, Branch, Commit, Trigger, Started. Select a run to see its
jobs (Job, Stage, Time; the first failed job is selected for you), and a job to see its log. While anything runs, the
tab refreshes itself every 15 seconds.

The log shows the last 4000 lines. A **failed** job's log opens at the first line that looks like an error
(highlighted); others open at the end. **First error** and **End** jump between them. Color codes and GitLab section
markers are stripped.

| Button | Does |
|---|---|
| Run | Start a pipeline on the current branch (GitLab), or the workflows with a `workflow_dispatch` trigger (GitHub). Needs a published branch. |
| Retry | Retry the failed jobs of a failed or canceled run |
| Cancel | Cancel a running or pending run |
| Retry job | One job |
| Run (manual) | Start a manual GitLab job |
| Reload log / Open | Read the log again / open the job on the web |
| **Artifacts** | Download build artifacts to `Downloads\<repo>-pipeline-<id>\` and show them in Explorer |

On GitLab, artifacts belong to each job, so select the job first. On GitHub, every artifact of the run is downloaded
(as `<name>.zip`; expired ones are skipped).

> **Example: get the installer a pipeline built**
> Pipelines > select the green pipeline > select the `package` job > **Artifacts**.
> Explorer opens on `job-9123-artifacts.zip`.

---

## 14. Stashes and tags

The **Stashes**, **Tags** and **Worktrees** tabs only appear when the repo has stashes, tags or linked worktrees.
Reach Tags and Worktrees any time from **More**, the command palette or Alt+8 / Alt+9. Reach Stashes with Alt+7, the
Overview's **Stashes** button or *Go to Stashes* in the palette.

**Stashes tab** (*Put unfinished work aside, then bring it back later*):
- **Stash my changes** (Ctrl+Shift+S) puts your uncommitted changes aside, new files included, with an optional note.
  It's only enabled when there are changes.
- The list shows Stash, Note and When. Select a stash to see its diff (new files included).
- **Apply** keeps the stash; **Pop** applies it and removes it; **Delete** asks first, then deletes it.

**Tags tab** (Tag, Commit, Message, Date):
- **New tag** opens *Create git tag*: **TAG NAME**, **COMMIT / TARGET** (HEAD), **ANNOTATED MESSAGE (OPTIONAL)**
  (empty = lightweight tag) and **Push tag to server right away** (ticked by default). History > **Tag** or
  right-click > *Create tag here...* opens it with the selected commit as the target.
- **Push tag** pushes the selected tag; **Push all tags** asks first.
- **Delete** (local) and **Delete on server** both ask first.

---

## 15. Worktrees

A worktree is a second folder checked out from the same repo on a different branch, for example `web-portal` on `main`
and `.web-portal-work` on `work`.

- Linked worktrees are **added to the list automatically**. They show a purple **worktree of X** badge.
- The **Worktrees tab** lists every worktree of the repo: Folder, Branch, Commit and a status (*main · this one*,
  *main*, *this one*, *in list*, *locked*, *missing - prune*). From there:
  - **Open**: show it as a card and select it
  - **Add worktree**: asks for the branch (an existing local branch, or a new name created from `origin/<default>`),
    then the folder (suggested as `<parent>\.<repo>-<branch>`)
  - **Remove**: delete the worktree folder; the branch and its commits are kept. With uncommitted changes it asks
    *Delete anyway*.
  - **Prune**: forget worktrees whose folder was deleted by hand
- Commits made in either folder show up on both cards within a couple of seconds.

> **Example: review `main` while your feature is half done**
> Worktrees > **Add worktree** > branch `main` > folder `..\.myrepo-main`.
> A new card appears for it. Run or test it there; your feature folder stays untouched.

---

## 16. SVN round trip

This applies only to repos with an SVN working copy. Both the working copy and its work folder show an **SVN** badge
and an **SVN** tab (Alt+0).

The setup:
- The SVN working copy's git `main` mirrors what SVN has committed (only SVN's committed state; local edits in the
  working copy are left out). Each mirror commit is `SVN rNNNN mirror: ADD: ...; MODIFY: ...`, tagged `svn-rNNNN`.
- You do your work on the **work** branch, in the work folder (a worktree next to the working copy).
- You never commit in the SVN working copy's git. The app refuses to (*This folder is the SVN working copy - its git
  branch only records what SVN has committed.*).

### Add an SVN repo

Click **Add repo > Add SVN repo...** (Ctrl+Alt+V, or *Add SVN repo...* in the palette) and fill in:
- **SVN URL**, e.g. `https://svn.example.com/svn/my-repo/trunk`. Leave it empty if the folder below is already a
  working copy.
- **SVN WORKING COPY**: suggested as `<profile clone folder>\<name>` from the URL (the part before `trunk`) until you
  type your own. SVN checks out here; git `main` mirrors it.
- **WORK BRANCH** (default `work`; not `main` / `master`) and **WORK FOLDER** (suggested as
  `<working copy>-<work branch>`, next to it, never inside it).
- **PROFILE**: the loaded profile (with All profiles, the first one).

**Set up** then runs, logging each step in the Activity panel:
1. Checks that git and svn are installed.
2. Checks out the URL, or uses the existing working copy.
3. Runs `git init -b main` and sets `core.autocrlf=false`, so files reach SVN byte for byte. It stops if the
   working copy's git is a worktree or submodule, or on a branch other than main / master.
4. Writes a `.gitignore` that excludes `.svn` and adds the folder's `svn:ignore` / `svn:global-ignores` rules. If a
   `.gitignore` already exists, it only adds `.svn/`.
5. Makes the first `SVN rNNNN mirror` commit on main, tagged `svn-rNNNN`.
6. Creates the work branch and checks it out in the work folder.

Both folders are then added as cards; the work folder is selected and a *SVN repo ready* banner appears. Steps that
are already done are skipped, so if setup stops part-way (a password prompt, a dropped connection), fix the cause and
run it again with the same folders. SVN runs non-interactively: if the server needs a password, run `svn info <url>`
once in a terminal so it's saved.

### The SVN tab

At the top: the **Work branch** picker (the branches of the working copy's worktrees), a summary line (*SVN rNNNN (git
'main') vs 'work' (\<folder\>): N file(s) to send, M not committed yet*) and any warnings (git on the wrong branch, svn
not answering, the working copy ahead of git main, or local changes in the working copy). The tab header shows a
**sent** pill while a Send is waiting to be committed in SVN.

The round trip:

| Step | Button |
|---|---|
| 1. Commit on the work branch as usual | Changes tab of the work card |
| 2. Bring SVN up to date, copy committed work into the SVN working copy, open the TortoiseSVN commit dialog | **Send to SVN** (or **Send ticked** for the ticked files) |
| 3. Commit in TortoiseSVN | (TortoiseSVN dialog) |
| 4. Record the new SVN revision on main and merge it into work | Automatic when the dialog closes with everything committed |

**Ship to SVN** (the main button) does steps 2 to 4 in one go. If the work branch has uncommitted changes, it offers
**Open Changes** instead (*Commit your work first*). Tick **Commit message here** and type the message to have it run
`svn commit -m` itself; otherwise the TortoiseSVN dialog opens as usual.

Send and Ship start with *Bring SVN and the branch up to date* (svn update, record SVN on main, merge main into work),
so you don't need to Sync first. Send also runs `svn add` / `svn delete` for new and deleted files. They stop if the
work folder has **any** uncommitted or untracked file (not just the ticked ones).

If you cancel the TortoiseSVN dialog, or close it with changes left, the step fails with *Not committed: SVN still
shows N local change(s)...*. Commit with **TortoiseSVN commit** (reopens the dialog), then **Sync from SVN**, or use
**Undo last send**.

A panel under the buttons, titled after the action (*Ship to SVN*, *SVN Send*, *SVN Send (N file(s))*, *Sync from
SVN*), lists each step: *Work branch is committed* (Ship only), *Bring SVN and the branch up to date*, *Apply the
branch to the SVN working copy*, *Commit to SVN*, *svn update*, *Record SVN on git main*, *Merge main into the work
branch*. Each one is pending, running, done, failed (with the reason) or skipped. **Clear** empties it; the Activity
log still has the details.

Other buttons:
- **Sync from SVN**: svn update, record it on main, merge main into work. It refuses while the working copy has local
  changes or the work branch has uncommitted changes.
- **Undo last send**: before you commit in SVN, puts the working copy back the way it was (also deletes files the
  Send added). Only enabled while a Send is waiting.
- **TortoiseSVN commit**: opens the commit dialog on the working copy, for example after you cancelled it.
- **Preview sync** / **Preview send**: show what would happen and change nothing (Activity log only).
- **Record SVN on main**: only records SVN's state on main (no update, no merge).
- **Move work folder**: renames or moves the work folder with `git worktree move`, so git keeps track of it. Type a
  new folder name to rename it in place, or a full path. The card follows the folder. The target must be outside the
  working copy and must not exist. Close VS Code and any terminal using the folder first; Windows can't move a folder
  with open files.

### Differs from SVN

The **Differs from SVN** view lists every file where the work folder (as it is on disk) differs from git main: a tick
box, the file name, its folder, *New* / *Modified* / *Deleted*, and *not committed* for files you haven't committed on
the work branch (those can't be ticked). Every sendable file is ticked to start with. **All** / **None** tick or
untick every file. **TortoiseMerge** compares the selected file side by side: SVN's version on the left, the work
folder's real file on the right (edits there change it). The diff pane on the right shows the selected file. An empty
list says *The work branch matches SVN.*

Tick **Folders** to group the list by folder (the same setting as Changes and History). A folder's tick box (*Send
every file in this folder*) ticks or unticks every sendable file under it, and shows a partial state when only some
are ticked.

### SVN log

The **SVN log** view lists the latest revisions on the SVN server (25, 50 (default), 100 or 250; **Refresh** reads
them again): Revision, Author, Date, Message and **git mirror**. The git mirror column shows the `svn-rNNNN` commit that
holds each revision (`in svn-rNNNN` when an update jumped over it), or a **not mirrored yet** pill until you Sync from
SVN. Click the mirror link, double-click the row or use **Show in History** to open that commit on the History tab.
The right-hand pane shows the selected revision's full message and its changed paths (A / M / D / R, with *from
\<path\>@\<rev\>* for copies; folded into folders when **Folders** is on).

> **Example: send two files**
> SVN tab > **Differs from SVN** > **None** > tick `a.cs` and `b.cs` > **Send ticked**.
> TortoiseSVN opens. Commit with `PROJ-77 Fix totals`. When the dialog closes, the app syncs from SVN and main gets
> `svn-r48812`.

---

## 17. Working with many repos at once

| Action | Covers |
|---|---|
| **Commit all** (Ctrl+Alt+C) | Every loaded repo with changes, after you review the list (see below) |
| **Fetch all** (Shift+F5) | Every loaded repo |
| **Pull all** (Ctrl+Alt+P) | Fetches first, then fast-forwards repos that are behind. Skips repos with changes, unpushed commits, a merge / rebase in progress or a detached HEAD. |
| **Push all** (Ctrl+Alt+U) | Fetches first, then shows the list. Only published branches with nothing to pull and the right identity; skips merges / rebases in progress and detached HEADs. |
| **Clean up branches** (Ctrl+Alt+X, Home) | Merged / gone branches in every repo, after you tick which |
| **Fetch / Pull / Push / Commit selected** | Only the cards you selected |

When Pull all, Push all or Commit all finishes cleanly, a banner says so (*Everything is up to date* when nothing was
behind). If anything was skipped or failed, a **results window** says *N done, N skipped, N failed* and lists every
repo (failures first) with *Done*, *Skipped* (and why: uncommitted changes, needs a pull, not published, wrong
identity...) or *Failed* (with git's or the server's message). **Retry failed** runs the failed ones again;
double-click a row to open that repo.

To select several cards, **Ctrl-click** or **Shift-click** them, then right-click and choose **Fetch selected**, **Pull
selected (fast-forward only)**, **Commit selected...** or **Push selected**. If you right-click a card that isn't part
of the selection, only that card is selected.

> **Example: end of day**
> Click the **To push** chip, Ctrl-click the three work repos, right-click, and choose **Push selected**.

**Commit all** (top bar, Home, or *Commit selected...*) lists every repo with changes. Each has a tick box and its own
message, suggested from the changed files with the branch's ticket in front. Type in **One message for all** and click
**Use for all ticked** to give them the same message. Each ticked repo stages everything (`git add -A`) and commits.
**Push after committing** also pushes the repos that have a remote, running their pre-push checks. The button reads
**Commit N repo(s)** and stays disabled while a ticked message is empty. Repos that can't be committed safely can't be
ticked, and show the reason:
- an SVN working copy (commit on its work folder's card instead)
- conflicts, or a merge or rebase in progress
- detached HEAD
- the wrong identity, when the identity check is *Stop and ask* (with *Warn*, it's ticked and shows a warning)

Every commit is logged, and Ctrl+Z on a repo undoes its commit.

**Progress and timeouts**: a single repo's Fetch, Pull and Push show git's progress in the repo header. Their 2-minute
network timeout counts from the last progress line, so a big transfer that keeps moving isn't cut off. Fetch all and
the background fetch run quietly. Clone has a fixed 15-minute timeout.

---

## 18. Repo health, remotes and submodules

### Health

**More > Repo health...** (or *Repo health...* in the palette) opens *Health of \<repo\>*. Green items are fine, blue
ones are for your information, yellow ones need attention, and red means files over 100 MB.

| Check | Tells you |
|---|---|
| `.git` size | *.git is N MB*; a warning over 1 GB |
| Branches to delete | Merged into the default branch, or deleted on the server |
| Stale branches | Not touched for 90+ days (shown when there are any) |
| Only on this PC | Branches with no upstream set, apart from the default branch (shown when there are any) |
| Big files | Files over 10 MB in the current tree, and whether Git LFS is set up |
| Stashes | How many, and how many are older than 30 days |
| Worktrees | Missing folders that should be pruned |

The buttons are **Clean up branches...** and **Compact (git gc)** (`git gc --prune=2.weeks.ago`).

### Remotes and submodules

**More > Remotes and submodules...** (palette: *Remotes & submodules...*).

- **Remotes:** **Add...**, **Change URL...**, **Rename...**, **Remove** (*Nothing on the server is deleted; this PC
  just forgets it.*). The app's Fetch, the Branches tab's server list and the MR / pipeline tabs use the main remote
  (`origin`, or the first remote).
- **Submodules:** each one's state (*up to date*, *not checked out*, *on a different commit*, *conflict*). The buttons
  are:
  - **Init & update all:** `git submodule update --init --recursive`
  - **Pull latest (remote):** `git submodule update --remote --recursive`, moving submodules to their branch's newest
    commit; commit the change afterwards
  - **Add...:** asks for the URL, then the folder (guessed from the URL)

> **Example: the server moved**
> Remotes > select `origin` > **Change URL...** > `https://gitlab.example.com/new-group/app.git`.

> **Example: follow an upstream fork**
> Remotes > **Add...** > name `upstream`, then the original project's URL. The app's buttons keep working with
> `origin`; use the quick git box for the other remote: `fetch upstream`, then `merge upstream/main`.

---

## 19. Command palette and keyboard shortcuts

Press **F1** (or the **?** button, or *F1 all shortcuts* in the status bar) for a filterable list of the keyboard
shortcuts.

### Command palette (Ctrl+K)

Press **Ctrl+K** (Ctrl+P also works), type a few letters and press **Enter** (or double-click). Matching is fuzzy: the
letters only need to appear in order (`cmr` finds *Create merge request*), and several words can be in any order. Best
matches come first, up to 80. The palette finds:
- repos: "Open \<repo\>"
- branches of the open repo: "Switch to \<branch\>", and "Check out origin/\<branch\>" for server branches you don't
  have yet
- tabs of the open repo: "Go to History", "Go to SVN", ...
- actions: Fetch, Pull, Commit, Undo history, Recover (reflog), Repo health, Remotes & submodules, Interactive
  rebase, Create MR, Cancel the running action, Undo ..., and so on
- app commands: Home, Overview (all repos), Fetch all, Settings, Manage profiles, Toggle theme, Profile: \<name\>,
  Add SVN repo, Keyboard shortcuts, and so on

Each action shows its keyboard shortcut on the right. Use Up / Down (or PgUp / PgDn) to move, Enter to run, Esc to
close.

> **Examples**
> - `web po` + Enter opens web-portal.
> - `sw main` + Enter switches the open repo to main.
> - `hist` + Enter goes to the History tab.
> - `push all` + Enter runs Push all.

### Shortcuts

Button tooltips end with their shortcut. Ctrl+Shift works on the selected repo, Ctrl+Alt on the app or every repo in
the loaded profile.

| Keys | Does |
|---|---|
| F1 | Keyboard shortcuts |
| Ctrl+K / Ctrl+P | Command palette |
| Ctrl+\` | Quick git command box (Activity panel) |
| Ctrl+F | Filter the repo list |
| Ctrl+Shift+F | Search all repos (commits, branches, merge requests, code) |
| Ctrl+Z | Undo the app's last action on this repo |
| Esc | Cancel the running action |
| Alt+1 ... Alt+9, Alt+0 | Overview, Changes, Branches, History, Merge requests, Pipelines, Stashes, Tags, Worktrees; SVN |
| F5 / Shift+F5 | Refresh all / Fetch all |
| Ctrl+F5 | Reload the current repo tab |
| Ctrl+0 / Ctrl+1..9 | Profile: All profiles / the profiles in order |
| Alt+Home | Home page |
| Ctrl+Alt+H | Overview (all repos) / back to Home |
| Ctrl+Shift+E / P / U / Y | Fetch / Pull / Push / Pull & push (selected repo) |
| Ctrl+Shift+N / B / M | Start branch / Branches tab / Update from default branch |
| Ctrl+Shift+R / X | Create merge request / Finish branch |
| Ctrl+Shift+A / S | Stage all / Stash changes |
| Ctrl+Shift+O / I / T / W | Open folder / editor / terminal / on the web |
| Ctrl+Alt+P / U / C | Pull all / Push all / Commit all |
| Ctrl+Alt+X / W | Clean up branches (all repos) / What I did |
| Ctrl+Alt+N / Ctrl+O / Ctrl+Alt+O / Ctrl+Alt+V | Clone / Add repo / Scan a folder / Add SVN repo |
| Ctrl+Alt+A / Ctrl+Alt+T / Ctrl+Shift+L / Ctrl+, | Accounts / Toggle theme / Activity log / Settings |
| Ctrl+Enter / Ctrl+Shift+Enter | Commit / Commit & push (in the message box) |
| S / U / D / H | Stage (or unstage) / unstage / discard selected diff lines / select whole hunk |
| F7 / Shift+F7 (Alt+Down / Alt+Up) | Next / previous change in a diff |
| Left / Right / Enter / Space | Fold a folder row (Folders view) |
| Enter | Run a history search (search modes) |

The table behind the toolbar keys, tooltips and palette is `Services\Shortcuts.cs`. The fixed keys (Esc, Ctrl+0..9,
Alt+0..9, Ctrl+P, the commit box and diff keys) are bound in the views.

### Quick git command (Ctrl+\`)

Type any git command after the `git` prefix, such as `remote -v` or `bisect start`, and press Enter (or **Run**). A
leading `git` you type is dropped. It runs in the selected repo and its output goes to the Activity log. Commands that
can lose work (`reset --hard`, `clean -f`, `branch -D`, ...) ask first. Up and Down recall your last 50 commands.
**Terminal** opens PowerShell / Windows Terminal in the repo.

---

## 20. Background fetch, notifications and the tray

- Each profile has its own interval (Settings > Profiles > **BACKGROUND FETCH EVERY (MINUTES, 0 = OFF)**, default 10).
  Only the loaded profile(s) are fetched, and only **watched** repos (right-click > **Watch / Unwatch (background
  fetch)**, or More > **Watch / unwatch**; the eye on the card shows it). At start-up, watched repos whose profile
  interval is above 0 are fetched once.
- Local status is re-read when the window gets focus (at most every 5 seconds). Each repo's `.git` folder is also
  watched, so commits, branch switches and fetches made in VS Code or a terminal appear within about 2 seconds. Edits
  to files aren't watched; they show on focus or F5.
- Tray notifications (titles start with *[profile]*) appear when:
  - a pipeline or workflow on your branch finishes
  - your branch's MR is merged
  - **new commits arrive on the server** for a watched repo (from the timed background fetch)

  Clicking a notification opens the window. Turn them off for the whole app (Settings > General) or for one profile
  (Settings > Profiles).

**Keep this branch up to date automatically** (Repo settings): after any fetch (background, start-up, Fetch all /
selected, Pull all, Push all), the branch is fast-forwarded when it's published, clean, has nothing to push, isn't
detached and has nothing in progress. Anything else is left for you.

**Tray**: the tray icon has a status dot for the loaded profile - red (something failing or needs you), amber
(changes to commit or push), green (all clear). Its tooltip reads *Repo Sync: N need you, N failing, N with changes,
N to push* or *Repo Sync: all clear*. Right-click it for **Open Repo Sync**, **Fetch all**, **Pull all
(fast-forward)** and **Exit**. With **Minimize to the tray** (Settings > General), minimizing hides the window;
double-click the icon to bring it back.

---

## 21. Settings reference

Open Settings with the gear in the toolbar, **Ctrl+,**, the gear next to the profile picker (Profiles tab), or the
palette (*Settings...*, *Manage profiles...*). **Save** keeps your changes, **Cancel** drops them.

### Profiles

The list shows each profile's color, name and repo count, with **Add** (asks for a name; color, clone folder
`%USERPROFILE%\code\<name>` and a 10-minute fetch are filled in), **Remove** (not for the last profile) and up / down
arrows (*the order sets Ctrl+1..9*). For the selected profile:

| Field | Default | Does |
|---|---|---|
| NAME | Personal / Work | Shown in the picker, pills and group headers |
| COLOR (#RRGGBB) | `#2F80ED` / `#0F9D8A` | Profile dot and pill color |
| COMMIT AS (NAME / EMAIL) | empty | Identity for this profile; empty = no identity check |
| CLONE INTO | empty | Where Clone puts new repos |
| BACKGROUND FETCH EVERY (MINUTES, 0 = OFF) | 10 | Background fetch interval |
| Notifications for this profile's repos | on | Tray notifications for this profile |
| WRONG IDENTITY ON COMMIT / PUSH | Stop and ask | Stop and ask / Warn, then go ahead / Don't check (applies to every profile) |

### General

| Field | Default | Does |
|---|---|---|
| THEME | Follow Windows | Follow Windows / Light / Dark (also the toolbar's Light / dark button) |
| Compact density | off | Smaller rows and padding |
| NEW BRANCH NAME PATTERN | `feature/{name}-{user}` | Start branch's name pattern (the dialog saves it too) |
| TICKET LINK | empty | `{ticket}` = the ticket in the branch name; added to new MRs |
| EDITOR COMMAND | `code` | Used by Open in editor and conflict Edit |
| EXTRA GITLAB SERVERS | empty | Comma-separated self-hosted GitLab servers |
| Notify me when a pipeline / workflow finishes or a merge / pull request is merged | on | Also covers new-commit notifications |
| Minimize to the tray | off | Minimizing hides the window to the tray |

### Global git identity

**NAME** and **EMAIL** are your `git config --global user.name` / `user.email`. They're written only when both are
filled in and one changed.

### Export / migrate

**Export repos and settings...**, **Show file** and **Import from a file...**; see
[Moving to a new computer](#moving-to-a-new-computer).

---

## 22. Safety rules

- Discard, delete, hard reset, abort, force push, rewrite, rebase onto and Undo from the Undo history window all ask
  first. Reset, rebase onto, force push and rewrite show *What will this do* with what would be lost.
- **Force push** uses `--force-with-lease`, so it won't overwrite server work newer than your last fetch.
- **Undo last commit** refuses once the commit is pushed. Use Revert for pushed commits.
- **Pull** and **Pull all** never merge: Pull fast-forwards or rebases with your work stashed; Pull all only
  fast-forwards.
- **Merge**, **Rebase onto**, **Update from \<default\>**, **Finish branch**, **Revert** and **Cherry-pick** refuse with
  uncommitted changes.
- **Start branch** uses `--no-track`, so a first push can't land on `main`.
- **Finish branch** deletes the branch only when its work is in the default branch or its MR was merged.
- **Recover / Go back here** uses `reset --keep`, which never throws away uncommitted work.
- **Interactive rebase** auto-stashes your work and refuses histories that contain merge commits.
- Pushes and force pushes with files over 100 MB are stopped before they reach the server.
- A repo's **pre-push check** (Repo settings > **BEFORE EVERY PUSH, RUN**, e.g. `dotnet test`) runs before every push
  and force push. If it fails you see its output and can fix it or **Push anyway**.
- **Undo** refuses to drop commits that are already on the server.
- The SVN working copy's git branch can't be committed to directly, and Send stops while the work branch has
  uncommitted files.
- The first write to a server (create, approve, merge, comment) asks you to confirm.

---

## 23. What's in this folder, and the files the app keeps

### The repository

| Path | What it is |
|---|---|
| `docs\USER-MANUAL.md` | This manual |
| `README.md` | Project overview |
| `Directory.Build.props` | Version (3.1.0, shown in the title bar) and shared build settings for the app and tests |
| `RepoSyncV3.slnx` | Solution with the app and the tests |
| `RepoSyncV3\` | The app (WPF, .NET 8). `RepoSyncV3.csproj` builds `repo-sync-v3.exe`; Release publishes one self-contained win-x64 file |
| `RepoSyncV3\Models\` | Settings, git, and GitHub / GitLab data shapes |
| `RepoSyncV3\Services\` | All git, svn and server logic, with no UI (git runner, repo actions, GitHub / GitLab clients, SVN workflow, undo, search, shortcuts table, settings and token storage) |
| `RepoSyncV3\ViewModels\` | The app shell (`MainViewModel.*`), one card (`RepoViewModel`), one repo's tabs (`RepoDetailViewModel.*`, one file per tab), Home (`DashboardViewModel`) |
| `RepoSyncV3\Views\` | The main window and every dialog; `Views\Tabs\` holds Home, Overview and each repo tab |
| `RepoSyncV3\Themes\` | Light and dark colors, control styles and the shared row templates |
| `RepoSyncV3\Converters\` | Small XAML helpers |
| `dist\` | Output of `dotnet publish` (not in git) |
| `RepoSyncV3.Tests\` | Automated tests |

The repo root also has `.github\workflows\build.yml`: on every push to `main` (and pull requests) GitHub
Actions builds, runs the tests on Windows and publishes `repo-sync-v3.exe` as a build artifact; pushing a tag like
`v3.2.0` also attaches the exe to a GitHub release.

### Tests

Run `dotnet test RepoSyncV3.Tests` (or `dotnet test RepoSyncV3.slnx`) from the repo root. git must be on PATH; the
SVN tests also use `svn` / `svnadmin` and are skipped without them. The tests create throwaway repos under
`%TEMP%\repo-sync-tests` (and a scratch SVN server for the SVN tests) and delete them afterwards. They cover git status
and log parsing, line staging, conflicts, undo, interactive rebase, previews, the history graph and labels, diffs,
the file tree, settings save and recovery, migration export / import, profiles, search, the shortcut table, the SVN
workflow and log, worktrees, the `.git` watcher, and that every window and tab loads.

### Files next to the exe

These live in the exe's folder (`dist`, or `bin\Debug\net8.0-windows` for
`dotnet run`). Copy the folder to move the app with its data.

| File | Holds | Written |
|---|---|---|
| `settings.json` | Repo list, profiles, preferences, window size, quick-command history | Whenever a setting changes, and on exit |
| `settings.json.bak` | The previous save | Each save |
| `settings.json.corrupt-<time>` | A `settings.json` that couldn't be read (the app loads the `.bak` instead and says so) | Only when that happens; never deleted |
| `tokens.json` | One access token per server, encrypted for your Windows account (only you, on this PC, can read them) | Accounts > Test & save / Remove |
| `gitlab-tokens.json` | Older token file; renamed to `tokens.json` on first start | Never (read once) |
| `status-cache.json` | Last known status per repo, for the fast start | After each refresh, and on exit |
| `feed.json` | The What changed feed (newest 200) | When lines are added or cleared |
| `errors.log` | Details of unexpected errors (the app shows a message and keeps running) | Only on an error; never trimmed |

The Activity log and the Undo history are kept in memory only.

### Other files the app writes

| File | Where | Why |
|---|---|---|
| `.gitignore` | In a repo | Ignore actions and the .gitignore editor; Add SVN repo setup |
| `git-sync-last-send.json` | `<SVN working copy>\.git\` | The last Send, for Undo last send and the *sent* pill. Deleted by Sync or Undo. |
| Global git config | `%USERPROFILE%\.gitconfig` | Settings > Global git identity |
| `<repo>-pipeline-<id>\` | `%USERPROFILE%\Downloads\` | Downloaded artifacts |
| `repo-sync-export-<MACHINE>-<time>.json` | Where you save it | Settings > Export. It has no tokens or passwords, but it does list machine, paths and remotes, so keep it private. |
| Scratch files | `%TEMP%` | Mirror commits, Send patches, TortoiseMerge copies (`repo-sync-svn-compare\`), interactive rebase plans (`repo-sync-rebase-*`). Most are deleted after use. TortoiseMerge copies are cleared at the next start once they're 12 hours old; a stopped rewrite's plan is removed when you Continue to the end or Abort (or at a start 30 days later). |

---

## 24. Troubleshooting

| Message or problem | Fix |
|---|---|
| *GitLab rejected the token (401)* / *GitHub rejected the token (401)* | The token expired or was revoked. Accounts > Create a token > Test & save. |
| *GitLab refused the request (403)* / GitHub 403 | The token lacks a scope (GitLab `api`; GitHub `repo`, `workflow`) or your account lacks access. Make a new token. |
| *No GitLab/GitHub token saved for \<host\>.* | Accounts > add a token for that server. Home also says *No token for \<host\>*. |
| *Could not reach \<server\>* / *\<server\> did not answer within Ns.* | Check the network or VPN, then retry. |
| *Timed out after 120s (a dead connection, VPN, or a sign-in window waiting for you).* | git got no answer for 2 minutes. Check VPN, or look for a hidden credential window, then retry. |
| *Could not start git ... Is Git for Windows installed?* | Install Git for Windows, or put `git.exe` on PATH. |
| Card says *folder not found* | The folder was moved or deleted. The Overview's **Remove from list** takes the card off; add the new location with Add repo. |
| *There are N uncommitted change(s) - commit or stash them before ...* | Commit or stash, then try again. |
| *A merge or rebase is in progress here.* | Finish it on the Changes tab (Continue or Abort). |
| *Not on a branch (detached HEAD).* | Switch to a branch on the Branches tab. |
| *Your commits conflict with the server's - the rebase is paused so you can resolve it.* | Resolve on the Changes tab (Resolve... / Keep mine / Take theirs), then Continue, or Abort. Your stashed work stays in Stashes until then. |
| *N file(s) are over 100 MB - the server will reject this push.* | See the `dump.sql` example in [section 5](#large-file-guard), or set up Git LFS. |
| *Pre-push check failed (exit N): \<command\>* | Fix what it found, or Push anyway. The check is set in Repo settings. |
| *The last commit is already on the server.* / *Can't undo '...': its commits are already on the server.* | Use Revert on the History tab. |
| *Turn off 'Ignore whitespace' to stage single lines.* | Untick Ignore whitespace (and Split diff) above the diff. |
| *Could not apply the selected lines* | The file changed after the diff was shown. Refresh (F5) and try again. |
| *The rebase stopped part way.* | Fix the conflicted files on the Changes tab and Continue, or Abort. Undo history or Recover can undo it afterwards. |
| Review window shows no files or *diff unavailable* | The MR's branch couldn't be fetched (deleted, or no access). Use **Open on the web**. |
| *This job/run has no artifacts (or they expired).* / *Pick a job first* | The job or run had none, or they expired. On GitLab, select a job first. |
| Activity chart is empty (*No commits by you in the last 14 days*) | It counts commits by each repo's git email. Check the identity in the repo header. |
| *GitHub's API limit is almost used up* / *... is used up for now.* | Wait until the time shown, or raise the background fetch interval in Settings > Profiles. |
| *settings.json could not be read and was kept as settings.json.corrupt-\<time\>.* | Nothing to do; the previous save was loaded. The old file is kept in case you need it. |
| *An unexpected error occurred and was suppressed so the app could keep running* | The details are in `errors.log` next to the exe. |
| SVN: *'git' is not on PATH.* / *'svn' is not on PATH.* | Install Git for Windows / the TortoiseSVN command line client tools, then run Add SVN repo again. |
| SVN: *'svn ...' failed* (password) | The app runs svn non-interactively. Run any svn command against the repo once in a terminal (e.g. `svn update`) so the login is saved. |
| SVN: *The SVN working copy has N local change(s).* | A Send isn't committed yet: commit it (TortoiseSVN commit) or **Undo last send**. |
| SVN: *Your branch 'work' has uncommitted changes in \<folder\>.* | Commit (or remove) every changed or untracked file on the work card, then send. |
| SVN: *'svn update' left conflicts* | Resolve them in SVN (TortoiseSVN > Resolve), then run it again. |
| SVN: *Your branch conflicts with the latest SVN changes. The merge was undone.* | In the work folder, run `git merge main` (quick git box), resolve, commit, then run it again. |
| SVN: *git in \<working copy\> is on 'X', not 'main'.* | Switch the working copy's git back: quick git `switch main` on the working copy card. |
| SVN: *core.autocrlf is 'X' in this repo; it must be 'false'* | Quick git on the working copy card: `config core.autocrlf false`. |
| SVN: *Branch 'work' is not checked out in any worktree folder.* | Add a worktree for it on the Worktrees tab. |
| SVN: *TortoiseProc.exe / TortoiseMerge.exe was not found* | Install TortoiseSVN. |
| SVN: *'git worktree move' failed.* | Close VS Code, terminals and dev servers using the work folder, then Move work folder again. |
| Something unexpected happened | Every action's git commands are in the **Activity** log, with their output. |
