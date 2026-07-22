﻿using Silk.NET.Vulkan;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using MathBoard.Core;

namespace MathBoard.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct Vertex
{
    public Vector2 Position;
    public Vector4 Color;
}

public sealed unsafe class StrokeRenderer : IDisposable
{
    private readonly VulkanContext _context;
    private readonly SwapchainManager _swapchain;
    private readonly RenderPassManager _renderPass;
    private readonly CommandManager _commandManager;
    private readonly Document _document;
    private readonly Camera _camera;

    private LibraryPanel? _libraryPanel;
    public event Action? OnSceneChanged;
    public bool IsDirty => _dirty;
    public void SetLibraryPanel(LibraryPanel panel) => _libraryPanel = panel;

    private bool _isEraser;
    private float _eraserSize = 8f;

    private Vector4 _currentColor = Settings.Colors[0];
    private float _currentBrushWidth = 22f;

    private Pipeline _pipeline;
    private PipelineLayout _pipelineLayout;

    private Silk.NET.Vulkan.Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;
    private ulong _vertexBufferAllocatedSize;
    private uint _vertexCount;
    private uint _strokeVertexCount;
    private uint _uiVertexCount;

    private readonly List<Vertex> _vertices = [];
    private bool _dirty = true;

    private Extent2D _extent;

    private RadialMenu? _radialMenu;

    private TextAtlas? _textAtlas;
    public TextAtlas TextAtlas => _textAtlas!;

    public bool IsSelectMode { get; set; } = false;
    public bool IsQuickShapeApplied { get; set; } = false;
    public List<Stroke> SelectedStrokes { get; } = new();

    public enum SelectionState { None, DrawingBox, Moving, Scaling, Rotating }
    public SelectionState CurrentSelectionState { get; set; } = SelectionState.None;

    private Vector2 _selectionStartScreen;
    private Vector2 _selectionEndScreen;
    private int _activeHandle = -1;
    private Dictionary<Stroke, List<Vector2>> _originalPoints = new();
    private (Vector2 Min, Vector2 Max) _originalBounds;
    private Vector2 _startMouseWorld;

    public StrokeRenderer(VulkanContext context, SwapchainManager swapchain, RenderPassManager renderPass, CommandManager commandManager, Document document, Camera camera)
    {
        _context = context;
        _swapchain = swapchain;
        _renderPass = renderPass;
        _commandManager = commandManager;
        _document = document;
        _camera = camera;
        _extent = _swapchain.Extent;

        _currentBrushWidth = Settings.DefaultBrushWidth;
        _eraserSize = Settings.DefaultEraserSize;
    }

    public void SetRadialMenu(RadialMenu menu) => _radialMenu = menu;
    public Camera Camera => _camera;

    public float CurrentBrushWidth { get => _currentBrushWidth; set => _currentBrushWidth = value; }

    public void Initialize()
    {
        CreatePipeline();
        _textAtlas = new TextAtlas(_context, _renderPass, _commandManager);
        _textAtlas.Initialize();
    }

    public void UpdateExtent(Extent2D extent) => _extent = extent;

    public Vector2 ScreenToWorld(Vector2 screen) => (screen - _camera.Position) / _camera.Zoom;
    public Vector2 WorldToScreen(Vector2 world) => world * _camera.Zoom + _camera.Position;

    public void SetDirty() => _dirty = true;

    // ==================== DRAWING ====================
    public void BeginStroke(Vector2 screenPos)
    {
        if (_isEraser) { _document.SaveState(); EraseAt(screenPos); return; }

        _document.SaveState();
        var worldPos = ScreenToWorld(screenPos);
        var stroke = new Stroke { Width = _currentBrushWidth, Color = _currentColor };
        stroke.Points.Add(worldPos);
        _document.Strokes.Add(stroke);
        _dirty = true;
    }

    public void AddPoint(Vector2 screenPos)
    {
        if (_isEraser) { EraseAt(screenPos); return; }

        if (_document.Strokes.Count == 0) return;
        var worldPos = ScreenToWorld(screenPos);
        _document.Strokes[^1].Points.Add(worldPos);
        _dirty = true;
    }

