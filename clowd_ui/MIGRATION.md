# Clowd WPF → Avalonia 11.3.13 Migration Plan

Authoritative plan for porting the Clowd editor UI from `/Users/caesay/Source/Clowd/src` (WPF) to
`/Users/caesay/Source/clowd-rust/clowd_ui` (Avalonia 11.3.13, net8.0). This document supersedes the
individual area surveys; where a survey and this document disagree, **this document wins**.

Scope: **editor fidelity is paramount** (EditorWindow + Clowd.Drawing must match the WPF app visually and
behaviorally). The shell (MainWindow, settings, tray, sessions) is a **practical adaptation**. Capture,
video recording, upload backends, LiveDraw, and printing are **out of scope** (stubbed or dropped).

Both projects are already referenced by `/Users/caesay/Source/clowd-rust/Clowd.slnx`:

```
clowd_ui/Clowd.Drawing/Clowd.Drawing.csproj
clowd_ui/Clowd.Ui/Clowd.Ui.csproj
```

`Clowd.Ui` references `Clowd.Drawing`. There is **no Clowd.Shared project**: all shared types that
Clowd.Drawing needs (INPC base, RelayCommand, SimpleKeyGesture, ToolType, the settings layer, upload
interfaces) live in `Clowd.Drawing/Shared/` so that Clowd.Drawing builds standalone and Clowd.Ui consumes
them via the project reference. Original namespaces are preserved (`Clowd`, `Clowd.Config`,
`Clowd.UI.Helpers`, `Clowd.Drawing`, `Clowd.Drawing.Graphics`, `Clowd.Drawing.Tools`, `Clowd.UI`,
`Clowd.UI.Controls`, `Clowd.UI.Converters`, `Clowd.UI.Dialogs`) to keep ported code diffable against WPF.

---

## 1. Target structure

### 1.1 clowd_ui/Clowd.Drawing/Clowd.Drawing.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <RootNamespace>Clowd.Drawing</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.13" />
    <PackageReference Include="RT.Util.Core" Version="2.0.1719" />
    <PackageReference Include="RT.Serialization.Xml" Version="2.0.1719" />
    <PackageReference Include="RT.Serialization.Binary" Version="2.0.1719" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="Cursors\*.cur" />
  </ItemGroup>
</Project>
```

Notes: no DependencyPropertyGenerator (styled properties are hand-written), no WriteableBitmapEx, no
System.Drawing.Common, no Vanara, no NLog. RT.* packages are netstandard2.0 and run on net8 everywhere.

```
clowd_ui/Clowd.Drawing/
├── Clowd.Drawing.csproj
│
├── Shared/                              # formerly Clowd.Shared (namespaces preserved)
│   ├── SimpleNotifyObject.cs            # Clowd.SimpleNotifyObject + DictionaryNotifyObject
│   ├── RelayCommand.cs                  # Clowd.UI.Helpers.RelayCommand (plain ICommand, see §2.4)
│   ├── SimpleKeyGesture.cs              # Clowd.SimpleKeyGesture (Avalonia Key/KeyModifiers)
│   ├── ToolType.cs                      # Clowd.ToolType enum (own file, moved out of SettingsEditor)
│   ├── EmbeddedResource.cs              # Clowd.EmbeddedResource (manifest stream loader)
│   ├── Upload.cs                        # IUploadProvider, SupportedUploadType, UploadResult (interfaces only)
│   └── Config/
│       ├── CategoryBase.cs              # Clowd.Config.CategoryBase (child-INPC propagation)
│       ├── AutoDictionary.cs            # Clowd.AutoDictionary<K,V>
│       ├── TimeOption.cs                # TimeOption + TimeOptionUnit
│       ├── ClassifySubstitutes.cs       # IClassifySubstitute for Avalonia Color/Point/Rect/Size/PixelRect,
│       │                                #   FontStyle/FontWeight/FontStretch, Key/KeyModifiers; static
│       │                                #   ClassifySubstitutes.EnsureRegistered() — see §2.9
│       ├── SettingsRoot.cs              # singleton, ClassifyXml file persistence
│       ├── SettingsGeneral.cs           # trimmed (LastSavePath, ConfirmClose, ClientId + compat fields)
│       ├── SettingsCapture.cs           # ScreenshotWithCursor, FilenamePattern, OpenSavedInExplorer, …
│       ├── SettingsEditor.cs            # SettingsEditor + SavedToolSettings
│       ├── SettingsHotkey.cs            # SettingsHotkey + GlobalTrigger (stub, see §6)
│       ├── SettingsUpload.cs            # SettingsUpload + UploadProviderInfo (no providers ship)
│       └── SettingsVideo.cs             # empty CategoryBase (deserialization compat)
│
├── DrawingCanvas.cs                     # Canvas subclass; 20 styled properties; tool routing; zoom/pan
├── GraphicCollection.cs                 # ICollection<GraphicBase> + GraphicVisual management
├── GraphicVisual.cs                     # internal Control, Render → graphic.Draw; Effect = drop shadow
├── CheckeredBackground.cs               # internal Control, procedural checker render (the _clickable)
├── UndoManager.cs                       # ClassifyXml snapshot + XML-diff merge (verbatim port)
├── StateChangedEventArgs.cs             # { XElement State }
├── Attributes.cs                        # [Flags] Skill, GraphicDescAttribute
├── HelperFunctions.cs                   # DefaultCursor, CreateRectSafe(Rounded), SnapPointToCommonAngle
├── DpiScale.cs                          # readonly record struct DpiScale(double DpiScaleX, double DpiScaleY)
├── RectExtensions.cs                    # IsEmptyRect(this Rect), IsEmptyRect(this PixelRect), ToRect(PixelRect)
├── MatrixHelper.cs                      # Rotation(deg, center), ScaleAt(sx, sy, center)
├── ToolTypeConverter.cs                 # Avalonia IValueConverter
├── DrawingCanvasException.cs
├── CursorResources.cs                   # .cur parser + Cursor(Bitmap, PixelPoint) cache, ScalingChanged flush
│
├── Cursors/
│   ├── CursorResources.Table.cs         # 48 static properties + GetResizeCursor(int) (copied table)
│   └── *.cur                            # 48 files copied verbatim from WPF repo (embedded resources)
│
├── Graphics/
│   ├── GraphicBase.cs
│   ├── GraphicRectangle.cs
│   ├── GraphicFilledRectangle.cs
│   ├── GraphicEllipse.cs
│   ├── GraphicLine.cs
│   ├── GraphicArrow.cs
│   ├── GraphicPolyLine.cs
│   ├── GraphicText.cs
│   ├── GraphicCount.cs
│   ├── GraphicImage.cs
│   └── GraphicSelectionRectangle.cs
│
├── Tools/
│   ├── ToolBase.cs                      # + SnapMode enum + PointerState struct (see §2.3)
│   ├── ToolPointer.cs
│   ├── ToolDraggable.cs
│   ├── ToolPolyLine.cs
│   ├── ToolText.cs
│   ├── ToolCount.cs
│   ├── ToolSelection.cs
│   ├── ToolPixelate.cs
│   └── ToolPanning.cs
│
└── Curves/
    ├── LICENSE.txt                      # keep (burningmime MIT)
    ├── VectorHelper.cs                  # single alias: VECTOR = Avalonia.Vector, FLOAT = double
    ├── CubicBezier.cs
    ├── CurveBuilder.cs
    ├── CurveFit.cs
    ├── CurveFitBase.cs
    ├── CurvePreprocess.cs
    ├── Spline.cs
    └── SplineBuilder.cs
```

**Not ported from Clowd.Drawing (dead code, verified zero references):** SerializationHelper.cs,
FontConversions.cs (folded into ClassifySubstitutes), OriginIndicator.cs, Cursors.targets (replaced by
csproj wildcard), the Vanara Win32 cursor-cache message window.

### 1.2 clowd_ui/Clowd.Ui/Clowd.Ui.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <RootNamespace>Clowd.UI</RootNamespace>
    <ApplicationIcon>Assets\clowd-default.ico</ApplicationIcon>
    <BuiltInComInteropSupport>false</BuiltInComInteropSupport>
    <ApplicationManifest></ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.3.13" />
    <PackageReference Include="Avalonia.Desktop" Version="11.3.13" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.3.13" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="11.3.13" />
    <PackageReference Include="Avalonia.Diagnostics" Version="11.3.13" Condition="'$(Configuration)'=='Debug'" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Clowd.Drawing\Clowd.Drawing.csproj" />
  </ItemGroup>
  <ItemGroup>
    <AvaloniaResource Include="Assets\**" />
  </ItemGroup>
</Project>
```

```
clowd_ui/Clowd.Ui/
├── Clowd.Ui.csproj
├── Program.cs                           # BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)
├── App.axaml / App.axaml.cs             # FluentTheme, merged resources, tray icon, lifetime, single-instance
├── AppStyles.cs                         # AccentColor, brushes, WindowIcon, GetIconElement, ResourceIcon enum
├── SystemThemedWindow.cs                # Window base: icon, Mica hint (Win), CustomUiWindow defaults
│
├── Assets/
│   ├── clowd-default.ico                # copied from /Users/caesay/Source/Clowd/artwork/clowd-default.ico
│   ├── VectorGeometries.axaml           # StreamGeometry resources (verbatim path data)
│   ├── VectorIcons.axaml                # all Icon* keys as StreamGeometry (converted from Path)
│   └── AppResources.axaml               # AccentColorBrush, ApplicationBackgroundColor theme dict, text styles
│
├── Controls/
│   ├── ToolButton.cs / ToolButton.axaml             # ToggleButton; Geometry IconPath; CanToggle
│   ├── CaptureToolButton.cs / CaptureToolButton.axaml
│   ├── SpinnerTextBox.cs / SpinnerTextBox.axaml     # Value/Suffix/DisplayScale redesign (see §2.7)
│   ├── ResetDefaultButton.cs
│   └── DockAndWrapPanel.cs
│
├── Converters/
│   ├── EnumMatchToBooleanConverter.cs   # HasFlag(parse(param)) → bool (replaces all visibility variants)
│   ├── ColorToBrushConverter.cs
│   ├── ColorToTextConverters.cs         # ColorTextHelper + hex/rgb/hsl IValueConverters
│   ├── OnOffBrushConverter.cs
│   └── NumericTypeConverter.cs          # decimal? (NumericUpDown) ↔ int/double settings props
│
├── Behaviors/
│   └── SelectTextOnFocus.cs             # attached property: GotFocus → SelectAll
│
├── Util/
│   ├── HslRgbColor.cs                   # + SimpleNotifyObject reuse from Clowd.Drawing
│   ├── ScreenGeometry.cs                # ScreenRect/ScreenPoint/ScreenSize records ({X,Y,Width,Height})
│   ├── FileSyncObject.cs
│   ├── TrulyObservableCollection.cs
│   ├── DisposableTimer.cs
│   ├── PathConstants.cs                 # SessionData dir, GetDatedFilePath, GetFreePatternFileName
│   ├── CheckerBrushes.cs                # code-generated tiled ImageBrushes: Light(10px), Medium(16px), Swatch
│   └── MutexArgsForwarder.cs            # named Mutex + NamedPipe, length-prefixed JSON (no PipeMethodCalls)
│
├── Session/
│   ├── SessionInfo.cs                   # SessionInfo, SessionOpenEditor, SessionWindow
│   └── SessionManager.cs
│
├── Services/
│   ├── PageManager.cs                   # + IPage, ISettingsPage, IScreenCapturePage/ILiveDrawPage stubs
│   ├── UploadManager.cs                 # stub: UploadSession → notice dialog; GetAvailableProviders → empty
│   └── TasksView.cs                     # ITasksView/ITasksViewItem no-op implementation
│
├── Helpers/
│   └── NiceDialog.cs                    # cross-platform: message/yes-no/color/font/file pickers (see §2.8)
│
├── Dialogs/
│   ├── MessageDialog.axaml(.cs)         # single window backing all NiceDialog prompts
│   ├── FontDialog.axaml(.cs)            # font family list + size + style toggles + preview → SelectedFont
│   └── ColorPicker/
│       ├── ColorWheel.cs                # CPU-generated wheel bitmap (replaces HLSL shader)
│       ├── ColorSlider.cs
│       ├── ColorPaletteItem.cs          # + ColorSelectedEventArgs
│       ├── ColorPalettes.cs             # PaintPalette only, as Avalonia Color[]
│       ├── ColorDialog.axaml(.cs)
│       └── MiniColorDialog.axaml(.cs)
│
├── Editor/
│   └── EditorWindow.axaml(.cs)          # incl. ShowSession / ShowAllPreviouslyActiveSessions
│
└── Main/
    ├── MainWindow.axaml(.cs)            # ListBox nav + ContentControl (replaces WPFUI NavigationFluent)
    ├── SettingsControlFactory.cs
    ├── GlobalTriggerEditor.cs
    └── Pages/
        ├── RecentSessionsPage.axaml(.cs)    # VM-side grouping (no CollectionViewSource)
        ├── GeneralSettingsPage.axaml(.cs)
        ├── AboutPage.axaml(.cs)
        └── UploadsPlaceholderPage.axaml     # "Upload providers are not available in this build."
```

