using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using Compositor.Core.Rendering;
using SkiaSharp;
using LayerTransform = Compositor.Core.Model.LayerTransform;
using SelectionMode = Compositor.Core.Document.SelectionMode;

namespace Compositor.Desktop;

/// <summary>
/// The editor window: the canvas, the layers panel and a status line. Projects are opened as folders,
/// because on Windows a `.comp` is an ordinary directory rather than the file-like package the Mac sees.
/// </summary>
public sealed class MainWindow : Window
{
    private static readonly IBrush Panel = new SolidColorBrush(Color.FromRgb(0x22, 0x24, 0x28));
    /// <summary>The tab in front, marked: a lighter panel than the strip it sits on.</summary>
    private static readonly IBrush Accent = new SolidColorBrush(Color.FromRgb(0x3A, 0x3E, 0x46));
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEB));

    private readonly CanvasView _canvas = new();
    /// <summary>The layers panel: several rows may be selected, and the current row is the one an edit acts on.</summary>
    private readonly ListBox _layers = new() { SelectionMode = Avalonia.Controls.SelectionMode.Multiple };
    private readonly TextBlock _status = new() { Margin = new Thickness(10, 3, 10, 3), Foreground = Ink };

    /// <summary>One row, because what ⌘E does depends on the panel selection: it is named for it here.</summary>
    private readonly MenuItem _merge = new() { HotKey = new KeyGesture(Key.E, KeyModifiers.Control) };

    /// <summary>The clipping, mask and visibility rows, whose names and availability follow the selection.</summary>
    private readonly MenuItem _visibility = new();
    private readonly MenuItem _showGrid = new();
    private readonly MenuItem _recentMenu = new() { Header = "Open _Recent" };
    private readonly RecentProjects _recent = new(RecentProjects.DefaultPath);
    private readonly MenuItem _snapToCanvas = new();
    private readonly MenuItem _snapToGuides = new();
    private readonly MenuItem _snapToLayers = new();
    private readonly MenuItem _snapToGrid = new();
    /// <summary>How thick the ruler strips are, in points.</summary>
    private const double RulerThickness = 18;

    private readonly RulerStrip _rulerAcross = new() { Axis = GuideAxis.Horizontal, Height = RulerThickness };
    private readonly RulerCorner _rulerCorner = new();
    private readonly RulerStrip _rulerDown = new() { Axis = GuideAxis.Vertical, Width = RulerThickness };
    private readonly MenuItem _showRulers = new();
    private bool _rulersVisible;
    private readonly MenuItem _showGuides = new();
    private readonly MenuItem _lockGuides = new();
    private readonly MenuItem _showTransform = new();
    private readonly MenuItem _pixelGrid = new();
    private readonly MenuItem _snapping = new();
    private bool _guidesVisible = true;
    private bool _guidesLocked;
    private bool _transformShown = true;
    private bool _pixelGridShown;
    private bool _snappingOn = true;

    private SnapTo _snapTo = SnapTo.All;
    private LayoutGrid _grid = new();
    private bool _gridVisible;
    /// <summary>The view's switches as they were left last time, which the View menu opens with.</summary>
    private readonly ToolDefaults _tools = ToolDefaults.Load(ToolDefaults.DefaultPath);
    private readonly ComboBox _blend = new();
    private readonly Slider _opacity = new() { Minimum = 0, Maximum = 100, Width = 130 };
    private readonly TextBlock _opacityReadout = new() { Width = 40, VerticalAlignment = VerticalAlignment.Center };
    private bool _showingAppearance;
    private bool _opacityDragging;
    private ClipboardImage? _clipboard;
    private FilterPreview? _preview;
    private DispatcherTimer? _previewTimer;
    private Func<CanvasDocument, bool>? _previewApply;
    /// <summary>The one layer a single-layer preview stands for, so a panel that thinks in layers can be shown.</summary>
    private Guid? _previewLayer;
    /// <summary>The box and the layers a distortion drag began with, so every step is measured from it.</summary>
    private LayerTransform? _distortBox;
    private List<Guid>? _distortLayers;

    /// <summary>The blend modes in the order the menu lists them, which is the order the enum declares.</summary>
    private static readonly LayerBlendMode[] BlendModes = Enum.GetValues<LayerBlendMode>();
    private readonly MenuItem _adjustmentMenu = new() { Header = "New _Adjustment Layer" };
    private readonly MenuItem _effectsMenu = new() { Header = "Layer _Effects" };
    private MenuItem _adjustmentSettings = new();
    private MenuItem _clearEffects = new();
    private readonly MenuItem _clipping = new() { HotKey = new KeyGesture(Key.G, KeyModifiers.Control | KeyModifiers.Alt) };
    private readonly MenuItem _addMask = new() { Header = "Add _Mask" };
    private readonly MenuItem _maskToggle = new();
    private readonly MenuItem _maskLink = new();

    /// <summary>The rest of the Layer menu, so all of it can go dead together when nothing is selected.</summary>
    private readonly List<MenuItem> _layerItems = [];

    /// <summary>
    /// The text being typed on the canvas, if any: it keeps the whole of the typing as one undo step, and
    /// letting it go puts the layer back the way it was.
    /// </summary>
    private TextSession? _text;

    /// <summary>How the Gradient tool paints: its shape, whether it fades to the background colour or to
    /// nothing, which way round, and the colour it fades to.</summary>
    private readonly MenuItem _gradientMenu = new() { Header = "Gradient _options" };

    private GradientShape _gradientShape = GradientShape.Linear;
    private bool _gradientToBackground;
    private bool _gradientReversed;
    private (double Red, double Green, double Blue) _gradientBackground = (1, 1, 1);

    /// <summary>Which shape the Shape tool draws.</summary>
    private readonly MenuItem _shapeKinds = new() { Header = "Shape _kind" };

    private ShapeKind _shapeKind = ShapeKind.Rectangle;

    /// <summary>A rectangle's corner radius, and a line's thickness, in document pixels.</summary>
    private double _shapeCornerRadius;
    private double _shapeLineWidth = 4;

    /// <summary>The crop frame's shape: the canvas's own, or one of the fixed ratios.</summary>
    private readonly MenuItem _cropRatios = new() { Header = "Crop _ratio" };

    /// <summary>The crop frame while the Crop tool is in hand; null is the whole canvas.</summary>
    private SKRectI? _cropFrame;

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

    /// <summary>
    /// One open project: everything that belongs to a document rather than to the window. There is always a
    /// tab, even before anything is open — the empty one is where the next project goes — so the five names
    /// below always have something to answer for.
    /// </summary>
    private sealed class Tab
    {
        /// <summary>Owns the pixels: the document's layers reference the snapshot's images, so the document
        /// disposes them and the snapshot is dropped rather than disposed.</summary>
        public CanvasDocument? Document { get; set; }

        public DocumentHistory History { get; } = new();

        /// <summary>Where this project was opened from, so Save writes back to it.</summary>
        public string? Path { get; set; }

        /// <summary>The project's folder watched for someone else writing it.</summary>
        public ProjectWatch? Watch { get; set; }

        /// <summary>The layer behind each row of the panel, so a selection can be turned back into an id.</summary>
        public List<Guid> Rows { get; } = [];

        /// <summary>Which row the panel had selected, so a tab comes back the way it was left.</summary>
        public int SelectedRow { get; set; }

        /// <summary>What the tab is called: the project's name, or what it is until it is saved.</summary>
        public string Name => Path is { } path ? System.IO.Path.GetFileName(path) : "Untitled";
    }

    private readonly List<Tab> _tabs = [];
    private Tab _open = new();
    private DispatcherTimer? _watchTimer;
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 4 };

    private CanvasDocument? _document
    {
        get => _open.Document;
        set => _open.Document = value;
    }

    private DocumentHistory _history => _open.History;

    private List<Guid> _rows => _open.Rows;

    private string? _projectPath
    {
        get => _open.Path;
        set => _open.Path = value;
    }

    private ProjectWatch? _watch
    {
        get => _open.Watch;
        set => _open.Watch = value;
    }

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
        Liquify,
        Smudge,
        Heal,
        Eyedropper,
        Type,
        Crop,
        Shape,
        Gradient,
    }

    private readonly Dictionary<Tool, MenuItem> _toolItems = [];
    private Tool _tool = Tool.Pan;

    /// <summary>The layer a transform drag is editing, while the pointer is down.</summary>
    private Guid? _transforming;

    /// <summary>What the box was when the drag began, and where every layer it moves was.</summary>
    private LayerTransform? _transformBox;
    private Dictionary<Guid, LayerTransform> _transformOriginals = [];

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
        _canvas.MarqueeFinished = (box, mode) => MarqueeFinished(box, mode, _tool == Tool.Ellipse);
        _canvas.LassoFinished = (points, mode) => LassoFinished(points, mode, _tool == Tool.Polygon);
        _canvas.WandClicked = WandClicked;
        _canvas.CloneSourceClicked = CloneSourceChosen;
        _canvas.EyedropperClicked = Picked;
        _canvas.ShapeFinished = ShapeFinished;
        _canvas.GradientFinished = GradientFinished;
        _canvas.GradientStarted = GradientStarted;
        _canvas.GradientChanged = GradientChanged;
        _canvas.DistortStarted = DistortStarted;
        _canvas.DistortChanged = DistortChanged;
        _canvas.DistortFinished = DistortFinished;
        _canvas.GuideDragStarted = GuideDragStarted;
        _canvas.GuideMoved = GuideMoved;
        _canvas.GuideDragFinished = GuideDragFinished;
        _canvas.CropChanged = CropChanged;
        _canvas.CropCommitted = ApplyCrop;
        BuildCropRatios();
        BuildShapeKinds();
        BuildGradientMenu();
        BuildAdjustmentMenu();
        RefreshRecent();
        _canvas.TextClicked = TypeHere;
        _canvas.TextTyped = TypedText;
        _canvas.TextBackspaced = BackspacedText;
        _canvas.TextDeleted = DeletedText;
        _canvas.TextMoved = MovedTextCaret;
        _canvas.TextCommitted = CommitText;
        _canvas.TextCancelled = CancelText;
        _canvas.TransformStarted = TransformStarted;
        _canvas.TransformChanged = TransformChanged;
        _canvas.TransformFinished = TransformFinished;
        _paintOnMask.Click += (_, _) => SetPaintingMask(!_paintingMask);
        _eraseToggle.Click += (_, _) => SetErasing(!_erasing);
        _merge.Click += (_, _) => MergeLayers();
        _visibility.Click += (_, _) => ToggleVisibility();
        // The View switches open where they were left last time, as the Mac build's tool defaults keep them.
        _snapTo = _tools.SnapTo;
        _grid = _tools.Grid();
        _gridVisible = _tools.ShowGrid;
        _canvas.Grid = _gridVisible ? _grid : null;
        _showGrid.Header = _gridVisible ? "_Hide Grid" : "Show _Grid";
        _showGrid.Click += (_, _) => ShowGrid();
        _rulersVisible = _tools.ShowRulers;
        _showRulers.Header = "R_ulers";
        _showRulers.ToggleType = MenuItemToggleType.CheckBox;
        _showRulers.IsChecked = _rulersVisible;
        _showRulers.Click += (_, _) => ShowRulers();
        _guidesVisible = _tools.ShowGuides;
        _guidesLocked = _tools.LockGuides;
        _transformShown = _tools.ShowTransformControls;
        _pixelGridShown = _tools.PixelGrid;
        _snappingOn = _tools.Snapping;
        Toggle("_Guides", _showGuides, _guidesVisible, () => ShowGuides());
        Toggle("_Lock Guides", _lockGuides, _guidesLocked, () => LockGuides());
        Toggle("Show _Transform Controls", _showTransform, _transformShown, () => ShowTransformControls());
        Toggle("_Pixel Grid (800% and above)", _pixelGrid, _pixelGridShown, () => ShowPixelGrid());
        Toggle("S_nap", _snapping, _snappingOn, () => ShowSnapping());
        PushViewSwitches();
        _canvas.ViewportChanged = UpdateRulers;
        foreach (var (item, flag, label) in SnapRows())
        {
            // A tick box, so the four read as switches rather than as commands. They open where they were left,
            // as the Mac build's tool defaults do.
            item.Header = label;
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = _tools.SnapTo.HasFlag(flag);
            item.Click += (_, _) => ToggleSnapTo(flag, label);
        }
        _clipping.Click += (_, _) => ToggleClipping();
        _maskToggle.Click += (_, _) => ToggleMask();
        _maskLink.Click += (_, _) => ToggleMaskLink();
        _addMask.Items.Add(Command("_Reveal All (White)", () => AddMask(revealing: true)));
        _addMask.Items.Add(Command("_Hide All (Black)", () => AddMask(revealing: false)));
        _layers.SelectionChanged += (_, _) => UpdateLayerMenu();
        _tabs.Add(_open);
        Content = Layout();
        RefreshTabs();
        UpdateLayerMenu();
        Say("File ▸ New Project… for a blank canvas, or File ▸ Open project folder… to load a .comp");
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
                        Command("_New Project…", () => _ = NewProject(), "Ctrl+N"),
                        Command("_Open project folder…", OpenProject),
                        _recentMenu,
                        Command("_Import image…", () => _ = ImportImage()),
                        Command("_Save", Save, "Ctrl+S"),
                        Command("Save _As…", SaveAs),
                        new Separator(),
                        Command("_Export PNG…", ExportPng),
                        Command("Export _JPEG…", () => _ = ExportJpeg()),
                        new Separator(),
                        Command("_Close Tab", () => _ = CloseTab(_open), "Ctrl+W"),
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
                        Command("Cu_t", Cut, "Ctrl+X"),
                        Command("_Copy", Copy, "Ctrl+C"),
                        Command("Copy _Merged", CopyMerged, "Ctrl+Shift+C"),
                        Command("_Paste", Paste, "Ctrl+V"),
                        Command("Layer via Cop_y", LayerViaCopy, "Ctrl+J"),
                        new Separator(),
                        Command("Fill with _Foreground Colour", () => FillPixels(BrushColour(), "Fill"), "Alt+Delete"),
                        Command("Fill with _Background Colour", () => FillPixels(BackgroundColour(), "Fill"), "Ctrl+Delete"),
                        Command("_Clear Selection Pixels", ClearPixels),
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
                        LayerCommand("Edit _Text…", () => _ = EditText(), null, (_, layer) => layer.Text is not null),
                        _maskLink,
                        new Separator(),
                        LayerCommand("New Blank Layer", NewBlankLayer,
                            new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift)),
                        LayerCommand("New F_older", NewFolder, null,
                            (document, _) => document.Layers.Count < LayerPlacement.MaxLayers),
                        _adjustmentMenu,
                        _adjustmentSettings,
                        _effectsMenu,
                        new Separator(),
                        _visibility,
                    },
                },
                new MenuItem
                {
                    Header = "_Image",
                    Items =
                    {
                        Command("_Hue/Saturation…", () => _ = ImageAdjustment(AdjustmentKind.HueSaturation)),
                        Command("_Levels…", () => _ = ImageAdjustment(AdjustmentKind.Levels)),
                        new MenuItem
                        {
                            Header = "_Auto Levels",
                            Items =
                            {
                                Command("Auto _Contrast", () => AutoLevels(LevelsAuto.Contrast)),
                                Command("Auto C_olour", () => AutoLevels(LevelsAuto.Color)),
                                Command("Auto Colour + Neutral _Midtones", () => AutoLevels(LevelsAuto.Neutral)),
                            },
                        },
                        Command("C_urves…", () => _ = ImageAdjustment(AdjustmentKind.Curves)),
                        Command("_Exposure…", () => _ = ImageAdjustment(AdjustmentKind.Exposure)),
                        Command("Black & _White…", () => _ = ImageAdjustment(AdjustmentKind.BlackWhite)),
                        Command("_Gradient Map…", () => _ = ImageAdjustment(AdjustmentKind.GradientMap)),
                        Command("C_olor Balance…", () => _ = ImageAdjustment(AdjustmentKind.ColorBalance)),
                        new Separator(),
                        Command("_Grain…", () => _ = ImageAdjustment(AdjustmentKind.Grain)),
                        Command("_Invert", () => _ = ImageAdjustment(AdjustmentKind.Invert)),
                        new Separator(),
                        Command("_Canvas Size…", () => _ = CanvasSize()),
                        Command("_Image Size…", () => _ = ImageSize()),
                        Command("_Trim…", () => _ = Trim()),
                    },
                },
                new MenuItem
                {
                    Header = "_Filter",
                    Items =
                    {
                        Command("_Camera Raw Filter…", () => _ = CameraRawFilter()),
                        new Separator(),
                        Command("_Gaussian Blur…", () => _ = ApplyFilter(FilterKind.GaussianBlur)),
                        Command("_Motion Blur…", () => _ = ApplyFilter(FilterKind.MotionBlur)),
                        Command("Add _Noise…", () => _ = ApplyFilter(FilterKind.AddNoise)),
                        Command("_Bloom / Glow…", () => _ = ApplyFilter(FilterKind.BloomGlow)),
                        Command("_Dither…", () => _ = DitherFilter()),
                        new Separator(),
                        Command("_Content-Aware Fill", ContentAwareFill),
                        new Separator(),
                        Command("_Vignette…", () => _ = ApplyFilter(FilterKind.Vignette)),
                        Command("_Tonal Contrast…", () => _ = ApplyFilter(FilterKind.TonalContrast)),
                        Command("Lens _Correction…", () => _ = ApplyFilter(FilterKind.LensCorrection)),
                    },
                },
                new MenuItem
                {
                    Header = "_Tools",
                    Items =
                    {
                        ToolItem("_Pan (drag to scroll)", Tool.Pan),
                        ToolItem("_Move (drag the layer; Ctrl-drag a corner to distort it)", Tool.Move),
                        ToolItem("Marquee (_rectangular selection)", Tool.Marquee),
                        ToolItem("_Elliptical marquee", Tool.Ellipse),
                        ToolItem("_Lasso (freehand)", Tool.Lasso),
                        ToolItem("_Polygonal lasso (click each corner)", Tool.Polygon),
                        ToolItem("Magic _wand (click a colour)", Tool.Wand),
                        ToolItem("_Brush", Tool.Brush),
                        ToolItem("_Clone stamp (Alt-click a source first)", Tool.Clone),
                        ToolItem("Blur brush", Tool.Blur),
                        ToolItem("_Liquify brush (push the pixels around)", Tool.Liquify),
                        ToolItem("S_mudge brush (drag the colour along)", Tool.Smudge),
                        ToolItem("Spot _healing", Tool.Heal),
                        ToolItem("_Eyedropper (click the canvas)", Tool.Eyedropper),
                        ToolItem("_Type (click where the text goes)", Tool.Type),
                        ToolItem("_Crop (drag a frame, then apply it)", Tool.Crop),
                        ToolItem("_Shape (drag out a rectangle, ellipse or line)", Tool.Shape),
                        ToolItem("_Gradient (drag the line it runs along)", Tool.Gradient),
                        new Separator(),
                        _gradientMenu,
                        new Separator(),
                        _shapeKinds,
                        new Separator(),
                        _cropRatios,
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
                        new Separator(),
                        Command("Layer's _Pixels", SelectLayerPixels),
                        Command("_Mask's Black Areas", SelectMaskBlack),
                        new Separator(),
                        Command("Colour _Range…", () => _ = ColorRange()),
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
                        new Separator(),
                        _showGrid,
                        _showRulers,
                        _showGuides,
                        _lockGuides,
                        _showTransform,
                        _pixelGrid,
                        _snapping,
                        Command("_Grid Settings…", () => _ = GridSettings()),
                        _snapToCanvas,
                        _snapToGuides,
                        _snapToLayers,
                        new Separator(),
                        Command("New _Guide…", () => _ = NewGuide(), "Ctrl+OemSemicolon"),
                        Command("_Clear Guides", ClearGuides),
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
        layers.Children.Add(Appearance());
        DockPanel.SetDock(layers.Children[1], Dock.Top);
        layers.Children.Add(new ScrollViewer { Content = _layers });
        var side = new Border
        {
            Width = 280,
            Background = Panel,
            Child = layers,
        };

        var statusBar = new Border { Height = 28, Background = Panel, Child = _status };

        var root = new DockPanel();
        var tabs = new Border
        {
            Background = Panel,
            Padding = new Thickness(8, 4, 8, 4),
            Child = _tabStrip,
        };
        DockPanel.SetDock(menu, Dock.Top);
        DockPanel.SetDock(tabs, Dock.Top);
        DockPanel.SetDock(side, Dock.Right);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(menu);
        root.Children.Add(tabs);
        root.Children.Add(side);
        root.Children.Add(statusBar);
        root.Children.Add(Views());
        return root;
    }

    /// <summary>
    /// The canvas with its rulers: a strip along the top and down the side, and the little square between them
    /// where the two meet. The strips only draw, so the canvas keeps every pointer position it worked out
    /// before — they are outside it rather than over it.
    /// </summary>
    private Control Views()
    {
        var lined = new Grid
        {
            RowDefinitions = new RowDefinitions($"{RulerThickness},*"),
            ColumnDefinitions = new ColumnDefinitions($"{RulerThickness},*"),
        };
        Grid.SetRow(_rulerCorner, 0);
        Grid.SetColumn(_rulerCorner, 0);
        Grid.SetRow(_rulerAcross, 0);
        Grid.SetColumn(_rulerAcross, 1);
        Grid.SetRow(_rulerDown, 1);
        Grid.SetColumn(_rulerDown, 0);
        Grid.SetRow(_canvas, 1);
        Grid.SetColumn(_canvas, 1);
        _rulerAcross.IsVisible = _rulersVisible;
        _rulerDown.IsVisible = _rulersVisible;
        _rulerCorner.IsVisible = _rulersVisible;
        lined.Children.Add(_rulerCorner);
        lined.Children.Add(_rulerAcross);
        lined.Children.Add(_rulerDown);
        lined.Children.Add(_canvas);
        return lined;
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
        // Into an empty tab when there is one and a tab of its own otherwise: an open project is not thrown
        // away by opening another, as the Mac build keeps one open per tab.
        _open = TabForNew();
        _document?.Dispose();
        // ToDocument takes the pixel references, so the document owns them from here on.
        _document = snapshot.ToDocument();
        _projectPath = path;
        // A fresh project starts with clean history, as opening a file does.
        _history.Reset();
        NoteRecent(path);
        Show(_open);
        Say($"{System.IO.Path.GetFileName(path)} — {_document.Width} by {_document.Height}, " +
            $"{_document.Layers.Count} layers, {_document.Resolution:0} pixels per inch");
    }

    /// <summary>
    /// The tab the next project goes into: the empty one when the tab in front holds nothing, and a new one
    /// otherwise. It is put in front, and the caller fills it in.
    /// </summary>
    private Tab TabForNew()
    {
        if (_open.Document is not null)
        {
            var made = new Tab();
            _tabs.Insert(_tabs.IndexOf(_open) + 1, made);
            _open = made;
        }
        return _open;
    }

    /// <summary>
    /// The tab brought in front: the canvas and the panel are given what it holds, and its folder is watched
    /// again. Whatever was half-done in the tab being left — a preview, a draft outline, a drag, a typing
    /// session — belongs to that tab, so it is let go rather than carried over.
    /// </summary>
    private void Bring(Tab tab)
    {
        if (!ReferenceEquals(tab, _open))
        {
            _open.SelectedRow = _layers.SelectedIndex;
            StopPreview();
            _canvas.CancelDraft();
            if (_text is not null) CancelText();
            _transforming = null;
            _transformBox = null;
            _transformOriginals.Clear();
            _cropFrame = null;
            _open = tab;
        }
        Show(tab);
    }

    /// <summary>The tab laid out: its document on the canvas, its layers in the panel, its folder watched.</summary>
    private void Show(Tab tab)
    {
        _canvas.Document = tab.Document;
        if (tab.Document is not null)
        {
            ShowLayers(tab.Document);
            _layers.SelectedIndex = tab.SelectedRow >= 0 && tab.SelectedRow < _rows.Count
                ? tab.SelectedRow
                : _rows.Count > 0 ? 0 : -1;
        }
        else
        {
            _rows.Clear();
            _layers.ItemsSource = new List<ListBoxItem>();
            _layers.SelectedIndex = -1;
        }
        WatchProject();
        ShowTransformBox();
        ShowCropBox();
        ShowTextCaret();
        RefreshTabs();
        Refresh();
    }

    /// <summary>
    /// A tab closed: it is asked about first when it holds work that was not saved, the document it owns is
    /// let go, and the tab beside it comes in front. The last tab is not closed — an empty one takes its place,
    /// so there is always somewhere for the next project to go.
    /// </summary>
    private async Task CloseTab(Tab tab)
    {
        if (!await MayReplace(tab)) return;
        var at = _tabs.IndexOf(tab);
        if (at < 0) return;
        var wasOpen = ReferenceEquals(tab, _open);
        _tabs.RemoveAt(at);
        if (_tabs.Count == 0) _tabs.Add(new Tab());
        tab.Document?.Dispose();
        var next = _tabs[Math.Min(at, _tabs.Count - 1)];
        if (wasOpen)
        {
            // Nothing is kept from a tab that has just been closed.
            _open = next;
            Show(next);
        }
        else
        {
            RefreshTabs();
        }
        Say($"{tab.Name} closed");
    }

    /// <summary>The tab strip: a button a tab, the one in front marked, and a way to start another.</summary>
    private void RefreshTabs()
    {
        _tabStrip.Children.Clear();
        foreach (var tab in _tabs)
        {
            var name = new Button { Content = tab.Name, Tag = tab };
            name.Click += (_, _) => Bring(tab);
            var close = new Button { Content = "×", Padding = new Thickness(4, 0, 4, 0), Tag = tab };
            close.Click += (_, _) => _ = CloseTab(tab);
            _tabStrip.Children.Add(new Border
            {
                Background = ReferenceEquals(tab, _open) ? Accent : Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 0, 4, 0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 2,
                    Children = { name, close },
                },
            });
        }
        var add = new Button { Content = "+", Padding = new Thickness(8, 0, 8, 0) };
        add.Click += (_, _) => _ = NewProject();
        _tabStrip.Children.Add(add);
    }

    /// <summary>
    /// File ▸ New: a blank canvas of the size asked for, with one empty layer over it. What was open is closed
    /// with it — a document that has been changed and not saved asks first, as a window would.
    /// </summary>
    private async Task NewProject()
    {
        if (await NewDocumentDialog.Ask(this) is not { } asked) return;
        var made = LayerPlacement.NewDocument(asked.Width, asked.Height, asked.Resolution);
        if (made is null)
        {
            Say("That size is too large for a canvas");
            return;
        }
        _open = TabForNew();
        _document = made;
        _projectPath = null;
        _history.Reset();
        Show(_open);
        if (_document.Layers.Count > 0) Reselect(_document.Layers[^1].ID);
        Say($"New {_document.Width} by {_document.Height} canvas at {_document.Resolution:0.##} per inch, not saved yet");
    }

    /// <summary>
    /// Whether the document that is open may be thrown away for something else. It may when it holds nothing
    /// that was not saved; otherwise the person is asked, and only a yes lets it go.
    /// </summary>
    private async Task<bool> MayReplace(Tab? tab = null)
    {
        var which = tab ?? _open;
        if (which.Document is null || !which.History.IsModified) return true;
        var named = which.Path is { } path
            ? $"{System.IO.Path.GetFileName(path)} has been changed since it was last saved."
            : "This project has not been saved.";
        return await ConfirmDialog.Ask(this, "Discard unsaved changes?",
            $"{named} Anything not saved is lost.", "Discard", "Keep");
    }

    /// <summary>The layer the panel has selected, or the top one when nothing is: what an edit acts on.</summary>
    private Guid? Selected =>
        _layers.SelectedIndex >= 0 && _layers.SelectedIndex < _rows.Count ? _rows[_layers.SelectedIndex]
        : _rows.Count > 0 ? _rows[^1]
        : null;

    /// <summary>
    /// Every layer the panel has selected, which is more than one when several rows are: what the verbs that
    /// can act on several at once — flip, delete, merge, group and the transform box — work from.
    /// </summary>
    private List<Guid> SelectedLayers
    {
        get
        {
            var chosen = _layers.SelectedItems?.OfType<ListBoxItem>()
                .Select(item => item.Tag).OfType<Guid>().ToList() ?? [];
            if (chosen.Count > 0) return chosen;
            return Selected is { } one ? [one] : [];
        }
    }

    /// <summary>One edit, wrapped in the history so it undoes in a single step. False when it changed nothing.</summary>
    private bool Edit(string name, Func<bool> change)
    {
        if (_document is not { } document) return false;
        _history.Begin(name, document, Selected);
        var changed = change();
        _history.End(document, Selected);
        Refresh();
        return changed;
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
    /// <summary>
    /// The two things a layer is combined with: how it blends and how much of it shows. Both act on the
    /// selected layer, and the slider takes effect when it is let go, so a drag is one undo step rather than
    /// one for every pixel of the drag.
    /// </summary>
    private Control Appearance()
    {
        _blend.ItemsSource = BlendModes.Select(mode => Spell(mode)).ToList();
        _blend.Width = 150;
        _blend.SelectionChanged += (_, _) =>
        {
            if (_showingAppearance) return;
            var index = _blend.SelectedIndex;
            if (index < 0 || index >= BlendModes.Length) return;
            if (_document is not { } document || Selected is not { } id) return;
            Edit("Blend Mode", () => LayerEdits.SetBlendMode(document, id, BlendModes[index]));
        };

        _opacity.PropertyChanged += (_, change) =>
        {
            if (change.Property != Slider.ValueProperty) return;
            _opacityReadout.Text = $"{_opacity.Value:0}%";
            if (_showingAppearance) return;
            if (_opacityDragging)
            {
                // The history was begun when the drag started: this only moves the layer under it.
                ApplyOpacity();
                return;
            }
            Edit("Opacity", ApplyOpacity);
        };
        _opacity.PointerPressed += (_, _) =>
        {
            if (_document is not { } document || Selected is not { } id) return;
            _opacityDragging = true;
            _history.Begin("Opacity", document, Selected);
        };
        _opacity.PointerReleased += (_, _) =>
        {
            if (!_opacityDragging) return;
            _opacityDragging = false;
            if (_document is not { } document) return;
            ApplyOpacity();
            _history.End(document, Selected);
            Refresh();
        };

        return new StackPanel
        {
            Margin = new Thickness(10, 0, 10, 8),
            Spacing = 4,
            Children =
            {
                _blend,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "Opacity", Width = 52, VerticalAlignment = VerticalAlignment.Center },
                        _opacity,
                        _opacityReadout,
                    },
                },
            },
        };
    }

    /// <summary>Puts the slider's value on the selected layer, as one edit's worth of change.</summary>
    private bool ApplyOpacity()
    {
        if (_document is not { } document || Selected is not { } id) return false;
        return LayerEdits.SetOpacity(document, id, Math.Clamp(_opacity.Value / 100.0, 0, 1));
    }

    /// <summary>Shows what the selected layer is set to, without that being taken for a change of its own.</summary>
    private void ShowAppearance(ImageLayer? layer)
    {
        _showingAppearance = true;
        try
        {
            _blend.SelectedIndex = layer is null ? -1 : Array.IndexOf(BlendModes, layer.BlendMode);
            _opacity.Value = (layer?.Opacity ?? 1) * 100;
            _opacityReadout.Text = $"{_opacity.Value:0}%";
            _blend.IsEnabled = _opacity.IsEnabled = layer is not null;
        }
        finally
        {
            _showingAppearance = false;
        }
    }

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

        var plan = document is not null && layer is not null ? LayerMerge.Plan(document, SelectedLayers, layer.ID) : null;
        _merge.Header = "_" + (plan?.Action ?? "Merge Down");
        _merge.IsEnabled = plan is not null;
        _visibility.Header = layer?.IsVisible == false ? "_Show Layer" : "_Hide Layer";
        _visibility.IsEnabled = layer is not null;
        ShowAppearance(layer);
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
        if (_document is not { } document) return;
        var ids = SelectedLayers;
        if (ids.Count == 0) return;
        // What to select afterwards: whatever takes the place of the one an edit acts on, as the Mac build
        // does. Several go as one step, each with its contents.
        var anchor = Selected ?? ids[0];
        var index = document.Layers.FindIndex(layer => layer.ID == anchor);
        _history.Begin(ids.Count > 1 ? "Delete Layers" : "Delete Layer", document, anchor);
        var refused = 0;
        foreach (var id in ids) if (!LayerEdits.Delete(document, id)) refused++;
        _history.End(document, anchor);
        if (refused > 0)
        {
            Say("A layer that stayed is clipped to one that went; macOS offers to bake or unlink it and this build cannot yet");
        }
        if (refused == ids.Count) return;
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
        if (_document is not { } document) return;
        var ids = SelectedLayers;
        if (ids.Count == 0) return;
        _history.Begin("Group Layers", document, Selected);
        var folder = LayerPlacement.GroupSelected(document, ids);
        _history.End(document, Selected);
        if (folder is null) { Say("Those layers could not be wrapped in a folder."); return; }
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
        if (_document is not { } document) return;
        var ids = SelectedLayers;
        if (ids.Count == 0 || Selected is not { } id) return;
        if (LayerMerge.Plan(document, ids, id) is not { } plan) return;
        _history.Begin(plan.Action, document, id);
        var made = LayerMerge.Merge(document, ids, id);
        _history.End(document, id);
        if (made is not { } merged)
        {
            Say("Nothing to merge");
            return;
        }
        Reselect(merged);
    }

    private void Flip(bool horizontal, bool canvas)
    {
        if (_document is not { } document) return;
        var ids = SelectedLayers;
        if (ids.Count == 0) return;
        Edit(canvas ? $"Flip Canvas {(horizontal ? "Horizontal" : "Vertical")}" : $"Flip {(horizontal ? "Horizontal" : "Vertical")}",
            () =>
            {
                if (canvas)
                {
                    LayerEdits.FlipCanvas(document, horizontal);
                    return true;
                }
                return LayerEdits.Flip(document, ids, horizontal);
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
        _canvas.TypeOnClick = tool == Tool.Type;
        if (tool != Tool.Type) CommitText();
        _canvas.CropEnabled = tool == Tool.Crop;
        _canvas.ShapeEnabled = tool == Tool.Shape;
        _canvas.GradientEnabled = tool == Tool.Gradient;
        // A crop frame belongs to the tool: leaving the tool lets go of it.
        if (tool != Tool.Crop) _cropFrame = null;
        ShowCropBox();
        _canvas.TransformEnabled = tool == Tool.Move;
        _canvas.ShapePreviewFor = tool == Tool.Shape ? dragged => ShapePlan(dragged) : null;
        _canvas.GuidesDraggable = tool == Tool.Move;
        ShowTransformBox();
        _canvas.PaintEnabled = tool is Tool.Brush or Tool.Clone or Tool.Blur or Tool.Liquify or Tool.Smudge or Tool.Heal;
        PushBrush();
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
            Tool.Liquify => $"Liquify brush: {_brush.Diameter:0} pixels — drag the pixels where they should go",
            Tool.Smudge => $"Smudge brush: {_brush.Diameter:0} pixels — drag the colour along",
            Tool.Heal => $"Spot healing ({_brush.Healing}): {_brush.Diameter:0} pixels — drag over what should go",
            Tool.Eyedropper => "Eyedropper — click the canvas to take its colour",
            Tool.Type => "Type — click where the text goes, then type it",
            Tool.Crop => "Crop — drag a frame, Alt to grow it from the middle, then Crop ▸ Apply",
            Tool.Shape => $"Shape ({_shapeKind}) — drag it out; Shift squares it, Alt grows it from the middle",
            Tool.Gradient => $"Gradient ({_gradientShape}, {(_gradientToBackground ? "to the background colour" : "to nothing")}) — drag the line it runs along",
            Tool.Move => "Move — drag the layer, or a handle to scale and turn it",
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

    /// <summary>The Gradient tool's options, as the Mac build's gradient bar has them.</summary>
    private void BuildGradientMenu()
    {
        foreach (var shape in Enum.GetValues<GradientShape>())
        {
            _gradientMenu.Items.Add(Command($"_{shape}", () => SetGradient(shape, null, null)));
        }
        _gradientMenu.Items.Add(new Separator());
        _gradientMenu.Items.Add(Command("_To the background colour", () => SetGradient(null, true, null)));
        _gradientMenu.Items.Add(Command("To _nothing", () => SetGradient(null, false, null)));
        _gradientMenu.Items.Add(new Separator());
        _gradientMenu.Items.Add(Command("_Reversed", () => SetGradient(null, null, !_gradientReversed)));
        _gradientMenu.Items.Add(Command("_Background colour…", () => _ = SetGradientBackground()));
    }

    private void SetGradient(GradientShape? shape, bool? toBackground, bool? reversed)
    {
        if (shape is { } wanted) _gradientShape = wanted;
        if (toBackground is { } fade) _gradientToBackground = fade;
        if (reversed is { } turn) _gradientReversed = turn;
        Say($"Gradient: {_gradientShape}, {(_gradientToBackground ? "to the background colour" : "to nothing")}" +
            (_gradientReversed ? ", reversed" : "") + ", opacity as the brush's");
        SetTool(_tool);
    }

    private async Task SetGradientBackground()
    {
        var current = $"{_gradientBackground.Red * 255:0},{_gradientBackground.Green * 255:0},{_gradientBackground.Blue * 255:0}";
        if (await TextPrompt.Ask(this, "Gradient background colour", "Red, green and blue, 0 to 255", current)
            is not { } typed)
        {
            return;
        }
        if (Colour(typed) is not { } colour)
        {
            Say("The colour has to be three numbers from 0 to 255, as in 255,0,0");
            return;
        }
        _gradientBackground = colour;
        _gradientToBackground = true;
        Say($"Gradient background {colour.Red * 255:0},{colour.Green * 255:0},{colour.Blue * 255:0}");
        SetTool(_tool);
    }

    /// <summary>
    /// A gradient drag: the colour runs from one end to the other, over the selected layer's pixels or its
    /// mask, at the brush's opacity, as one undo step.
    /// </summary>
    /// <summary>The gradient's line has been taken hold of: the canvas starts showing what it would do.</summary>
    private void GradientStarted()
    {
        if (_document is not { } document || Selected is not { } id) return;
        StartPreview(document, id);
    }

    /// <summary>
    /// The line has moved: the gradient is filled into the preview so the run of it can be seen before the
    /// mouse comes up. Nothing is committed until it does.
    /// </summary>
    private void GradientChanged(SKPoint start, SKPoint end)
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (!GradientEdits.HasLine(start, end)) return;
        var (mask, from, to, opacity, shape) = GradientPlan(document, id);
        RequestPreview((target, layer) => GradientEdits.Fill(target, layer, mask, start, end, from, to, opacity, shape));
    }

    /// <summary>
    /// What the gradient tool would do with the brush as it stands: which of the layer's two surfaces it fills,
    /// between which colours, and in which shape. The same answer serves the drag's preview and the fill the
    /// drag ends up making.
    /// </summary>
    private (bool Mask, SKColor From, SKColor To, double Opacity, GradientShape Shape) GradientPlan(
        CanvasDocument document, Guid layerID)
    {
        var mask = _paintingMask && document.Layers.FirstOrDefault(layer => layer.ID == layerID)?.Mask is not null;
        var from = new SKColor(
            (byte)Math.Clamp(Math.Round(_brush.Red * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(_brush.Green * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(_brush.Blue * 255), 0, 255));
        var to = _gradientToBackground
            ? new SKColor(
                (byte)Math.Clamp(Math.Round(_gradientBackground.Red * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(_gradientBackground.Green * 255), 0, 255),
                (byte)Math.Clamp(Math.Round(_gradientBackground.Blue * 255), 0, 255))
            : new SKColor(from.Red, from.Green, from.Blue, 0);
        if (_gradientReversed) (from, to) = (to, from);
        return (mask, from, to, _brush.Opacity, _gradientShape);
    }

    /// <summary>The gradient's line has been let go: the fill is made, as one undo step.</summary>
    private void GradientFinished(SKPoint start, SKPoint end)
    {
        if (_document is not { } document || Selected is not { } id) return;
        StopPreview();
        if (!GradientEdits.HasLine(start, end))
        {
            Say("Drag the line the gradient should run along");
            return;
        }
        var (mask, from, to, opacity, shape) = GradientPlan(document, id);
        Edit(mask ? "Gradient Mask" : "Gradient",
            () => GradientEdits.Fill(document, id, mask, start, end, from, to, opacity, shape));
        Reselect(id);
        Say($"Gradient over {Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)):0} pixels");
    }

    /// <summary>The shapes the Shape tool draws, and the two numbers that shape them.</summary>
    private void BuildShapeKinds()
    {
        foreach (var kind in Enum.GetValues<ShapeKind>())
        {
            var item = Command($"_{kind}", () => SetShapeKind(kind));
            _shapeKinds.Items.Add(item);
            _shapeKindItems[kind] = item;
        }
        _shapeKinds.Items.Add(new Separator());
        _shapeKinds.Items.Add(Command("Corner _radius…", () => _ = SetShapeNumber(ShapeNumber.CornerRadius)));
        _shapeKinds.Items.Add(Command("_Line width…", () => _ = SetShapeNumber(ShapeNumber.LineWidth)));
        SetShapeKind(ShapeKind.Rectangle);
    }

    private readonly Dictionary<ShapeKind, MenuItem> _shapeKindItems = [];

    private void SetShapeKind(ShapeKind kind)
    {
        _shapeKind = kind;
        foreach (var (which, item) in _shapeKindItems) item.IsChecked = which == kind;
    }

    /// <summary>Asks for one of the two numbers that shape a shape.</summary>
    private async Task SetShapeNumber(ShapeNumber which)
    {
        var corner = which == ShapeNumber.CornerRadius;
        var current = corner ? _shapeCornerRadius : _shapeLineWidth;
        if (await Ask(corner ? "Corner radius" : "Line width", "Document pixels, 0 to 1000",
                $"{current:0.##}", 0, 1000) is not { } value)
        {
            return;
        }
        if (corner) _shapeCornerRadius = value;
        else _shapeLineWidth = Math.Max(1, value);
        Say($"Shape: {_shapeKind}, {(corner ? "corner radius" : "line width")} {value:0.##} pixels");
    }

    private enum ShapeNumber
    {
        CornerRadius,
        LineWidth,
    }

    /// <summary>
    /// A finished shape drag: the box it made becomes a new layer of its own pixels, still knowing the shape
    /// it is, so a later size change draws it again rather than stretching it.
    /// </summary>
    private void ShapeFinished(SKPoint anchor, SKRectI box, SKPoint lineEnd)
    {
        if (_document is not { } document) return;
        var style = new LayerShapeStyle
        {
            Kind = _shapeKind,
            Red = _brush.Red,
            Green = _brush.Green,
            Blue = _brush.Blue,
            CornerRadius = _shapeCornerRadius,
        };
        var target = box;
        if (_shapeKind == ShapeKind.Line)
        {
            // The layer is the box around the line with room for the stroke's own thickness and its round ends.
            var half = (float)(_shapeLineWidth / 2);
            target = CropEdits.Snapped(SKRect.Create(box.Left - half, box.Top - half,
                box.Width + half * 2, box.Height + half * 2));
            style.LineWidth = _shapeLineWidth;
            style.Start = Unit(target, anchor);
            style.End = Unit(target, lineEnd);
        }
        if (ShapeEdits.TooLarge(target.Width, target.Height))
        {
            Say("That shape is too large to draw as one layer");
            return;
        }
        _history.Begin(_shapeKind.ToString(), document, Selected);
        var made = ShapeEdits.Add(document, style, target, Selected);
        _history.End(document, Selected);
        if (made is null)
        {
            Say("That shape could not be drawn");
            return;
        }
        Reselect(made);
        Say($"{_shapeKind}: {target.Width}x{target.Height} at {target.Left},{target.Top}");
    }

    /// <summary>Where a document point sits in a box, as a fraction of its sides.</summary>
    private static JsonPoint Unit(SKRectI box, SKPoint point) => new(
        box.Width > 0 ? (point.X - box.Left) / box.Width : 0.5,
        box.Height > 0 ? (point.Y - box.Top) / box.Height : 0.5);

    /// <summary>
    /// The Camera Raw filter: its sliders are asked for, then the Light, Color and Effects stages run over the
    /// selected layer's own pixels, held to the selection, as one undo step. The Mac build previews it while
    /// the panel is open; this applies it when the panel is dismissed.
    /// </summary>
    private async Task CameraRawFilter()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false })
        {
            Say("Camera Raw needs a layer with pixels of its own");
            return;
        }
        // The panel shows what it is doing: as its sliders move the layer is filtered into a copy of the
        // document and the canvas draws that, while the document itself is not touched until Apply.
        StartPreview(document, id);
        // The preview carries the panel's overlay switches: clipped shadows and highlights and the sharpening
        // mask are shown over the grade while the amounts are moved, and the overlay is what is shown when one
        // is on. The edit that is finally made is the grade alone, never the overlay.
        var asked = await CameraRawDialog.Ask(this, new CameraRawSettings(), BrushColour(),
            (settings, shadows, highlights, mask) => RequestPreview(document =>
                CameraRawEdits.Overlay(document, id, settings, shadows, highlights, mask)
                || CameraRawEdits.Apply(document, id, settings)));
        StopPreview();
        if (asked is not { } settings) return;
        if (_document is not { } current) return;
        Edit("Camera Raw Filter", () => CameraRawEdits.Apply(current, id, settings));
        Reselect(id);
        Say($"Camera Raw: exposure {settings.Exposure:0.##}, contrast {settings.Contrast:0}, " +
            $"saturation {settings.Saturation:0}");
    }

    /// <summary>
    /// One of the filters that are not Camera Raw. Its amounts are asked for, then it runs over the selected
    /// layer's own pixels, held to the selection, as one undo step.
    /// </summary>
    private async Task ApplyFilter(FilterKind kind)
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false })
        {
            Say($"{kind} needs a layer with pixels of its own");
            return;
        }
        StartPreview(document, id);
        var asked = await FilterDialog.Ask(this, kind, new FilterSettings(),
            settings => RequestPreview((target, layer) => FilterEdits.Apply(target, layer, kind, settings)));
        StopPreview();
        if (asked is not { } settings) return;
        if (_document is not { } current) return;
        Edit($"{kind} Filter", () => FilterEdits.Apply(current, id, kind, settings));
        Reselect(id);
        Say($"{kind} applied");
    }

    /// <summary>
    /// Dither: its look and amounts are asked for, then the layer's own pixels are reduced to ink, held to the
    /// selection, as one undo step.
    /// </summary>
    private async Task DitherFilter()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false })
        {
            Say("Dither needs a layer with pixels of its own");
            return;
        }
        StartPreview(document, id);
        var asked = await DitherDialog.Ask(this, new DitherSettings(),
            (style, settings) => RequestPreview((target, layer) => DitherEdits.Apply(target, layer, style, settings)));
        StopPreview();
        if (asked is not { } chosen) return;
        if (_document is not { } current) return;
        Edit("Dither", () => DitherEdits.Apply(current, id, chosen.Style, chosen.Settings));
        Reselect(id);
        Say($"Dither: {chosen.Style}, {chosen.Settings.Levels:0} tones");
    }

    /// <summary>
    /// Content-Aware Fill: the selected part of the layer is made up out of the pixels around it, as one undo
    /// step. It needs a selection, and something outside it to take the fill from.
    /// </summary>
    private void ContentAwareFill()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Selection.Path is null)
        {
            Say("Content-Aware Fill needs a selection to fill");
            return;
        }
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false })
        {
            Say("Content-Aware Fill needs a layer with pixels of its own");
            return;
        }
        if (!Edit("Content-Aware Fill", () => ContentFillEdits.Apply(document, id)))
        {
            Say("Content-Aware Fill found nothing to fill from: make the selection smaller");
            return;
        }
        Reselect(id);
        Say("Content-Aware Fill applied");
    }

    /// <summary>
    /// The kinds of adjustment layer the Layer menu offers, and the settings verb beside it.
    /// </summary>
    private void BuildAdjustmentMenu()
    {
        foreach (var (label, kind) in new (string Label, AdjustmentKind Kind)[]
                 {
                     ("_Hue/Saturation", AdjustmentKind.HueSaturation),
                     ("_Levels", AdjustmentKind.Levels),
                     ("C_urves", AdjustmentKind.Curves),
                     ("_Exposure", AdjustmentKind.Exposure),
                     ("_Black & White", AdjustmentKind.BlackWhite),
                     ("_Gradient Map", AdjustmentKind.GradientMap),
                     ("_Grain", AdjustmentKind.Grain),
                     ("_Add Noise", AdjustmentKind.AddNoise),
                     ("_Gaussian Blur", AdjustmentKind.GaussianBlur),
                     ("_Motion Blur", AdjustmentKind.MotionBlur),
                     ("C_olor Balance", AdjustmentKind.ColorBalance),
                     ("_Invert", AdjustmentKind.Invert),
                 })
        {
            _adjustmentMenu.Items.Add(Command(label, () => _ = NewAdjustment(kind)));
        }
        _adjustmentSettings = LayerCommand("Adjustment _Settings…", () => _ = EditAdjustment(), null,
            (_, layer) => layer.Adjustment is not null);
        BuildEffectsMenu();
    }

    /// <summary>
    /// The effects a layer draws around itself, one row each plus a row that takes them all away. Each row
    /// opens the panel for that effect, ticked when the layer already has it.
    /// </summary>
    private void BuildEffectsMenu()
    {
        foreach (var kind in Enum.GetValues<EffectKind>())
        {
            var wanted = kind;
            _effectsMenu.Items.Add(LayerCommand(EffectDialog.TitleFor(kind) + "…", () => _ = EditEffect(wanted), null,
                (_, layer) => layer.IsGroup == false));
        }
        _effectsMenu.Items.Add(new Separator());
        _clearEffects = LayerCommand("_Clear Effects", ClearEffects, null, (_, layer) => layer.Effects is not null);
        _effectsMenu.Items.Add(_clearEffects);
    }

    /// <summary>One effect's panel, with the layer's own effect as it starts out.</summary>
    private async Task EditEffect(EffectKind kind)
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { } layer) return;
        if (await EffectDialog.Ask(this, kind, layer.Effects) is not { } effects) return;
        if (_document is not { } current) return;
        Edit(EffectDialog.TitleFor(kind), () => LayerEdits.SetEffect(current, id, kind, effects));
        Reselect(id);
    }

    /// <summary>Every effect taken off the layer, as one undo step.</summary>
    private void ClearEffects()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id)?.Effects is null)
        {
            Say("This layer has no effects");
            return;
        }
        Edit("Clear Effects", () => LayerEdits.SetEffects(document, id, null));
        Say("Effects cleared");
    }

    /// <summary>A new adjustment layer over the selected one, with its settings asked for straight away.</summary>
    private async Task NewAdjustment(AdjustmentKind kind)
    {
        if (_document is not { } document) return;
        _history.Begin("New Adjustment Layer", document, Selected);
        var made = LayerPlacement.AddAdjustment(document, kind, Selected);
        _history.End(document, Selected);
        if (made is null)
        {
            Say("This document already holds as many layers as it may.");
            return;
        }
        Reselect(made);
        await EditAdjustment();
    }

    /// <summary>The selected adjustment layer's settings, changed and put back as one undo step.</summary>
    private async Task EditAdjustment()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (LayerAdjustmentEdits.Settings(document, id) is not { } settings) return;
        StartPreview(document, id);
        var asked = await AdjustmentDialog.Ask(this, settings,
            changed => RequestPreview((target, layer) => LayerAdjustmentEdits.Set(target, layer, changed)));
        StopPreview();
        if (asked is not { } changed) return;
        if (_document is not { } current) return;
        Edit($"{LayerPlacement.Name(changed.Kind)} Adjustment", () => LayerAdjustmentEdits.Set(current, id, changed));
        Reselect(id);
        Say($"{LayerPlacement.Name(changed.Kind)} adjustment set");
    }

    /// <summary>
    /// One of the colour adjustments from the Image menu: its amounts are asked for, then it runs over the
    /// selected layer's own pixels, held to the selection, as one undo step. The same settings can be left
    /// on an adjustment layer instead, from the Layer menu.
    /// </summary>
    private async Task ImageAdjustment(AdjustmentKind kind)
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false, Adjustment: null })
        {
            Say($"{LayerPlacement.Name(kind)} needs a layer with pixels of its own");
            return;
        }
        StartPreview(document, id);
        var asked = await AdjustmentDialog.Ask(this, new LayerAdjustment { Kind = kind },
            settings => RequestPreview((target, layer) => FilterEdits.ApplyAdjustment(target, layer, settings)));
        StopPreview();
        if (asked is not { } settings) return;
        if (_document is not { } current) return;
        Edit(LayerPlacement.Name(kind), () => FilterEdits.ApplyAdjustment(current, id, settings));
        Reselect(id);
        Say($"{LayerPlacement.Name(kind)} applied");
    }

    /// <summary>Edit ▸ Copy: the selected pixels of the active layer, held for a paste.</summary>
    private void Copy()
    {
        if (_document is not { } document || Selected is not { } id) return;
        var copied = SelectionClipboard.Copy(document, id);
        if (copied is null)
        {
            Say("Select something on a layer with pixels of its own first");
            return;
        }
        Adopt(copied);
        Say($"Copied {copied.Region.Width} x {copied.Region.Height}");
    }

    /// <summary>Edit ▸ Copy Merged: the selected pixels of everything that is drawn.</summary>
    private void CopyMerged()
    {
        if (_document is not { } document) return;
        var copied = SelectionClipboard.CopyMerged(document);
        if (copied is null)
        {
            Say("Select something to copy first");
            return;
        }
        Adopt(copied);
        Say($"Copied {copied.Region.Width} x {copied.Region.Height} from the flattened picture");
    }

    /// <summary>Edit ▸ Cut: the selected pixels taken off, and held for a paste.</summary>
    private void Cut()
    {
        if (_document is not { } document || Selected is not { } id) return;
        ClipboardImage? copied;
        _history.Begin("Cut", document, Selected);
        try
        {
            if (!SelectionClipboard.Cut(document, id, out copied) || copied is null)
            {
                Say("Select something on a layer with pixels of its own first");
                return;
            }
        }
        finally
        {
            _history.End(document, Selected);
        }
        Adopt(copied);
        Reselect(id);
        Say($"Cut {copied.Region.Width} x {copied.Region.Height}");
    }

    /// <summary>Edit ▸ Paste: the clipboard as a layer, where on the document it came from.</summary>
    private void Paste()
    {
        if (_document is not { } document) return;
        if (_clipboard is not { } clipboard)
        {
            Say("There is nothing to paste");
            return;
        }
        Guid? made = null;
        _history.Begin("Paste", document, Selected);
        try
        {
            made = SelectionClipboard.Paste(document, clipboard, Selected);
        }
        finally
        {
            _history.End(document, Selected);
        }
        if (made is null)
        {
            Say("This document already holds as many layers as it may.");
            return;
        }
        Reselect(made);
        Say($"Pasted {clipboard.Region.Width} x {clipboard.Region.Height}");
    }

    /// <summary>Edit ▸ Layer via Copy: the selected pixels of the active layer as a layer of their own.</summary>
    private void LayerViaCopy()
    {
        if (_document is not { } document || Selected is not { } id) return;
        Guid? made = null;
        _history.Begin("Layer via Copy", document, Selected);
        try
        {
            made = SelectionClipboard.LayerViaCopy(document, id, Selected);
        }
        finally
        {
            _history.End(document, Selected);
        }
        if (made is null)
        {
            Say("Select something on a layer with pixels of its own first");
            return;
        }
        Reselect(made);
        Say("Layer made from the selection");
    }

    /// <summary>The clipboard the window holds: one piece of the canvas at a time, as the Mac build keeps it.</summary>
    private void Adopt(ClipboardImage copied)
    {
        _clipboard?.Dispose();
        _clipboard = copied;
    }

    /// <summary>
    /// Image ▸ Image Size: the canvas and every layer's pixels resampled to a new size, as one undo step.
    /// </summary>
    private async Task ImageSize()
    {
        if (_document is not { } document) return;
        if (await ImageSizeDialog.Ask(this, document.Width, document.Height, document.Resolution,
                LayerSampling.HighQuality) is not { } asked)
        {
            return;
        }
        if (_document is not { } current) return;
        if (!Edit("Image Size", () => ImageEdits.Resize(current, asked.Width, asked.Height, asked.Resolution, asked.Sampling)))
        {
            Say("That size is too large to resample to.");
            return;
        }
        _canvas.Fit();
        Say($"Image is now {asked.Width} x {asked.Height} at {asked.Resolution:0.##} per inch");
    }

    /// <summary>A distortion has been taken hold of: one undo step for the whole drag, as a slider drag gets.</summary>
    private void DistortStarted()
    {
        if (_document is not { } document || Selected is not { } id) return;
        _distortBox = _canvas.TransformBox;
        _distortLayers = SelectedLayers;
        _history.Begin(_distortLayers.Count > 1 ? "Distort Layers" : "Distort", document, Selected);
        if (_distortLayers.Count > 1) StartPreview(document, _distortLayers);
        else StartPreview(document, id);
    }

    /// <summary>
    /// A corner has moved: the layers are resampled into the shape the corners make in the preview, so the
    /// distortion can be seen while it is being made rather than only after it is let go. One layer's corners
    /// are the shape itself; several layers are each carried by the box's own perspective, so they keep the
    /// shape they had between them.
    /// </summary>
    private void DistortChanged(IReadOnlyList<SKPoint> corners)
    {
        if (_distortLayers is { Count: > 1 } ids && _distortBox is { } box)
        {
            RequestPreview(document => DistortEdits.Distort(document, ids, box, corners));
            return;
        }
        RequestPreview((target, layer) => DistortEdits.Distort(target, layer, corners));
    }

    /// <summary>
    /// The distortion has been let go: the layers' pixels are resampled into that shape, which is the one
    /// edit. The shape is only drawn while it is dragged — nothing is resampled until it is let go.
    /// </summary>
    private void DistortFinished(IReadOnlyList<SKPoint> corners)
    {
        if (_document is not { } document || Selected is not { } id) return;
        StopPreview();
        var ids = _distortLayers ?? [id];
        var box = _distortBox;
        _distortLayers = null;
        _distortBox = null;
        var distorted = ids.Count > 1 && box is { } group
            ? DistortEdits.Distort(document, ids, group, corners)
            : DistortEdits.Distort(document, id, corners);
        if (!distorted) Say("That shape cannot be made");
        _history.End(document, Selected);
        Reselect(id);
    }

    /// <summary>A guide has been taken hold of: one undo step for the whole drag, as a slider drag gets.</summary>
    private void GuideDragStarted()
    {
        if (_document is not { } document) return;
        _history.Begin("Move Guide", document, Selected);
    }

    /// <summary>The guide follows the pointer; the history step was begun when it was taken hold of.</summary>
    private void GuideMoved(Guid id, double position)
    {
        if (_document is not { } document) return;
        if (!GuideEdits.Move(document, id, position)) return;
        _canvas.InvalidateVisual();
        Say($"Guide at {position:0.#}");
    }

    /// <summary>
    /// The drag has ended. A guide left off the canvas is taken away, as Photoshop takes it away — which is
    /// how a guide is got rid of without a menu.
    /// </summary>
    private void GuideDragFinished()
    {
        if (_document is not { } document) return;
        if (document.Guides.FirstOrDefault(guide => !GuideEdits.OnCanvas(document, guide)) is { } away)
        {
            GuideEdits.Remove(document, away.ID);
            Say("Guide taken away");
        }
        _history.End(document, Selected);
        Refresh();
    }

    /// <summary>The three things a drag can line up with, as the View menu lists them.</summary>
    private (MenuItem Item, SnapTo Flag, string Label)[] SnapRows() =>
    [
        (_snapToCanvas, SnapTo.Canvas, "Snap to Canvas"),
        (_snapToGuides, SnapTo.Guides, "Snap to Guides"),
        (_snapToLayers, SnapTo.Layers, "Snap to Layers"),
        (_snapToGrid, SnapTo.Grid, "Snap to Grid"),
    ];

    /// <summary>View ▸ Snap to …: one kind of thing a drag lines up with, on or off.</summary>
    private void ToggleSnapTo(SnapTo flag, string label)
    {
        _snapTo = _snapTo.HasFlag(flag) ? _snapTo & ~flag : _snapTo | flag;
        var on = _snapTo.HasFlag(flag);
        foreach (var (item, at, _) in SnapRows())
        {
            if (at == flag) item.IsChecked = on;
        }
        KeepSwitches();
        Say($"{label} {(on ? "on" : "off")}");
    }

    /// <summary>The view's switches written down, so the next launch opens the way this one was left.</summary>
    private void KeepSwitches()
    {
        _tools.ShowGrid = _gridVisible;
        _tools.ShowRulers = _rulersVisible;
        _tools.ShowGuides = _guidesVisible;
        _tools.LockGuides = _guidesLocked;
        _tools.ShowTransformControls = _transformShown;
        _tools.PixelGrid = _pixelGridShown;
        _tools.Snapping = _snappingOn;
        _tools.GridSpacing = _grid.Spacing;
        _tools.GridSubdivisions = _grid.Subdivisions;
        _tools.SnapTo = _snapTo;
        _tools.Save(ToolDefaults.DefaultPath);
    }

    /// <summary>View ▸ Show Grid: the layout grid on or off, which the canvas draws under everything else.</summary>
    private void ShowGrid()
    {
        _gridVisible = !_gridVisible;
        _canvas.Grid = _gridVisible ? _grid : null;
        _showGrid.Header = _gridVisible ? "_Hide Grid" : "Show _Grid";
        KeepSwitches();
        _canvas.InvalidateVisual();
        Say(_gridVisible ? $"Grid every {_grid.Spacing} pixels" : "Grid hidden");
    }

    /// <summary>A View menu row that is a switch: it opens where it was left and turns over when clicked.</summary>
    private static void Toggle(string header, MenuItem item, bool on, Action flip)
    {
        item.Header = header;
        item.ToggleType = MenuItemToggleType.CheckBox;
        item.IsChecked = on;
        item.Click += (_, _) => flip();
    }

    /// <summary>View ▸ Guides: whether the guides are drawn, which does not change them.</summary>
    private void ShowGuides()
    {
        _guidesVisible = !_guidesVisible;
        _showGuides.IsChecked = _guidesVisible;
        PushViewSwitches();
        KeepSwitches();
        Say(_guidesVisible ? "Guides shown" : "Guides hidden");
    }

    /// <summary>View ▸ Lock Guides: whether a guide may be dragged. Locked, a click on one passes by it.</summary>
    private void LockGuides()
    {
        _guidesLocked = !_guidesLocked;
        _lockGuides.IsChecked = _guidesLocked;
        PushViewSwitches();
        KeepSwitches();
        Say(_guidesLocked ? "Guides locked" : "Guides unlocked");
    }

    /// <summary>View ▸ Show Transform Controls: whether the Move tool draws its handles.</summary>
    private void ShowTransformControls()
    {
        _transformShown = !_transformShown;
        _showTransform.IsChecked = _transformShown;
        PushViewSwitches();
        KeepSwitches();
        Say(_transformShown ? "Transform controls shown" : "Transform controls hidden");
    }

    /// <summary>View ▸ Pixel Grid: a line around each document pixel when the view is in far enough.</summary>
    private void ShowPixelGrid()
    {
        _pixelGridShown = !_pixelGridShown;
        _pixelGrid.IsChecked = _pixelGridShown;
        PushViewSwitches();
        KeepSwitches();
        Say(_pixelGridShown ? "Pixel grid shown from 800% up" : "Pixel grid hidden");
    }

    /// <summary>View ▸ Snap: whether a drag lines up with anything at all.</summary>
    private void ShowSnapping()
    {
        _snappingOn = !_snappingOn;
        _snapping.IsChecked = _snappingOn;
        KeepSwitches();
        Say(_snappingOn ? "Snapping on" : "Snapping off");
    }

    /// <summary>The canvas told where each view switch stands, so what is drawn and what is caught agree.</summary>
    private void PushViewSwitches()
    {
        _canvas.ShowsGuides = _guidesVisible;
        _canvas.LocksGuides = _guidesLocked;
        _canvas.ShowsTransformControls = _transformShown;
        _canvas.PixelGrid = _pixelGridShown;
        _canvas.InvalidateVisual();
    }

    /// <summary>View ▸ Rulers: the strips along the top and down the side of the canvas, on or off.</summary>
    private void ShowRulers()
    {
        _rulersVisible = !_rulersVisible;
        _showRulers.IsChecked = _rulersVisible;
        _rulerAcross.IsVisible = _rulersVisible;
        _rulerDown.IsVisible = _rulersVisible;
        _rulerCorner.IsVisible = _rulersVisible;
        KeepSwitches();
        UpdateRulers();
        Say(_rulersVisible ? "Rulers shown" : "Rulers hidden");
    }

    /// <summary>
    /// Starts watching the project that is open, so a copy of it written by something else — an editor beside
    /// this window — is taken up. Nothing is watched when no project has been saved yet, since there is no
    /// folder to watch.
    /// </summary>
    private void WatchProject()
    {
        _watch ??= ProjectWatch.For(_projectPath);
        if (_watch is null)
        {
            _watchTimer?.Stop();
            return;
        }
        _watchTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchTimer.Tick -= ProjectWritten;
        _watchTimer.Tick += ProjectWritten;
        _watchTimer.Start();
    }

    /// <summary>
    /// The project on disk has been written by something else. What it holds now is put in place of what is
    /// open, with the view and the selection kept, which is how the canvas takes a change an editor outside
    /// made. A document with changes of its own is never thrown away for it: it is only said that the project
    /// has moved on, and the person's own work waits until they save or reopen.
    /// </summary>
    private void ProjectWritten(object? sender, EventArgs e)
    {
        if (_watch?.Changed() is not true) return;
        if (_document is not { } document || _projectPath is not { } path) return;
        if (_history.IsModified)
        {
            Say("The project has been written by something else — save or reopen to take the change up");
            return;
        }
        ProjectSnapshot snapshot;
        try
        {
            snapshot = ProjectStore.Load(path);
        }
        catch (ProjectException)
        {
            Say("The project has been written by something else, and what is there now cannot be read");
            return;
        }
        var viewport = _canvas.Viewport;
        var selected = SelectedLayers;
        // As in Open: the document takes the snapshot's pixel references, so the snapshot is left alone rather
        // than disposed — disposing it would free the pixels the document is holding.
        _document = snapshot.ToDocument();
        _canvas.Document = _document;
        _canvas.RestoreViewport(viewport);
        document.Dispose();
        _history.Reset();
        ShowLayers(_document);
        var again = selected.FirstOrDefault(id => _document.Layers.Any(layer => layer.ID == id));
        if (again != Guid.Empty) Reselect(again);
        Refresh();
        Say($"{Path.GetFileName(path)} — taken up again, {_document.Layers.Count} layers");
    }

    /// <summary>
    /// The strips numbered the way the canvas is scrolled and zoomed: the same zoom and the same document
    /// place at the top left corner, so a tick lines up with what it measures.
    /// </summary>
    private void UpdateRulers()
    {
        _rulerAcross.Scale = _canvas.Zoom;
        _rulerAcross.Origin = _canvas.OriginX;
        _rulerDown.Scale = _canvas.Zoom;
        _rulerDown.Origin = _canvas.OriginY;
        _rulerAcross.InvalidateVisual();
        _rulerDown.InvalidateVisual();
    }

    /// <summary>View ▸ Grid Settings: how far apart the lines are and how finely each square is split.</summary>
    private async Task GridSettings()
    {
        if (await GridSettingsDialog.Ask(this, _grid) is not { } asked) return;
        _grid = asked;
        if (_gridVisible) _canvas.Grid = _grid;
        KeepSwitches();
        _canvas.InvalidateVisual();
        Say($"Grid every {_grid.Spacing} pixels, split {_grid.Subdivisions} ways");
    }

    /// <summary>View ▸ New Guide: a line across the canvas to line things up against.</summary>
    private async Task NewGuide()
    {
        if (_document is not { } document) return;
        if (await GuideDialog.Ask(this, document.Width, document.Height) is not { } asked) return;
        if (_document is not { } current) return;
        _history.Begin("New Guide", current, Selected);
        var made = GuideEdits.Add(current, asked.Axis, asked.Position);
        _history.End(current, Selected);
        if (made is null)
        {
            Say("This document already holds as many guides as it may.");
            return;
        }
        Refresh();
        Say($"{asked.Axis} guide at {asked.Position:0.#}");
    }

    /// <summary>View ▸ Clear Guides: every guide taken away, as one undo step.</summary>
    private void ClearGuides()
    {
        if (_document is not { } document) return;
        if (document.Guides.Count == 0)
        {
            Say("There are no guides to clear");
            return;
        }
        Edit("Clear Guides", () => GuideEdits.Clear(document) > 0);
        Say("Guides cleared");
    }

    /// <summary>
    /// Image ▸ Auto Levels: the levels the picture itself asks for, worked out from its own histogram and
    /// put straight on the layer. The Mac build shows them in the Levels panel instead; this skips the panel,
    /// since what it works out is the whole of the edit.
    /// </summary>
    private void AutoLevels(LevelsAuto mode)
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Asset: not null, IsGroup: false, Adjustment: null })
        {
            Say("Auto Levels needs a layer with pixels of its own");
            return;
        }
        if (!Edit("Auto Levels", () => LevelsEdits.Auto(document, id, mode))) Say("There is nothing in that layer to stretch");
        else Say($"Auto Levels: {mode}");
        Reselect(id);
    }

    /// <summary>
    /// Starts showing what a panel would do to a layer, before anything is committed. Nothing happens when
    /// the layer cannot be previewed, and the panel still works.
    /// </summary>
    /// <summary>The brush's colour, which the panels that think in colours start from.</summary>
    private SKColor BrushColour() => new(
        (byte)Math.Clamp(Math.Round(_brush.Red * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(_brush.Green * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(_brush.Blue * 255), 0, 255));

    /// <summary>Starts showing what a panel would do to a layer, before anything is committed.
    private void StartPreview(CanvasDocument document, Guid layerID) =>
        StartPreview(FilterPreview.Begin(document, layerID), layerID);

    /// <summary>The same for a look that changes several layers at once, which is what a group distortion is.</summary>
    private void StartPreview(CanvasDocument document, IReadOnlyList<Guid> layerIDs) =>
        StartPreview(FilterPreview.Begin(document, layerIDs), null);

    private void StartPreview(FilterPreview? preview, Guid? layerID)
    {
        if (preview is null) return;
        _preview = preview;
        _previewLayer = layerID;
        _canvas.PreviewDocument = preview.Document;
    }

    /// <summary>
    /// An amount has moved: the edit is remembered and runs once the amounts have been still for a moment,
    /// rather than on every tick of a drag.
    /// </summary>
    /// <summary>
    /// What the shape tool would make of a drag from <paramref name="box"/> outward: the style it will be
    /// drawn with and the box its pixels will cover. The same answer serves the drag's preview and the layer
    /// the drag ends up making, so what is seen while dragging is what arrives.
    /// </summary>
    private (LayerShapeStyle Style, SKRectI Box) ShapePlan(SKRectI box, SKPoint? anchor = null, SKPoint? lineEnd = null)
    {
        var style = new LayerShapeStyle
        {
            Kind = _shapeKind,
            Red = _brush.Red,
            Green = _brush.Green,
            Blue = _brush.Blue,
            CornerRadius = _shapeCornerRadius,
        };
        if (_shapeKind != ShapeKind.Line) return (style, box);
        // A line's layer is the box around it with room for the stroke's own thickness and its round ends.
        var half = (float)(_shapeLineWidth / 2);
        var target = CropEdits.Snapped(SKRect.Create(box.Left - half, box.Top - half,
            box.Width + half * 2, box.Height + half * 2));
        style.LineWidth = _shapeLineWidth;
        style.Start = Unit(target, anchor ?? new SKPoint(box.Left, box.Top));
        style.End = Unit(target, lineEnd ?? new SKPoint(box.Right, box.Bottom));
        return (style, target);
    }

    private void RequestPreview(Func<CanvasDocument, Guid, bool> apply)
    {
        if (_previewLayer is not { } layer) return;
        RequestPreview(document => apply(document, layer));
    }

    private void RequestPreview(Func<CanvasDocument, bool> apply)
    {
        if (_preview is null) return;
        _previewApply = apply;
        _previewTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        _previewTimer.Stop();
        _previewTimer.Tick -= ShowPreviewOnce;
        _previewTimer.Tick += ShowPreviewOnce;
        _previewTimer.Start();
    }

    /// <summary>Runs the edit on the preview once the amounts have settled, and draws it.</summary>
    private void ShowPreviewOnce(object? sender, EventArgs e)
    {
        _previewTimer?.Stop();
        if (_preview is not { } preview || _previewApply is not { } apply) return;
        if (preview.Show(apply)) _canvas.InvalidateVisual();
    }

    /// <summary>
    /// Puts the preview away. The canvas is told to stop drawing it before it is disposed, or a redraw could
    /// reach pixels that have just been freed.
    /// </summary>
    private void StopPreview()
    {
        _previewTimer?.Stop();
        _previewApply = null;
        _previewLayer = null;
        _canvas.PreviewDocument = null;
        _preview?.Dispose();
        _preview = null;
        _canvas.InvalidateVisual();
    }

    /// <summary>
    /// Select ▸ Colour Range: everything in the picture near a colour, wherever it is. What is matched is the
    /// canvas as shown, as the Mac build matches, so a colour counts wherever it appears.
    /// </summary>
    private async Task ColorRange()
    {
        if (_document is not { } document) return;
        if (document.Width <= 0 || document.Height <= 0) return;
        var start = new SKColor((byte)Math.Round(_brush.Red * 255), (byte)Math.Round(_brush.Green * 255),
            (byte)Math.Round(_brush.Blue * 255));
        if (await ColorRangeDialog.Ask(this, start) is not { } asked) return;
        using var sample = DocumentRenderer.Render(document);
        Edit($"Colour Range {asked.Fuzziness}", () => SelectionEdits.SelectColorRange(
            document, sample, [asked.Colour], [], asked.Fuzziness, asked.Invert, asked.Mode));
        Say($"Selected what is near {asked.Colour} within {asked.Fuzziness}");
    }

    /// <summary>Image ▸ Canvas Size: the canvas in pixels, with the picture kept at one of nine anchors.</summary>
    private async Task CanvasSize()
    {
        if (_document is not { } document) return;
        if (await CanvasSizeDialog.Ask(this, document.Width, document.Height, CanvasEdits.CentreAnchor) is not { } asked) return;
        if (_document is not { } current) return;
        Edit("Canvas Size", () => CanvasEdits.Resize(current, asked.Width, asked.Height, asked.Anchor));
        Say($"Canvas is now {asked.Width} x {asked.Height}");
    }

    /// <summary>Image ▸ Trim: the canvas cut back to what is actually drawn on it.</summary>
    private async Task Trim()
    {
        if (_document is not { } document) return;
        if (await TrimDialog.Ask(this, new TrimOptions()) is not { } options) return;
        if (_document is not { } current) return;
        if (!Edit("Trim", () => TrimEdits.Trim(current, options))) Say("There was nothing to trim");
        else Say($"Trimmed to {current.Width} x {current.Height}");
    }

    /// <summary>
    /// File ▸ Open Recent: the projects opened or saved lately, and a row that forgets them. The list is read
    /// back each time the menu is rebuilt, so a project that has since been moved or deleted is not offered.
    /// </summary>
    private void RefreshRecent()
    {
        _recentMenu.Items.Clear();
        var projects = _recent.All();
        foreach (var project in projects)
        {
            var path = project;
            _recentMenu.Items.Add(Command(Path.GetFileName(path), () => OpenRecent(path)));
        }
        if (projects.Count == 0)
        {
            _recentMenu.Items.Add(new MenuItem { Header = "Nothing yet", IsEnabled = false });
        }
        _recentMenu.Items.Add(new Separator());
        var clear = Command("_Clear Menu", () =>
        {
            _recent.Forgot();
            RefreshRecent();
        });
        clear.IsEnabled = projects.Count > 0;
        _recentMenu.Items.Add(clear);
    }

    /// <summary>Notes a project as one of the recent ones, as opening or saving it does.</summary>
    private void NoteRecent(string path)
    {
        _recent.Note(path);
        RefreshRecent();
    }

    /// <summary>Opens a project from the recent list; one that has gone says so rather than failing quietly.</summary>
    private void OpenRecent(string path)
    {
        try
        {
            Open(path);
        }
        catch (Exception error)
        {
            Say($"Could not open {Path.GetFileName(path)}: {error.Message}");
            RefreshRecent();
        }
    }

    /// <summary>The ratios the Crop tool offers, as the Mac build's ratio menu does.</summary>
    private void BuildCropRatios()
    {
        foreach (var (label, ratio) in new (string Label, double? Ratio)[]
                 {
                     ("_Free", null), ("_Original", -1), ("1:_1", 1), ("4:_3", 4.0 / 3), ("3:_4", 3.0 / 4),
                     ("16:_9", 16.0 / 9), ("9:1_6", 9.0 / 16),
                 })
        {
            _cropRatios.Items.Add(Command(label, () => SetCropRatio(ratio)));
        }
        _cropRatios.Items.Add(new Separator());
        _cropRatios.Items.Add(Command("_Apply", ApplyCrop, "Ctrl+Return"));
        _cropRatios.Items.Add(Command("_Cancel", CancelCrop));
    }

    /// <summary>Holds the crop frame to a ratio from now on, and shapes the frame it has to it.</summary>
    private void SetCropRatio(double? ratio)
    {
        _canvas.CropRatio = ratio;
        if (_document is not { } document) return;
        if (ratio is null)
        {
            Say("Crop: any shape");
            return;
        }
        var wanted = ratio == -1 ? CropEdits.OriginalRatio(document) : ratio.Value;
        var frame = _cropFrame ?? CropEdits.Snapped(SKRect.Create(0, 0, document.Width, document.Height));
        _cropFrame = CropEdits.ApplyRatio(frame, wanted);
        ShowCropBox();
        Refresh();
        Say($"Crop: {wanted:0.##} to 1");
    }

    /// <summary>The crop frame follows the tool: the whole canvas until it is dragged.</summary>
    private void ShowCropBox()
    {
        if (_tool != Tool.Crop || _document is not { } document)
        {
            _canvas.CropBox = null;
            return;
        }
        _canvas.CropBox = _cropFrame ?? SKRectI.Create(0, 0, document.Width, document.Height);
    }

    /// <summary>
    /// A crop drag: the frame is snapped to whatever edge is nearby — a frame being moved by its nearest
    /// edge, so it keeps its size, and one being shaped by the edge the pointer is on.
    /// </summary>
    private void CropChanged(SKRectI frame, SKPoint pointer)
    {
        if (_document is not { } document || _tool != Tool.Crop) return;
        var tolerance = TransformSnap.Distance / Math.Max(_canvas.Zoom, 0.0001);
        double? lineX, lineY;
        var middle = new SKPoint((float)frame.MidX, (float)frame.MidY);
        var snapped = _canvas.CropMoving
            ? CropEdits.SnapMove(document, frame, tolerance, out lineX, out lineY)
            : CropEdits.Snap(document, frame, pointer, middle, symmetric: false, tolerance, out lineX, out lineY);
        _cropFrame = CropEdits.Valid(snapped) ? snapped : frame;
        _canvas.SnapLines = (lineX, lineY);
        _canvas.CropBox = _cropFrame;
        Refresh();
    }

    /// <summary>Takes the crop, as one undo step, and lets the frame go.</summary>
    private void ApplyCrop()
    {
        if (_document is not { } document) return;
        if (_cropFrame is not { } frame)
        {
            Say("Drag a frame first");
            return;
        }
        if (!CropEdits.Valid(frame)) { Say("That frame is not one the canvas can be cropped to"); return; }
        _canvas.SnapLines = (null, null);
        Edit("Crop", () => CanvasEdits.Crop(document, frame));
        _cropFrame = null;
        ShowCropBox();
    }

    /// <summary>Lets the crop frame go, leaving the canvas as it is.</summary>
    private void CancelCrop()
    {
        _cropFrame = null;
        _canvas.SnapLines = (null, null);
        ShowCropBox();
        Refresh();
        Say("Crop: let go");
    }

    /// <summary>
    /// The Type tool: a click says where the text goes, and the dialog says what it says. A text layer is
    /// pixels and the style that drew them, so choosing Edit Text on one draws it again rather than painting
    /// over it.
    /// </summary>
    /// <summary>
    /// A click with the Type tool: on a live text layer it joins that layer's words, and anywhere else it
    /// starts new text there. Typing then draws the layer as it goes, so the words appear where they go.
    /// </summary>
    private void TypeHere(SKPoint origin)
    {
        if (_document is not { } document) return;
        // A click inside the text already being typed stays in that session, and puts the caret where it
        // was clicked rather than at the end.
        if (_text is { } session && session.Contains(document, origin))
        {
            if (session.PlaceCaret(document, origin)) ShowTextCaret();
            return;
        }
        CommitText();
        if (document.Layers.FirstOrDefault(layer => layer.Transform.Contains(origin) && layer.Text is not null)
            is { } target)
        {
            _text = TextSession.Editing(target);
            _history.Begin("Edit Text", document, target.ID);
        }
        else
        {
            _text = TextSession.New(new LayerTextStyle
            {
                Content = "",
                FontName = "Arial",
                FontSize = 72,
                Red = _brush.Red,
                Green = _brush.Green,
                Blue = _brush.Blue,
            }, origin);
            _history.Begin("Type", document, Selected);
        }
        _canvas.BeginText();
        ShowTextCaret();
        Refresh();
        Say("Typing — Escape lets it go, Ctrl+Enter keeps it");
    }

    /// <summary>Keys that were typed, put into the text and drawn as they go.</summary>
    private void TypedText(string typed)
    {
        if (_document is not { } document || _text is not { } session) return;
        // A carriage return arrives with Enter as well as the key press, and one newline is enough.
        typed = typed.Replace("\r", "");
        if (typed.Length == 0) return;
        if (!session.Type(document, typed))
        {
            Say("That text could not be drawn: its box is too big for one surface");
            return;
        }
        ShowText();
    }

    /// <summary>The delete key: the last character goes.</summary>
    private void BackspacedText()
    {
        if (_document is not { } document || _text is not { } session) return;
        if (session.Backspace(document)) ShowText();
    }

    /// <summary>The delete key: the character after the caret goes, which does not change the layer's size.</summary>
    private void DeletedText()
    {
        if (_document is not { } document || _text is not { } session) return;
        if (session.Delete(document)) ShowText();
    }

    /// <summary>An arrow or Home or End: the caret moves through the words, and is drawn where it lands.</summary>
    private void MovedTextCaret(TextSession.TextMove move)
    {
        if (_text is not { } session) return;
        if (session.MoveCaret(move)) ShowTextCaret();
    }

    /// <summary>Puts the caret where the text ends and keeps the panel on the layer being typed on.</summary>
    private void ShowText()
    {
        if (_document is not { } document || _text is not { } session) return;
        ShowTextCaret();
        ShowLayers(document);
        if (session.LayerID is { } made)
        {
            var row = _rows.IndexOf(made);
            if (row >= 0) _layers.SelectedIndex = row;
        }
        Refresh();
    }

    private void ShowTextCaret() =>
        _canvas.TextCaret = _document is { } document && _text is { } session ? session.Caret(document) : null;

    /// <summary>Ctrl and Enter: the words are kept, and the whole session is one undo step.</summary>
    private void CommitText()
    {
        if (_text is not { } session) return;
        var typed = session.Content;
        _text = null;
        _canvas.EndText();
        if (_document is not { } document) return;
        var kept = session.Commit(document);
        _history.End(document, kept);
        if (kept is { } layer) Reselect(layer);
        else Refresh();
        if (kept is not null) Say($"Text: {typed.Replace('\n', ' ').Trim().Length} characters");
    }

    /// <summary>Escape: the words go back to what they were, and the history drops the step.</summary>
    private void CancelText()
    {
        if (_text is not { } session) return;
        _text = null;
        _canvas.EndText();
        if (_document is not { } document) return;
        session.Cancel(document);
        _history.End(document, session.LayerID);
        Refresh();
        Say("Text let go");
    }

    /// <summary>Changes the selected text layer's face, size or colour and draws it again.</summary>
    private async Task EditText()
    {
        CommitText();
        if (_document is not { } document || Selected is not { } id) return;
        if (document.Layers.FirstOrDefault(layer => layer.ID == id) is not { Text: { } text } layer)
        {
            Say("That layer is not text");
            return;
        }
        var origin = new SKPoint((float)layer.Transform.X, (float)layer.Transform.Y);
        if (await TextDialog.Ask(this, "Edit text", text.Style) is not { } wanted) return;
        if (_document is not { } current) return;
        _history.Begin("Edit Text", current, id);
        var changed = TextEdits.SetStyle(current, id, wanted);
        _history.End(current, id);
        if (!changed) { Say("That text could not be drawn"); return; }
        Reselect(id);
        Say($"Text: {wanted.Content.Length} characters at {origin.X:0},{origin.Y:0}");
    }

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
    private bool Change(string name, Func<CanvasDocument, bool> change)
    {
        if (_document is not { } document) return false;
        return Edit(name, () => change(document));
    }

    /// <summary>Lets the selection go, and any half-drawn outline with it.</summary>
    private void Deselect()
    {
        _canvas.CancelDraft();
        Change("Deselect", SelectionEdits.Deselect);
    }

    /// <summary>
    /// Select ▸ Layer's Pixels: what the layer shows becomes the selection, in its place on the document —
    /// what Photoshop takes when a layer's thumbnail is command-clicked.
    /// </summary>
    private void SelectLayerPixels()
    {
        if (Selected is not { } id) return;
        if (!Change("Select Layer's Pixels", document => SelectionEdits.SelectLayerPixels(document, id)))
        {
            Say("That layer shows no pixels to take a selection from");
        }
    }

    /// <summary>Select ▸ Mask's Black Areas: what the layer's mask hides becomes the selection.</summary>
    private void SelectMaskBlack()
    {
        if (Selected is not { } id) return;
        if (!Change("Select Mask's Black Areas", document => SelectionEdits.SelectMaskDark(document, id)))
        {
            Say("That layer has no mask, or none of it is hidden");
        }
    }

    /// <summary>The background colour, which the gradient tool draws towards and a fill can use.</summary>
    private SKColor BackgroundColour() => new(
        (byte)Math.Clamp(Math.Round(_gradientBackground.Red * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(_gradientBackground.Green * 255), 0, 255),
        (byte)Math.Clamp(Math.Round(_gradientBackground.Blue * 255), 0, 255));

    /// <summary>
    /// Edit ▸ Fill: what the selection covers takes the colour. A mask is the exception — filling it sets how
    /// much it reveals rather than painting a colour, so its three channels are read as one gray.
    /// </summary>
    private void FillPixels(SKColor colour, string name)
    {
        if (_document is not { } document || Selected is not { } id) return;
        var filled = Edit(_paintingMask ? "Fill Mask" : name, () => _paintingMask
            ? FillEdits.FillMask(document, id,
                (byte)Math.Clamp(Math.Round((colour.Red + colour.Green + colour.Blue) / 3.0), 0, 255))
            : FillEdits.Fill(document, id, colour));
        if (!filled)
        {
            Say("Nothing to fill: that layer holds no pixels, or the selection does not reach it");
        }
    }

    /// <summary>
    /// Edit ▸ Clear: what the selection covers is made transparent. On a mask there is no transparency to
    /// clear, so it is filled with black instead, which is what hiding that part of the layer means.
    /// </summary>
    private void ClearPixels()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (_paintingMask)
        {
            if (!Edit("Clear Mask", () => FillEdits.FillMask(document, id, 0)))
            {
                Say("Nothing to clear: the selection does not reach that mask");
            }
            return;
        }
        if (!Edit("Clear", () => FillEdits.Clear(document, id)))
        {
            Say("Nothing to clear: that layer holds no pixels, or the selection does not reach it");
        }
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
            Tool.Liquify => "Liquify",
            Tool.Smudge => "Smudge",
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
            // Liquify and Smudge work on pixels that are already there: a blank layer has nothing to push.
            if (_tool is Tool.Liquify or Tool.Smudge)
            {
                return WarpEdits.Warp(document, id, stroke, Warp(_tool), settings);
            }
            // A blank layer gets its pixels on the first paint, as the Mac build does.
            BrushEdits.EnsurePixels(document, id);
            return BrushEdits.Paint(document, id, stroke, settings);
        });
    }

    /// <summary>Which of the two push modes the tool in hand is.</summary>
    private static WarpMode Warp(Tool tool) => tool == Tool.Smudge ? WarpMode.Smudge : WarpMode.Liquify;

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
    /// The transform handles follow the selected layer: the tool shows its box while nothing is being
    /// dragged, and nothing at all when there is no layer with pixels to transform.
    /// </summary>
    private void ShowTransformBox()
    {
        if (_tool != Tool.Move || _document is not { } document)
        {
            _canvas.TransformBox = null;
            _canvas.DistortEnabled = false;
            return;
        }
        // One box around everything the transform moves, which for one layer is its own.
        _canvas.TransformBox = TransformEdits.GroupBox(document, SelectedLayers);
        // A corner can be dragged on its own whenever the box stands for something with pixels: a box around
        // several layers resamples each of them into the shape the box is dragged into.
        _canvas.DistortEnabled = _canvas.TransformBox is not null;
    }

    /// <summary>
    /// The pointer took hold of the box: the whole drag is one step in the history, and the layers it moves
    /// are remembered as they are now, so every step of the drag is measured from where it began.
    /// </summary>
    private void TransformStarted()
    {
        if (_document is not { } document || Selected is not { } id) return;
        if (TransformEdits.GroupBox(document, SelectedLayers) is not { } box) return;
        _transforming = id;
        _transformBox = box;
        _transformOriginals = TransformEdits.GroupMembers(document, SelectedLayers)
            .ToDictionary(layer => layer.ID, layer => layer.Transform);
        _history.Begin(_transformOriginals.Count > 1 ? "Transform Layers" : "Transform", document, id);
    }

    /// <summary>
    /// A drag under way: the box the handles worked out goes on the layer, snapped to whatever is nearby,
    /// and the lines it snapped to are drawn along.
    /// </summary>
    private void TransformChanged(LayerTransform draft)
    {
        if (_document is not { } document || _transforming is null) return;
        if (_transformBox is not { } from) return;
        var tolerance = TransformSnap.Distance / Math.Max(_canvas.Zoom, 0.0001);
        // The grid is only a target while it is being shown: snapping to lines that are not there would be
        // a surprise. It is the one target that carries a value rather than a place.
        var placed = TransformEdits.Snap(document, draft, _transformOriginals.Keys, tolerance,
            out var lineX, out var lineY, _snappingOn ? _snapTo : SnapTo.None, _gridVisible ? _grid : null);
        _canvas.SnapLines = (lineX, lineY);
        // Every layer is carried along by the box's own move, so several keep the shape they had.
        TransformEdits.Carry(document, _transformOriginals, from, placed);
        _canvas.TransformBox = placed;
        Refresh();
    }

    private void TransformFinished()
    {
        if (_document is not { } document || _transforming is not { } id) return;
        _transforming = null;
        _transformBox = null;
        _transformOriginals.Clear();
        _canvas.SnapLines = (null, null);
        _history.End(document, id);
        ShowTransformBox();
        Refresh();
    }

    /// <summary>Repaints the canvas and says where the history stands.</summary>
    private void Refresh()
    {
        _canvas.InvalidateVisual();
        UpdateLayerMenu();
        if (_transforming is null) ShowTransformBox();
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
                // The row carries the layer it stands for, so a multi-selection can be read back.
                Tag = record.ID,
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

    /// <summary>
    /// Imports an image as a layer, or as the whole project when none is open. Everything the importer reads
    /// is offered, HEIC and camera RAW included: the file picker lists exactly what it can read.
    /// </summary>
    private async Task ImportImage()
    {
        try
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import an image",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images") { Patterns = [.. ImageImporter.Extensions.Select(e => "*" + e)] },
                    FilePickerFileTypes.All,
                ],
            });
            if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { } path) return;
            if (!ImageImporter.LooksImportable(path))
            {
                Say($"{Path.GetExtension(path)} files are not read; {string.Join(", ", ImageImporter.Extensions)} are");
                return;
            }
            // Decoded once: a camera RAW is minutes of work, so the picture the document gets is this one.
            var image = ImageImporter.Decode(path, Fitting());
            if (_document is not { } document)
            {
                _document = ImageImporter.NewDocument(image);
                _canvas.Document = _document;
                _projectPath = null;
                // Nothing is saved yet, so there is no folder to watch.
                WatchProject();
                _history.Reset();
                ShowLayers(_document);
                Say($"{Path.GetFileName(path)} — {_document.Width} by {_document.Height}, " +
                    $"{_document.Layers.Count} layer, not saved yet");
                Refresh();
                return;
            }
            var origin = new SKPoint(
                (float)((document.Width - image.Width) / 2.0), (float)((document.Height - image.Height) / 2.0));
            _history.Begin("Import image", document, Selected);
            document.Layers.Add(new ImageLayer(Guid.NewGuid(), image,
                new LayerTransform(origin.X, origin.Y, image.Width, image.Height), image.Name));
            _history.End(document, Selected);
            Reselect(document.Layers[^1].ID);
            Say($"Imported {Path.GetFileName(path)} at {image.Width} by {image.Height}");
        }
        catch (Exception error)
        {
            Say($"Could not import that image: {error.Message}");
        }
    }

    /// <summary>The canvas an SVG should be drawn to fit, if one is open.</summary>
    private SKSizeI? Fitting() => _document is { } document ? new SKSizeI(document.Width, document.Height) : null;

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
            RefreshTabs();
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
            // A save is the app's own writing, so the watch takes what is on disk now as what it holds: the
            // folder is only worth watching for what someone else writes afterwards.
            _watch = ProjectWatch.For(path);
            WatchProject();
            NoteRecent(path);
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

    /// <summary>
    /// File ▸ Export JPEG: the flattened document written at a quality that is asked for. A JPEG has to be
    /// made whole, so a canvas too big to hold is refused rather than quietly written wrong.
    /// </summary>
    private async Task ExportJpeg()
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
                Title = "Export JPEG",
                SuggestedFileName = "Compositor export.jpg",
                DefaultExtension = "jpg",
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            if (await QualityDialog.Ask(this) is not { } quality) return;
            if (!ImageWriter.Write(document, path, quality))
            {
                Say("That canvas is too large to write as one JPEG.");
                return;
            }
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
