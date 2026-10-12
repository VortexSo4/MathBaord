using System.Numerics;
using MathBoard.Graphics;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;

namespace MathBoard.Backend.OpenGL;

/// <summary>
/// OpenGL 2.1 / GLSL 1.20 backend for old integrated GPUs without Vulkan. Without instancing every primitive is
/// expanded into four vertices on the CPU; stroke vertices stay on the GPU and only changed ranges are re-uploaded.
/// </summary>
public sealed unsafe class OpenGLBackend : IRenderBackend
{
    // corner(2) + A(4) + B(4) + color(4) + extra(4) + clip(4)
    private const int FloatsPerVertex = 22;
    private const int FloatsPerPrim = FloatsPerVertex * 4;
    // GL_ALPHA8 is a compatibility-profile format that Silk.NET's enums leave out.
    private const InternalFormat AlphaInternalFormat = (InternalFormat)0x803C;
    private static readonly string[] Attributes = ["aCorner", "aA", "aB", "aColor", "aExtra", "aClip"];
    private static readonly int[] AttributeSizes = [2, 4, 4, 4, 4, 4];

    private IWindow? _window;
    private GL? _gl;
    private uint _program;
    private int _transformLocation, _pixelLocation;
    private uint _strokeVbo, _uiVbo, _indexBuffer, _atlasTexture;
    private int _strokeCapacity, _indexCapacity, _atlasSize;
    private int _strokeCount, _uiCount;
    private float[] _scratch = [];

    public string Name => "OpenGL";
    public int Priority => 200;