**Not ported from the Clowd app (dropped):** LiveDraw (entire folder), FloatingButtonWindow, DPadControl,
VideoCaptureWindow + all video UI, OutlinedText(.Block), FadingScrollViewer, CornerClippingBorder,
UploadProgressBar, SizeAwareProgressBar, ProgressRing, TransitioningContentControl, SimpleStackPanel
(use StackPanel.Spacing), CustomWindowStyle.xaml, MetroIconTemplate, ResizeThumbStyle, z-depth effects,
Raleway.ttf, all WPFUI/Vanara/Squirrel/Sentry/NLog/Hardcodet/Toolkit-Notifications/DotNetZip/
PipeMethodCalls/Clowd.Clipboard.Wpf dependencies, ProcessWatcher ("watch" arg), analytics.

---

## 2. Foundation contracts (single source of truth)

Agents porting leaf files code against these signatures. All conflicts between surveys are resolved here.

### 2.1 GraphicBase hierarchy (Clowd.Drawing.Graphics)

```csharp
public abstract class GraphicBase : SimpleNotifyObject
{
    protected GraphicBase(Color objectColor, double lineWidth);                 // DropShadowEffect = true
    protected GraphicBase(Color objectColor, double lineWidth, bool dropShadowEffect);

    public string Id { get; set; }                       // default Guid.NewGuid().ToString()
    public virtual Color ObjectColor { get; set; }
    public virtual double LineWidth { get; set; }
    public virtual bool DropShadowEffect { get; set; }
    [ClassifyIgnore] public virtual bool IsSelected { get; set; }
    public abstract Rect Bounds { get; }

    internal const double UnscaledControlSize = 12.0;
    internal const double UnscaledBorderSize = 2.0;
    internal static IBrush HandleBrush  { get; set; }    // default blue (0,0,255); set by DrawingCanvas.HandleColor
    internal static readonly IBrush HandleBrush2;        // white

    internal abstract int HandleCount { get; }
    internal abstract bool Contains(Point point);
    internal abstract void Move(double deltaX, double deltaY);
    internal abstract void MoveHandleTo(Point point, int handleNumber);
    internal abstract Cursor GetHandleCursor(int handleNumber);
    internal abstract Point GetHandle(int handleNumber, DpiScale uiscale);

    internal virtual void Activate(DrawingCanvas canvas) { }
    internal virtual void Normalize() { }
    internal virtual int MakeHitTest(Point point, DpiScale uiscale);  // -1 miss, 0 body, 1..N handle
    internal virtual void Draw(DrawingContext ctx, DpiScale uiscale); // DrawObject + trackers if selected
    internal virtual void DrawObject(DrawingContext ctx);             // export path (no chrome)
    internal void DisconnectFromParent();                             // clears PropertyChanged
}
```

Transform scoping rule (resolves the WPF push-without-pop pattern): rotation is pushed by the **caller of
the shape body**, with `using`-scoped `DrawingContext.PushedState`, and **selection trackers are drawn
inside the same rotated scope** (handles visually rotate with the shape; hit-testing unrotates points).
Concretely `GraphicRectangle.Draw` is:
`using (ctx.PushTransform(MatrixHelper.Rotation(Angle, CenterOfRotation))) { DrawRectangle(ctx); if (IsSelected) DrawTrackers(ctx, uiscale); }`
and `DrawObject` pushes the same rotation around `DrawRectangle` only. `GraphicImage.DrawTransformed`
becomes `void DrawTransformed(DrawingContext ctx, Action<DrawingContext> body, Action<DrawingContext> trackers = null)`
with order rotate → flip → temp-mirror, body inside all three, trackers inside rotate only.

```csharp
public class GraphicRectangle : GraphicBase
{
    public GraphicRectangle(Color objectColor, double lineWidth, Rect rect);
    public GraphicRectangle(Color objectColor, double lineWidth, Rect rect, double angle, bool dropShadowEffect = true);
    public double Left, Top, Right, Bottom { get; set; }   // Set + OnPropertyChanged
    public double Angle { get; set; }
    public Point CenterOfRotation { get; protected set; }
    public virtual Rect UnrotatedBounds { get; }            // CreateRectSafeRounded (whole-pixel)
    internal Point ApplyRotation(Point p);  internal Point UnapplyRotation(Point p);
    internal virtual void DrawRectangle(DrawingContext ctx);          // subclass shape body
    // HandleCount = 9; handle 9 = rotation, at (Right + 32*uiscale.DpiScaleX, yCenter)
    // GetHandleCursor: 9→Rotate; 1-8 → CursorResources.GetResizeCursor(((int)((45*h + Angle + 272.5)/5)) % 36)
}

public class GraphicFilledRectangle : GraphicRectangle  { public GraphicFilledRectangle(Color c, Rect r, double angle = 0); }
public class GraphicEllipse : GraphicRectangle          { public GraphicEllipse(Color c, double lw, Rect r, double angle = 0); }

public class GraphicLine : GraphicBase
{
    public GraphicLine(Color objectColor, double lineWidth, Point start, Point end);
    public Point LineStart { get; set; }  public Point LineEnd { get; set; }
    // HandleCount = 2 (1=start, 2=end); cursor SizeAll; grab width max(LineWidth, 8)
}
public class GraphicArrow : GraphicLine { public GraphicArrow(Color c, double lw, Point start, Point end); }

public class GraphicPolyLine : GraphicRectangle
{
    public GraphicPolyLine(Color objectColor, double lineWidth, Point start);
    internal void BeginDrawing();  internal void AddPoint(Point p);  internal void EndDrawing(bool updateBounds);
    // EndDrawing: CurvePreprocess.Linearize(points, 8) → CurveFit.Fit(linearized, 2) → StreamGeometry
}

public class GraphicText : GraphicRectangle
{
    public const int TextPadding = 15;
    public GraphicText(DrawingCanvas canvas, Point position);                    // pastel cycle, ±4° random
    public GraphicText(Color objectColor, double lw, Point pos, double angle = 0, string body = null);
    [ClassifyIgnore] public bool Editing { get; set; }
    public string Body { get; set; }                       // SetAndNormalize
    public string FontName { get; set; }                   // default "Segoe UI"
    public double FontSize { get; set; }                   // default 12
    public FontStyle FontStyle { get; set; }  public FontWeight FontWeight { get; set; }
    public FontStretch FontStretch { get; set; }
    // HandleCount = 1 (rotation, delegates to base handle 9); Activate → canvas.ToolText.CreateTextBox(this, canvas, false)
}
public class GraphicCount : GraphicText
{
    public GraphicCount(DrawingCanvas canvas, Point pos, string body = null);    // default "#"
    public GraphicCount(Color c, double lw, Point pos, string body = null);
}

public class GraphicImage : GraphicRectangle
{
    public GraphicImage(string imageFilePath, Size imageSize);
    public GraphicImage(string imageFilePath, Rect displayRect, PixelRect crop, double angle = 0,
                        int flipX = 1, int flipY = 1, string cursorFilePath = default,
                        PixelRect cursorPosition = default, bool cursorVisible = false);
    public bool Editing { get; }                 // crop mode; PropertyChanged("Editing") raised
    public bool HasCursor { get; }
    public int BitmapPixelWidth { get; }  public int BitmapPixelHeight { get; }
    public string BitmapFilePath { get; set; }  public string CursorFilePath { get; set; }
    public PixelRect CursorPosition { get; set; }  public bool CursorVisible { get; set; }
    public PixelRect Crop { get; set; }  public int FlipX { get; set; }  public int FlipY { get; set; }
    public Size OriginalSize { get; set; }
    public ObscuredShape[] ObscuredShapes { get; set; }
    public record struct ObscuredShape(Point P0, Point P1, Point P2, Point P3, double BlurRadius);
    internal void AddObscuredArea(Rect rect, double blurRadius);     // double, NOT int
    // Activate toggles crop mode; EndCrop → canvas.AddCommandToHistory(false); IsSelected=false ends crop
}

internal class GraphicSelectionRectangle : GraphicRectangle { public GraphicSelectionRectangle(Rect rect); }
```

**Classify field names** (serialization contract, keep exact): GraphicBase `_id,_objectColor,_lineWidth,
_dropShadowEffect`; +Rectangle `_centerOfRotation,_left,_top,_right,_bottom,_angle`; +Line
`_lineStart,_lineEnd`; +PolyLine `_points`; +Text `_body,_fontName,_fontSize,_fontStyle,_fontWeight,
_fontStretch`; +Image `_cursorFilePath,_cursorPosition,_cursorVisible,_bitmapFilePath,_scaleX,_scaleY,
_crop,_originalSize,_obscuredShapes`. `[ClassifyIgnore]` on all transient caches.

### 2.2 DrawingCanvas (Clowd.Drawing)

```csharp
public class DrawingCanvas : Canvas
{
    // Styled properties — hand-written AvaloniaProperty.Register; names/defaults exact;
    // TwoWay default binding mode where marked.
    public ToolType Tool { get; set; }                       // ctor sets Pointer
    public Color ArtworkBackground { get; set; }
    public double LineWidth { get; set; }                    // = 2d, TwoWay
    public Color ObjectColor { get; set; }                   // TwoWay
    public double ObjectAngle { get; set; }                  // TwoWay
    public bool ObjectColorAuto { get; set; }                // TwoWay
    public bool ObjectCursorVisible { get; set; }            // TwoWay
    public Color HandleColor { get; set; }                   // setter → GraphicBase.HandleBrush
    public string TextFontFamilyName { get; set; }           // = "Tahoma", TwoWay
    public FontStyle TextFontStyle { get; set; }             // TwoWay
    public FontWeight TextFontWeight { get; set; }           // TwoWay
    public FontStretch TextFontStretch { get; set; }         // TwoWay
    public double TextFontSize { get; set; }                 // = 12d, TwoWay
    public double BlurRadius { get; set; }                   // = 8d, TwoWay
    public bool IsPanning { get; set; }
    public Point ContentOffset { get; set; }
    public double ContentScale { get; set; }                 // = 1d
    public GraphicCollection GraphicsList { get; set; }
    public Skill SubjectSkill { get; private set; }
    public string SubjectType { get; private set; }
    public string SubjectName { get; private set; }

    public GraphicBase this[int index] { get; }
    public int Count { get; }   public int SelectedCount { get; }
    public event EventHandler<StateChangedEventArgs> StateUpdated;
    public DpiScale CanvasUiElementScale { get; }            // (dpi*(1/ContentScale), same)

    // Commands (RelayCommand): SelectAll(Ctrl+A), UnselectAll(Esc), Delete(Del), DeleteAll,
    // MoveToFront(Home), MoveToBack(End), MoveForward(Ctrl+Home), MoveBackward(Ctrl+End),
    // ResetRotation, Undo(Ctrl+Z), Redo(Ctrl+Y), ZoomPanAuto("Ctrl+0"), ZoomPanActualSize("Ctrl+1",
    // double param), CropImage.
    public RelayCommand CommandSelectAll, CommandUnselectAll, CommandDelete, CommandDeleteAll,
        CommandMoveToFront, CommandMoveToBack, CommandMoveForward, CommandMoveBackward,
        CommandResetRotation, CommandUndo, CommandRedo, CommandZoomPanAuto,
        CommandZoomPanActualSize, CommandCropImage;

    public void AddGraphic(GraphicBase g);   public void AddGraphics(GraphicBase[] gs);
    public void SetBackgroundColor(Color c);
    public void SelectAll();  public void UnselectAll();  public void UnselectAllExcept(params GraphicBase[] keep);
    public void Delete();     public void DeleteAll();
    public void Nudge(int dx, int dy);                       // mergable history step
    public void CancelCurrentOperation();
    public void RestoreState(XElement state);                // ClearHistory(state)
    public void Undo();  public void Redo();
    public void ZoomPanFit();  public void ZoomPanActualSize(double zoom = 1);
    public void ZoomPanCenter();  public void ZoomPanAuto();
    public void UpdateScaleTransform();  public void UpdateClickableSurface();
    public Bitmap DrawGraphicsToBitmap();                    // RenderTargetBitmap; shadows preserved (§2.10)
    public bool IsToolDragActive { get; }                    // true while _isToolMouseDown (EditorWindow guards)

    // Internal surface used by Tools/Graphics:
    internal ToolPointer ToolPointer { get; }   internal ToolText ToolText { get; }
    internal void AddCommandToHistory(bool mergable);
    internal void InternalAddVisualChild(Visual v);          // VisualChildren insert (tools/overlays)
    internal void InternalRemoveVisualChild(Visual v);
    internal void CaptureMouse(IPointer pointer);            // tracks _capturedPointer
    internal void ReleaseMouseCapture();
    internal bool IsMouseCaptured { get; }
    internal void RequeryCommands();                         // RaiseCanExecuteChanged over all 14 commands
}
```

