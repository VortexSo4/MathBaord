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
    private float _pad0;
    private float _pad1;
    public Vector4 Color;
}

[StructLayout(LayoutKind.Sequential)]
public struct StrokePoint
{
    public Vector2 Pos;
}

[StructLayout(LayoutKind.Sequential)]
public struct StrokeInfo
{
    public uint PointDataOffset;
    public uint PointCount;
    public float Width;
    private float _pad0;
    public Vector4 Color;
    public uint PointVertexOffset;
    public uint SegmentVertexOffset;
    private uint _pad1;
    private uint _pad2;
}

[StructLayout(LayoutKind.Sequential)]
public struct ComputePushConstants
{
    public uint CircleSegments;
    public uint TotalStrokes;
    public uint Mode; // 0 = points, 1 = segments
    public uint Pad;
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

    // Stroke Buffers (GPU Generated)
    private Silk.NET.Vulkan.Buffer _strokeVertexBuffer;
    private DeviceMemory _strokeBufferMemory;
    private ulong _strokeBufferAllocatedSize;
    private uint _strokeVertexCount;

    private Silk.NET.Vulkan.Buffer _strokeInfoBuffer;
    private DeviceMemory _strokeInfoMemory;
    private ulong _strokeInfoAllocatedSize;

    private Silk.NET.Vulkan.Buffer _strokePointBuffer;
    private DeviceMemory _strokePointMemory;
    private ulong _strokePointAllocatedSize;

    // UI Buffers (CPU Generated)
    private Silk.NET.Vulkan.Buffer _uiVertexBuffer;
    private DeviceMemory _uiVertexBufferMemory;
    private ulong _uiVertexBufferAllocatedSize;
    private uint _uiVertexCount;

    private readonly List<Vertex> _uiVertices = [];
    private bool _dirty = true;

    private Extent2D _extent;
    private RadialMenu? _radialMenu;
    private TextAtlas? _textAtlas;
    public TextAtlas TextAtlas => _textAtlas!;

    // Compute Pipeline
    private Pipeline _computePipeline;
    private PipelineLayout _computeLayout;
    private DescriptorSetLayout _computeDescSetLayout;
    private DescriptorPool _computeDescPool;
    private DescriptorSet _computeDescSet;

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
        CreateGraphicsPipeline();
        CreateComputeResources();
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
        _document.IsDirty = true;
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
        else _document.IsDirty = true;
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
            Vector2 minB = _originalBounds.Min;
            Vector2 maxB = _originalBounds.Max;
            float midX = (minB.X + maxB.X) / 2f;
            float midY = (minB.Y + maxB.Y) / 2f;

            Vector2 fixedCorner = Vector2.Zero;
            switch (_activeHandle)
            {
                case 0: fixedCorner = maxB; break;
                case 1: fixedCorner = new Vector2(midX, maxB.Y); break;
                case 2: fixedCorner = new Vector2(minB.X, maxB.Y); break;
                case 3: fixedCorner = new Vector2(maxB.X, midY); break;
                case 4: fixedCorner = new Vector2(minB.X, midY); break;
                case 5: fixedCorner = new Vector2(maxB.X, minB.Y); break;
                case 6: fixedCorner = new Vector2(midX, minB.Y); break;
                case 7: fixedCorner = minB; break;
            }

            Vector2 newMin = minB;
            Vector2 newMax = maxB;
            
            switch (_activeHandle)
            {
                case 0:
                case 3:
                case 5:
                    newMin.X = mouseWorld.X;
                    break;
                case 2:
                case 4:
                case 7:
                    newMax.X = mouseWorld.X;
                    break;
            }
            
            switch (_activeHandle)
            {
                case 0:
                case 1:
                case 2:
                    newMin.Y = mouseWorld.Y;
                    break;
                case 5:
                case 6:
                case 7:
                    newMax.Y = mouseWorld.Y;
                    break;
            }

            Vector2 origSize = maxB - minB;
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
                Stroke? closest = null;
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
            if (Vector2.DistanceSquared(screenPos, handles[i]) < 150f)
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

