using System.Numerics;

namespace MathBoard.Graphics;

/// <summary>
/// Everything needed to draw one frame. Positions are in logical pixels; multiply by <see cref="UiScale"/>
/// to get framebuffer pixels.
/// </summary>
public readonly ref struct FrameData
{
    public Vector4 ClearColor { get; init; }

    /// <summary>Logical screen position of a world point: <c>world * CameraZoom + CameraPosition</c>.</summary>
    public Vector2 CameraPosition { get; init; }
    public float CameraZoom { get; init; }
    public float UiScale { get; init; }

    /// <summary>Stroke primitives in world space.</summary>
    public ReadOnlySpan<Prim> Strokes { get; init; }

    /// <summary>Index of the first stroke primitive changed since the last presented frame; <c>Strokes.Length</c> if none.</summary>
    public int StrokesDirtyFrom { get; init; }

    /// <summary>
    /// Primitives below this index only change when <see cref="StrokesDirtyFrom"/> is below it too. Those above
    /// belong to the stroke being drawn and may be rewritten every frame, so caches should not keep them.
    /// </summary>
    public int StrokesStable { get; init; }

    /// <summary>
    /// The selection being moved, scaled or rotated: its primitives in world space as they were before, drawn above the
    /// strokes with <see cref="MovingTransform"/> applied to their capsule ends (the width stays), so dragging it only
    /// changes the matrix. <see cref="MovingVersion"/> changes whenever the primitives do, not when only the matrix does.
    /// </summary>
    public ReadOnlySpan<Prim> Moving { get; init; }
    public Matrix3x2 MovingTransform { get; init; }
    public int MovingVersion { get; init; }

    /// <summary>
    /// Primitives in logical screen space drawn below the strokes (grid, labels). They follow the camera, so a cache
    /// that is shifted with the camera stays valid until <see cref="BackgroundVersion"/> changes.
    /// </summary>
    public ReadOnlySpan<Prim> Background { get; init; }
    public int BackgroundVersion { get; init; }

    /// <summary>Pictures in world space, drawn above the background and below the strokes.</summary>
    public ReadOnlySpan<ImageDraw> Images { get; init; }

    /// <summary>Changes whenever <see cref="Images"/> does, so a cache of the drawn pictures can be kept until then.</summary>
    public int ImagesVersion { get; init; }

    /// <summary>Pictures in logical screen space, drawn above the strokes and below the interface.</summary>
    public ReadOnlySpan<ImageDraw> OverlayImages { get; init; }

    /// <summary>Interface primitives in logical screen space, in draw order.</summary>
    public ReadOnlySpan<Prim> Ui { get; init; }

    /// <summary>Single-channel coverage atlas sampled by <see cref="PrimType.Glyph"/> primitives.</summary>
    public ReadOnlySpan<byte> AtlasPixels { get; init; }
    public int AtlasSize { get; init; }

    /// <summary>Atlas rows changed since the last presented frame: [AtlasDirtyTop, AtlasDirtyBottom).</summary>
    public int AtlasDirtyTop { get; init; }
    public int AtlasDirtyBottom { get; init; }
}