Visual tree order (bottom→top): `_clickable` (CheckeredBackground, Children[0]) → `_artworkBackground`
(tiny Control filling `GraphicsList.ContentBounds` with `ArtworkBackground`, VisualChildren index 1) →
GraphicVisuals (index `i + 2`, list order = z-order) → remaining `Children` (e.g. ToolText's TextBox
overlay). No `VisualChildrenCount`/`GetVisualChild` overrides — `VisualChildren` list order is managed
directly. `RenderTransform = TransformGroup{ScaleTransform, TranslateTransform}` with
`RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative)` (**mandatory** — Avalonia
defaults to center). `dpiZoom = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0`.

### 2.3 ToolBase + PointerState (Clowd.Drawing.Tools, all internal)

```csharp
internal enum SnapMode { None = 0, Diagonal = 1, All = 2 }

// Carries everything tools need; DrawingCanvas caches the last PointerState so Shift up/down can replay
// a synthetic move (replaces WPF's synthesized MouseMove). DrawingCanvas updates Modifiers on key events.
internal readonly record struct PointerState(
    Point Position,            // relative to canvas
    KeyModifiers Modifiers,
    bool LeftPressed, bool MiddlePressed, bool RightPressed,
    IPointer Pointer)          // null for synthetic replays (capture state is unchanged then)
{
    public static PointerState From(PointerEventArgs e, Visual relativeTo);
}

internal abstract class ToolBase
{
    protected ToolBase(Func<Cursor> cursorFn, SnapMode snapMode = SnapMode.None);
    public void OnMouseDown(DrawingCanvas canvas, PointerState s, int clickCount);
    public void OnMouseMove(DrawingCanvas canvas, PointerState s);
    public void OnMouseUp(DrawingCanvas canvas, PointerState s);
    protected virtual void OnMouseDownImpl(DrawingCanvas canvas, Point pt) { }
    protected virtual void OnMouseMoveImpl(DrawingCanvas canvas, Point pt) { }
    protected virtual void OnMouseUpImpl(DrawingCanvas canvas) { }
    public virtual void AbortOperation(DrawingCanvas canvas) { }
    public virtual void SetCursor(DrawingCanvas canvas);          // canvas.Cursor = CursorFn()
    protected Point LastMouseDownPt, LastMouseMovePt;
    // Snap gate: (s.Modifiers & KeyModifiers.Shift) != 0  — left/right shift both snap (fixes WPF bug).
    // OnMouseUp always: re-run move, release capture, OnMouseUpImpl, Tool = Pointer, Cursor = Default.
}

internal class ToolPointer : ToolBase
{
    public enum SelectionMode { None, Move, HandleDrag, GroupSelection }
    public SelectionMode Selection { get; }
    public GraphicBase MakeHitTest(DrawingCanvas canvas, Point point, out int handleNumber);
    // Rect.Empty sentinels in GetTransformedRect/ScaleRectToAspect/TranslateDestAroundHandle → Rect? null.
    // Drag bookkeeping stays screen-space: PixelPoint via canvas.PointToScreen / PointToClient.
}

internal class ToolText : ToolBase
{
    public ToolText(Func<Cursor> cursorFn = null, SnapMode snapMode = SnapMode.None);
    public void CreateTextBox(GraphicText graphic, DrawingCanvas canvas, bool newGraphic = false);
}

internal class ToolDraggable<T> : ToolBase where T : GraphicBase
{
    public ToolDraggable(Func<Cursor> cursor, Func<Point,T> create, Action<Point,T> update,
                         Action<T> end = null, SnapMode snapMode = SnapMode.None);
}
internal class ToolPolyLine : ToolBase { }
internal abstract class ToolSelection : ToolBase { protected abstract void MakeSelection(DrawingCanvas c, Rect area); }
internal class ToolPixelate : ToolSelection { }
internal class ToolCount : ToolText { }
internal class ToolPanning : ToolBase { }   // does NOT revert to Pointer on mouse-up; system SizeAll cursor
```

### 2.4 RelayCommand / SimpleKeyGesture (Clowd.UI.Helpers / Clowd)

```csharp
public delegate void RelayExecute(object parameter);
public delegate bool RelayCanExecute(object parameter);

public class RelayCommand : SimpleNotifyObject, ICommand
{
    public RelayExecute Executed { get; set; }
    public RelayCanExecute CanExecute { get; set; }
    public string Text { get; set; }                 // may contain WPF '_' mnemonic; strip for Avalonia Header
    public string GestureText { get; set; }          // explicit override (e.g. "Ctrl+0")
    public SimpleKeyGesture Gesture { get; set; }
    public object Icon { get; set; }
    public event EventHandler CanExecuteChanged;
    public void RaiseCanExecuteChanged();
    bool ICommand.CanExecute(object p);  void ICommand.Execute(object p);
    public KeyBinding CreateKeyBinding();            // Avalonia KeyBinding; null if Gesture is null or bare
    public MenuItem CreateMenuItem();                // Header=Text(stripped), InputGesture, Command=this
    public bool IsBareGesture { get; }               // Gesture != null && Modifiers == None
}

public class SimpleKeyGesture : IEquatable<SimpleKeyGesture>
{
    public SimpleKeyGesture(Key key, KeyModifiers modifiers = KeyModifiers.None);
    public Key Key { get; }  public KeyModifiers Modifiers { get; }
    public KeyGesture ToKeyGesture();
    public override string ToString();   // "Ctrl+Z" style: Control→Ctrl, Delete→Del, Escape→Esc, Snapshot→PrtScr
}
```

There is **no BareKeyBinding class**. Bare (modifier-less) gestures — Escape, Delete, Home, End, and the
single-letter tool shortcuts — are routed exclusively by EditorWindow's window-level tunnel `KeyDown`
handler, which returns immediately when `e.Source is TextBox` or `e.KeyModifiers != KeyModifiers.None`
(except the arrow-nudge path which allows Ctrl). Modifier-carrying command gestures become
`Window.KeyBindings`. On macOS, every Ctrl gesture is also registered with `KeyModifiers.Meta`.

### 2.5 GraphicCollection / GraphicVisual / UndoManager

```csharp
public class GraphicCollection : SimpleNotifyObject, ICollection<GraphicBase>
{
    public GraphicCollection(DrawingCanvas parent);
    public int Count { get; }  public int VisualCount { get; }
    public GraphicBase[] SelectedItems { get; }      // INPC "SelectedItems"
    public Rect ContentBounds { get; }               // INPC "ContentBounds"; default(Rect) when empty
    internal DpiScale Dpi { get; set; }              // setter invalidates selected graphics' visuals
    public GraphicBase this[int index] { get; }
    public void Add(GraphicBase g);  public void Insert(int index, GraphicBase g);
    public bool Remove(GraphicBase g);  public void RemoveAt(int index);  public void Clear();
    public int IndexOf(GraphicBase g);  public bool Contains(GraphicBase g);
    public GraphicBase[] GetGraphicList(bool selectedOnly);   // excludes GraphicSelectionRectangle
    public Bitmap DrawGraphicsToBitmap(IBrush background);    // see §2.10
    // PropertyChanged names raised: exactly "Count", "SelectedItems", "ContentBounds".
}

internal sealed class GraphicVisual : Control      // one per graphic, child of DrawingCanvas.VisualChildren
{
    public GraphicBase Graphic { get; }
    public bool ObjectOnly { get; set; }            // export mode: Render calls DrawObject instead of Draw
    // Render(ctx) => Graphic.Draw(ctx, collection.Dpi) | Graphic.DrawObject(ctx)
    // Effect = new DropShadowEffect { OffsetX = 1.414, OffsetY = 1.414, BlurRadius = 5,
    //          Color = Color.FromArgb(0x80, 0, 0, 0) } when Graphic.DropShadowEffect, else null.
    // graphic.PropertyChanged → InvalidateVisual() (+ effect sync, bounds invalidation via collection).
}

internal class UndoManager
{
    public UndoManager(DrawingCanvas canvas);
    public bool CanUndo { get; }  public bool CanRedo { get; }
    public event EventHandler<StateChangedEventArgs> StateChanged;
    public void ClearHistory(XElement initial = null);
    public void AddCommandStep(bool mergable);
    public void Undo();  public void Redo();
    internal static SortedSet<string> GetChangedXmlNodes(XElement prev, XElement next);
}
public class StateChangedEventArgs : EventArgs { public XElement State { get; } }
```

### 2.6 Settings layer (Clowd / Clowd.Config — lives in Clowd.Drawing/Shared)

```csharp
public enum ToolType { None, Pointer, Rectangle, FilledRectangle, Ellipse, Line, Arrow, PolyLine, Text, Count, Pixelate }

[Flags] public enum Skill { None=0, Color=1, AutoColor=2, Stroke=4, Font=8, Angle=16,
                            CanvasBackground=32, Crop=64, Cursor=128, BlurRadius=256 }
public class GraphicDescAttribute : Attribute { public GraphicDescAttribute(string name); public string Name; public Skill Skills; }

public class SavedToolSettings : SimpleNotifyObject
{
    public bool AutoColor { get; set; } = true;
    public Color ObjectColor { get; set; } = Colors.Red;
    public double LineWidth { get; set; } = 2d;
    public string FontFamily { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 12d;
    public FontStyle FontStyle { get; set; } = FontStyle.Normal;
    public FontWeight FontWeight { get; set; } = FontWeight.Normal;
    public FontStretch FontStretch { get; set; } = FontStretch.Normal;
    public double BlurRadius { get; set; } = 8d;
}

public class SettingsEditor : CategoryBase
{
    public bool RestoreSessionsOnClowdStart { get; set; } = true;
    public Color CanvasBackground { get; set; } = Colors.Transparent;
    public int StartupPadding { get; set; } = 30;
    public AutoDictionary<ToolType, SavedToolSettings> Tools { get; set; }
    public TimeOption DeleteSessionsAfter { get; set; }      // 30 Days
}

public class SettingsRoot : SimpleNotifyObject
{
    public static SettingsRoot Current { get; }
    public SettingsGeneral General { get; }  public SettingsHotkey Hotkeys { get; }
    public SettingsCapture Capture { get; }  public SettingsEditor Editor { get; }
    public SettingsUpload Uploads { get; }   public SettingsVideo Video { get; }
    public static void LoadDefault();  public static void CreateNew();  public void Save();
    // file: Environment.SpecialFolder.ApplicationData / "Clowd" / "Clowd.Settings.xml" (DEBUG variant in debug)
}

public class GlobalTrigger : SimpleNotifyObject   // STUB: no OS hotkey registration in this migration
{
    public SimpleKeyGesture KeyGesture { get; set; }
    public bool IsRegistered => false;
    public string Error => "Global hotkeys are not supported in this build.";
    public string KeyGestureText { get; }
    public event EventHandler TriggerExecuted;     // never fires
    public static bool IsPaused { get; set; }
}
```

