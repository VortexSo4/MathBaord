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
    // Shows the zoom in percent; pressing it does nothing (quick bar only).
    ZoomLevel,
    // An empty sector of the radial menu, to even out the ring.
    Spacer
}
