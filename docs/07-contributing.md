# Daily Workflow

This is a personal-use plugin — no PR review, no public testing, no plugin-repo submission. Workflow is just **edit → build → test in-game → commit**.

## The loop

```bash
# from the project root
cd "C:/Users/akkis/AppData/Roaming/XIVLauncher/devPlugins/noWickyXIV"

# 1. edit code
# 2. build
dotnet build noWickyXIV.csproj -c Debug

# 3. in-game: /xlplugins → noWickyXIV → Disable + Enable
#    (or restart the game for changes that touch hooks/static init)

# 4. when happy with the change:
git add -A
git commit -m "describe the change"
git push origin main
```

If the game's running, the build's copy step might fail on the live DLL because Dalamud has it mapped. In that case Dalamud holds the file open in shared mode, but the file IS overwriteable on most setups; if the build complains, disable the plugin first via `/xlplugins`, build, re-enable.

## Branching

We work directly on `main` for this fork. No feature branches needed for personal use. If a change is risky:
- Tag a commit before the change: `git tag pre-experiment`
- Make the change, test
- If it breaks, `git reset --hard pre-experiment`

## Commit message style

The project so far uses prefixed conventional-style messages:
```
feat: add ADS-on-RMB
fix: clamp position float to ±0.5m
docs: update camera architecture
chore: bump Dalamud.NET.Sdk to 15.0.0
```

But for personal use, plain English is fine. Look at `git log --oneline` for examples.

## Adding a new dynamic-feel feature

Steps (use ADS-on-RMB as the model — see [04-camera-architecture.md](04-camera-architecture.md)):

1. **Add config** to [`Configuration.cs`](../Configuration.cs) — bool toggle + magnitude floats. Pick reasonable defaults that produce identity behavior when off (so a fresh user sees no change).
2. **Add UI rows** to [`PluginUI.cs`](../PluginUI.cs) `DrawCameraDynamics()` — wrap in a `BeginGroupBox(...)`, gate on `DynamicsSectionMatches("YourSectionName")` so the search filter works.
3. **Add reset entries** to [`PluginUI.cs`](../PluginUI.cs) `ResetAllDynamicsToDefaults()` so the reset button clears your fields.
4. **Add per-frame method** to [`CameraDynamics.cs`](../CameraDynamics.cs) — name like `UpdateMyFeature(GameCamera* cam, bool tps, float dt)`. Gate on the config toggle. Use `ExpDecay` for smoothing.
5. **Wire into `Update()`** — call from the right spot in the order. If you write to `currentHRotation` or `currentVRotation`, it must come AFTER `UpdateSensitivity` (which reads pre-write deltas).
6. **Build, test, iterate.**

## Adding a new hotkey

1. Add `int MyHotkey = (int)VirtualKey.X;` to [`Configuration.cs`](../Configuration.cs).
2. Add `HotkeyRow("My feature", ref ... , defaultVk: ...)` to the Hotkeys section in [`PluginUI.cs`](../PluginUI.cs) `DrawCameraDynamics()`.
3. Add `private static void UpdateMyHotkey()` in [`InputHandler.cs`](../InputHandler.cs):
   ```csharp
   int vk = noWickyXIV.Config.MyHotkey;
   if (vk == 0) return;
   if (EdgePressed(vk)) {
       // do the thing, save config, etc.
   }
   ```
4. Call it from `InputHandler.Update()`.

## Don't accidentally publish

This is a private repo and a private fork. Things to keep that way:
- `gh repo create --private` was used initially. Confirm with `gh repo view ebhemmanuel/noWickyXIV --json visibility`.
- The original Cammy is licensed (check its repo). For personal use you can do anything; for public distribution you'd need to comply (typically GPL-style copyleft would require keeping your fork open-source too).
- Don't open a PR against [DalamudPluginsD17](https://github.com/goatcorp/DalamudPluginsD17) — that's the official plugin repo and submission would trigger review + AI-policy compliance + maintenance commitment.

## Updating from upstream Cammy

If you ever want to pull new Cammy changes:

1. `git remote add upstream https://github.com/UnknownX7/Cammy.git`
2. `git fetch upstream master`
3. Cherry-pick or merge specific commits — `git cherry-pick <hash>` is safest because Cammy's history is one-author and our renames mean automated merges will conflict on most files.
4. Re-run the rename pattern (see commit `1c39298`) on any newly-pulled file that still says `Cammy`.

For the foreseeable future, this isn't worth the effort — our fork is divergent enough that upstream tracking adds friction without much benefit.

## Tests

There are none. We test in-game by feel. Future improvement could be a headless test harness for the math helpers (`ExpDecay`, `AngleDelta`, `CubicBezier`) but for camera feel, no automated test substitutes for a real session.