### 2.7 Clowd.Ui controls

```csharp
public class ToolButton : ToggleButton
{
    public Geometry IconPath { get; set; }          // StyledProperty; **type changed from UIElement to Geometry**
    public bool CanToggle { get; set; }             // default false; Toggle() suppressed when false
}

public class CaptureToolButton : Button
{
    public Geometry IconPath { get; set; }  public Geometry IconPathAlternate { get; set; }
    public Control Overlay { get; set; }
    public bool PulseBackground { get; set; }  public bool ShowAlternateIcon { get; set; }
    public bool ShowHover { get; set; } = true;  public double IconSize { get; set; } = 26;
    public string Text { get; set; }  public bool Primary { get; set; }
    public bool IsDragHandle { get; set; }
    public List<SimpleKeyGesture> Gestures { get; set; }
    public event EventHandler Executed;             // Click + gesture match
    public bool ProcessKeyState(KeyModifiers modifiers, Key key);
}

public class SpinnerTextBox : TemplatedControl     // REDESIGN (replaces WPF Text+reflection hack)
{
    public double Value { get; set; }               // StyledProperty, TwoWay default
    public double SpinAmount { get; set; } = 1;
    public double? Min { get; set; }  public double? Max { get; set; }
    public bool SnapToWholeNumber { get; set; }
    public string Suffix { get; set; }              // "%", "px", "°" — display only
    public double DisplayScale { get; set; } = 1;   // zoom uses 100
    // Display = $"{Math.Round(Value*DisplayScale, 2)} {Suffix}". Commit on Enter/LostFocus (parse, strip
    // suffix, /DisplayScale; revert on failure). Spin: snap→±SpinAmount→wrap iff BOTH Min&Max set
    // (up: v>Max → Min+(v-Max); down: v<Min → Max+(v-Min)), else clamp. Wheel + Up/PageUp/Down/PageDown.
}
// EditorWindow bindings: Zoom {ContentScale, DisplayScale=100, Suffix=%, Min=0.1, SpinAmount=0.1};
// Stroke {LineWidth, Suffix=px, Min=0, Snap}; Angle {ObjectAngle, Suffix=°, Min=-180, Max=180,
// SpinAmount=10, Snap — wraps}; Blur {BlurRadius, Suffix=px, Min=1, Snap}.
// The ZoomScale/StringPixel/Angle converters are NOT ported.

public class ResetDefaultButton : Border
{
    public object CurrentValue { get; set; }        // StyledProperty, TwoWay default
    public object DefaultValue { get; set; }        // CLR
    // 10x10 circle #6AB1EB; IsVisible = !isDefault (ref → string → Convert.ToDouble equality cascade);
    // PointerPressed → CurrentValue = DefaultValue.
}

public class DockAndWrapPanel : Panel
{
    public Orientation Orientation { get; set; }    // Horizontal
    public double ItemWidth { get; set; }  public double ItemHeight { get; set; }   // NaN
    public static bool GetDockToEnd(Control c);  public static void SetDockToEnd(Control c, bool v);
    // Verbatim UVSize math; docked-line right-alignment only when single line && cross item size set.
}
```

### 2.8 Color picker + NiceDialog (Clowd.Ui)

Exactly the contract from the ColorPicker survey, with these bindings made final:

```csharp
public sealed class HslRgbColor : SimpleNotifyObject, IEquatable<HslRgbColor>
{   // Hue 0-360; Saturation/Lightness/Alpha 0-1; R/G/B int. Avalonia Color in ToColor()/FromColor().
    // Preserve UpdateHsl early-return at lightness 0/1. Statics White/Black/Transparent. Clone(). ==/!=.
}

public partial class MiniColorDialog : UserControl
{
    public HslRgbColor CurrentColor { get; set; }   // StyledProperty
    public bool Realtime { get; set; }              // hides button row when true
    public Action<Color> ColorSelectFn { get; set; }
    public Window ParentWindow { get; set; }
    public event EventHandler Cancelled;
}
public partial class ColorDialog : Window
{
    public ColorDialog(HslRgbColor previousColor = null, bool asDialog = true);
    public HslRgbColor CurrentColor { get; }
    public Task<bool?> ShowAsync(Window owner);     // ShowDialog<bool?> when owner != null, else non-modal+TCS
}
public partial class ColorWheel : Control       { public HslRgbColor CurrentColor { get; set; } }   // CPU bitmap
public partial class ColorSlider : Control      { public double Value, ValueMax; public IBrush SliderBrush;
                                                  public IBrush Background; public CornerRadius CornerRadius; }
public partial class ColorPaletteItem : Control { public Color Color; public bool IsSelected;
                                                  public event EventHandler<ColorSelectedEventArgs> Clicked; }
public class ColorSelectedEventArgs : EventArgs { public Color SelectedColor; public int ClickCount; }

public enum NiceDialogIcon { None, Information, Warning, Error, Shield, ShieldBlueBar, ShieldGrayBar,
                             ShieldWarningYellowBar, ShieldErrorRedBar, ShieldSuccessGreenBar }
public static class NiceDialog
{
    public static Task ShowNoticeAsync(Visual parent, NiceDialogIcon icon, string content, string mainInstruction = null);
    public static Task<bool> ShowPromptAsync(Visual parent, NiceDialogIcon icon, string content, string mainInstruction, string promptTxt);
    public static Task<bool> ShowYesNoPromptAsync(Visual parent, NiceDialogIcon icon, string content, string mainInstruction = null);
    public static Task<Color> ShowColorPromptAsync(Visual parent, Color initial);   // initial on cancel
    public static void ShowColorViewer(Color? initial = null);                      // non-modal
    public static Task<SelectedFont> ShowFontDialogAsync(Visual parent, string family, double size, FontStyle style, FontWeight weight);
    public class SelectedFont { public string TextFontFamilyName; public double TextFontSize;
                                public FontStyle TextFontStyle; public FontWeight TextFontWeight; }
    public static Task<string[]> ShowSelectFilesDialog(Visual parent, string title = null, string initialDirectory = null,
                                                       bool multiSelect = false, FilePickerFileType[] filter = null);
    public static Task<string> ShowSaveImageDialog(Visual parent, Bitmap bitmap, string directory, string filePattern);
}
```

### 2.9 Classify substitutes (registration owned by Clowd.Drawing)

`ClassifySubstitutes.EnsureRegistered()` is called from the static constructors of `UndoManager` and
`SettingsRoot`. Substitutes (all string-based, WPF-era shapes where free):
`Color ↔ "#AARRGGBB"` (accepts `#RRGGBB`), `Point ↔ "x,y"`, `Size ↔ "w,h"`, `Rect ↔ "x,y,w,h"`,
`PixelRect ↔ "x,y,w,h"` (replaces Int32Rect), `FontStyle/FontWeight/FontStretch ↔ enum name string`
(parse failure → Normal), `Key`/`KeyModifiers ↔ name string`. **The undo XML-diff in
`GetChangedXmlNodes` depends on the element shape — these substitutes keep one element per property,
which preserves merge semantics.** Legacy WPF-file compatibility is a non-goal (§6), but field names are
kept identical anyway.

### 2.10 Export pipeline (DrawGraphicsToBitmap)

To preserve drop shadows in exports (WPF preserved them): build a transient, unrooted `Canvas` sized to
`ceil(ContentBounds)`, add a background `Border` (artwork color) plus one `GraphicVisual { ObjectOnly =
true, Effect = shadow when applicable }` per graphic offset by `(-bounds.Left, -bounds.Top)`; `Measure` +
`Arrange` it; render with `new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96))` via
`rtb.Render(canvas)`. WP15 validates this on macOS; if `RenderTargetBitmap.Render` of unrooted visuals
proves unreliable, the documented fallback is direct `CreateDrawingContext()` + `DrawObject` per graphic
(losing shadows in exports only).

### 2.11 Session / shell contracts (Clowd.Ui)

```csharp
public class SessionManager
{
    public static SessionManager Current { get; }
    public TrulyObservableCollection<SessionInfo> Sessions { get; }
    public SessionInfo CreateNewSession();  public SessionInfo GetSessionFromPath(string path);
    public void OpenSession(SessionInfo s);   // EditorWindow.ShowSession
    public void DeleteSession(SessionInfo s); // throws if OpenEditor != null
    public void CopySession(SessionInfo s);   // clipboard: "image/png" bytes of PreviewImgPath
    public string GetNextSessionDirectory();
}
public class SessionInfo : FileSyncObject
{   // JSON keys exact (shared with Rust capture side): CreatedUtc, Name, LastModifiedUtc, PreviewImgPath,
    // DesktopImgPath, CursorImgPath, CursorPosition, CroppedRect, OriginalBounds (ScreenRect {X,Y,Width,Height}),
    // OpenEditor, Windows[], UploadFileKey, UploadUrl, UploadProgress, GraphicsStream (obsolete).
}
public sealed class SessionOpenEditor { public Guid? VirtualDesktopId; public bool IsTopMost, IsMinimized, IsMaximized; public ScreenRect RestorePosition; }
public sealed class SessionWindow { public string Caption, Class, ImgPath; public bool Selected; public int Id; public ScreenRect Position; }

public partial class EditorWindow : SystemThemedWindow
{
    public EditorWindow(SessionInfo session);
    public static void ShowSession(SessionInfo session);          // null → CreateNewSession
    public static void ShowAllPreviouslyActiveSessions();
}

public enum SettingsPageTab { RecentSessions, SettingsGeneral, SettingsHotkeys, SettingsCapture,
                              SettingsEditor, SettingsUploads, SettingsVideo, About }
public interface IPage { event EventHandler Closed; void Close(); }
public interface ISettingsPage : IPage { void Open(SettingsPageTab? selectedTab = null); }
public interface IScreenCapturePage : IPage { void Open(ScreenRect captureArea); }   // stub impl logs
public interface ITasksView { ITasksViewItem CreateTask(string name); }              // no-op impl
public static class UploadManager       // stub
{
    public static Task UploadSession(SessionInfo s, IUploadProvider p = null);       // notice dialog
    public static IEnumerable<IUploadProvider> GetAvailableProviders(SupportedUploadType t); // empty
}

public static class AppStyles
{
    public static Color AccentColor { get; }                  // FluentTheme "SystemAccentColor"
    public static IBrush AccentBackgroundBrush { get; }
    public static IBrush IdealBackgroundBrush { get; }        // #FF373737
    public static IBrush IdealForegroundBrush { get; }        // White
    public static IBrush CheckerboardBrushSmall { get; }
    public static bool IsDarkTheme { get; }
    public static WindowIcon AppIcon { get; }
    public static Control GetIconElement(ResourceIcon icon);  // new Path per call from StreamGeometry table
    public static string UiDateTimePattern { get; }
}
```

Editor ↔ shell glue invariants: clipboard custom format string is `"{65475a6c-9dde-41b1-946c-663ceb4d7b15}"`
(raw ClassifyBinary bytes of `GraphicBase[]`); image clipboard format is `"image/png"` (PNG bytes);
graphics autosave file is `<sessionDir>/graphics.xml` (the `StateUpdated` XElement); editor preview PNGs
get GUID names in the session dir.

---

## 3. WPF → Avalonia decision table

One replacement per API. No alternatives.

