namespace MathBoard.Core;

/// <summary>Commands available both as keyboard shortcuts and as on-screen quick buttons.</summary>
public enum UiAction
{
    ToggleLibrary,
    Undo,
    Redo,
    Save,
    NewFile,
    OpenAutoSave,
    Settings,
    Brush,
    Eraser,
    Select,
    Pan,
    Color,
    ZoomIn,
    ZoomOut,
    FitView,
    Keyboard,
    Shapes,
    Grid,
    Screenshot,
    InsertImage,
    Thickness,
    ClearBoard,
    BackgroundColor,
    Export,
    Fill,
    FormatPainter,
    // Turning solids in space by dragging over them.
    TurnSolids,
    // Shows the zoom in percent; pressing it sets 100 %.
    ZoomLevel,
    Minimap,
    // An empty sector of the radial menu, to even out the ring.
    Spacer
}