        var simplified = RDP(stroke.Points, diag * 0.05f);
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
            var merged = (distinct[0] + distinct[^1]) * 0.5f;
            distinct[0] = merged;
            distinct.RemoveAt(distinct.Count - 1);

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
            stroke.Points = GenerateLine(stroke.Points[0], stroke.Points[^1]);
        }
        _dirty = true;
        _document.IsDirty = true;
    }

    private static List<Vector2> FilterCorners(List<Vector2> pts)
    {
        if (pts.Count <= 3) return pts.ToList();

        var result = new List<Vector2>();
        int n = pts.Count;
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

    private List<Vector2> GenerateLine(Vector2 p1, Vector2 p2) => new() { p1, p2 };

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
        const int segments = 64;
        for (int i = 0; i < segments; i++)
        {
            float a = i * MathF.PI * 2f / segments;
            pts.Add(center + new Vector2(MathF.Cos(a) * rx, MathF.Sin(a) * ry));
        }
        pts.Add(pts[0]);
        return pts;
    }

    // ==================== GPU DATA UPLOAD ====================
    private void UpdateStrokeDataBuffers()
    {
        uint totalPoints = 0;
        uint totalSegments = 0;
        foreach (var s in _document.Strokes)
        {
            if (s.Points.Count == 0) continue;
            totalPoints += (uint)s.Points.Count;
            totalSegments += (uint)Math.Max(0, s.Points.Count - 1);
        }

        int circleSegments = Settings.StrokeCircleSegments;
        uint totalVertices = totalPoints * (uint)circleSegments * 3 + totalSegments * 6;

        // Resize Vertex Buffer
        ulong requiredVertexSize = totalVertices * (ulong)sizeof(Vertex);
        if (_strokeVertexBuffer.Handle == 0 || _strokeBufferAllocatedSize < requiredVertexSize)
        {
            if (_strokeVertexBuffer.Handle != 0)
            {
                _context.Vk.DeviceWaitIdle(_context.Device);
                _context.Vk.DestroyBuffer(_context.Device, _strokeVertexBuffer, null);
                _context.Vk.FreeMemory(_context.Device, _strokeBufferMemory, null);
            }
            var allocSize = Math.Max(requiredVertexSize * 2, 1UL << 20);
            CreateStrokeVertexBuffer(allocSize);
            _strokeBufferAllocatedSize = allocSize;
        }
        _strokeVertexCount = totalVertices;

        var infos = new StrokeInfo[_document.Strokes.Count];
        var points = new StrokePoint[totalPoints];

        uint pointDataOffset = 0;
        uint pointVertexOffset = 0;
        uint segmentVertexOffset = totalPoints * (uint)circleSegments * 3;

        int infoIdx = 0;
        int pointIdx = 0;
        foreach (var s in _document.Strokes)
        {
            if (s.Points.Count == 0) continue;

            infos[infoIdx] = new StrokeInfo
            {
                PointDataOffset = pointDataOffset,
                PointCount = (uint)s.Points.Count,
                Width = s.Width,
                Color = s.Color,
                PointVertexOffset = pointVertexOffset,
                SegmentVertexOffset = segmentVertexOffset
            };
            infoIdx++;

            foreach (var p in s.Points)
            {
                points[pointIdx] = new StrokePoint { Pos = p };
                pointIdx++;
            }

            pointDataOffset += (uint)s.Points.Count;
            pointVertexOffset += (uint)s.Points.Count * (uint)circleSegments * 3;
            segmentVertexOffset += (uint)Math.Max(0, s.Points.Count - 1) * 6;
        }

        // Upload StrokeInfo
        ulong infoSize = (ulong)(infos.Length * sizeof(StrokeInfo));
        if (infoSize > 0)
        {
            if (_strokeInfoAllocatedSize < infoSize)
            {
                if (_strokeInfoBuffer.Handle != 0)
                {
                    _context.Vk.DeviceWaitIdle(_context.Device);
                    _context.Vk.DestroyBuffer(_context.Device, _strokeInfoBuffer, null);
                    _context.Vk.FreeMemory(_context.Device, _strokeInfoMemory, null);
                }
                CreateStorageBuffer(Math.Max(infoSize * 2, 256), out _strokeInfoBuffer, out _strokeInfoMemory);
                _strokeInfoAllocatedSize = Math.Max(infoSize * 2, 256);
                UpdateComputeDescriptorSet();
            }

            void* mappedInfo;
            _context.Vk.MapMemory(_context.Device, _strokeInfoMemory, 0, infoSize, 0, &mappedInfo);
            fixed (StrokeInfo* src = infos)
                System.Buffer.MemoryCopy(src, mappedInfo, infoSize, infoSize);
            _context.Vk.UnmapMemory(_context.Device, _strokeInfoMemory);
        }

        // Upload StrokePoints
        ulong pointSize = (ulong)(points.Length * sizeof(StrokePoint));
        if (pointSize > 0)
        {
            if (_strokePointAllocatedSize < pointSize)
            {
                if (_strokePointBuffer.Handle != 0)
                {
                    _context.Vk.DeviceWaitIdle(_context.Device);
                    _context.Vk.DestroyBuffer(_context.Device, _strokePointBuffer, null);
                    _context.Vk.FreeMemory(_context.Device, _strokePointMemory, null);
                }
                CreateStorageBuffer(Math.Max(pointSize * 2, 256), out _strokePointBuffer, out _strokePointMemory);
                _strokePointAllocatedSize = Math.Max(pointSize * 2, 256);
                UpdateComputeDescriptorSet();
            }

            void* mappedPoints;
            _context.Vk.MapMemory(_context.Device, _strokePointMemory, 0, pointSize, 0, &mappedPoints);
            fixed (StrokePoint* src = points)
                System.Buffer.MemoryCopy(src, mappedPoints, pointSize, pointSize);
            _context.Vk.UnmapMemory(_context.Device, _strokePointMemory);
        }
    }

    public void UpdateGeometry()
    {
        if (_dirty)
        {
            UpdateStrokeDataBuffers();

            // UI Vertices still generated on CPU (they change per frame and are simple)
            _uiVertices.Clear();
            _textAtlas?.BeginFrame();

            if (IsSelectMode) RenderSelectionUI(_uiVertices);
            if (_radialMenu?.IsOpen == true) _radialMenu.RenderUI(_uiVertices);
            if (_libraryPanel?.IsOpen == true) _libraryPanel.RenderToVertices(_uiVertices, new Vector2(_extent.Width, _extent.Height));

            UpdateUIVertexBuffer();
            _textAtlas?.UploadFrameVertices();
            _dirty = false;
        }
    }

    private void UpdateUIVertexBuffer()
    {
        _uiVertexCount = (uint)_uiVertices.Count;
        if (_uiVertices.Count == 0) return;

        var requiredSize = (ulong)(sizeof(Vertex) * _uiVertices.Count);
        if (_uiVertexBuffer.Handle == 0 || _uiVertexBufferAllocatedSize < requiredSize)
        {
            if (_uiVertexBuffer.Handle != 0)
            {
                _context.Vk.DeviceWaitIdle(_context.Device);
                _context.Vk.DestroyBuffer(_context.Device, _uiVertexBuffer, null);
                _context.Vk.FreeMemory(_context.Device, _uiVertexBufferMemory, null);
            }
            var allocSize = Math.Max(requiredSize * 2, 1UL << 16);
            CreateBuffer(allocSize, BufferUsageFlags.VertexBufferBit, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _uiVertexBuffer, out _uiVertexBufferMemory);
            _uiVertexBufferAllocatedSize = allocSize;
        }

        void* mapped;
        _context.Vk.MapMemory(_context.Device, _uiVertexBufferMemory, 0, requiredSize, 0, &mapped);
        var span = CollectionsMarshal.AsSpan(_uiVertices);
        fixed (Vertex* src = &MemoryMarshal.GetReference(span))
            System.Buffer.MemoryCopy(src, mapped, requiredSize, requiredSize);
        _context.Vk.UnmapMemory(_context.Device, _uiVertexBufferMemory);
    }

    // ==================== RENDER ====================
    public void Render(CommandBuffer cmd)
    {
        // 1. Dispatch Compute Shader to generate stroke vertices
        DispatchCompute(cmd);

        var vp = stackalloc Viewport[1];
        vp[0] = new Viewport { X = 0, Y = 0, Width = _extent.Width, Height = _extent.Height, MinDepth = 0, MaxDepth = 1 };
        var sc = stackalloc Rect2D[1];
        sc[0] = new Rect2D { Offset = new Offset2D(0, 0), Extent = _extent };
        _context.Vk.CmdSetViewport(cmd, 0, 1, vp);
        _context.Vk.CmdSetScissor(cmd, 0, 1, sc);

        _context.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);

        var offset = 0ul;

        // 2. Draw Strokes (GPU generated)
        if (_strokeVertexCount > 0 && _strokeVertexBuffer.Handle != 0)
        {
            var vbArr = stackalloc Silk.NET.Vulkan.Buffer[1];
            vbArr[0] = _strokeVertexBuffer;
            _context.Vk.CmdBindVertexBuffers(cmd, 0, 1, vbArr, &offset);

            var transform = stackalloc Matrix4x4[1];
            transform[0] = ComputeCameraTransform();
            _context.Vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), transform);
            _context.Vk.CmdDraw(cmd, _strokeVertexCount, 1, 0, 0);
        }

        // 3. Draw UI (CPU generated)
        if (_uiVertexCount > 0 && _uiVertexBuffer.Handle != 0)
        {
            var vbArr = stackalloc Silk.NET.Vulkan.Buffer[1];
            vbArr[0] = _uiVertexBuffer;
            _context.Vk.CmdBindVertexBuffers(cmd, 0, 1, vbArr, &offset);

            var transform = stackalloc Matrix4x4[1];
            transform[0] = Matrix4x4.CreateOrthographicOffCenter(0, _extent.Width, 0, _extent.Height, -1f, 1f);
            _context.Vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(Matrix4x4), transform);
            _context.Vk.CmdDraw(cmd, _uiVertexCount, 1, 0, 0);
        }

        _textAtlas?.Render(cmd, _extent);
    }

    private void DispatchCompute(CommandBuffer cmd)
    {
        if (_strokeVertexCount == 0) return;

        _context.Vk.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _computePipeline);
        
        var descSet = _computeDescSet;
        _context.Vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _computeLayout, 0, 1, &descSet, 0, null);

        var pc = stackalloc ComputePushConstants[1];
        pc[0] = new ComputePushConstants
        {
            CircleSegments = (uint)Settings.StrokeCircleSegments.Value,
            TotalStrokes = (uint)_document.Strokes.Count,
            Mode = 0 // Points
        };
        _context.Vk.CmdPushConstants(cmd, _computeLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(ComputePushConstants), pc);

        uint groupSize = 64;
        uint totalPoints = 0;
        uint totalSegments = 0;
        foreach (var s in _document.Strokes)
        {
            if (s.Points.Count == 0) continue;
            totalPoints += (uint)s.Points.Count;
            totalSegments += (uint)Math.Max(0, s.Points.Count - 1);
        }

        if (totalPoints > 0)
        {
            uint pointGroups = (totalPoints + groupSize - 1) / groupSize;
            _context.Vk.CmdDispatch(cmd, pointGroups, 1, 1);
        }

        if (totalSegments > 0)
        {
            pc[0].Mode = 1; // Segments
            _context.Vk.CmdPushConstants(cmd, _computeLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(ComputePushConstants), pc);
            uint segGroups = (totalSegments + groupSize - 1) / groupSize;
            _context.Vk.CmdDispatch(cmd, segGroups, 1, 1);
        }

        // Memory Barrier: Compute Write -> Vertex Attribute Read
        var barriers = stackalloc BufferMemoryBarrier[1];
        barriers[0] = new BufferMemoryBarrier
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.VertexAttributeReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = _strokeVertexBuffer,
            Offset = 0,
            Size = Vk.WholeSize
        };

        _context.Vk.CmdPipelineBarrier(
            cmd,
            PipelineStageFlags.ComputeShaderBit,
            PipelineStageFlags.VertexInputBit,
            0,
            0, null,
            1, barriers,
            0, null);
    }

    private Matrix4x4 ComputeCameraTransform()
    {
        float z = _camera.Zoom;
        var cam = _camera.Position;
        var view = Matrix4x4.CreateScale(z, z, 1f) * Matrix4x4.CreateTranslation(cam.X, cam.Y, 0f);
        var proj = Matrix4x4.CreateOrthographicOffCenter(0, _extent.Width, 0, _extent.Height, -1f, 1f);
        return view * proj;
    }

    // ==================== Vulkan Pipelines ====================
    private void CreateGraphicsPipeline()
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

        var vp = stackalloc Viewport[1];
        vp[0] = new Viewport { Width = 1, Height = 1, MinDepth = 0, MaxDepth = 1 };
        var sc = stackalloc Rect2D[1];
        sc[0] = new Rect2D { Extent = new Extent2D { Width = 1, Height = 1 } };
        var viewportState = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, PViewports = vp, ScissorCount = 1, PScissors = sc };

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

    private void CreateComputeResources()
    {
        // 1. Descriptor Set Layout
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        bindings[0] = new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit };
        bindings[1] = new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit };
        bindings[2] = new DescriptorSetLayoutBinding { Binding = 2, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit };

        var layoutInfo = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 3, PBindings = bindings };
        _context.Vk.CreateDescriptorSetLayout(_context.Device, &layoutInfo, null, out _computeDescSetLayout);

        // 2. Descriptor Pool
        var poolSize = new DescriptorPoolSize { Type = DescriptorType.StorageBuffer, DescriptorCount = 3 };
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 1, PPoolSizes = &poolSize, MaxSets = 1 };
        _context.Vk.CreateDescriptorPool(_context.Device, &poolInfo, null, out _computeDescPool);

        // 3. Descriptor Set
        fixed (DescriptorSetLayout* pSetLayout = &_computeDescSetLayout)
{
    var allocInfo = new DescriptorSetAllocateInfo
    {
        SType = StructureType.DescriptorSetAllocateInfo,
        DescriptorPool = _computeDescPool,
        DescriptorSetCount = 1,
        PSetLayouts = pSetLayout
    };
    _context.Vk.AllocateDescriptorSets(_context.Device, &allocInfo, out _computeDescSet);
}

        // 4. Compute Pipeline
        var computeShader = LoadShader("Shaders/stroke.comp.spv");
        var stage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.ComputeBit,
            Module = computeShader,
            PName = (byte*)SilkMarshal.StringToPtr("main")
        };

        var pushConstant = new PushConstantRange { StageFlags = ShaderStageFlags.ComputeBit, Size = (uint)sizeof(ComputePushConstants) };
        fixed (DescriptorSetLayout* pSetLayout = &_computeDescSetLayout)
        {
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = 1,
                PSetLayouts = pSetLayout,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstant
            };
            _context.Vk.CreatePipelineLayout(_context.Device, &pipelineLayoutInfo, null, out _computeLayout);
        }

        var pipelineInfo = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = stage,
            Layout = _computeLayout
        };
        _context.Vk.CreateComputePipelines(_context.Device, default, 1, &pipelineInfo, null, out _computePipeline);

        SilkMarshal.Free((nint)stage.PName);
        _context.Vk.DestroyShaderModule(_context.Device, computeShader, null);
    }

    private void UpdateComputeDescriptorSet()
    {
        var bufferInfos = stackalloc DescriptorBufferInfo[3];
        bufferInfos[0] = new DescriptorBufferInfo { Buffer = _strokeInfoBuffer, Offset = 0, Range = Vk.WholeSize };
        bufferInfos[1] = new DescriptorBufferInfo { Buffer = _strokePointBuffer, Offset = 0, Range = Vk.WholeSize };
        bufferInfos[2] = new DescriptorBufferInfo { Buffer = _strokeVertexBuffer, Offset = 0, Range = Vk.WholeSize };

        var writes = stackalloc WriteDescriptorSet[3];
        for (int i = 0; i < 3; i++)
        {
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _computeDescSet,
                DstBinding = (uint)i,
                DstArrayElement = 0,
                DescriptorType = DescriptorType.StorageBuffer,
                DescriptorCount = 1,
                PBufferInfo = bufferInfos + i
            };
        }
        _context.Vk.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
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

    private void CreateStrokeVertexBuffer(ulong size)
    {
        var bufferInfo = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit, SharingMode = SharingMode.Exclusive };
        _context.Vk.CreateBuffer(_context.Device, &bufferInfo, null, out _strokeVertexBuffer);

        MemoryRequirements memReq;
        _context.Vk.GetBufferMemoryRequirements(_context.Device, _strokeVertexBuffer, &memReq);

        var allocInfo = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = memReq.Size, MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit) };
        _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out _strokeBufferMemory);
        _context.Vk.BindBufferMemory(_context.Device, _strokeVertexBuffer, _strokeBufferMemory, 0);

        UpdateComputeDescriptorSet();
    }

    private void CreateStorageBuffer(ulong size, out Silk.NET.Vulkan.Buffer buffer, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = size, Usage = BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit, SharingMode = SharingMode.Exclusive };
        _context.Vk.CreateBuffer(_context.Device, &bufferInfo, null, out buffer);

        MemoryRequirements memReq;
        _context.Vk.GetBufferMemoryRequirements(_context.Device, buffer, &memReq);

        var allocInfo = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = memReq.Size, MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit) };
        _context.Vk.AllocateMemory(_context.Device, &allocInfo, null, out memory);
        _context.Vk.BindBufferMemory(_context.Device, buffer, memory, 0);
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
    // (Оставлены без изменений для UI)
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
        _context.Vk.DeviceWaitIdle(_context.Device);

        if (_strokeVertexBuffer.Handle != 0) { _context.Vk.DestroyBuffer(_context.Device, _strokeVertexBuffer, null); _context.Vk.FreeMemory(_context.Device, _strokeBufferMemory, null); }
        if (_strokeInfoBuffer.Handle != 0) { _context.Vk.DestroyBuffer(_context.Device, _strokeInfoBuffer, null); _context.Vk.FreeMemory(_context.Device, _strokeInfoMemory, null); }
        if (_strokePointBuffer.Handle != 0) { _context.Vk.DestroyBuffer(_context.Device, _strokePointBuffer, null); _context.Vk.FreeMemory(_context.Device, _strokePointMemory, null); }
        if (_uiVertexBuffer.Handle != 0) { _context.Vk.DestroyBuffer(_context.Device, _uiVertexBuffer, null); _context.Vk.FreeMemory(_context.Device, _uiVertexBufferMemory, null); }

        if (_pipeline.Handle != 0) _context.Vk.DestroyPipeline(_context.Device, _pipeline, null);
        if (_pipelineLayout.Handle != 0) _context.Vk.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        if (_computePipeline.Handle != 0) _context.Vk.DestroyPipeline(_context.Device, _computePipeline, null);
        if (_computeLayout.Handle != 0) _context.Vk.DestroyPipelineLayout(_context.Device, _computeLayout, null);
        if (_computeDescPool.Handle != 0) _context.Vk.DestroyDescriptorPool(_context.Device, _computeDescPool, null);
        if (_computeDescSetLayout.Handle != 0) _context.Vk.DestroyDescriptorSetLayout(_context.Device, _computeDescSetLayout, null);

        _textAtlas?.Dispose();
    }
}