| # | WPF-only API / pattern | Decision (Avalonia 11.3.13) |
|---|---|---|
| 1 | `DrawingVisual` + `VisualCollection` + `AddVisualChild`/`GetVisualChild` overrides | One `GraphicVisual : Control` per graphic; manage `VisualChildren` list order directly (`[_clickable, _artworkBackground, …graphics, …Children]`) |
| 2 | `DependencyProperty` + DependencyPropertyGenerator | Hand-written `AvaloniaProperty.Register<TOwner,T>(name, default, defaultBindingMode)` + `OnPropertyChanged(AvaloniaPropertyChangedEventArgs)` dispatch. Generator NOT used anywhere |
| 3 | `Rect.Empty` / `Rect.IsEmpty` | `default(Rect)` + `RectExtensions.IsEmptyRect` (W or H ≤ 0); `Rect?` for ToolPointer sentinel helpers |
| 4 | `Int32Rect` (incl. `.IsEmpty`) | `PixelRect` + `IsEmptyRect` extension |
| 5 | Mouse events / `MouseButtonEventArgs` / `ClickCount` / button state | `PointerPressed/Moved/Released`; `e.ClickCount`; `e.GetCurrentPoint(v).Properties`; `e.InitialPressMouseButton` |
| 6 | `CaptureMouse()` / `IsMouseCaptured` / `LostMouseCapture` | `DrawingCanvas.CaptureMouse(IPointer)` / `ReleaseMouseCapture()` tracking `_capturedPointer`; `PointerCaptureLost` → `CancelCurrentOperation()` |
| 7 | `Keyboard.IsKeyDown` / `Keyboard.Modifiers` | `PointerState.Modifiers` threaded through tool calls (no global keyboard state) |
| 8 | Synthetic `MouseMove` on Shift press/release mid-drag | Cached `PointerState` replay: canvas re-invokes `tool.OnMouseMove` with stored position and updated modifiers |
| 9 | Left-vs-right shift distinction (ToolBase snap bug) | `KeyModifiers.Shift` (both shifts snap; WPF bug fixed deliberately) |
| 10 | `CommandManager.InvalidateRequerySuggested` / `RequerySuggested` | `RelayCommand.RaiseCanExecuteChanged()`; `DrawingCanvas.RequeryCommands()` called at the 3 WPF call sites (GraphicsList PropertyChanged ×2, UndoManager StateChanged) |
| 11 | `BindingOperations.SetBinding/ClearBinding` (SyncObjectState) | `this.Bind(Property, new Binding(path){ Source = … })` → collect `IDisposable`s in `_skillBindings`, dispose-all to clear |
| 12 | `NotifyOnSourceUpdated` + `SourceUpdated` → mergable undo step | Subscribe bound graphic's `PropertyChanged` for the bound property names → `AddCommandToHistory(true)`, with `_syncingState` re-entrancy flag set during SyncObjectState/undo restore |
| 13 | `PresentationSource…TransformToDevice.M11` / `VisualTreeHelper.GetDpi` / `DpiScale` | `TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0`; own `record struct DpiScale` |
| 14 | `FindResource("CheckeredLargeLightWhiteBackgroundBrush")` in ctor | `CheckeredBackground : Control` renders the checker procedurally (parallax math unchanged); no resource lookup in Clowd.Drawing |
| 15 | Implicit top-left `RenderTransformOrigin` | Explicit `RenderTransformOrigin = new RelativePoint(0,0,RelativeUnit.Relative)` on DrawingCanvas and the ToolText TextBox |
| 16 | MouseWheel delta ±120 | Accumulate `e.Delta.Y`; one zoom step per accumulated ±1.0, remainder kept (trackpad-safe) |
| 17 | `ContextMenu` `PlacementMode.MousePoint` | `ContextMenu` with `Placement = PlacementMode.Pointer`, assigned to `DrawingCanvas.ContextMenu`; Popup variant: `Placement=Pointer`, `IsLightDismissEnabled=true` |
| 18 | `Cursor(path/stream, scaleWithDpi)` from `.cur` + Vanara WM_DPICHANGED window | Runtime `.cur` parser (ICONDIR; per-frame PNG + hotspot at entry offsets 4/6 for hotspot words) → `new Cursor(Bitmap, PixelPoint)`; frame picked by `32 * RenderScaling`; cache keyed (file, scaling bucket), flushed on `TopLevel.ScalingChanged`. Vanara code deleted |
| 19 | `DropShadowEffect{Opacity=.5, ShadowDepth=2, Direction=315}` on DrawingVisual | `GraphicVisual.Effect = new DropShadowEffect { OffsetX = 1.414, OffsetY = 1.414, BlurRadius = 5, Color = #80000000 }` |
| 20 | `RenderTargetBitmap(w,h,96,96,Pbgra32)` + `Render(visual)` | `new RenderTargetBitmap(new PixelSize(w,h), new Vector(96,96))`; export renders a measured transient Canvas (§2.10); other RTB uses draw via `CreateDrawingContext()` |
| 21 | `CroppedBitmap(src, Int32Rect)` | `ctx.DrawImage(source, sourceRect, destRect)` with crop as source rect (bitmaps normalized to 96 DPI on load) |
| 22 | WriteableBitmapEx `BitmapFactory.FromStream` + `Blit` (cursor composite) | `new Bitmap(stream)`; composite into a `RenderTargetBitmap` at image pixel size / 96 DPI (image then cursor rect) |
| 23 | `TransformedBitmap` downscale (pixelate) | `bitmap.CreateScaledBitmap(pixelSize, BitmapInterpolationMode.LowQuality)`, cached per scale factor |
| 24 | `VisualBitmapScalingMode = NearestNeighbor` | `ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None })` inside the obscure-cache RTB |
| 25 | `Geometry.GetWidenedPathGeometry` | Rendering: `ctx.DrawLine(pen, …)` / pen-stroked geometry. Hit tests: `Geometry.StrokeContains(new Pen(Brushes.Black, width), pt)` (always a real brush, never null). Bounds: `GetRenderBounds(new Pen(null, LineWidth))` |
| 26 | `CombinedGeometry` Union for arrow render | Shaft via `DrawLine` + filled triangle StreamGeometry (opaque colors → visually identical); a CombinedGeometry kept only for arrow Bounds/Contains |
| 27 | Push transform without Pop (rotation left applied for trackers) | `using`-scoped `PushedState`; `Draw` owns the rotation scope and draws trackers inside it (§2.1) |
| 28 | `RotateTransform(a, cx, cy)` in DrawingContext | `ctx.PushTransform(MatrixHelper.Rotation(angleDeg, center))` |
| 29 | `Matrix.Rotate(deg)` + `Transform(Vector)` | `vector * Matrix.CreateRotation(Matrix.ToRadians(deg))` |
| 30 | `Vector.Normalize()` (mutating) | `v = v.Normalize()` (returns copy) |
| 31 | `FormattedText(…, Ideal, pixelsPerDip)` | `new FormattedText(text, culture, FlowDirection.LeftToRight, new Typeface(family, style, weight, stretch), size, brush)`; `ctx.DrawText(ft, origin)` |
| 32 | `TextAlignment.Center` centered on origin X (GraphicCount) | Draw at `(centerX − ft.Width/2, centerY − ft.Height/2)` with `ft.TextAlignment = Center` for multi-line |
| 33 | `FontStyle/FontWeight/FontStretch` structs + TypeConverters | Avalonia enums + `Enum.TryParse` (fallback Normal); FontConversions.cs not ported |
| 34 | `Visibility` (+ all Bool/Enum→Visibility converters) | `IsVisible` bool bindings; `EnumMatchToBooleanConverter`; `{x:Static BoolConverters.Not}`; visibility converters not ported |
| 35 | `RoutedUICommand` / `CommandBindings` / `ApplicationCommands` | Plain `RelayCommand`/ICommand properties on the window + explicit `Window.KeyBindings` (Ctrl+S/C/X/V/U, Ctrl+0..3 incl. NumPad; + Meta variants on macOS). Secondary legacy gestures (Ctrl+Insert, Shift+Insert/Delete) dropped |
| 36 | `BareKeyBinding` (bare keys suppressed in TextBox) | Single EditorWindow tunnel `KeyDown` handler (`RoutingStrategies.Tunnel`); skips when `e.Source is TextBox`; handles tool letters D/S/R/F/E/L/A/P/T/N/O, Escape, Delete, Home, End, arrows |
| 37 | `KeyEventArgs.IsRepeat` (accelerating nudge) | `HashSet<Key>` pressed-set; KeyDown for already-present key = repeat |
| 38 | `PreviewKeyDown/Up`, `PreviewMouseDown` | `AddHandler(…Event, handler, RoutingStrategies.Tunnel)` |
| 39 | `LostKeyboardFocus` (text edit commit) | `LostFocus` once + `TopLevel.Deactivated` → `FinishEdit` |
| 40 | `Style = null` on overlay TextBox | Minimal local `ControlTheme` for the edit TextBox (transparent background/border in all pseudo-classes, Padding 0); alignment fudge `TEXTBOX_ALIGN_X = -2` kept as named const, re-tuned in WP15 |
| 41 | `ToolTypeConverter.ConvertBack` returning an exception object | Return `AvaloniaProperty.UnsetValue` |
| 42 | `DrawingBrush` checkered tiles (10/16/50px) + `x:Shared=False` Path icons | Checkers: code-generated tiled `ImageBrush`es in `CheckerBrushes` (+ procedural `CheckeredBackground` for the canvas). Icons: `StreamGeometry` resources + `AppStyles.GetIconElement` factory; `ToolButton.IconPath` typed `Geometry` |
| 43 | Storyboard hover fades (`FillBehavior=Stop`, 0.1s) / pulse spline keyframes | `Transitions` (DoubleTransition Opacity 0:0:0.1) + pseudo-class selectors; pulse = `Style.Animations` keyframes 0.2→0.8@50%→0.2, 5s, Infinite |
| 44 | SpinnerTextBox `BindingExpression.ResolvedSource` + `Exposed.From` | `Value`/`Suffix`/`DisplayScale` redesign (§2.7); Zoom/Pixel/Angle converters deleted |
| 45 | WPFUI theme / Mica `Watcher` / immersive dark titlebar / `SystemAccentColor` | `FluentTheme`, `RequestedThemeVariant = Default`; `TransparencyLevelHint = [Mica, AcrylicBlur, None]` on Windows only; FluentTheme `SystemAccentColor` resource; `ActualThemeVariantChanged` event |
| 46 | `pack://` URIs / `Application.GetResourceStream` | `avares://Clowd.Ui/Assets/…` + `AssetLoader.Open` |
| 47 | `System.Drawing.Icon` + WinForms tray sizing | `WindowIcon` from the .ico asset; Avalonia `TrayIcon` |
| 48 | Hardcodet `TaskbarIcon` (+ double-click) | Avalonia `TrayIcon` + `NativeMenu`; single `Clicked` opens settings; gesture text appended to header strings; menu rebuilt on settings change |
| 49 | WinForms `TaskDialog` / `FontDialog` / file dialogs | `MessageDialog.axaml` (one window, all NiceDialog prompts); custom `FontDialog.axaml`; `StorageProvider.OpenFilePickerAsync` / `SaveFilePickerAsync` / `OpenFolderPickerAsync` |
| 50 | `PrintDialog.PrintVisual` / `DrawGraphicsToVisual` | **Dropped.** No print command in the port |
| 51 | `ClipboardWpf` (CF_DIB image, custom formats) | `TopLevel.Clipboard` + `DataObject`: `"image/png"` PNG bytes for images; custom GUID format for graphics bytes |
| 52 | `CollectionViewSource` grouping/sorting (RecentSessions) | VM-side `ObservableCollection<SessionGroupVm>` rebuilt on collection Reset (throttled 250 ms) |
| 53 | WPFUI `NavigationFluent`/`Breadcrumb`/`Frame`, `NumberBox` | ListBox nav + cached `ContentControl` pages; `NumericUpDown` + `NumericTypeConverter` |
| 54 | `GroupBox` | `HeaderedContentControl` with a simple ControlTheme |
| 55 | Win32 window placement (`SystemParameters` borders, SW_HIDE restore, VirtualDesktopId, RestoreBounds) | `Screens.ScreenFromBounds` + `Screen.WorkingArea` + `Window.Position` (physical px; divide by `Screen.Scaling` for logical); border compensation dropped (constant padding only); virtual-desktop restore dropped; normal-state bounds tracked manually while `WindowState == Normal` |
| 56 | `OnDpiChanged` | `ScalingChanged` → `drawingCanvas.UpdateScaleTransform()` |
| 57 | Win32 `RegisterHotKey` global hotkeys | Stubbed (`GlobalTrigger` never registers; editor unaffected) |
| 58 | `SYSTEM_WINDOWS_VECTOR` define (Curves) | Single alias `VECTOR = Avalonia.Vector; FLOAT = double` (no `#if` ladder; double precision preserved) |
| 59 | RT.Util `PointD`/`LengthProjectedOnto` (angle snap) | Inlined: projection = dot(drag, (cos θ, sin θ)); exact rounding formulas kept |
| 60 | HLSL `ShaderEffect` color wheel | CPU-generated `WriteableBitmap` (same sector math, 1.5px feathered rim), rebuilt on size/scaling change |
| 61 | `DropShadowEffect` on MiniColorDialog border | `BoxShadow="0 2 8 0 #80000000"` |
| 62 | `OpacityMask` SV-square (mini picker) | Keep `OpacityMask` (supported in 11.3); verified on macOS in WP15 |
| 63 | `Popup PopupAnimation=Fade` | Dropped (no animation) |
| 64 | `Binding.DoNothing` / `DependencyProperty.UnsetValue` | `BindingOperations.DoNothing` / `AvaloniaProperty.UnsetValue` |
| 65 | `SelectTextOnFocus` behavior | Attached property: `GotFocus → SelectAll()` |
| 66 | `Mouse.GetPosition` inside `OnRender` (DPadControl) | DPadControl dropped entirely |
| 67 | PipeMethodCalls named-pipe RPC | Length-prefixed JSON over `NamedPipeServer/ClientStream` (dependency removed) |
| 68 | `DispatcherUnhandledException` | `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException` + `Dispatcher.UIThread.UnhandledException` |
| 69 | `SynchronizationContextEventHandler` | `Dispatcher.UIThread.Post` marshalling |
| 70 | `Window` Width/Height via Style setters (`CustomUiWindow`) | Defaults set in `SystemThemedWindow` ctor / per-window XAML; MinWidth 460, MinHeight 100, FontSize 13 via theme |

