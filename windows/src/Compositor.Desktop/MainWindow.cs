using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using SelectionMode = Compositor.Core.Document.SelectionMode;

namespace Compositor.Desktop;

/// <summary>
/// The editor window: the canvas, the layers panel and a status line. Projects are opened as folders,
/// because on Windows a `.comp` is an ordinary directory rather than the file-like package the Mac sees.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly IBrush Panel = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x28));
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEB));

    private readonly CanvasView _canvas = new();
    private readonly ListBox _layers = new();
    private readonly TextBlock _status = new() { Margin = new Thickness(10, 3, 10, 3), Foreground = Ink };
    private readonly DocumentHistory _history = new();

    /// <summary>One row, because what ⌘E does depends on the panel selection: it is named for it here.</summary>
    private readonly MenuItem _merge = new() { HotKey = new KeyGesture(Key.E, KeyModifiers.Control) };

    /// <summary>The clipping, mask and visibility rows, whose names and availability follow the selection.</summary>
    private readonly MenuItem _visibility = new();
    private readonly MenuItem _clipping = new() { HotKey = new KeyGesture(Key.G, KeyModifiers.Control | KeyModifiers.Alt) };
    private readonly MenuItem _addMask = new() { Header = "Add _Mask" };
    private readonly MenuItem _maskToggle = new();
    private readonly MenuItem _maskLink = new();

    /// <summary>The rest of the Layer menu, so all of it can go dead together when nothing is selected.</summary>
    private readonly List<MenuItem> _layerItems = [];

    /// <summary>Whether a brush stroke goes on the active layer's mask instead of its pixels.</summary>
    private readonly MenuItem _paintOnMask = new()
    {
        Header = "Paint on the layer _mask",
        ToggleType = MenuItemToggleType.CheckBox,
    };

    /// <summary>Whether the brush paints or erases; on a mask, that is white or black.</summary>
    private readonly MenuItem _eraseToggle = new()
    {
        Header = "Brush _erases",
        ToggleType = MenuItemToggleType.CheckBox,
    };

    private bool _paintingMask;
    private bool _erasing;
    private readonly List<(MenuItem Item, Func<CanvasDocument, ImageLayer, bool> Ready)> _layerRows = [];

    /// <summary>Owns the pixels: the document's layers reference the snapshot's images, so the document
    /// disposes them and the snapshot is dropped rather than disposed.</summary>
    private CanvasDocument? _document;

    /// <summary>The layer behind each row of the panel, so a selection can be turned back into an id.</summary>
    private readonly List<Guid> _rows = [];

    /// <summary>Where this project was opened from, so Save writes back to it.</summary>
    private string? _projectPath;

    /// <summary>Which pointer tool is in hand, and the menu rows that show it.</summary>
    private enum Tool
    {
        Pan,
        Move,
        Marquee,
        Ellipse,
        Lasso,
        Polygon,
        Wand,
        Brush,
        Clone,
        Blur,
        Heal,
        Eyedropper,
    }

    private readonly Dictionary<Tool, MenuItem> _toolItems = [];
    private Tool _tool = Tool.Pan;

    /// <summary>The layer a move gesture is moving, while the pointer is down.</summary>
    private Guid? _moving;

    /// <summary>The brush's settings, as the options bar would hold them, and where Clone Stamp copies from.</summary>
    private BrushSettings _brush = new();
    private SKPoint? _cloneSource;
    private SKPointI? _cloneOffset;

    public MainWindow()
    {
        Title = "Compositor";
        Width = 1280;
        Height = 820;
        Background = new SolidColorBrush(Color.FromRgb(0x18, 0x1A, 0x1E));
        _canvas.StrokeFinished = Painted;
        _canvas.MoveStarted = MoveStarted;
        _canvas.MoveChanged = MoveChanged;
        _canvas.MoveFinished = MoveFinished;
        _canvas.MarqueeFinished = (box, mode) => MarqueeFinished(box, mode, _tool == Tool.Ellipse);
        _canvas.LassoFinished = (points, mode) => LassoFinished(points, mode, _tool == Tool.Polygon);
        _canvas.WandClicked = WandClicked;
        _canvas.CloneSourceClicked = CloneSourceChosen;
        _canvas.EyedropperClicked = Picked;
        _paintOnMask.Click += (_, _) => SetPaintingMask(!_paintingMask);
        _eraseToggle.Click += (_, _) => SetErasing(!_erasing);
        _merge.Click += (_, _) => MergeLayers();
        _visibility.Click += (_, _) => ToggleVisibility();
        _clipping.Click += (_, _) => ToggleClipping();
        _maskToggle.Click += (_, _) => ToggleMask();
        _maskLink.Click += (_, _) => ToggleMaskLink();
        _addMask.Items.Add(Command("_Reveal All (White)", () => AddMask(revealing: true)));
        _addMask.Items.Add(Command("_Hide All (Black)", () => AddMask(revealing: false)));
        _layers.SelectionChanged += (_, _) => UpdateLayerMenu();
        Content = Layout();
        UpdateLayerMenu();
        Say("File ▸ Open project folder… to load a .comp");
    }

    private Control Layout()
    {
        var menu = new Menu
        {
            Items =
            {
                new MenuItem
                {
                    Header = "_File",
                    Items =
                    {
                        Command("_Open project folder…", OpenProject),
                        Command("_Save", Save, "Ctrl+S"),
                        Command("Save _As…", SaveAs),
                        new Separator(),
                        Command("_Export PNG…", ExportPng),
                        new Separator(),
                        Command("E_xit", Close),
                    },
                },
                new MenuItem
                {
                    Header = "_Edit",
                    Items =
                    {
                        Command("_Undo", Undo, "Ctrl+Z"),
                        Command("_Redo", Redo, "Ctrl+Shift+Z"),
                        new Separator(),
                        Command("Flip Layer _Horizontal", () => Flip(horizontal: true, canvas: false)),
                        Command("Flip Layer _Vertical", () => Flip(horizontal: false, canvas: false)),
                        Command("Flip _Canvas Horizontal", () => Flip(horizontal: true, canvas: true)),
                        Command("Flip Canvas _Vertical", () => Flip(horizontal: false, canvas: true)),
                    },
                },
                new MenuItem
                {
                    Header = "_Layer",
                    Items =
                    {
                        LayerCommand("_Duplicate Layer", DuplicateLayer, new KeyGesture(Key.J, KeyModifiers.Control)),
                        LayerCommand("_Rename Layer…", () => _ = RenameLayer(), new KeyGesture(Key.F2)),
                        LayerCommand("_Delete Layer", DeleteLayer, new KeyGesture(Key.Delete)),
                        new Separator(),
                        LayerCommand("Move Layer _Up", () => MoveLayer(1), new KeyGesture(Key.OemCloseBrackets, KeyModifiers.Control)),
                        LayerCommand("Move Layer _Down", () => MoveLayer(-1), new KeyGesture(Key.OemOpenBrackets, KeyModifiers.Control)),
                        new Separator(),
                        _clipping,
                        LayerCommand("_Group Selected Layers", GroupSelected,
                            new KeyGesture(Key.G, KeyModifiers.Control),
                            (document, layer) => document.Layers.Count < LayerPlacement.MaxLayers),
                        LayerCommand("Move _Out of Folder", MoveOutOfFolder, null,
                            (_, layer) => layer.ParentID is not null),
                        _merge,
                        new Separator(),
                        _addMask,
                        _maskToggle,
                        LayerCommand("_Delete Mask", DeleteMask, null, (_, layer) => layer.Mask is not null),
                        _maskLink,
                        new Separator(),
                        LayerCommand("New Blank Layer", NewBlankLayer,
                            new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift)),
                        LayerCommand("New F_older", NewFolder, null,
                            (document, _) => document.Layers.Count < LayerPlacement.MaxLayers),
                        new Separator(),
                        _visibility,
                    },
                },
                new MenuItem
                {
                    Header = "_Tools",
                    Items =
                    {
                        ToolItem("_Pan (drag to scroll)", Tool.Pan),
                        ToolItem("_Move (drag the layer)", Tool.Move),
                        ToolItem("Marquee (_rectangular selection)", Tool.Marquee),
                        ToolItem("_Elliptical marquee", Tool.Ellipse),
                        ToolItem("_Lasso (freehand)", Tool.Lasso),
                        ToolItem("_Polygonal lasso (click each corner)", Tool.Polygon),
                        ToolItem("Magic _wand (click a colour)", Tool.Wand),
                        ToolItem("_Brush", Tool.Brush),
                        ToolItem("_Clone stamp (Alt-click a source first)", Tool.Clone),
                        ToolItem("Blur brush", Tool.Blur),
                        ToolItem("Spot _healing", Tool.Heal),
                        ToolItem("_Eyedropper (click the canvas)", Tool.Eyedropper),
                        new Separator(),
                        _paintOnMask,
                        _eraseToggle,
                        new Separator(),
                        new MenuItem
                        {
                            Header = "_Brush settings",
                            Items =
                            {
                                Command("_Size…", () => _ = SetBrush(BrushSetting.Size)),
                                Command("_Hardness…", () => _ = SetBrush(BrushSetting.Hardness)),
                                Command("_Opacity…", () => _ = SetBrush(BrushSetting.Opacity)),
                                Command("_Colour…", () => _ = SetBrush(BrushSetting.Colour)),
                                new Separator(),
                                Command("Spot healing: _Content-Aware", () => Heal(HealingMode.ContentAware)),
                                Command("Spot healing: Create _Texture", () => Heal(HealingMode.CreateTexture)),
                                Command("Spot healing: Proximity _Match", () => Heal(HealingMode.ProximityMatch)),
                            },
                        },
                    },
                },
                new MenuItem
                {
                    Header = "_Select",
                    Items =
                    {
                        Command("Select _All", () => Change("Select All", SelectionEdits.SelectAll)),
                        Command("_Deselect", Deselect),
                        Command("_Inverse", () => Change("Inverse", SelectionEdits.Invert)),
                        new Separator(),
                        Command("_Expand…", () => _ = ModifySelection(SelectionAmount.Expand)),
                        Command("_Contract…", () => _ = ModifySelection(SelectionAmount.Contract)),
                        Command("_Feather…", () => _ = ModifySelection(SelectionAmount.Feather)),
                    },
                },
                new MenuItem
                {
                    Header = "_View",
                    Items =
                    {
                        Command("Zoom _in", () => { _canvas.ZoomBy(1.25); Say(); }),
                        Command("Zoom _out", () => { _canvas.ZoomBy(1 / 1.25); Say(); }),
                        Command("_Fit on screen", () => { _canvas.Fit(); Say(); }),
                        Command("Actual _pixels", () => { _canvas.ActualSize(); Say(); }),
                    },
                },
            },
        };

        var layers = new DockPanel();
        layers.Children.Add(new TextBlock
        {
            Text = "Layers",
            Margin = new Thickness(10, 8, 10, 6),
            Foreground = Ink,
            FontWeight = FontWeight.SemiBold,
        });
        DockPanel.SetDock(layers.Children[0], Dock.Top);
        layers.Children.Add(new ScrollViewer { Content = _layers });
        var side = new Border
        {
            Width = 280,
            Background = Panel,
            Child = layers,
        };

        var statusBar = new Border { Height = 28, Background = Panel, Child = _status };

        var root = new DockPanel();
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(side, Dock.Right);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(side);
        root.Children.Add(statusBar);
        root.Children.Add(_canvas);
        return root;
    }

    private static MenuItem Command(string header, Action action, string? gesture = null)
    {
        var item = new MenuItem { Header = header };
        if (gesture is not null) item.HotKey = KeyGesture.Parse(gesture);
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A row of the Layer menu, remembered so it can be greyed out with the others.</summary>
    private MenuItem LayerCommand(string header, Action action, KeyGesture gesture)
    {
        var item = Command(header, action);
        item.HotKey = gesture;
        _layerItems.Add(item);
        return item;
    }

    /// <summary>
    /// A row that needs more than a selection to be available: it takes its own test, which is asked
    /// whenever the panel changes.
    /// </summary>
    private MenuItem LayerCommand(string header, Action action, KeyGesture? gesture,
        Func<CanvasDocument, ImageLayer, bool> ready)
    {
        var item = Command(header, action);
        if (gesture is not null) item.HotKey = gesture;
        _layerRows.Add((item, ready));
        return item;
    }

    private async void OpenProject()
    {
        try
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Open a Compositor project folder",
                AllowMultiple = false,
            });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
            Open(path);
        }
        catch (Exception error)
        {
            Say($"Could not open: {error.Message}");
        }
    }

    private void Open(string path)
    {
        var snapshot = ProjectStore.Load(path);
        _document?.Dispose();
        // ToDocument takes the pixel references, so the document owns them from here on.
        _document = snapshot.ToDocument();
        _canvas.Document = _document;
        _projectPath = path;
        // A fresh document starts with clean history, as reopening a file does.
        _history.Reset();
        ShowLayers(_document);
        Say($"{Path.GetFileName(path)} — {_document.Width} by {_document.Height}, " +
            $"{_document.Layers.Count} layers, {_document.Resolution:0} pixels per inch");
    }

    /// <summary>The layer the panel has selected, or the top one when nothing is.</summary>
    private Guid? Selected =>
        _layers.SelectedIndex >= 0 && _layers.SelectedIndex < _rows.Count ? _rows[_layers.SelectedIndex]
        : _rows.Count > 0 ? _rows[^1]
        : null;

    /// <summary>One edit, wrapped in the history so it undoes in a single step.</summary>
    private void Edit(string name, Func<bool> change)
    {
        if (_document is not { } document) return;
        _history.Begin(name, document, Selected);
        change();
        _history.End(document, Selected);
        Refresh();
    }

    private void Undo()
    {
        if (_document is not { } document || _history.Undo() is not { } snapshot || snapshot.Document is null) return;
        document.Adopt(snapshot.Document);
        ShowLayers(document);
        Refresh();
    }

    private void Redo()
    {
        if (_document is not { } document || _history.Redo() is not { } snapshot || snapshot.Document is null) return;
        document.Adopt(snapshot.Document);
        ShowLayers(document);
        Refresh();
    }

    /// <summary>
    /// The Layer menu follows the panel: the merge row is named for what ⌘E would do, the clipping and mask
    /// rows for what they would change, and a row is off when it would do nothing.
    /// </summary>
    private void UpdateLayerMenu()
    {
        var document = _document;
        var layer = document is not null && Selected is { } id
            ? document.Layers.FirstOrDefault(candidate => candidate.ID == id)
            : null;
        foreach (var item in _layerItems) item.IsEnabled = layer is not null;
        foreach (var (item, ready) in _layerRows)
        {
            item.IsEnabled = document is not null && layer is not null && ready(document, layer);
        }

        var plan = document is not null && layer is not null ? LayerMerge.Plan(document, [layer.ID], layer.ID) : null;
        _merge.Header = "_" + (plan?.Action ?? "Merge Down");
        _merge.IsEnabled = plan is not null;
        _visibility.Header = layer?.IsVisible == false ? "_Show Layer" : "_Hide Layer";
        _visibility.IsEnabled = layer is not null;
        _clipping.Header = layer?.MaskSourceID is not null ? "Release _Clipping Mask" : "Create _Clipping Mask";
        _clipping.IsEnabled = document is not null && layer is not null && LayerMaskEdits.CanToggle(document, layer.ID);
        _addMask.IsEnabled = layer is { Mask: null };
        _maskToggle.Header = layer?.Mask?.IsEnabled == false ? "_Enable Mask" : "_Disable Mask";
        _maskToggle.IsEnabled = layer?.Mask is not null;
        _maskLink.Header = layer?.Mask?.IsLinked == false ? "Li_nk Mask" : "Un_ink Mask";
        _maskLink.IsEnabled = layer is { IsGroup: false, Mask: not null };
    }

    /// <summary>Rebuilds the panel and puts the selection back on a given layer.</summary>
    private void Reselect(Guid? layer)
    {
        if (_document is not { } document) return;
        ShowLayers(document);
        var row = layer is { } id ? _rows.IndexOf(id) : -1;
        if (row >= 0) _layers.SelectedIndex = row;
        Refresh();
    }

    /// <summary>Inserts a copy of the selected layer just above it, and selects the copy.</summary>
    private void DuplicateLayer()
    {
        if (_document is not { } document || Selected is not { } id) return;
        _history.Begin("Duplicate Layer", document, id);
        var copy = LayerEdits.Duplicate(document, id);
        _history.End(document, id);
        if (copy is null)
        {
            Say("That layer could not be copied: there is no room for another 10,000 layers.");
            return;
        }
        Reselect(copy);
    }

    /// <summary>
    /// Deletes the selected layer, and anything a folder holds. Refused when a layer that stays is clipped
    /// to it, because the Mac build asks whether to bake or unlink and this build cannot ask yet.
    /// </summary>
    private void DeleteLayer()
    {
        if (_document is not { } document || Selected is not { } id) return;
        // What to select afterwards: whatever takes its place in the stack, as the Mac build does.
        var index = document.Layers.FindIndex(layer => layer.ID == id);
        _history.Begin("Delete Layer", document, id);
        var deleted = LayerEdits.Delete(document, id);
        _history.End(document, id);
        if (!deleted)
        {
            Say("That layer supplies a live mask; macOS offers to bake or unlink it and this build cannot yet");
            return;
        }
        var left = document.Layers;
        Reselect(left.Count == 0 ? null : left[Math.Clamp(index, 0, left.Count - 1)].ID);
    }

    private async Task RenameLayer()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) return;
        if (await TextPrompt.Ask(this, "Rename Layer", "Layer name", layer.Name) is not { } name) return;
        if (_document is not { } current) return;
        _history.Begin("Rename Layer", current, id);
        var renamed = LayerEdits.Rename(current, id, name);
        _history.End(current, id);
        if (!renamed)
        {
            Say("That name was blank, or was already the layer's name");
            return;
        }
        Reselect(id);
    }

    /// <summary>Moves the selected layer one place through the layers it shares a folder with.</summary>
    private void MoveLayer(int offset)
    {
        if (_document is not { } document || Selected is not { } id) return;
        Edit(offset > 0 ? "Move Layer Up" : "Move Layer Down", () => LayerEdits.MoveBy(document, id, offset));
        Reselect(id);
    }

    private void ToggleVisibility()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) return;
        var visible = !layer.IsVisible;
        Edit(visible ? "Show Layer" : "Hide Layer", () => LayerEdits.SetVisible(document, id, visible));
        Reselect(id);
    }

    /// <summary>Adds a layer with no pixels, which it gets on the first paint.</summary>
    private void NewBlankLayer()
    {
        if (_document is not { } document) return;
        _history.Begin("New Blank Layer", document, Selected);
        var made = LayerPlacement.AddBlank(document, Selected);
        _history.End(document, Selected);
        if (made is null) { Say("This document already holds as many layers as it may."); return; }
        Reselect(made);
    }

    private void NewFolder()
    {
        if (_document is not { } document) return;
        _history.Begin("New Folder", document, Selected);
        var made = LayerPlacement.AddFolder(document, Selected);
        _history.End(document, Selected);
        if (made is null) { Say("This document already holds as many layers as it may."); return; }
        Reselect(made);
    }

    /// <summary>Wraps the selected layer in a folder.</summary>
    private void GroupSelected()
    {
        if (_document is not { } document || Selected is not { } id) return;
        _history.Begin("Group Layers", document, id);
        var folder = LayerPlacement.GroupSelected(document, [id]);
        _history.End(document, id);
        if (folder is null) { Say("That layer could not be wrapped in a folder."); return; }
        Reselect(folder);
    }

    private void MoveOutOfFolder()
    {
        if (_document is not { } document || Selected is not { } id) return;
        Edit("Move Layer", () => LayerPlacement.MoveOutOfFolder(document, id));
        Reselect(id);
    }

    /// <summary>Clips the selected layer to the one beneath it, or releases the clip it holds.</summary>
    private void ToggleClipping()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) return;
        Edit(layer.MaskSourceID is not null ? "Release Clipping Mask" : "Create Clipping Mask",
            () => LayerMaskEdits.Toggle(document, id));
        Reselect(id);
    }

    private void AddMask(bool revealing)
    {
        if (_document is not { } document || Selected is not { } id) return;
        Edit(revealing ? "Add Reveal-All Mask" : "Add Hide-All Mask", () => LayerMaskEdits.Add(document, id, revealing));
        Reselect(id);
    }

    private void ToggleMask()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id)?.Mask is not { } mask) return;
        var enabled = !mask.IsEnabled;
        Edit(enabled ? "Enable Layer Mask" : "Disable Layer Mask", () => LayerMaskEdits.SetEnabled(document, id, enabled));
        Reselect(id);
    }

    private void DeleteMask()
    {
        if (_document is not { } document || Selected is not { } id) return;
        Edit("Delete Layer Mask", () => LayerMaskEdits.Remove(document, id));
        Reselect(id);
    }

    private void ToggleMaskLink()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id)?.Mask is not { } mask) return;
        var linked = !mask.IsLinked;
        Edit(linked ? "Link Layer Mask" : "Unlink Layer Mask", () => LayerMaskEdits.SetLinked(document, id, linked));
        Reselect(id);
    }

    /// <summary>
    /// ⌘E: the selected layer and what it merges with become one layer, composited the way the canvas shows
    /// them, as one undo step. The result is left selected, so pressing it again carries on down the stack.
    /// </summary>
    private void MergeLayers()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (LayerMerge.Plan(document, [id], id) is not { } plan) return;
        _history.Begin(plan.Action, document, id);
        var made = LayerMerge.Merge(document, [id], id);
        _history.End(document, id);
        if (made is not { } merged)
        {
            Say("Nothing to merge");
            return;
        }
        Reselect(merged);
    }

    private void Flip(bool horizontal, bool canvas)    {
        if (_document is not { } document || Selected is not { } id) return;
        Edit(canvas ? $"Flip Canvas {(horizontal ? "Horizontal" : "Vertical")}" : $"Flip {(horizontal ? "Horizontal" : "Vertical")}",
            () =>
            {
                if (canvas)
                {
                    LayerEdits.FlipCanvas(document, horizontal);
                    return true;
                }
                return LayerEdits.Flip(document, [id], horizontal);
            });
    }

    private MenuItem ToolItem(string header, Tool tool)
    {
        var item = new MenuItem
        {
            Header = header,
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = tool == Tool.Pan,
        };
        item.Click += (_, _) => SetTool(tool);
        _toolItems[tool] = item;
        return item;
    }

    private void SetTool(Tool tool)
    {
        _tool = tool;
        _canvas.SampleSourceOnClick = tool == Tool.Clone;
        _canvas.EyedropperOnClick = tool == Tool.Eyedropper;
        _canvas.PaintEnabled = tool is Tool.Brush or Tool.Clone or Tool.Blur or Tool.Heal;
        PushBrush();
        _canvas.MoveEnabled = tool == Tool.Move;
        _canvas.Selection = tool switch
        {
            Tool.Marquee => SelectionTool.Rectangle,
            Tool.Ellipse => SelectionTool.Ellipse,
            Tool.Lasso => SelectionTool.Lasso,
            Tool.Polygon => SelectionTool.Polygon,
            Tool.Wand => SelectionTool.Wand,
            _ => SelectionTool.None,
        };
        // An outline that is half drawn is let go when the tool changes, rather than left hanging.
        _canvas.CancelDraft();
        foreach (var (which, item) in _toolItems) item.IsChecked = which == tool;
        Say(tool switch
        {
            Tool.Brush => $"Brush: {_brush.Diameter:0} pixels, {Spell(_brush)} — drag on the canvas",
            Tool.Clone => _cloneSource is null
                ? "Clone stamp — Alt-click where it should copy from first"
                : $"Clone stamp copying from {_cloneSource.Value.X:0},{_cloneSource.Value.Y:0} — drag on the canvas",
            Tool.Blur => $"Blur brush: {_brush.Diameter:0} pixels — drag over what should soften",
            Tool.Heal => $"Spot healing ({_brush.Healing}): {_brush.Diameter:0} pixels — drag over what should go",
            Tool.Eyedropper => "Eyedropper — click the canvas to take its colour",
            Tool.Move => "Move — drag the selected layer",
            Tool.Marquee => "Marquee — drag a rectangle; Shift adds, Alt subtracts",
            Tool.Ellipse => "Elliptical marquee — drag an oval; Shift adds, Alt subtracts",
            Tool.Lasso => "Lasso — drag round a shape; Shift adds, Alt subtracts",
            Tool.Polygon => "Polygonal lasso — click each corner, double-click to close",
            Tool.Wand => "Magic wand — click a colour to take everything like it",
            _ => "Pan — drag to scroll",
        });
    }

    /// <summary>What the brush is set to, in words, for the status line.</summary>
    private static string Spell(BrushSettings brush) =>
        (brush.Hardness >= 1 ? "hard" : $"{brush.Hardness * 100:0}% hard") +
        (brush.Opacity < 1 ? $", {brush.Opacity * 100:0}%" : "") +
        $", colour {brush.Red * 255:0},{brush.Green * 255:0},{brush.Blue * 255:0}";

    /// <summary>Puts the current brush, with the mode the tool in hand calls for, on the canvas.</summary>
    private void PushBrush() =>
        _canvas.Brush = _brush with
        {
            Erasing = _erasing,
            Mode = _tool switch
            {
                Tool.Clone => BrushMode.Clone,
                Tool.Blur => BrushMode.Blur,
                Tool.Heal => BrushMode.Heal,
                _ => BrushMode.Paint,
            },
        };

    /// <summary>Where a brush stroke goes: the layer's pixels, or its mask.</summary>
    private void SetPaintingMask(bool mask)
    {
        _paintingMask = mask;
        _paintOnMask.IsChecked = mask;
        Say(mask
            ? "The brush paints on the layer's mask — white reveals, and Erase paints black"
            : "The brush paints on the layer's pixels");
    }

    /// <summary>The brush erases rather than paints: on a mask, that is black rather than white.</summary>
    private void SetErasing(bool erasing)
    {
        _erasing = erasing;
        _eraseToggle.IsChecked = erasing;
        PushBrush();
        Say(erasing ? "The brush erases" : "The brush paints");
    }

    /// <summary>The eyedropper: the colour under the click becomes the brush's.</summary>
    private void Picked(SKPoint point)
    {
        if (_document is not { } document) return;
        if ((long)document.Width * document.Height > DocumentLimits.MaxSurfacePixels)
        {
            Say("This canvas is too big to read a colour from in one piece");
            return;
        }
        var x = (int)Math.Floor(point.X);
        var y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= document.Width || y >= document.Height) return;
        using var rendered = DocumentRenderer.Render(document);
        var colour = rendered.GetPixel(x, y);
        if (colour.Alpha == 0)
        {
            Say("Nothing is drawn there");
            return;
        }
        // A transparent pixel has no colour to take; a part-transparent one is read as it looks on white.
        _brush = _brush with
        {
            Red = colour.Red / 255.0,
            Green = colour.Green / 255.0,
            Blue = colour.Blue / 255.0,
        };
        PushBrush();
        Say($"Brush colour {colour.Red},{colour.Green},{colour.Blue}");
    }

    /// <summary>How Spot Healing works out what to put in the painted area.</summary>
    private void Heal(HealingMode mode)
    {
        _brush = _brush with { Healing = mode };
        PushBrush();
        Say($"Spot healing: {mode}");
    }

    /// <summary>Asks for one of the brush's settings and takes it, as an options bar would.</summary>
    private async Task SetBrush(BrushSetting which)
    {
        switch (which)
        {
            case BrushSetting.Size:
                if (await Ask("Brush size", "Diameter in pixels, 1 to 2000",
                        $"{_brush.Diameter:0}", 1, 2000) is { } size)
                {
                    _brush = _brush with { Diameter = size };
                }
                break;
            case BrushSetting.Hardness:
                if (await Ask("Brush hardness", "Percent, 0 for a soft tip and 100 for a hard one",
                        $"{_brush.Hardness * 100:0}", 0, 100) is { } hardness)
                {
                    _brush = _brush with { Hardness = hardness / 100.0 };
                }
                break;
            case BrushSetting.Opacity:
                if (await Ask("Brush opacity", "Percent, 1 to 100", $"{_brush.Opacity * 100:0}", 1, 100) is { } opacity)
                {
                    _brush = _brush with { Opacity = opacity / 100.0 };
                }
                break;
            default:
                if (await TextPrompt.Ask(this, "Brush colour", "Red, green and blue, 0 to 255",
                        $"{_brush.Red * 255:0},{_brush.Green * 255:0},{_brush.Blue * 255:0}") is not { } typed)
                {
                    return;
                }
                if (Colour(typed) is not { } colour)
                {
                    Say("The colour has to be three numbers from 0 to 255, as in 255,0,0");
                    return;
                }
                _brush = _brush with { Red = colour.Red, Green = colour.Green, Blue = colour.Blue };
                break;
        }
        PushBrush();
        Say($"Brush: {_brush.Diameter:0} pixels, {Spell(_brush)}");
    }

    private async Task<double?> Ask(string title, string label, string initial, double least, double most)
    {
        if (await TextPrompt.Ask(this, title, label, initial) is not { } typed) return null;
        if (!double.TryParse(typed.Trim(), out var value) || value < least || value > most)
        {
            Say($"That has to be a number from {least:0} to {most:0}");
            return null;
        }
        return value;
    }

    /// <summary>A colour typed as three numbers from 0 to 255.</summary>
    private static (double Red, double Green, double Blue)? Colour(string typed)
    {
        var parts = typed.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) return null;
        var values = new double[3];
        for (var index = 0; index < 3; index++)
        {
            if (!double.TryParse(parts[index], out values[index]) || values[index] < 0 || values[index] > 255) return null;
            values[index] /= 255;
        }
        return (values[0], values[1], values[2]);
    }

    /// <summary>One of the brush settings the options bar would show.</summary>
    private enum BrushSetting
    {
        Size,
        Hardness,
        Opacity,
        Colour,
    }

    /// <summary>One change to the selection, as one undo step.</summary>
    private void Change(string name, Func<CanvasDocument, bool> change)
    {
        if (_document is not { } document) return;
        Edit(name, () => change(document));
    }

    /// <summary>Lets the selection go, and any half-drawn outline with it.</summary>
    private void Deselect()
    {
        _canvas.CancelDraft();
        Change("Deselect", SelectionEdits.Deselect);
    }

    /// <summary>
    /// A marquee drag: the box becomes the selection, or is added to it or taken out of it, as the
    /// modifiers asked.
    /// </summary>
    private void MarqueeFinished(SKRectI box, SelectionMode mode, bool ellipse) =>
        Change(ellipse ? "Elliptical Marquee" : "Rectangular Marquee", document => mode == SelectionMode.Replace
            ? ellipse ? SelectionEdits.SelectEllipse(document, box) : SelectionEdits.Select(document, box)
            : SelectionEdits.Apply(document, SelectionEdits.Shape(box, ellipse), mode));

    /// <summary>A lasso or polygonal lasso drag: the outline through the points it gathered.</summary>
    private void LassoFinished(IReadOnlyList<SKPoint> points, SelectionMode mode, bool polygonal) =>
        Change(polygonal ? "Polygonal Lasso" : "Lasso", document => mode == SelectionMode.Replace
            ? SelectionEdits.SelectLasso(document, points)
            : SelectionEdits.Apply(document, SelectionEdits.Lasso(points), mode));

    /// <summary>A click of the wand: everything like the pixel under it, read from the canvas as shown.</summary>
    private void WandClicked(SKPoint point, SelectionMode mode) =>
        Change("Magic Wand", document =>
        {
            using var sample = SelectionEdits.Sample(document, null);
            return sample is not null && SelectionEdits.SelectWand(document, sample,
                (int)Math.Floor(point.X), (int)Math.Floor(point.Y), new WandOptions(), mode);
        });

    /// <summary>Asks for an amount and modifies the selection by it, as Select ▸ Modify does.</summary>
    private async Task ModifySelection(SelectionAmount which)
    {
        if (_document is not { } document || document.Selection.Path is null)
        {
            Say("Select something first");
            return;
        }
        var most = which == SelectionAmount.Feather ? SelectionEdits.MaxFeather : SelectionEdits.MaxAmount;
        var label = which == SelectionAmount.Feather ? "Feather radius in pixels" : "Pixels";
        if (await TextPrompt.Ask(this, $"Modify Selection — {which}", label, "4") is not { } typed) return;
        if (!int.TryParse(typed.Trim(), out var amount) || amount < 1 || amount > most)
        {
            Say($"The amount has to be a whole number from 1 to {most}");
            return;
        }
        if (_document is not { } current) return;
        Edit($"{which} Selection", () => which switch
        {
            SelectionAmount.Expand => SelectionEdits.Expand(current, amount),
            SelectionAmount.Contract => SelectionEdits.Contract(current, amount),
            _ => SelectionEdits.Feather(current, amount),
        });
    }

    private enum SelectionAmount
    {
        Expand,
        Contract,
        Feather,
    }

    /// <summary>Paints a finished stroke into the selected layer, as one undo step.</summary>
    private void Painted(IReadOnlyList<SKPoint> stroke)
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (BrushFor(stroke) is not { } settings) return;
        var name = _tool switch
        {
            Tool.Clone => "Clone Stamp",
            Tool.Blur => "Blur",
            Tool.Heal => "Spot Healing",
            _ => "Brush",
        };
        Edit(_paintingMask ? $"{name} on the mask" : name, () =>
        {
            if (_paintingMask)
            {
                // White reveals and black hides; the brush's Erase is what paints black, as the Mac build's
                // paint-white switch does.
                var value = _erasing ? 0 : 1;
                return BrushEdits.PaintMask(document, id,
                    stroke, settings with { Red = value, Green = value, Blue = value, Erasing = false });
            }
            // A blank layer gets its pixels on the first paint, as the Mac build does.
            BrushEdits.EnsurePixels(document, id);
            return BrushEdits.Paint(document, id, stroke, settings);
        });
    }

    /// <summary>
    /// The brush a stroke should be painted with. A Clone Stamp stroke needs a source, and its offset is
    /// fixed by the stroke that follows the click: later strokes keep it, so the source travels with the
    /// brush as the Mac build's alignment does.
    /// </summary>
    private BrushSettings? BrushFor(IReadOnlyList<SKPoint> stroke)
    {
        if (_tool != Tool.Clone) return _canvas.Brush;
        if (_cloneSource is not { } source)
        {
            Say("Alt-click where the Clone Stamp should copy from first");
            return null;
        }
        _cloneOffset ??= new SKPointI(
            (int)Math.Round(source.X - stroke[0].X), (int)Math.Round(source.Y - stroke[0].Y));
        return _canvas.Brush with { CloneFrom = _cloneOffset };
    }

    /// <summary>Alt-clicking with the Clone Stamp: where the next stroke copies from.</summary>
    private void CloneSourceChosen(SKPoint point)
    {
        _cloneSource = point;
        // A new source starts a new alignment, as the Mac build's does.
        _cloneOffset = null;
        Say($"Clone stamp copying from {point.X:0},{point.Y:0} — drag on the canvas");
    }

    /// <summary>
    /// A drag of the move tool. The edit is opened when the pointer goes down and closed when it comes up,
    /// so the whole drag is a single step in the history rather than one per mouse move.
    /// </summary>
    private void MoveStarted()
    {
        if (_document is not { } document || Selected is not { } id) return;
        _moving = id;
        _history.Begin("Move", document, id);
    }

    private void MoveChanged(SKPoint delta)
    {
        if (_document is not { } document || _moving is not { } id) return;
        LayerEdits.Move(document, id, delta.X, delta.Y);
        Refresh();
    }

    private void MoveFinished()
    {
        if (_document is not { } document || _moving is not { } id) return;
        _moving = null;
        _history.End(document, id);
        Refresh();
    }

    /// <summary>Repaints the canvas and says where the history stands.</summary>
    private void Refresh()
    {
        _canvas.InvalidateVisual();
        UpdateLayerMenu();
        var undo = _history.CanUndo ? $"Undo {_history.UndoName}" : "";
        var redo = _history.CanRedo ? $"Redo {_history.RedoName}" : "";
        var edited = _history.IsModified ? "edited" : "";
        Say(string.Join("    ", new[] { undo, redo, edited }.Where(part => part.Length > 0)));
    }

    private void ShowLayers(CanvasDocument document)
    {
        var rows = new List<ListBoxItem>();
        _rows.Clear();
        // Top of the stack first, as the Mac build's panel lists it.
        foreach (var entry in document.HierarchyEntries(topFirst: true))
        {
            var record = entry.Layer;
            var notes = new List<string>();
            if (!entry.Visible) notes.Add("hidden");
            if (record.BlendMode is { } blend && blend != LayerBlendMode.Normal) notes.Add(Spell(blend));
            if (record.Opacity is { } opacity and < 1) notes.Add($"{opacity:0.##}");
            if (record.MaskFile is not null) notes.Add("mask");
            if (record.MaskSourceID is not null) notes.Add("clipped");
            rows.Add(new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = new string(' ', entry.Depth * 3) + record.Name +
                        (notes.Count > 0 ? "  ·  " + string.Join(", ", notes) : ""),
                    Foreground = Ink,
                },
            });
            _rows.Add(record.ID);
        }
        var selected = _layers.SelectedIndex;
        _layers.ItemsSource = rows;
        // A row is the layer an edit acts on, so the top of the stack starts selected.
        _layers.SelectedIndex = selected >= 0 && selected < rows.Count ? selected : rows.Count > 0 ? 0 : -1;
    }

    private void Save()
    {
        if (_document is not { } document) return;
        if (_projectPath is null)
        {
            SaveAs();
            return;
        }
        WriteTo(document, _projectPath);
    }

    private async void SaveAs()
    {
        if (_document is not { } document) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save the project",
                SuggestedFileName = _projectPath is { } known ? Path.GetFileName(known) : "Untitled.comp",
                DefaultExtension = "comp",
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            // A project is a folder on Windows, so a path that is already a file cannot be written as one.
            if (File.Exists(path))
            {
                Say("A project is a folder, and that path is a file.");
                return;
            }
            WriteTo(document, path);
            _projectPath = path;
        }
        catch (Exception error)
        {
            Say($"Could not save: {error.Message}");
        }
    }

    private void WriteTo(CanvasDocument document, string path)
    {
        try
        {
            // The snapshot shares the document's pixels and only reads them, so it is not disposed here.
            ProjectStore.Save(ProjectSnapshot.FromDocument(document), path);
            _history.MarkSaved();
            Refresh();
            Say($"Saved {path}");
        }
        catch (Exception error)
        {
            Say($"Could not save: {error.Message}");
        }
    }

    private async void ExportPng()
    {
        if (_document is not { } document)
        {
            Say("Nothing to export yet.");
            return;
        }
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export PNG",
                SuggestedFileName = "Compositor export.png",
                DefaultExtension = "png",
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            // A band of tiles at a time, so the canvas size does not have to fit in one buffer.
            TiledPngWriter.Write(document, path);
            Say($"Exported {path}");
        }
        catch (Exception error)
        {
            Say($"Could not export: {error.Message}");
        }
    }

    private static string Spell<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonSerializer.Serialize(value, ManifestJson.Options).Trim('"');

    private string _message = "";

    /// <summary>Puts a message on the status line; an empty one just refreshes the zoom reading.</summary>
    private void Say(string message = "")
    {
        if (message.Length > 0) _message = message;
        var zoom = _document is null ? "" : $"{_canvas.Zoom * 100:0}%";
        var limit = _canvas.ZoomedOutAsFarAsItGoes ? "as far out as one screenful can be drawn" : "";
        _status.Text = string.Join("    ", new[] { _message, zoom, limit }.Where(part => part.Length > 0));
    }
}