    public void EraseAt(Vector2 screenPos, bool saveState = false)
    {
        var worldPos = ScreenToWorld(screenPos);
        var radius = _eraserSize;

        var toRemove = new List<int>();
        for (int i = 0; i < _document.Strokes.Count; i++)
        {
            if (IsStrokeHitByEraser(_document.Strokes[i], worldPos, radius))
                toRemove.Add(i);
        }

        if (toRemove.Count == 0) return;
        if (saveState) _document.SaveState();
        for (int i = toRemove.Count - 1; i >= 0; i--)
            _document.Strokes.RemoveAt(toRemove[i]);
        _dirty = true;
    }
    
    public void RequestRadialMenuIcons() => _radialMenu?.RequestIcons(_textAtlas!);

    private static bool IsStrokeHitByEraser(Stroke stroke, Vector2 eraserPos, float eraserRadius)
    {
        float threshold = eraserRadius + stroke.Width * 0.5f;
        float thresholdSq = threshold * threshold;
        var pts = stroke.Points;
        if (pts.Count == 0) return false;
        if (pts.Count == 1) return Vector2.DistanceSquared(pts[0], eraserPos) < thresholdSq;

        for (int i = 0; i < pts.Count - 1; i++)
        {
            if (PointToSegmentDistanceSq(eraserPos, pts[i], pts[i + 1]) < thresholdSq)
                return true;
        }
        return false;
    }