---

## 4. Port order & work packages

Each package is sized for one agent. "Fidelity: identical" = pixel/behavior parity with WPF is the
acceptance bar; "practical" = same capability, adapted UX acceptable. WPF source root =
`/Users/caesay/Source/Clowd/src`. All packages must conform to §2 exactly — that is what makes parallel
work compile together.

Dependency graph:

```
WP1 ─┬─ WP2 ─┐
     ├─ WP3 ─┼─ WP4 ─┬─ WP5 ─┐
     │       │       ├─ WP6 ─┼─ WP7 ──────────────┐
     ├─ WP8 ─┬─ WP9 ─────────┤                    │
     │       ├─ WP10 ────────┤                    ├─ WP13 ─┬─ WP15
     │       └─ WP11 ────────┤                    │        │
     └─ WP12 ────────────────┴────────────────────┴─ WP14 ─┘
```

### WP1 — Clowd.Drawing project + Shared foundation
- **Read:** `Clowd.Shared/SimpleNotifyObject.cs`, `RelayCommand.cs`, `SimpleKeyGesture.cs`,
  `EmbeddedResource.cs`, `Upload.cs`, `Clowd.Shared/Config/*` (SettingsRoot, CategoryBase,
  AutoDictionary, TimeOption, SettingsEditor, SettingsGeneral, SettingsCapture, SettingsHotkey,
  SettingsUpload, SettingsVideo, Attributes), `Clowd.Drawing/Attributes.cs`,
  `Clowd.Drawing/HelperFunctions.cs`, `Clowd.Drawing/DrawingCanvasException.cs`,
  `Clowd.Drawing/ToolTypeConverter.cs`.
- **Write:** `Clowd.Drawing.csproj`, everything under `Shared/`, `Attributes.cs`, `HelperFunctions.cs`,
  `DpiScale.cs`, `RectExtensions.cs`, `MatrixHelper.cs`, `ToolTypeConverter.cs`,
  `DrawingCanvasException.cs`, `Shared/Config/ClassifySubstitutes.cs`.