    public WindowOptions ConfigureWindow(WindowOptions options) => options with
    {
        API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Compatability, ContextFlags.Default, new APIVersion(2, 1)),
        VSync = true
    };

    public void Initialize(IWindow window)
    {
        _window = window;
        _gl = GL.GetApi(window);
        var gl = _gl;

        string version = gl.GetStringS(StringName.Version) ?? "";
        if (!int.TryParse(version.Split('.')[0], out int major) || major < 2)
            throw new NotSupportedException($"OpenGL 2.0 or newer is required, the driver reports '{version}'");
        Console.WriteLine($"OpenGL: {version}, {gl.GetStringS(StringName.Renderer)}");

        _program = CreateProgram();
        _transformLocation = gl.GetUniformLocation(_program, "uTransform");
        _pixelLocation = gl.GetUniformLocation(_program, "uPixel");

        _strokeVbo = gl.GenBuffer();
        _uiVbo = gl.GenBuffer();
        _indexBuffer = gl.GenBuffer();
        _atlasTexture = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, _atlasTexture);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
    }

    public bool Render(in FrameData frame)
    {
        var gl = _gl!;
        var size = _window!.FramebufferSize;
        if (size.X == 0 || size.Y == 0) return false;

        UploadAtlas(frame);
        UploadStrokes(frame.Strokes, frame.StrokesDirtyFrom);
        _uiCount = frame.Ui.Length;
        Upload(_uiVbo, frame.Ui, 0, frame.Ui.Length, reallocate: true);
        EnsureIndices(Math.Max(_strokeCount, _uiCount));

        gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        gl.ClearColor(frame.ClearColor.X, frame.ClearColor.Y, frame.ClearColor.Z, frame.ClearColor.W);
        gl.Clear(ClearBufferMask.ColorBufferBit);
        gl.Enable(EnableCap.Blend);
        gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
        gl.UseProgram(_program);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.BindTexture(TextureTarget.Texture2D, _atlasTexture);
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _indexBuffer);

        float scale = frame.UiScale;
        float zoom = frame.CameraZoom * scale;
        var cam = frame.CameraPosition * scale;
        // OpenGL clip space has y pointing up, so the top of the framebuffer is mapped to +1.
        var proj = Matrix4x4.CreateOrthographicOffCenter(0, size.X, size.Y, 0, -1f, 1f);
        Draw(_strokeVbo, _strokeCount,
            Matrix4x4.CreateScale(zoom, zoom, 1f) * Matrix4x4.CreateTranslation(cam.X, cam.Y, 0f) * proj, 1f / zoom);
        Draw(_uiVbo, _uiCount, Matrix4x4.CreateScale(scale, scale, 1f) * proj, 1f / scale);

        _window.GLContext!.SwapBuffers();
        return true;
    }

    private void Draw(uint vbo, int primCount, Matrix4x4 transform, float pixel)
    {
        if (primCount == 0) return;
        var gl = _gl!;
        gl.UniformMatrix4(_transformLocation, 1, false, (float*)&transform);
        gl.Uniform1(_pixelLocation, pixel);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);

        uint offset = 0;
        for (uint i = 0; i < Attributes.Length; i++)
        {
            gl.EnableVertexAttribArray(i);
            gl.VertexAttribPointer(i, AttributeSizes[i], VertexAttribPointerType.Float, false,
                FloatsPerVertex * sizeof(float), (void*)(offset * sizeof(float)));
            offset += (uint)AttributeSizes[i];
        }

        gl.DrawElements(PrimitiveType.Triangles, (uint)(primCount * 6), DrawElementsType.UnsignedInt, null);
    }

    private void UploadStrokes(ReadOnlySpan<Prim> strokes, int dirtyFrom)
    {
        bool grow = strokes.Length > _strokeCapacity;
        if (grow) _strokeCapacity = Math.Max(strokes.Length + strokes.Length / 2, 4096);
        Upload(_strokeVbo, strokes, grow ? 0 : Math.Clamp(dirtyFrom, 0, strokes.Length), _strokeCapacity, grow);
        _strokeCount = strokes.Length;
    }

    /// <summary>Writes primitives [from, prims.Length) into the buffer, reallocating it to hold <paramref name="capacity"/> first if asked.</summary>
    private void Upload(uint vbo, ReadOnlySpan<Prim> prims, int from, int capacity, bool reallocate)
    {
        var gl = _gl!;
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        if (reallocate)
            gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(Math.Max(capacity, 1) * FloatsPerPrim * sizeof(float)), null, BufferUsageARB.DynamicDraw);

        int count = prims.Length - from;
        if (count <= 0) return;
        if (_scratch.Length < count * FloatsPerPrim) _scratch = new float[count * FloatsPerPrim * 3 / 2];

        for (int i = 0; i < count; i++)
        {
            var p = prims[from + i];
            int baseIndex = i * FloatsPerPrim;
            for (int corner = 0; corner < 4; corner++)
            {
                int o = baseIndex + corner * FloatsPerVertex;
                _scratch[o] = corner is 1 or 2 ? 1f : 0f;
                _scratch[o + 1] = corner >= 2 ? 1f : 0f;
                Write(o + 2, p.A);
                Write(o + 6, p.B);
                Write(o + 10, p.Color);
                Write(o + 14, p.Extra);
                Write(o + 18, p.Clip);
            }
        }

        fixed (float* data = _scratch)
            gl.BufferSubData(BufferTargetARB.ArrayBuffer, from * FloatsPerPrim * sizeof(float),
                (nuint)(count * FloatsPerPrim * sizeof(float)), data);
    }

    private void Write(int offset, Vector4 v)
    {
        _scratch[offset] = v.X;
        _scratch[offset + 1] = v.Y;
        _scratch[offset + 2] = v.Z;
        _scratch[offset + 3] = v.W;
    }

    private void EnsureIndices(int primCount)
    {
        if (primCount <= _indexCapacity) return;
        _indexCapacity = Math.Max(primCount + primCount / 2, 4096);
        var indices = new uint[_indexCapacity * 6];
        for (uint i = 0; i < _indexCapacity; i++)
        {
            uint v = i * 4, o = i * 6;
            indices[o] = v;
            indices[o + 1] = v + 1;
            indices[o + 2] = v + 2;
            indices[o + 3] = v;
            indices[o + 4] = v + 2;
            indices[o + 5] = v + 3;
        }

        var gl = _gl!;
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _indexBuffer);
        fixed (uint* data = indices)
            gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indices.Length * sizeof(uint)), data, BufferUsageARB.StaticDraw);
    }

    private void UploadAtlas(in FrameData frame)
    {
        var gl = _gl!;
        gl.BindTexture(TextureTarget.Texture2D, _atlasTexture);
        int size = frame.AtlasSize;
        fixed (byte* pixels = frame.AtlasPixels)
        {
            if (size != _atlasSize)
            {
                _atlasSize = size;
                gl.TexImage2D(TextureTarget.Texture2D, 0, AlphaInternalFormat, (uint)size, (uint)size, 0,
                    PixelFormat.Alpha, PixelType.UnsignedByte, pixels);
                return;
            }

            int top = frame.AtlasDirtyTop, bottom = frame.AtlasDirtyBottom;
            if (bottom > top)
                gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, top, (uint)size, (uint)(bottom - top),
                    PixelFormat.Alpha, PixelType.UnsignedByte, pixels + top * size);
        }
    }

    private uint CreateProgram()
    {
        var gl = _gl!;
        uint vertex = CompileShader(ShaderType.VertexShader, Shaders.Vertex);
        uint fragment = CompileShader(ShaderType.FragmentShader, Shaders.Fragment);
        uint program = gl.CreateProgram();
        gl.AttachShader(program, vertex);
        gl.AttachShader(program, fragment);
        for (uint i = 0; i < Attributes.Length; i++)
            gl.BindAttribLocation(program, i, Attributes[i]);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out int linked);
        if (linked == 0) throw new NotSupportedException("Shader link failed: " + gl.GetProgramInfoLog(program));
        gl.DeleteShader(vertex);
        gl.DeleteShader(fragment);

        gl.UseProgram(program);
        gl.Uniform1(gl.GetUniformLocation(program, "uAtlas"), 0);
        return program;
    }

    private uint CompileShader(ShaderType type, string source)
    {
        var gl = _gl!;
        uint shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out int compiled);
        if (compiled == 0) throw new NotSupportedException($"{type} compile failed: " + gl.GetShaderInfoLog(shader));
        return shader;
    }

    public void Dispose()
    {
        if (_gl == null) return;
        if (_program != 0) _gl.DeleteProgram(_program);
        if (_strokeVbo != 0) _gl.DeleteBuffer(_strokeVbo);
        if (_uiVbo != 0) _gl.DeleteBuffer(_uiVbo);
        if (_indexBuffer != 0) _gl.DeleteBuffer(_indexBuffer);
        if (_atlasTexture != 0) _gl.DeleteTexture(_atlasTexture);
        _gl.Dispose();
        _gl = null;
    }
}
