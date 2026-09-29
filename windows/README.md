# Compositor for Windows

A Windows build of [Compositor](https://github.com/robbietilton/Compositor), the macOS image editor. It is a
port, not a wrapper: the document model, the tiled compositing engine, every tool, every filter and the whole
interface are written again for Windows, and the two builds read and write the **same `.comp` project format**.

It lives in `windows/` on the `compositor_win` branch, beside the macOS app (`Compositor/`, Swift and AppKit),
which stays the original and the reference. The macOS code in this branch is **not** compiled by anything here;
it is kept so the port can be read against it.

## This branch is Windows-only, and it does not merge `main`

**The rule: `main` (and `upstream/main`) is read, never merged.** Features are tracked by porting them by hand —
read the Swift that does the work, write the same rule in C#, hold it to the same expectation — and never by
pulling the branch in. A merge would drag the whole macOS source tree, its Xcode project and its CI along with
it, none of which compiles or runs on Windows, and it would make every one of the port's own files fight a
conflict against Swift code it has nothing to do with. So if you are working on this branch: **read upstream,
port the feature, do not `git merge main`.**

The port tracks **macOS 1.3.7** (the version `src/Compositor.Desktop` reports, which is also the version the
update feed is compared against). Nothing newer is tracked. See *Keeping this alive* at the end.

## What is here

| Project | What it is |
|---|---|
| `src/Compositor.Core` | Everything that is not Windows-specific: the document model, the tiled renderer, all 24 blend modes, selections, every tool's session rules, the filters and adjustments, the `.comp` reader and writer, undo. No UI dependency, so it is testable on its own. |
| `src/Compositor.Desktop` | The editor window: Avalonia 12, the canvas control, the layers panel, the tool rail, the options bar, the status line, and the panels that are not dialogs. Also the headless self-checks below. |
| `src/Compositor.Cli` | A console tool for reading, writing and rendering projects without a window — the same engine, so a project renders identically to what the window shows. |
| `tests/Compositor.Core.Tests` | 792 xunit tests. The macOS `CompositorTests` are the behavior spec, so a ported kernel is held to the same expectation the Swift one is. |

**Requires the .NET 10 SDK.** Build, test and publish:

```sh
dotnet build windows/Compositor.slnx
dotnet test  windows/tests/Compositor.Core.Tests/Compositor.Core.Tests.csproj
dotnet publish windows/src/Compositor.Desktop -c Release -o dist-app   # the app
dotnet publish windows/src/Compositor.Cli     -c Release -o dist       # the CLI
```

There is no installer and nothing is signed: `dist-app` is a folder you can run `Compositor.Desktop.exe` from.
The `test` command is the whole verification story — the suite is green in Debug and Release at **0 warnings**,
which this port holds itself to because a warning has repeatedly been the thing that caught a name resolving to
the wrong member, and because there is no UI-testing harness behind the window for a compiler to fall back on.

## Checking the window without a pointer

Most of the port is Core and testable, but the window is not — there is no UI-testing harness, so the desktop
app carries self-checks that build the real window, drive it and draw it. Each takes a PNG path, prints what it
did, and returns; each leaves no process behind.

```sh
Compositor.Desktop.exe --window      <out.png>   # build the window and draw it
Compositor.Desktop.exe --tabs        <out.png>   # drive the tab strip: open, switch, close
Compositor.Desktop.exe --tools       <out.png>   # every tool: the rail, the Tools menu and the options bar agree
Compositor.Desktop.exe --camera-raw  <out.png>   # the Camera Raw panel: amounts, scope, readout, guides, apply
Compositor.Desktop.exe --shortcuts   <out.png>   # every key against the menu row that shows it, and a rebind
Compositor.Desktop.exe --dialogs     <out.png>   # the dialogs' bodies and the controls they are made of
Compositor.Desktop.exe --theme-probe [word]      # the colors the theme resolves to
Compositor.Desktop.exe --updates                 # read the real update feed and report the verdict
Compositor.Desktop.exe --render <project|--demo> <out.png> [--grid|--shape|--gradient|--preview|--pixel-grid|--zoom-in]
Compositor.Desktop.exe --rulers      <out.png> <scale> <origin>
```

**A bitmap cannot show everything, and the checks say so where that matters:** a control's template is applied
when it reaches a live window, so offscreen a `TextBox` is a box with no text and a `ScrollViewer` lays nothing
out. That is why `--tools` draws the rail's column on its own as well as the window, and why `--shortcuts` draws
the shortcut sheet's list of rows beside the window rather than inside it. A menu popup cannot be drawn at all,
so the gestures a menu carries are checked as properties, not as pixels.

The strongest check of all for anything that draws is a **differential render**: render the same project twice,
with and without the thing, and diff the PNGs. It is how the guides, the grid and the pixel grid were proved.

## Following the macOS build

To port a feature from the Mac to here:

1. **Read the Swift that does it** — `Compositor/Document`, `Compositor/Rendering` — and its test in
   `CompositorTests`. The test is the specification: a ported kernel that passes the same expectation is right.
2. **Put the rule in `Compositor.Core`, not in the window.** Every tool that is a drag or a keyboard session is
   a core object the window merely feeds; that is what makes 792 core tests possible and the window thin.
3. **Hold it to the Mac's expectation in a test** — the same buffer, the same numbers, and a note in the test
   where the port deliberately differs.
4. **Wire it to the window** and, where the window's own paths are involved, extend one of the self-checks.
5. **Run the audit, because a rule ported into the core and never called looks exactly like a feature nothing
   reaches:** for every `public static` member of `Compositor.Core`, find the ones nothing outside their own file
   mentions. This has found five dead members and one *unreachable* panel control so far.
6. **Run the suite, the self-checks, and a differential render.**

**The honesty rule this port holds to:** nothing is silently skipped. An unimplemented render feature throws
rather than drawing nothing, so a project can never render plausibly-but-wrong, and an import that cannot keep
something editable keeps the pixels and reports it. A menu row or a shortcut bound to something the port cannot
actually do would be worse than the missing feature, so those gaps are listed below instead.

## Known differences from the macOS build

Nothing on this list is hidden in the code — each is either a deliberate refusal or a documented gap.

**Absent features**

- **Remove Background, Object Selection and Select ▸ Subject are not here at all** — no menu rows, no disabled
  items. All three are Apple Vision subject masks on the Mac. Adding them means an ONNX segmentation dependency
  (a model, its licence, its size), and that decision was taken deliberately: parked, with no dependency added.
  Everything downstream of a selection is complete, so the loss is the segmentation step alone — a subject must
  be cut out here with the wand, a lasso or Color Range. **This is the only feature-level gap left.**

**Smaller divergences**

- **No color picker**: a color is three or four RGB numbers or sliders, where the Mac opens the system picker.
  Every value is reachable, and tools that only have a few colors get swatches.
- The **Dither** panel's tone heading says "Levels" where the Mac says "Tones"; the panel does not retain its
  look when reopened; and the blend-mode menu has no rules between its groups where the Mac's draws them (the
  order is the same).