- **Fidelity:** identical semantics (pure C#); settings defaults exact (§2.6); Classify substitutes per §2.9.
- **Accept:** project compiles standalone; round-trip unit check: serialize/deserialize a SavedToolSettings
  and a Color/Rect/PixelRect via ClassifyXml, element-per-property shape confirmed.

### WP2 — Curves library
- **Read:** `Clowd.Drawing/Curves/*` (8 files + LICENSE).
- **Write:** `Curves/*` with single alias `VECTOR = Avalonia.Vector, FLOAT = double`; `VectorHelper` body
  updated (Distance/SquaredLength/Dot/Normalize via Avalonia.Vector; `EPSILON = 1.2e-12` kept).
- **Fidelity:** identical (bit-identical fitting output vs WPF — double precision).
- **Accept:** `CurveFit.Fit` of a sample stroke produces same beziers as WPF reference values (hardcode one
  expected output in a test or debug assert).

### WP3 — CursorResources
- **Read:** `Clowd.Drawing/CursorResources.cs`, `Clowd.Drawing/Cursors/CursorResources.cs`,
  `Cursors.targets`; copy all 48 `.cur` files.
- **Write:** `CursorResources.cs` (parser/cache/ScalingChanged flush), `Cursors/CursorResources.Table.cs`,
  `Cursors/*.cur`.
- **Fidelity:** identical asset surface (`Default…SizeAll`, `Size0..Size35`, `GetResizeCursor`); hotspots
  from .cur entries; frame chosen by scaling.
- **Accept:** all 48 cursors load; hotspot of Default = (1,1) at 32px; out-of-range GetResizeCursor throws.

### WP4 — Graphics object model (depends WP1, WP2, WP3)
- **Read:** `Clowd.Drawing/Graphics/*.cs` (all 11).
- **Write:** `Graphics/*.cs` per §2.1 contracts, with the transform-scope restructure, GetWidenedPathGeometry
  replacements (#25/#26), GraphicImage pipeline (#21–#24), FormattedText mapping (#31/#32).
- **Fidelity:** identical — geometry math verbatim; whole-pixel rounding in `CreateRectSafeRounded`;
  tracker/dash rendering exact (blue/white/blue rings, green rotation tracker, 4-on/4-off dashes, crop
  bracket sizes 30/6/2 × scale); pastel cycle + ±4° random; pill radius rule; pixelate cell math.
- **Accept:** compiles against WP1–3; trackers render inside rotation; `Rect.Inflate/Deflate` return-values
  reassigned (no mutate-in-place bugs); hit-test pens use `Brushes.Black`.

### WP5 — GraphicCollection + GraphicVisual + UndoManager (depends WP4)
- **Read:** `Clowd.Drawing/GraphicCollection.cs`, `UndoManager.cs`.
- **Write:** `GraphicCollection.cs`, `GraphicVisual.cs`, `UndoManager.cs`, `StateChangedEventArgs.cs`.
- **Fidelity:** identical: INPC names ("Count"/"SelectedItems"/"ContentBounds"); z-order = list order at
  VisualChildren index+2; shadow effect params (§2.5); undo merge semantics (`_canMergeNext` + same
  changed-paths); export per §2.10.
- **Accept:** add/modify/undo/redo round-trips a 3-graphic document through ClassifyXml; merging coalesces
  two identical-path mergable steps.

### WP6 — Tools (depends WP4, WP3)
- **Read:** `Clowd.Drawing/Tools/*.cs` (all 9).
- **Write:** `Tools/*.cs` per §2.3.
- **Fidelity:** identical behavior: ToolPointer state machine verbatim (exact-equality modifier checks for
  Ctrl/Shift add-to-selection, `_wasEdit`, screen-space drag deltas via PointToScreen/PointToClient,
  contained-only rubber band that doesn't clear prior selection, Normalize-on-up); one-shot tool revert;
  ToolPanning no-revert + system SizeAll; ToolText overlay flow incl. Escape/Enter/Shift+Enter, live Body
  update, `TEXTBOX_ALIGN_X` const, `Dispatcher.UIThread.Post` focus, `ContextFlyout = null`; ToolCount
  arrow-then-badge ordering and inside-bubble arrow removal; ToolPixelate AddObscuredArea(double).
- **Accept:** compiles; undo entries created exactly at the WPF points (pointer edit end, draggable up,
  polyline end, text change commit, pixelate with ≥1 image) and nowhere else.

### WP7 — DrawingCanvas (depends WP5, WP6)
- **Read:** `Clowd.Drawing/DrawingCanvas.cs`.
- **Write:** `DrawingCanvas.cs`, `CheckeredBackground.cs`.
- **Fidelity:** identical: all 20 styled properties + defaults; zoom stops/anchored wheel zoom/Floor pixel
  snapping/parallax clickable surface; SyncObjectState three-mode logic with exact Subject* strings; right
  click = synthesized left-up → Pointer → selection retarget → context menu; double-click Activate; shift
  replay; PointerCaptureLost → CancelCurrentOperation; OnSizeChanged recenter + auto-fit.
- **Accept:** all §2.2 members present; ContentScale/Offset math verified at 2× RenderScaling; commands
  requery at the 3 sites.

### WP8 — Clowd.Ui project skeleton + theme + resources (depends WP1 only)
- **Read:** `Clowd/App.xaml`, `Clowd/AppStyles.cs`, `Clowd/Assets/VectorGeometries.xaml`,
  `Assets/VectorIcons.xaml`, `Assets/WpfUICustomStyles.xaml`, `Clowd/UI/SystemThemedWindow.cs`;
  `/Users/caesay/Source/Clowd/artwork/clowd-default.ico`.
- **Write:** `Clowd.Ui.csproj`, `Program.cs`, `App.axaml(.cs)` (theme + resources only; lifetime filled by
  WP14), `AppStyles.cs`, `SystemThemedWindow.cs`, `Assets/*` (ico + 3 axaml dictionaries),
  `Util/CheckerBrushes.cs`, `Converters/*`, `Behaviors/SelectTextOnFocus.cs`.
- **Fidelity:** practical; icon geometry data verbatim; ApplicationBackgroundColor theme dict Light
  `#FAFAFA` / Dark `#202020`.
- **Accept:** blank SystemThemedWindow runs on macOS; all icon keys resolve; `AppStyles.AccentColor`
  returns the FluentTheme accent.

### WP9 — Custom controls + themes (depends WP8)
- **Read:** `Clowd/UI/Controls/ToolButton.cs`, `CaptureToolButton.cs`, `SpinnerTextBox.xaml(.cs)`,
  `ResetDefaultButton.cs`, `DockAndWrapPanel.cs`; App.xaml lines 119–289 (templates).
- **Write:** `Controls/*` per §2.7 + ControlThemes.
- **Fidelity:** identical visuals (sizes, colors #33FFFFFF/#55FFFFFF/#454545/#666666/#6AB1EB, 0.1s fades,
  ▲▼ FontSize 6, 70x22 spinner, wrap-around spin); SpinnerTextBox plumbing redesigned per §2.7.
- **Accept:** test window shows toolbar-button hover/checked states and a wrapping DockAndWrapPanel with
  right-docked items; angle spinner wraps 180→−170.

### WP10 — Color picker (depends WP8, WP9 for ToolButton)
- **Read:** `Clowd/Util/HslRgbColor.cs`, `Clowd/UI/Converters/ColorToTextConverter.cs`,
  `Clowd/UI/Dialogs/ColorPicker/*` (all), `ColorWheelShader.hlsl`.
- **Write:** `Util/HslRgbColor.cs`, `Converters/ColorToTextConverters.cs`, `Dialogs/ColorPicker/*`.
- **Fidelity:** identical (editor-critical MiniColorDialog: #202124/#464648 chrome, SV square + pointer
  math, hue/alpha sliders, hex box, 24-color palette, Realtime callback ordering); wheel per #60; fix the
  OnCurrentColorChanged unsubscribe bug (detach from old value) in all 3 places.
- **Accept:** clicking pure-red rim → cursor returns to same pixel; Realtime drag fires ColorSelectFn per
  change; slider thumb overhang not clipped.

### WP11 — NiceDialog + MessageDialog + FontDialog (depends WP8)
- **Read:** `Clowd/UI/Helpers/NiceDialog.cs`.
- **Write:** `Helpers/NiceDialog.cs`, `Dialogs/MessageDialog.axaml(.cs)`, `Dialogs/FontDialog.axaml(.cs)`.
- **Fidelity:** practical; signatures per §2.8 (call-site compatible); font dialog preserves px↔pt nothing
  (sizes stay in px as the editor uses them).
- **Accept:** notice/yes-no/color/font/save flows all awaitable and parented correctly.

### WP12 — Session, settings plumbing, service stubs (depends WP1, WP8)
- **Read:** `Clowd/SessionManager.cs`, `Clowd/Util/FileSyncObject.cs`,
  `Clowd/UI/Helpers/TrulyObservableCollection.cs`, `Clowd/Util/DisposableTimer.cs`, `Constants.cs`/
  `PathConstants` usages, `Clowd/UI/PageManager.cs`, `Clowd/Util/MutexArgsForwarder.cs`,
  `Clowd.PlatformUtil` geometry records.
- **Write:** `Util/ScreenGeometry.cs`, `Util/FileSyncObject.cs`, `Util/TrulyObservableCollection.cs`,
  `Util/DisposableTimer.cs`, `Util/PathConstants.cs`, `Util/MutexArgsForwarder.cs`, `Session/*`,
  `Services/*`.
- **Fidelity:** identical JSON schema (session.json byte-compatible with the Rust capture writer);
  practical elsewhere.
- **Accept:** create/enumerate/delete sessions on disk; FSW add/remove updates `Sessions`; no echo loop on
  macOS (LastModifiedUtc guard).

### WP13 — EditorWindow (depends WP7, WP9, WP10, WP11, WP12)
- **Read:** `Clowd/UI/EditorWindow.xaml` + `.xaml.cs` (entirely), `Clowd/UI/Converters/
  EnumMatchToVisibilityConverter.cs` (semantics).
- **Write:** `Editor/EditorWindow.axaml(.cs)`.
- **Fidelity:** **identical** — full §5 spec: layout, properties bar, tool column, shortcuts, popup color
  flow, session load/save/autosave, ShowSession placement (practical subset: exact-place math without
  border compensation; restore position/state; no virtual desktops), clipboard formats, Cut = Copy +
  DeleteAll (preserved deliberately).
- **Accept:** §5 checklist passes end-to-end with a session containing a screenshot GraphicImage.

### WP14 — Shell: MainWindow, pages, tray, lifetime (depends WP8, WP11, WP12; UI factory pieces from WP9)
- **Read:** `Clowd/UI/MainWindow.xaml(.cs)`, `Clowd/UI/Config/*` (RecentSessionsPage,
  GeneralSettingsPage, AboutPage, SettingsControlFactory, GlobalTriggerEditor), `Clowd/Program.cs`,
  `Clowd/App.xaml.cs` (startup/tray/exit), `Clowd/UI/Converters/TimeAgoConverter.cs`.
- **Write:** `Main/*`, App.axaml.cs lifetime (OnExplicitShutdown, tray + NativeMenu, single-instance,
  first-run About, RestoreSessionsOnClowdStart), exit-confirm dialog.
- **Fidelity:** practical adaptation (ListBox nav; grouped recents per #52; factory-generated
  Hotkeys/Capture/Editor pages; Uploads placeholder; hotkeys stubbed with Error text).
- **Accept:** app stays alive tray-only; Image Editor / Recents / Settings / Exit menu items work; Editor
  settings page edits SavedToolSettings live (color row, Reset Tools button, TimeOption row).

### WP15 — Integration & parity pass (depends WP13, WP14)
- **Read:** this document §5; WPF app screenshots/reference behavior as needed.
- **Write:** fixes only (any file).
- Tasks: full build of `Clowd.slnx`; tune `TEXTBOX_ALIGN_X`; verify export shadows (§2.10 fallback decision
  if needed); macOS checks (OpacityMask SV square, cursor scaling on Retina, Cmd shortcuts, popup
  placement, wheel-zoom accumulation on trackpad); undo-merge behavior during slider drags; clipboard
  copy/paste between two editor windows; session restore on relaunch.
- **Accept:** the §5 fidelity checklist signed off on macOS and (when available) Windows.

---

## 5. Editor fidelity spec (EditorWindow + DrawingCanvas)

### 5.1 Window & layout
- Title "Edit - Clowd"; 1050×750 default, MinWidth 460, MinHeight 100; CenterScreen for new sessions;
  Foreground White; FontSize 13; dark chrome.
- Root grid Background `#535353`. Row 0 (Auto): properties bar = `DockAndWrapPanel` ItemHeight 30,
  horizontal; each group is a horizontal StackPanel Margin 4,0,4,0 Height 30. Row 1 (*): inner grid —
  Col 0 (Auto): 1px `#666666` separator (Margin 4,0,4,0, top) above vertical `WrapPanel` toolbar
  (Margin 0,4,0,0; wraps to a second column when short); Col 1 (*): Border Background `#1e1e1e`,
  ClipToBounds, containing the DrawingCanvas (stretch).
- `miniColorPopup`: Popup Placement=Pointer, offsets −5/−5, light-dismiss, hosting MiniColorDialog
  Margin 10, Realtime=True.
- On Opened: `drawingCanvas.Focus()`, then post `ZoomPanAuto()` after first layout.

### 5.2 Left tool column (ToolButton 30×30, Padding 7 unless noted)
Order: Pan(None, "Pan Tool (D)\nCan also hold SHIFT to enter Pan Mode."), Pointer "Selection Tool (S)",
Rectangle (R), FilledRectangle (F), Ellipse (E), Line (L), Arrow (A), PolyLine "Pencil (P)",
Count "Numerical Step Count (N)" (Padding 8), Text (T) (Padding 8), Pixelate "Obscure (O)", then
Undo "Undo (Ctrl+Z)" and Redo "Redo (Ctrl+Y)" (canvas commands). Tool buttons: `Command=SelectToolCommand`
with tool-name string parameter; `IsChecked` OneWay from `drawingCanvas.Tool` via ToolTypeConverter;
CanToggle=false (clicking active tool never unchecks). Hover `#33FFFFFF` fade 0.1s; checked `#55FFFFFF`;
disabled content 50%.

### 5.3 Properties bar (left→right; visibility = `SubjectSkill` HasFlag via EnumMatchToBooleanConverter → IsVisible)
1. Subject label (always): Run `SubjectType` + bold Run `SubjectName` ("Mode Panning" / "Tool Rectangle" /
   "Selection Multiple"…).
2. Zoom (always): "Zoom:" + SpinnerTextBox(ContentScale, ×100 "%", Min 0.1, step 0.1) +
   ResetDefaultButton(1).
3. Canvas (CanvasBackground): "Canvas:" + 22×22 swatch — black outer border 1px, white inner border 1px,
   checkered tile under `ArtworkBackground` fill, Hand cursor, click → background color popup.
4. Stroke (Stroke): "Stroke:" + SpinnerTextBox(LineWidth, "px", Min 0, snap).
5. Color (Color): "Color:" + same swatch style showing `ObjectColor`, click → object color popup; hidden
   while `ObjectColorAuto`. AutoColor flag adds the 20px vertical mini-stack: 10×10 checkbox bound
   `ObjectColorAuto` + "Auto" (FontSize 8).
6. Font (Font): "Font:" + button (bg `#454545`, border `#666666` 1px, H 22, Padding 10,2, hover
   `#66FFFFFF`) whose Content/FontFamily/FontStyle/FontStretch preview `TextFontFamilyName`; click →
   `NiceDialog.ShowFontDialogAsync`, result writes back family/size/style/weight.
7. Angle (Angle): "Angle:" + SpinnerTextBox(ObjectAngle, "°", −180..180, step 10, snap, wraps) +
   ResetDefaultButton(0).
8. Blur (BlurRadius): "Blur:" + SpinnerTextBox(BlurRadius, "px", Min 1, snap).
9. Crop (Crop): ToolButton Padding 6, Command=CommandCropImage, IconCrop, "Crop Image".
10. Cursor (Cursor): ToolButton Padding 5, CanToggle, IconToolPointerFilled, IsChecked TwoWay
    `ObjectCursorVisible`, "Toggle Captured Mouse Cursor".
Right-docked (DockToEnd; visual right-to-left: Pin, Save, Copy, Upload): Upload (IconClowd, Ctrl+U,
right-click provider menu — empty in this build so no menu), Copy (IconCopySmall, Ctrl+C), Save
(IconSaveSmall, Padding 4, Ctrl+S), Pin (IconPinned, Padding 4, CanToggle, IsChecked TwoWay Topmost,
"Always on top").

### 5.4 Keyboard
- Tunnel KeyDown (skipped when `e.Source is TextBox`): bare letters select tools (D S R F E L A P T N O);
  Escape → `CancelCurrentOperation()`; Delete → delete selection; Home/End → front/back; arrows → `Nudge`
  1px, with Ctrl: distance `min(max(10, repeat*2), 40)` using the pressed-set repeat tracker.
- KeyBindings: Ctrl+A select all, Ctrl+Home/End forward/backward, Ctrl+Z/Y undo/redo, Ctrl+0..3 (+NumPad)
  zoom auto/100%/200%/300%, Ctrl+S/C/X/V/U save/copy/cut/paste/upload. macOS: Meta duplicates.
- Shift-pan: Shift down (no active tool drag, via `IsToolDragActive`) saves current tool, sets
  Tool=None; Shift up restores.
- SelectToolCommand ignored while `drawingCanvas.IsToolDragActive`.
- Cut = Copy + **DeleteAll** (entire canvas — intentional WPF behavior, do not "fix").

### 5.5 Canvas visuals & zoom
- Bottom layer: viewport-fixed checker (`#11FFFFFF` 2×2 checker geometry tiled at 50px·scale) with
  parallax: `parallaxSize = 100*scale`, offset `((translate % parallaxSize) − parallaxSize)/scale`.
- Artwork page: solid `ArtworkBackground` rect over `ContentBounds`.
- Zoom: wheel anchored at pointer; stops {0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3}, then ±1 to max 10, min 0.1;
  `scale = ContentScale/RenderScaling` so 1 image px = 1 device px at 100%; translate floored to device
  pixels. ZoomPanAuto = actual size if it fits, else fit; window resize keeps content centered and
  re-auto-fits if auto.
- Selection handles: 12px (×uiscale) circles — blue ring/white ring/blue core; HandleBrush color =
  `AppStyles.AccentColor` (assigned in EditorWindow ctor); constant on-screen size at any zoom
  (`Dpi = dpi·(1/ContentScale)`); rotation handle 32px·scale right of shape with green stem + green dot;
  dashed selection borders = white solid pen + black [4,4] dash pen; rubber band = 1px crisp
  (half-pixel-snapped) white + black dashed rect.
- Per-graphic drop shadow (when enabled): offset (1.414, 1.414), blur 5, #80000000.

### 5.6 Tool behaviors (one-line refreshers; full detail in WPF source per WP)
- All draw tools: left-drag creates, Shift snaps (diagonal for rect/ellipse → square/circle; 45° for
  line/arrow), mouse-up commits one undo step and reverts to Pointer with the custom crosshair Default
  cursor. Right-click mid-draw finishes via synthesized mouse-up.
- Pointer: handle hit beats body on selected items; Ctrl- or Shift-click adds to selection (exact-equality
  modifier check); drag moves all selected; corner-handle + Shift keeps aspect on rectangles, endpoint +
  Shift angle-snaps lines; rubber band selects fully-contained only; cursor = Move over bodies, 36-step
  rotated resize cursors over handles, Rotate over rotation handles, SizeAll over line endpoints.
- Text: click places (follows cursor until release), then inline TextBox editing — transparent chrome,
  rotated with the graphic (RenderTransformOrigin TopLeft), Enter commits, Shift+Enter newline, Escape
  aborts (new empty → removed), LostFocus/window-deactivate commits, live Body update grows the pastel
  note behind; double-click any GraphicText re-edits.
- Count: places auto-numbered badge (max existing + 1) + drag-out arrow; arrow removed if fully inside the
  badge; number editable immediately.
- Pixelate: drag region; applies mosaic (cell ≈ BlurRadius px) to every GraphicImage; undo step only if an
  image exists.
- Image crop: double-click/Crop button enters bracket-handle crop with dimmed full image; edges clamp to
  original extent; second Activate or deselect commits (undo step via EndCrop).
- Panning (tool None or Shift held): drag pans; `ContentOffset += delta * ContentScale`; stays active
  after mouse-up.

### 5.7 Session lifecycle
- Load: `graphics.xml` → RestoreState; else legacy GraphicsStream (ClassifyBinary base64); else
  DesktopImgPath → GraphicImage with crop + cursor sprite (`CursorVisible = Capture.ScreenshotWithCursor`).
- Every undo-state change autosaves `graphics.xml`; Activated/Deactivated/Loaded update
  `SessionInfo.OpenEditor` (topmost/min/max/restore-position); Closing renders preview PNG (GUID name,
  old file deleted), clears OpenEditor.
- Save: render → save-picker (pattern default "yyyy-MM-dd HH-mm-ss", .png) → LastSavePath, optional
  reveal-in-Finder/Explorer. Copy: PNG + custom GUID format (selected-only when a selection exists).
  Paste: prefer custom format (foreign image paths copied into session dir with GUID names); else
  clipboard PNG → new GraphicImage; else "The clipboard does not contain an image." notice. All gated by
  `VerifyArtworkExists` ("Canvas Empty" notice).

---

## 6. Risks & resolutions (all closed)

| Risk | Resolution (final) |
|---|---|
| Legacy WPF file compatibility (old graphics.xml / settings.xml / clipboard blobs) | **Non-goal.** This is a fresh port in a new repo. Classify field names and element shapes are kept anyway (free), so most simple documents will load, but no migration shims are written or tested. |
| Undo XML-diff shape changing under Avalonia types | Closed by §2.9 string substitutes (one element per property); WP5 acceptance test verifies merge behavior. |
| `SourceUpdated` replacement double-firing / feedback loops | `_syncingState` guard set during SyncObjectState, undo restore, and binding writes; property-name whitelist per binding mode; history merging additionally dedupes identical snapshots. WP7+WP15 test slider drags = single merged step. |
| Stale command CanExecute (no CommandManager) | `RequeryCommands()` at exactly the 3 WPF invalidation sites + after Undo/Redo; EditorWindow buttons bind to the same RelayCommand instances. |
| RenderTransformOrigin default (center vs top-left) | Explicit `RelativePoint(0,0,Relative)` on DrawingCanvas and ToolText TextBox; called out in WP7/WP6 acceptance. |
| Pointer-capture semantics / right-click synth mouse-up / capture-lost ordering | Canvas-owned `_capturedPointer` wrapper (§2.2); `PointerCaptureLost` → CancelCurrentOperation; right-press path calls `tool.OnMouseUp` directly with a synthetic PointerState before opening the context menu. |
| Trackpad fractional wheel deltas hyper-zooming | Delta accumulation, one stop per ±1.0 (#16). |
| 48 .cur cursors unsupported | Runtime .cur parser → bitmap+hotspot cursors (#18). macOS Retina sizing checked in WP15; if cursors render 2× too large on macOS, select the 32px frame and let the OS scale (decision pre-made). |
| Export loses drop shadows | Transient-canvas + `RenderTargetBitmap.Render` export (§2.10); pre-decided fallback = shadow-less export if unrooted rendering fails on a backend. |
| Text metrics differ cross-platform (Tahoma/Segoe UI missing on macOS) | Accepted. Defaults stay "Tahoma"/"Segoe UI"; Avalonia FontManager fallback applies; Inter is bundled as app font for shell UI. Pixel-identical text across OSes is a non-goal; GraphicText auto-resizes so layout stays self-consistent. |
| GraphicCount center-text shift | Manual origin math (#32); multi-char ("10") and multi-line tested in WP15. |
| ToolText overlay alignment (the −2 fudge) | Named const `TEXTBOX_ALIGN_X = -2`, minimal local TextBox ControlTheme; tuned by visual comparison in WP15. |
| Bare keys firing while typing | All bare gestures live only in the EditorWindow tunnel handler with `e.Source is TextBox` check (#36); no Avalonia KeyBindings for modifier-less keys anywhere. |
| macOS shortcut conventions | Every Ctrl KeyBinding gets a Meta twin on macOS (registered in code at window construction). Menus display platform gesture text. |
| `Geometry.Transform` respected by Contains/GetRenderBounds (GraphicPolyLine) | Assumed yes (Avalonia routes through platform geometry); WP4 includes a smoke assert (transform a known geometry, check bounds). If it fails, bake the transform into the points at draw time (pre-decided fallback). |
| Pen with null brush in hit tests | Always `Brushes.Black` for hit-test pens (#25). |
| OpacityMask SV square backend quirks | Keep OpacityMask (#62); WP15 verifies on macOS; pre-decided fallback = two stacked gradients (white→hue horizontal, transparent→black vertical). |
| DrawingBrush absence / checker fidelity | All checkers code-generated (`CheckerBrushes`) or procedural (canvas); pixel sizes 10/16/50 (+13.4 mini-popup tile approximated at 13) over the same `#96969696`/`#11FFFFFF` colors. |
| `x:Shared=False` icons | Geometry resources + factory; ToolButton/CaptureToolButton `IconPath` typed `Geometry` (#42). |
| SpinnerTextBox reflection hack unportable | Value/Suffix/DisplayScale redesign (§2.7); converters deleted; wrap/clamp/snap semantics preserved and regression-tested (WP9). |
| Hover/pulse animation semantics (FillBehavior=Stop) | Symmetric Transitions accepted as visually equivalent; pulse uses linear keyframes (accepted deviation from spline easing). |
| Global hotkeys (RegisterHotKey) | Stubbed entirely this migration (§2.6 GlobalTrigger). Hotkeys settings page renders gestures + "not supported" status. Revisit post-migration (SharpHook or Rust-side registration). |
| Clipboard interop with other apps | `"image/png"` bytes both directions + custom GUID format for app-internal graphics. CF_DIB/Windows-specific formats deferred; acceptable degradation noted. |
| Window placement physical/logical mixing | All placement math in physical px, divided by `Screen.Scaling` only when setting Width/Height; border compensation dropped (constant `StartupPadding` only); virtual-desktop restore dropped. |
| App lifetime (tray-resident) | `ShutdownMode.OnExplicitShutdown`; Exit menu (with ConfirmClose dialog) is the only shutdown path; MainWindow close just closes (PageManager recreates). |
| TrayIcon no double-click / no gesture text | Single click opens settings; gesture text appended to NativeMenuItem headers; menu rebuilt on hotkey-setting change. |
| FileSyncObject FSW echo loops on macOS | `_busy` guard + compare-LastModifiedUtc-before-repopulate; RecentSessions regroup throttled 250 ms. |
| Single-instance on macOS (stale mutex/pipes) | Keep Mutex+NamedPipe (net8 maps to unix sockets); wrap first-instance acquisition in try/catch with stale-pipe retry; acceptable because secondary-instance forwarding is best-effort. |
| Mica absent off-Win11 | Hint-only; editor chrome is hardcoded dark so visual delta is negligible. |
| WPFUI exact theme colors | Light `#FAFAFA` / Dark `#202020` hardcoded for `ApplicationBackgroundColor`; FluentTheme text/accent resources used directly; close-enough bar for shell (practical scope). |
| Linux accent color | Out of scope (targets are macOS/Windows); FluentTheme default accent acceptable if ever run on Linux. |
| Dead code temptation | Confirmed dropped (see §1 lists): no agent ports OriginIndicator, SerializationHelper, FontConversions, DPadControl, ProgressRing, TransitioningContentControl, FadingScrollViewer, OutlinedText*, CornerClippingBorder, Upload/SizeAware progress bars, CustomWindowStyle.xaml, LiveDraw, FloatingButtonWindow, video UI. |
| Right-shift snap bug parity | Fixed intentionally (#9) — both shifts snap in draw tools (matches ToolPointer's existing behavior and user expectation). |
| `sgblank` empty StreamGeometry parse failure | Not ported (placeholder with no consumers). |
| NumericUpDown decimal? vs int/double settings | `NumericTypeConverter` per TypeCode in SettingsControlFactory. |
| Trimming/AOT vs reflection bindings | Trimming and AOT disabled (default); reflection bindings in SettingsControlFactory are fine. |

## 7. Settings system rewrite (post-migration; supersedes §2.6/§2.9 settings notes)

The RT.Serialization (Classify XML) settings stack was replaced with standard .NET appsettings
infrastructure. The old `Clowd.Settings.xml` format is **not** migrated — first launch after this
change starts from defaults.

- **File:** `%AppData%\Clowd\Clowd.Settings.json` (`Clowd.DEBUG.Settings.json` in debug builds).
- **Load:** `SettingsService.Load()` (Clowd.Shared) builds a `ConfigurationBuilder` +
  `AddJsonFile(optional: true)` and binds with `Get<SettingsRoot>()`. It is a pure parse with zero
  side effects: missing file/sections/values fall back to compiled-in defaults; it throws only on
  malformed JSON (App offers a reset). `SettingsRoot.Current` is assigned explicitly in
  `App.SetupSettings` — never inside a constructor (the old singleton enforcement, auto-save-on-
  PropertyChanged cascade, `CategoryBase` subscribe machinery and `DiscoverProviders()`-on-load are
  all gone; provider discovery is an explicit App startup step).
- **Save:** `SettingsService.Save(root)` writes indented System.Text.Json atomically (temp file +
  move-overwrite) with a 5 s retry loop on sharing violations (replaces `Ut.WaitSharingVio`).
- **Representation (shared by both paths):** enums by name (`JsonStringEnumConverter` / binder);
  `Color` as `"#AARRGGBB"` (a `TypeConverter` registered via `TypeDescriptor.AddAttributes` for the
  binder + a `JsonConverter` for save); `SimpleKeyGesture` as `"Control+Shift+S"`
  (`SimpleKeyGesture.ToSerializedString`/`Parse`; a cleared gesture is written as `"None"` because
  the binder never assigns null converted values — `SettingsHotkey` normalizes `Key.None` back to
  null); `Editor.Tools` is a plain `Dictionary<ToolType, SavedToolSettings>`
  (`SettingsEditor.GetToolSettings` provides the old AutoDictionary lazy-create behaviour).
- **Explicit-save policy:** settings classes are inert INPC data; every user-visible mutation path
  saves at the UI layer — MainWindow attaches a PropertyChanged→Save hook per settings tab
  (incl. one level of nested objects like `TimeOption`), EditorWindow saves after `LastSavePath`
  changes and on close (tool preferences), HotkeyManager saves on gesture edits, and App saves on
  exit/shutdown.
- **Hotkeys decoupled:** `SettingsHotkey` holds only 7 `SimpleKeyGesture` properties.
  `Clowd.Ui/Services/HotkeyManager.cs` owns the `IGlobalTriggerHost` (SharpHook
  `GlobalHotkeyHost`), maps `HotkeyId` → `Action` (wired in `App.SetupGlobalHotkeys`), exposes
  per-hotkey `HotkeyEntry` (gesture write-through + live IsRegistered/Error) for
  `GlobalTriggerEditor`, supports `Refresh()` and `IsPaused` (gesture capture). The old
  `GlobalTrigger`/static-Host model was deleted.
- **RT.* status:** removed from Clowd.Shared entirely; Clowd.Drawing keeps RT for the graphics
  undo/clipboard serialization (`ClassifySubstitutes.cs` moved into Clowd.Drawing) — replacing that
  is a later phase.
- **Tests:** `clowd_ui/Clowd.Shared.Tests` (xunit) proves Save→Load round-trips a non-default graph
  (Color, gestures incl. cleared, Tools entry, TimeOption, enums).