    private static float PointToSegmentDistanceSq(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.LengthSquared();
        if (lenSq < 1e-6f) return Vector2.DistanceSquared(p, a);
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / lenSq, 0f, 1f);
        return Vector2.DistanceSquared(p, a + ab * t);
    }

    public void EndStroke() => _dirty = true;

    // ==================== SELECTION TOOL ====================
    public void ClearSelection()
    {
        SelectedStrokes.Clear();
        CurrentSelectionState = SelectionState.None;
        SetDirty();
    }

    public void HandleSelectionDown(Vector2 screenPos)
    {
        _document.SaveState();
        if (SelectedStrokes.Count > 0)
        {
            int handle = HitTestHandles(screenPos);
            if (handle != -1)
            {
                CurrentSelectionState = (handle == 8) ? SelectionState.Rotating : SelectionState.Scaling;
                _activeHandle = handle;
                _originalPoints = new Dictionary<Stroke, List<Vector2>>();
                foreach (var s in SelectedStrokes) _originalPoints[s] = s.Points.ToList();
                _originalBounds = GetSelectedBounds();
                _startMouseWorld = ScreenToWorld(screenPos);
                return;
            }

            var bbox = GetSelectedBounds();
            var minS = WorldToScreen(bbox.Min);
            var maxS = WorldToScreen(bbox.Max);
            if (screenPos.X >= minS.X && screenPos.X <= maxS.X && screenPos.Y >= minS.Y && screenPos.Y <= maxS.Y)
            {
                CurrentSelectionState = SelectionState.Moving;
                _originalPoints = new Dictionary<Stroke, List<Vector2>>();
                foreach (var s in SelectedStrokes) _originalPoints[s] = s.Points.ToList();
                _startMouseWorld = ScreenToWorld(screenPos);
                return;
            }
        }

        SelectedStrokes.Clear();
        CurrentSelectionState = SelectionState.DrawingBox;
        _selectionStartScreen = screenPos;
        _selectionEndScreen = screenPos;
        SetDirty();
    }

    public void HandleSelectionMove(Vector2 screenPos)
    {
        if (CurrentSelectionState == SelectionState.DrawingBox)
        {
            _selectionEndScreen = screenPos;
            SetDirty();
            return;
        }
        if (CurrentSelectionState == SelectionState.None) return;

        var mouseWorld = ScreenToWorld(screenPos);
        Matrix3x2 m = Matrix3x2.Identity;

        if (CurrentSelectionState == SelectionState.Moving)
        {
            var delta = mouseWorld - _startMouseWorld;
            m = Matrix3x2.CreateTranslation(delta);
        }
        else if (CurrentSelectionState == SelectionState.Scaling)
        {
            Vector2 fixedCorner = Vector2.Zero;
            switch (_activeHandle)
            {
                case 0: fixedCorner = _originalBounds.Max; break;
                case 1: fixedCorner = new Vector2((_originalBounds.Min.X + _originalBounds.Max.X) / 2, _originalBounds.Max.Y); break;
                case 2: fixedCorner = _originalBounds.Min; break;
                case 3: fixedCorner = new Vector2(_originalBounds.Max.X, (_originalBounds.Min.Y + _originalBounds.Max.Y) / 2); break;
                case 4: fixedCorner = new Vector2(_originalBounds.Min.X, (_originalBounds.Min.Y + _originalBounds.Max.Y) / 2); break;
                case 5: fixedCorner = _originalBounds.Max; break;
                case 6: fixedCorner = new Vector2((_originalBounds.Min.X + _originalBounds.Max.X) / 2, _originalBounds.Min.Y); break;
                case 7: fixedCorner = _originalBounds.Min; break;
            }

            Vector2 newMin = _originalBounds.Min;
            Vector2 newMax = _originalBounds.Max;
            if (_activeHandle == 0 || _activeHandle == 3 || _activeHandle == 5) newMin.X = mouseWorld.X;
            if (_activeHandle == 2 || _activeHandle == 4 || _activeHandle == 7) newMax.X = mouseWorld.X;
            if (_activeHandle == 0 || _activeHandle == 1 || _activeHandle == 2) newMin.Y = mouseWorld.Y;
            if (_activeHandle == 5 || _activeHandle == 6 || _activeHandle == 7) newMax.Y = mouseWorld.Y;

            Vector2 origSize = _originalBounds.Max - _originalBounds.Min;
            Vector2 newSize = newMax - newMin;
            if (MathF.Abs(origSize.X) < 0.001f) origSize.X = 0.001f;
            if (MathF.Abs(origSize.Y) < 0.001f) origSize.Y = 0.001f;
            Vector2 scale = new Vector2(newSize.X / origSize.X, newSize.Y / origSize.Y);
            if (float.IsNaN(scale.X) || float.IsInfinity(scale.X)) scale.X = 1;
            if (float.IsNaN(scale.Y) || float.IsInfinity(scale.Y)) scale.Y = 1;

            m = Matrix3x2.CreateScale(scale, fixedCorner);
        }
        else if (CurrentSelectionState == SelectionState.Rotating)
        {
            Vector2 center = (_originalBounds.Min + _originalBounds.Max) * 0.5f;
            float startAngle = MathF.Atan2(_startMouseWorld.Y - center.Y, _startMouseWorld.X - center.X);
            float currentAngle = MathF.Atan2(mouseWorld.Y - center.Y, mouseWorld.X - center.X);
            float deltaAngle = currentAngle - startAngle;
            m = Matrix3x2.CreateRotation(deltaAngle, center);
        }

        foreach (var stroke in SelectedStrokes)
        {
            var origPts = _originalPoints[stroke];
            for (int i = 0; i < stroke.Points.Count; i++)
            {
                stroke.Points[i] = Vector2.Transform(origPts[i], m);
            }
        }
        SetDirty();
    }

    public void HandleSelectionUp(Vector2 screenPos)
    {
        if (CurrentSelectionState == SelectionState.DrawingBox)
        {
            var minS = Vector2.Min(_selectionStartScreen, screenPos);
            var maxS = Vector2.Max(_selectionStartScreen, screenPos);
            var minW = ScreenToWorld(minS);
            var maxW = ScreenToWorld(maxS);

            bool isZeroBox = (maxS - minS).LengthSquared() < 25f;

            if (isZeroBox)
            {
                var clickWorld = ScreenToWorld(screenPos);
                Stroke closest = null;
                float minDist = float.MaxValue;
                foreach (var s in _document.Strokes)
                {
                    for (int i = 0; i < s.Points.Count - 1; i++)
                    {
                        float d = PointToSegmentDistanceSq(clickWorld, s.Points[i], s.Points[i + 1]);
                        if (d < minDist) { minDist = d; closest = s; }
                    }
                }
                if (closest != null && minDist < (closest.Width * 0.5f + 10f / _camera.Zoom) * (closest.Width * 0.5f + 10f / _camera.Zoom))
                {
                    SelectedStrokes.Clear();
                    SelectedStrokes.Add(closest);
                }
            }
            else
            {
                foreach (var s in _document.Strokes)
                {
                    var bbox = GetBoundingBox(s.Points);
                    if (bbox.Max.X >= minW.X && bbox.Min.X <= maxW.X &&
                        bbox.Max.Y >= minW.Y && bbox.Min.Y <= maxW.Y)
                    {
                        SelectedStrokes.Add(s);
                    }
                }
            }
        }
        CurrentSelectionState = SelectionState.None;
        SetDirty();
    }

    private int HitTestHandles(Vector2 screenPos)
    {
        var bbox = GetSelectedBounds();
        var minS = WorldToScreen(bbox.Min);
        var maxS = WorldToScreen(bbox.Max);
        Vector2[] handles = GetHandlePositions(minS, maxS);
        for (int i = 0; i < handles.Length; i++)
        {
            if (Vector2.DistanceSquared(screenPos, handles[i]) < 150f) // ~12px radius
                return i;
        }
        return -1;
    }

    private Vector2[] GetHandlePositions(Vector2 minS, Vector2 maxS)
    {
        return new Vector2[]
        {
            new(minS.X, minS.Y), new((minS.X+maxS.X)/2, minS.Y), new(maxS.X, minS.Y),
            new(minS.X, (minS.Y+maxS.Y)/2), new(maxS.X, (minS.Y+maxS.Y)/2),
            new(minS.X, maxS.Y), new((minS.X+maxS.X)/2, maxS.Y), new(maxS.X, maxS.Y),
            new((minS.X+maxS.X)/2, minS.Y - 25f)
        };
    }

    public (Vector2 Min, Vector2 Max) GetSelectedBounds()
    {
        if (SelectedStrokes.Count == 0) return (Vector2.Zero, Vector2.Zero);
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var s in SelectedStrokes)
        {
            var b = GetBoundingBox(s.Points);
            min = Vector2.Min(min, b.Min);
            max = Vector2.Max(max, b.Max);
        }
        return (min, max);
    }

    public static (Vector2 Min, Vector2 Max) GetBoundingBox(List<Vector2> points)
    {
        if (points.Count == 0) return (Vector2.Zero, Vector2.Zero);
        Vector2 min = points[0], max = points[0];
        foreach (var p in points)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }
        return (min, max);
    }

    // ==================== QUICKSHAPE ====================
    public void ApplyQuickShape()
    {
        if (_document.Strokes.Count == 0) return;
        var stroke = _document.Strokes[^1];
        if (stroke.Points.Count < 3) return;

        var bbox = GetBoundingBox(stroke.Points);
        float diag = (bbox.Max - bbox.Min).Length();
        if (diag < 15f) return;

        // RDP simplification
        var simplified = RDP(stroke.Points, diag * 0.05f);

        // Remove duplicate consecutive points
        var distinct = new List<Vector2>();
        foreach (var p in simplified)
        {
            if (distinct.Count == 0 || Vector2.Distance(distinct[^1], p) > 1f) distinct.Add(p);
        }

        if (distinct.Count < 2) return;

        bool isClosed = distinct.Count > 2 &&
                        Vector2.Distance(distinct[0], distinct[^1]) < diag * 0.25f;

        if (isClosed)
        {
            // Start and end represent the same corner - merge them into one
            var merged = (distinct[0] + distinct[^1]) * 0.5f;
            distinct[0] = merged;
            distinct.RemoveAt(distinct.Count - 1);

            // Keep only real corners (significant turn angle)
            var corners = FilterCorners(distinct);

            if (corners.Count == 3)
                stroke.Points = GenerateTriangle(corners);
            else if (corners.Count == 4)
                stroke.Points = GenerateRectangle(bbox.Min, bbox.Max);
            else
                stroke.Points = GenerateCircle(bbox.Min, bbox.Max);
        }
        else
        {
            // Open path - straight line from start to end as drawn (no 45° snapping)
            stroke.Points = GenerateLine(stroke.Points[0], stroke.Points[^1]);
        }
        _dirty = true;
    }

    private static List<Vector2> FilterCorners(List<Vector2> pts)
    {
        if (pts.Count <= 3) return pts.ToList();

        var result = new List<Vector2>();
        int n = pts.Count;
        // ~25° turn threshold: anything straighter than this is NOT a corner
        const float angleThreshold = MathF.PI / 7f;

        for (int i = 0; i < n; i++)
        {
            var prev = pts[(i - 1 + n) % n];
            var curr = pts[i];
            var next = pts[(i + 1) % n];

            var v1 = curr - prev;
            var v2 = next - curr;
            float len1 = v1.Length();
            float len2 = v2.Length();
            if (len1 < 0.001f || len2 < 0.001f) continue;

            float cos = Math.Clamp(Vector2.Dot(v1, v2) / (len1 * len2), -1f, 1f);
            float angle = MathF.Acos(cos);

            // angle is the turn: 0 = straight, π = backtrack (very sharp)
            if (angle > angleThreshold)
                result.Add(curr);
        }

        return result;
    }

    private List<Vector2> RDP(List<Vector2> points, float epsilon)
    {
        if (points.Count < 3) return points.ToList();
        float maxDist = 0; int index = 0;
        for (int i = 1; i < points.Count - 1; i++)
        {
            float d = PointToSegmentDistanceSq(points[i], points[0], points[^1]);
            if (d > maxDist) { maxDist = d; index = i; }
        }
        List<Vector2> result = new();
        if (maxDist > epsilon * epsilon)
        {
            var left = RDP(points.GetRange(0, index + 1), epsilon);
            var right = RDP(points.GetRange(index, points.Count - index), epsilon);
            result.AddRange(left.Take(left.Count - 1));
            result.AddRange(right);
        }
        else
        {
            result.Add(points[0]);
            result.Add(points[^1]);
        }
        return result;
    }

    private List<Vector2> GenerateLine(Vector2 p1, Vector2 p2)
    {
        // No 45° snapping - line goes exactly from start to end as drawn
        return new List<Vector2> { p1, p2 };
    }

    private List<Vector2> GenerateRectangle(Vector2 min, Vector2 max)
    {
        return new List<Vector2> { new(min.X, min.Y), new(max.X, min.Y), new(max.X, max.Y), new(min.X, max.Y), new(min.X, min.Y) };
    }

    private List<Vector2> GenerateTriangle(List<Vector2> pts) => new() { pts[0], pts[1], pts[2], pts[0] };

    private List<Vector2> GenerateCircle(Vector2 min, Vector2 max)
    {
        List<Vector2> pts = new();
        Vector2 center = (min + max) * 0.5f;
        float rx = (max.X - min.X) * 0.5f;
        float ry = (max.Y - min.Y) * 0.5f;
        // Increased detail: 64 segments instead of 32
        const int segments = 64;
        for (int i = 0; i < segments; i++)
        {
            float a = i * MathF.PI * 2f / segments;
            pts.Add(center + new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry));
        }
        pts.Add(pts[0]);
        return pts;
    }

    // ==================== VERTEX GENERATION (WORLD SPACE) ====================
    private void RebuildAllVertices()
    {
        _vertices.Clear();

        int circleSegments = Settings.StrokeCircleSegments;
        const float twoPi = MathF.PI * 2f;

        foreach (var stroke in _document.Strokes)
        {
            if (stroke.Points.Count == 0) continue;

            var color = stroke.Color;
            var radius = stroke.Width * 0.5f;

            foreach (var p in stroke.Points)
            {
                for (int i = 0; i < circleSegments; i++)
                {
                    float a1 = i * twoPi / circleSegments;
                    float a2 = (i + 1) * twoPi / circleSegments;

                    _vertices.Add(new Vertex { Position = p, Color = color });
                    _vertices.Add(new Vertex { Position = p + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * radius, Color = color });
                    _vertices.Add(new Vertex { Position = p + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * radius, Color = color });
                }
            }

            for (int i = 0; i < stroke.Points.Count - 1; i++)
            {
                var p1 = stroke.Points[i];
                var p2 = stroke.Points[i + 1];

                var dir = Vector2.Normalize(p2 - p1);
                var perp = new Vector2(-dir.Y, dir.X);

                var p1l = p1 + perp * radius;
                var p1r = p1 - perp * radius;
                var p2l = p2 + perp * radius;
                var p2r = p2 - perp * radius;

                _vertices.Add(new Vertex { Position = p1l, Color = color });
                _vertices.Add(new Vertex { Position = p1r, Color = color });
                _vertices.Add(new Vertex { Position = p2l, Color = color });

                _vertices.Add(new Vertex { Position = p2l, Color = color });
                _vertices.Add(new Vertex { Position = p1r, Color = color });
                _vertices.Add(new Vertex { Position = p2r, Color = color });
            }
        }

        _strokeVertexCount = (uint)_vertices.Count;

        // --- UI: SCREEN SPACE ---
        _textAtlas?.BeginFrame();

        if (IsSelectMode)
        {
            RenderSelectionUI(_vertices);
        }

        if (_radialMenu?.IsOpen == true)
        {
            _radialMenu.RenderUI(_vertices);
        }
        if (_libraryPanel?.IsOpen == true)
        {
            _libraryPanel.RenderToVertices(_vertices, new Vector2(_extent.Width, _extent.Height));
        }

        _uiVertexCount = (uint)_vertices.Count - _strokeVertexCount;
        _vertexCount = (uint)_vertices.Count;
    }

    private void RenderSelectionUI(List<Vertex> vertices)
    {
        if (CurrentSelectionState == SelectionState.DrawingBox)
        {
            var minS1 = Vector2.Min(_selectionStartScreen, _selectionEndScreen);
            var maxS1 = Vector2.Max(_selectionStartScreen, _selectionEndScreen);
            DrawRectOutline(vertices, minS1, maxS1 - minS1, new Vector4(0.4f, 0.78f, 1.0f, 0.8f), 1.5f);
        }

        if (SelectedStrokes.Count == 0) return;

        var bbox = GetSelectedBounds();
        var minS = WorldToScreen(bbox.Min);
        var maxS = WorldToScreen(bbox.Max);

        DrawRectOutline(vertices, minS, maxS - minS, new Vector4(0.4f, 0.78f, 1.0f, 0.9f), 1.5f);

        Vector2[] handles = GetHandlePositions(minS, maxS);
        float handleSize = 8f;
        for (int i = 0; i < 8; i++)
        {
            DrawRect(vertices, handles[i] - new Vector2(handleSize / 2, handleSize / 2), new Vector2(handleSize, handleSize), new Vector4(0.9f, 0.9f, 0.9f, 0.9f));
        }
        DrawCircle(vertices, handles[8], handleSize, new Vector4(0.9f, 0.9f, 0.9f, 0.9f), 16);
        DrawLine(vertices, handles[1], handles[8], new Vector4(0.4f, 0.78f, 1.0f, 0.9f), 1.5f);
    }

    public void UpdateGeometry()
    {
        if (_dirty)
        {
            RebuildAllVertices();
            UpdateVertexBuffer();
            _textAtlas?.UploadFrameVertices();
            _dirty = false;
        }
    }

    // ==================== RENDER ====================
    public void Render(CommandBuffer cmd)
    {
        if (_vertexCount == 0 || _vertexBuffer.Handle == 0)
            return;

        var viewport = new Viewport { X = 0, Y = 0, Width = _extent.Width, Height = _extent.Height, MinDepth = 0, MaxDepth = 1 };
        var scissor = new Rect2D { Offset = new Offset2D(0, 0), Extent = _extent };

        _context.Vk.CmdSetViewport(cmd, 0, 1, &viewport);
        _context.Vk.CmdSetScissor(cmd, 0, 1, &scissor);

        _context.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);

        var vb = _vertexBuffer;
        var offset = 0ul;
        _context.Vk.CmdBindVertexBuffers(cmd, 0, 1, &vb, &offset);

        if (_strokeVertexCount > 0)
        {
            var transform = ComputeCameraTransform();
            _context.Vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &transform);
            _context.Vk.CmdDraw(cmd, _strokeVertexCount, 1, 0, 0);
        }

        if (_uiVertexCount > 0)
        {
            var transform = Matrix4x4.CreateOrthographicOffCenter(0, _extent.Width, 0, _extent.Height, -1f, 1f);
            _context.Vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), &transform);
            _context.Vk.CmdDraw(cmd, _uiVertexCount, 1, _strokeVertexCount, 0);
        }

        _textAtlas?.Render(cmd, _extent);
    }

    private Matrix4x4 ComputeCameraTransform()
    {
        float z = _camera.Zoom;
        var cam = _camera.Position;
        var view = Matrix4x4.CreateScale(z, z, 1f) * Matrix4x4.CreateTranslation(cam.X, cam.Y, 0f);
        var proj = Matrix4x4.CreateOrthographicOffCenter(0, _extent.Width, 0, _extent.Height, -1f, 1f);
        return view * proj;
    }

    // ==================== Vulkan Pipeline ====================
    private void CreatePipeline()
    {
        var vertShader = LoadShader("Shaders/stroke.vert.spv");
        var fragShader = LoadShader("Shaders/stroke.frag.spv");

        var vertStage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertShader, PName = (byte*)SilkMarshal.StringToPtr("main") };
        var fragStage = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragShader, PName = (byte*)SilkMarshal.StringToPtr("main") };

        var shaderStages = stackalloc PipelineShaderStageCreateInfo[2] { vertStage, fragStage };

        var binding = new VertexInputBindingDescription { Binding = 0, Stride = (uint)sizeof(Vertex), InputRate = VertexInputRate.Vertex };

        var attributes = stackalloc VertexInputAttributeDescription[2];
        attributes[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = (uint)Marshal.OffsetOf<Vertex>("Position") };
        attributes[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = (uint)Marshal.OffsetOf<Vertex>("Color") };

        var vertexInput = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo, VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &binding, VertexAttributeDescriptionCount = 2, PVertexAttributeDescriptions = attributes };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };

        var dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicStateInfo = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynamicStates };

        var viewport = new Viewport { Width = 1, Height = 1, MinDepth = 0, MaxDepth = 1 };
        var scissor = new Rect2D { Extent = new Extent2D { Width = 1, Height = 1 } };
        var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, PViewports = &viewport, ScissorCount = 1, PScissors = &scissor };

        var rasterizer = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, CullMode = CullModeFlags.None, LineWidth = 1.0f };
        var multisampling = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = _context.GetSampleCount() };

        var colorBlendAttachment = new PipelineColorBlendAttachmentState { BlendEnable = true, SrcColorBlendFactor = BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add, SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.Zero, AlphaBlendOp = BlendOp.Add, ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit };
        var colorBlending = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &colorBlendAttachment };

        var pushConstant = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Size = (uint)sizeof(Matrix4x4) };
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, PushConstantRangeCount = 1, PPushConstantRanges = &pushConstant };

        _context.Vk.CreatePipelineLayout(_context.Device, &pipelineLayoutInfo, null, out _pipelineLayout);

        var pipelineInfo = new GraphicsPipelineCreateInfo { SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = shaderStages, PVertexInputState = &vertexInput, PInputAssemblyState = &inputAssembly, PViewportState = &viewportState, PRasterizationState = &rasterizer, PMultisampleState = &multisampling, PColorBlendState = &colorBlending, PDynamicState = &dynamicStateInfo, Layout = _pipelineLayout, RenderPass = _renderPass.RenderPass, Subpass = 0 };

        _context.Vk.CreateGraphicsPipelines(_context.Device, default, 1, &pipelineInfo, null, out _pipeline);

        SilkMarshal.Free((nint)vertStage.PName);
        SilkMarshal.Free((nint)fragStage.PName);
        _context.Vk.DestroyShaderModule(_context.Device, vertShader, null);
        _context.Vk.DestroyShaderModule(_context.Device, fragShader, null);
    }

    private ShaderModule LoadShader(string path)
    {
        var code = File.ReadAllBytes(path);
        fixed (byte* pCode = code)
        {
            var createInfo = new ShaderModuleCreateInfo { SType = StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)code.Length, PCode = (uint*)pCode };
            _context.Vk.CreateShaderModule(_context.Device, &createInfo, null, out var module);
            return module;
        }
    }

    private void UpdateVertexBuffer()
    {
        _vertexCount = (uint)_vertices.Count;
        if (_vertices.Count == 0) return;

        var requiredSize = (ulong)(sizeof(Vertex) * _vertices.Count);

        if (_vertexBuffer.Handle == 0 || _vertexBufferAllocatedSize < requiredSize)
        {
            if (_vertexBuffer.Handle != 0)
            {
                _context.Vk.DeviceWaitIdle(_context.Device);
                _context.Vk.DestroyBuffer(_context.Device, _vertexBuffer, null);
                _context.Vk.FreeMemory(_context.Device, _vertexBufferMemory, null);
            }

            var allocSize = Math.Max(requiredSize * 2, 1UL << 20);
            CreateBuffer(allocSize, BufferUsageFlags.VertexBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _vertexBuffer, out _vertexBufferMemory);
            _vertexBufferAllocatedSize = allocSize;
        }

        void* mapped;
        _context.Vk.MapMemory(_context.Device, _vertexBufferMemory, 0, requiredSize, 0, &mapped);
        fixed (Vertex* src = CollectionsMarshal.AsSpan(_vertices))
        {
            System.Buffer.MemoryCopy(src, mapped, requiredSize, requiredSize);
        }
        _context.Vk.UnmapMemory(_context.Device, _vertexBufferMemory);
    }

    private void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties, out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
        _context.Vk.CreateBuffer(_context.Device, &bufferInfo, null, out buffer);

        MemoryRequirements memReq;
        _context.Vk.GetBufferMemoryRequirements(_context.Device, buffer, &memReq);

        var allocInfo = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = memReq.Size, MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, properties) };
        _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out memory);
        _context.Vk.BindBufferMemory(_context.Device, buffer, memory, 0);
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memProps;
        _context.Vk.GetPhysicalDeviceMemoryProperties(_context.PhysicalDevice, &memProps);

        for (int i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << i)) != 0 && (memProps.MemoryTypes[i].PropertyFlags & properties) == properties)
                return (uint)i;
        }

        throw new Exception("Failed to find suitable memory type");
    }

    public void ToggleEraser() => _isEraser = !_isEraser;
    public void ToggleEraser(bool enable) => _isEraser = enable;
    public void SetColor(Vector4 color) => _currentColor = color;

    public void ClearAll()
    {
        _document.SaveState();
        _document.Strokes.Clear();
        SelectedStrokes.Clear();
        _dirty = true;
    }

    public void Undo() { _document.Undo(); _dirty = true; }
    public void Redo() { _document.Redo(); _dirty = true; }

    // ==================== UI DRAWING HELPERS ====================
    private static void DrawRect(List<Vertex> v, Vector2 pos, Vector2 size, Vector4 color)
    {
        var p1 = pos; var p2 = pos + new Vector2(size.X, 0);
        var p3 = pos + size; var p4 = pos + new Vector2(0, size.Y);

        v.Add(new Vertex { Position = p1, Color = color });
        v.Add(new Vertex { Position = p2, Color = color });
        v.Add(new Vertex { Position = p3, Color = color });
        v.Add(new Vertex { Position = p1, Color = color });
        v.Add(new Vertex { Position = p3, Color = color });
        v.Add(new Vertex { Position = p4, Color = color });
    }

    private static void DrawRectOutline(List<Vertex> v, Vector2 pos, Vector2 size, Vector4 color, float thickness)
    {
        DrawRect(v, pos, new Vector2(size.X, thickness), color);
        DrawRect(v, new Vector2(pos.X, pos.Y + size.Y - thickness), new Vector2(size.X, thickness), color);
        DrawRect(v, pos, new Vector2(thickness, size.Y), color);
        DrawRect(v, new Vector2(pos.X + size.X - thickness, pos.Y), new Vector2(thickness, size.Y), color);
    }

    private static void DrawCircle(List<Vertex> v, Vector2 center, float r, Vector4 color, int segments)
    {
        float step = MathF.PI * 2f / segments;
        for (int i = 0; i < segments; i++)
        {
            float a1 = i * step; float a2 = (i + 1) * step;
            v.Add(new Vertex { Position = center, Color = color });
            v.Add(new Vertex { Position = center + new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * r, Color = color });
            v.Add(new Vertex { Position = center + new Vector2(MathF.Cos(a2), MathF.Sin(a2)) * r, Color = color });
        }
    }

    private static void DrawLine(List<Vertex> v, Vector2 p1, Vector2 p2, Vector4 color, float thickness)
    {
        var dir = Vector2.Normalize(p2 - p1);
        var perp = new Vector2(-dir.Y, dir.X) * (thickness * 0.5f);
        v.Add(new Vertex { Position = p1 + perp, Color = color });
        v.Add(new Vertex { Position = p1 - perp, Color = color });
        v.Add(new Vertex { Position = p2 + perp, Color = color });
        v.Add(new Vertex { Position = p2 + perp, Color = color });
        v.Add(new Vertex { Position = p1 - perp, Color = color });
        v.Add(new Vertex { Position = p2 - perp, Color = color });
    }

    public void Dispose()
    {
        if (_vertexBuffer.Handle != 0)
        {
            _context.Vk.DeviceWaitIdle(_context.Device);
            _context.Vk.DestroyBuffer(_context.Device, _vertexBuffer, null);
            _context.Vk.FreeMemory(_context.Device, _vertexBufferMemory, null);
        }

        if (_pipeline.Handle != 0) _context.Vk.DestroyPipeline(_context.Device, _pipeline, null);
        if (_pipelineLayout.Handle != 0) _context.Vk.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);

        _textAtlas?.Dispose();
    }
}