- **Bloom / Glow** is a take on the look, not Core Image's arithmetic, and the code says so where a reader will
  look. Every other filter and adjustment is a port of the Mac's own kernel or operator.
- **Updates**: the feed both builds read publishes a macOS `.dmg`, so Help ▸ Check for Updates reports the news
  and links the release page; building from this repository is what updates a Windows copy.
- **Most of the window has now been driven by a real pointer, and some of it has not.** `--clicks` builds the
  real window on Avalonia's headless platform, clicks it and photographs it: a brush stroke, a marquee, an
  ellipse with the Anti-alias tick both ways, a wand click, a Control-drag of a selection's pixels, a guide
  pulled off a ruler and dragged, the Type tool's caret and words, the opacity slider, the Crop tool from its
  seeded frame to its corner handle to Enter, a Control-drag distortion of a transform corner, the filter
  preview's two states, the blend pop-up, the rail's own button and the sample ring. A script that clicks the
  *published* app has also opened menus (which is how the gestures are known to draw), made a document from the
  New Project dialog and driven the shortcut sheet's recorder with real keys. **What neither drives:** the curve
  editor's handles and the filter dialogs' own widgets — both inside modal windows, and `ShowDialog` wants a
  dispatcher loop the checks do not run — and the Camera Raw panel's own controls, whose behaviour `--camera-raw`
  drives through the panel's API rather than by clicking them.

## Keeping this alive, or handing it on

This port was built to be *finished*, not to be a treadmill, and the honest position is written down here rather
than promised elsewhere.

**The branch stands on its own.** It is a complete, working Windows build at macOS 1.3.7 parity. If nobody ever
touches it again it does not rot into something broken — it stays what it is, and this README says which version
of the Mac it was made against. That is a deliverable, not a failure.

**If you can only do one thing to keep it honest:** there is now a CI workflow at
`.github/workflows/windows.yml` — `dotnet build -warnaserror`, `dotnet test`, and the window driven with a
pointer through the headless platform — scoped to `paths: windows/**` so a change to the Mac app cannot make
it red, and one new file so it cannot conflict with upstream's own `verify.yml`. That is the only mechanism
that keeps the port green without anyone watching; if it ever goes red and nobody has time, delete the
workflow rather than leave a red badge that means nothing.

**Publishing it as a repository of its own.** The port's history is separable from the macOS app's, because
almost every commit that made it touches only `windows/`:

```sh
git subtree split --prefix=windows -b windows-only   # 85 commits, 197 files, no Swift; the port at the root
git push git@github.com:<you>/Compositor-Windows.git windows-only:main
```

Two things that command does *not* carry over, both easy to miss: **`LICENSE` sits one level up**, so copy it
into the new repository or the MIT notice is lost, and the CI workflow has to be added at the new repository's
root, since `.github/` is not inside `windows/`.

**Contributing it upstream.** The macOS app is Swift in an Xcode project, so the port cannot be merged into it as
code — there is no version of this that makes `windows/` compile into the Mac app. What can be offered, in
increasing order of how much upstream has to take on:

1. **A pointer.** One line in the macOS README linking to a Windows repository. Small, changes nothing for them,
   and it is what actually makes the port discoverable. This is the PR most likely to be accepted.
2. **A repository of its own**, presented as the project's Windows build — a named fork, or a repository owned
   beside upstream's — with upstream's README linking to it.
3. **In-tree.** `windows/` accepted into upstream's repository. It changes nothing about their build, but it
   asks a solo maintainer to own a second platform's dependencies, CI and support, so expect a no — and ask
   before assuming either way.

**Do it in this order:** open an *issue* on upstream first — a short proposal saying what exists, that it costs
their build nothing, and the three options above — and take whichever they pick. Do not open a large pull
request cold, and do not present it as official work before they have said it is.

**Licence.** Compositor is MIT, © 2026 Wonder Assembly LLC. A port is a derivative work, so publishing this
branch as its own repository is fine **as long as `LICENSE` and its copyright notice travel with it**, and it is
described as a port rather than as Compositor itself — MIT grants no rights to the name. If it is contributed
in-tree, the same licence covers it.
