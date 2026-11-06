# Claude Account Switcher

A small Windows app for moving Claude Code desktop sessions between accounts on the same PC.

Claude Desktop keeps each account's sessions in
`%APPDATA%\Claude\claude-code-sessions\<accountUuid>\<orgUuid>\local_<id>.json`.
This app lists those folders, shows which email each UUID belongs to, lists each account's latest
sessions, and copies or moves the ones you pick to another account.

## Download

Grab the latest exe from [Releases](../../releases):

- `ClaudeAccountSwitcher-vX.Y.Z-win-x64.exe` is small and needs the [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0).
- `ClaudeAccountSwitcher-vX.Y.Z-win-x64-standalone.exe` is larger and runs without it.

## Build

```
dotnet publish src/ClaudeAccountSwitcher -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

## Releases and versions

Versions follow `MAJOR.MINOR.PATCH`, starting at `0.0.1`. Pushing a tag such as `v0.0.2` runs the
[Release workflow](.github/workflows/release.yml), which builds the app with that version, runs the smoke
test, and publishes a GitHub Release with both exes:

```
git tag v0.0.2
git push origin v0.0.2
```

## How emails are found

1. A label you set yourself (✎ on an account card).
2. `oauthAccount` in `~/.claude.json` (and `.claude.json.backup`), for the account the CLI is signed in with.
3. The claude.ai storage Claude Desktop keeps (`IndexedDB`, `Local Storage`), which holds the
   `uuid → email_address` profile of each account that has signed in.
4. Whatever an earlier run found (kept in `%APPDATA%\ClaudeAccountSwitcher\settings.json`).

## What a transfer does

- Copies `local_<id>.json` into the target account folder (via a temp file, so it is never half-written).
- Deletes a `deleted_<id>` marker in the target if there is one, since that marker would hide the session.
- Keeps archived state in step through `archived-sessions.idx`.
- **Move** also removes the file from the source after it is copied.
- Each session shows how it compares with the target: **New**, **Newer here**, **In sync**, or **Newer there**.
  By default an existing copy is replaced only when yours is newer.
- Every file that gets replaced or removed is first backed up to
  `%APPDATA%\ClaudeAccountSwitcher\backups\<timestamp>\`, along with a `manifest.json`.
  To undo a transfer, copy those files back.

If Claude Desktop is running, restart it to see the new sessions.

## Tests

`dotnet run --project tests/SwitcherCore.Smoke` reads the real sessions folder without changing it,
then runs copy and move transfers on a temporary copy of it. Add `-- --fixture` to use a generated
two-account tree instead (this is what CI runs).
