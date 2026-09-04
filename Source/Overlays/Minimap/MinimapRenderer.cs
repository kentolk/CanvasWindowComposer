using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.D3DCompiler;

namespace CanvasDesktop;

/// <summary>
/// D3D11 minimap renderer. UI thread pushes a projected snapshot
/// (<see cref="UpdateSnapshot"/>) and the render thread draws at vsync.
/// No paint work on the UI thread; mirrors <see cref="GridRenderer"/>'s
/// thread + swap-chain structure.
/// </summary>
/// <summary>
/// One window as the minimap needs it: where it sits in the world, and which
/// atlas slot holds its application icon (-1 when we could not read one).
/// </summary>
internal readonly record struct MinimapWindow(WorldRect Rect, int IconSlot);

internal sealed class MinimapRenderer
 : IDisposable
{
    private const int FullscreenTriangleVertexCount = 3;
    private const int VsyncInterval = 1;
    private const int RenderThreadJoinTimeoutMs = 1000;
    private const int CbAlignmentMask = 15;

    // Up to 256 windows shown. Anything past that is dropped silently — a
    // minimap that dense isn't useful anyway.
    private const int WindowBufferCapacity = 32700;
    // float2 mn, float2 mx, float pinned, float iconSlot
    private const int WindowStructBytes = 24;
    private const int WindowStructFloats = WindowStructBytes / 4;

    /// <summary>Edge length of one icon in the atlas, in pixels.</summary>
    public const int IconPx = 16;
    private const int IconsPerRow = 16;

    /// <summary>How many distinct application icons the atlas can hold.</summary>
    public const int IconSlotCount = IconsPerRow * IconsPerRow;
    private const int IconAtlasPx = IconPx * IconsPerRow;



    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11Buffer? _windowsBuffer;
    private ID3D11ShaderResourceView? _windowsSrv;
    private ID3D11Texture2D? _iconAtlas;
    private ID3D11ShaderResourceView? _iconAtlasSrv;
    private ID3D11SamplerState? _iconSampler;

    private int _width, _height;

    [StructLayout(LayoutKind.Sequential)]
    private struct MinimapConstants
    {
        public float ScreenW, ScreenH;
        public float MapOriginX, MapOriginY;
        public float MapW, MapH;
        public int WindowCount;
        public int _pad0;
        public float ViewportMinX, ViewportMinY, ViewportMaxX, ViewportMaxY;
    }

    private static byte[]? _vsBytecode;
    private static byte[]? _psBytecode;

    public static bool CompileShaders()
    {
        Compiler.Compile(ShaderSource, "VSMain", "", "vs_5_0", out var vsBlob, out var vsErr);
        if (vsBlob == null) { vsErr?.Dispose(); return false; }

        Compiler.Compile(ShaderSource, "PSMain", "", "ps_5_0", out var psBlob, out var psErr);
        if (psBlob == null) { vsBlob.Dispose(); psErr?.Dispose(); return false; }

        _vsBytecode = vsBlob.AsSpan().ToArray();
        _psBytecode = psBlob.AsSpan().ToArray();

        vsBlob.Dispose();
        psBlob.Dispose();
        return true;
    }

    private const string ShaderSource = @"
cbuffer MinimapCB : register(b0)
{
    float screenW;
    float screenH;
    float mapOriginX;
    float mapOriginY;
    float mapW;
    float mapH;
    int windowCount;
    int _pad0;
    float4 viewportRect; // minX, minY, maxX, maxY in map-local pixels
};

struct MapRect
{
    float2 mn;
    float2 mx;
    float  pinned;
    float  iconSlot; // index into the icon atlas, negative when unknown
};

StructuredBuffer<MapRect> windows : register(t0);

Texture2D iconAtlas : register(t1);
SamplerState iconSampler : register(s0);

static const float ICON_PX = 16.0;
static const float ICONS_PER_ROW = 16.0;
static const float ATLAS_PX = 256.0;

// Sample one atlas slot. `t` is 0..1 within the icon; inset by half a texel so
// bilinear filtering cannot bleed a neighbouring slot in at the edges.
float4 sampleIcon(float slot, float2 t)
{
    float2 inset = clamp(t, 0.5 / ICON_PX, 1.0 - 0.5 / ICON_PX);
    float col = fmod(slot, ICONS_PER_ROW);
    float row = floor(slot / ICONS_PER_ROW);
    float2 uv = (float2(col, row) + inset) * (ICON_PX / ATLAS_PX);
    return iconAtlas.SampleLevel(iconSampler, uv, 0);
}


struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * 2.0 - 1.0, 0.0, 1.0);
    o.pos.y = -o.pos.y;
    return o;
}

// Signed distance from p to a rounded rect [mn, mx] with corner radius r.
// Negative inside, positive outside, zero on the edge.
float sdfRoundedRect(float2 p, float2 mn, float2 mx, float r)
{
    float2 halfSize = (mx - mn) * 0.5;
    float2 center = (mx + mn) * 0.5;
    // Clamp radius to the smaller half-extent so small rects don't invert.
    r = min(r, min(halfSize.x, halfSize.y));
    float2 q = abs(p - center) - halfSize + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

float4 PSMain(VSOut input) : SV_Target
{
    float2 screenPos = input.uv * float2(screenW, screenH);
    float2 mapPos = screenPos - float2(mapOriginX, mapOriginY);
    float2 mapSize = float2(mapW, mapH);

    bool insideMap = mapPos.x >= 0 && mapPos.y >= 0 && mapPos.x <= mapSize.x && mapPos.y <= mapSize.y;

    float3 color = float3(0.118, 0.118, 0.118);
    if (insideMap)
    {
        color = float3(0.078, 0.078, 0.078);

        // Windows are iterated topmost-first (matching canvas z-order). First
        // window containing the pixel wins — higher windows visually occlude
        // the ones below.
        const float cornerRadius = 2.5;
        float3 winFill = float3(0.31, 0.63, 1.0) * 0.55;
        float3 winEdge = float3(0.39, 0.71, 1.0);
        // Pinned windows are screen-fixed and no longer move with the canvas.
        // Red so that state is obvious at a glance rather than something you
        // discover by wondering why one window refuses to move.
        float3 pinFill = float3(1.0, 0.32, 0.32) * 0.55;
        float3 pinEdge = float3(1.0, 0.45, 0.42);
        for (int i = 0; i < windowCount; i++)
        {
            MapRect w = windows[i];
            float sd = sdfRoundedRect(mapPos, w.mn, w.mx, cornerRadius);
            if (sd >= 0.5) continue; // fully outside this window (with AA margin)
            float f = 1.0 - smoothstep(-0.5, 0.5, sd);
            float e = 1.0 - smoothstep(0.0, 1.5, abs(sd + 0.75));
            float3 fill = w.pinned > 0.5 ? pinFill : winFill;
            float3 edge = w.pinned > 0.5 ? pinEdge : winEdge;
            color = lerp(color, lerp(fill, edge, e), f);

            // App icon, centred. Below ~10px of window there is no room for it
            // to read as anything, so it is skipped rather than drawn as mush.
            float2 sz = w.mx - w.mn;
            float shortest = min(sz.x, sz.y);
            if (w.iconSlot >= 0.0 && shortest >= 10.0)
            {
                float box = clamp(shortest * 0.55, 8.0, 20.0);
                float2 ctr = (w.mn + w.mx) * 0.5;
                float2 bmn = ctr - box * 0.5;
                if (mapPos.x >= bmn.x && mapPos.x <= bmn.x + box &&
                    mapPos.y >= bmn.y && mapPos.y <= bmn.y + box)
                {
                    float4 icon = sampleIcon(w.iconSlot, (mapPos - bmn) / box);
                    color = lerp(color, icon.rgb, icon.a);
                }
            }
            break;


        }

        // Viewport outline (sharp corners).
        float2 vmn = viewportRect.xy;
        float2 vmx = viewportRect.zw;
        if (mapPos.x >= vmn.x && mapPos.y >= vmn.y &&
            mapPos.x <= vmx.x && mapPos.y <= vmx.y)
        {
            float2 dMn = mapPos - vmn;
            float2 dMx = vmx - mapPos;
            float d = min(min(dMn.x, dMx.x), min(dMn.y, dMx.y));
            if (d < 1.5) color = float3(1.0, 0.78, 0.20);
        }

        // Inner border.
        float2 dMnB = mapPos;
        float2 dMxB = mapSize - mapPos;
        float dB = min(min(dMnB.x, dMxB.x), min(dMnB.y, dMxB.y));
        if (dB < 1.0) color = float3(0.5, 0.5, 0.5);
    }

    return float4(color, 1.0);
}
";

    public bool Initialize(IntPtr hwnd, int width, int height, int mapOriginX, int mapOriginY, int mapW, int mapH)
    {
        _width = width;
        _height = height;
        _mapOriginX = mapOriginX;
        _mapOriginY = mapOriginY;
        _mapW = mapW;
        _mapH = mapH;

        var swapDesc = new SwapChainDescription
        {
            BufferCount = 1,
            BufferDescription = new ModeDescription((uint)width, (uint)height, Format.R8G8B8A8_UNorm),
            BufferUsage = Usage.RenderTargetOutput,
            OutputWindow = hwnd,
            SampleDescription = new SampleDescription(1, 0),
            Windowed = true,
            SwapEffect = SwapEffect.Discard
        };

        var hr = D3D11.D3D11CreateDeviceAndSwapChain(
            null!, DriverType.Hardware, DeviceCreationFlags.None, null!,
            swapDesc, out _swapChain, out _device, out _, out _context);

        if (hr.Failure) return false;

        CreateRenderTarget();
        if (!CreateShaders()) return false;
        CreateConstantBuffer();
        CreateWindowsBuffer();
        CreateIconAtlas();

        return true;
    }

    private void CreateRenderTarget()
    {
        using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(backBuffer);
    }

    private bool CreateShaders()
    {
        if (_vsBytecode == null || _psBytecode == null) return false;
        _vertexShader = _device!.CreateVertexShader(_vsBytecode);
        _pixelShader = _device.CreatePixelShader(_psBytecode);
        return true;
    }

    private void CreateConstantBuffer()
    {
        int cbSize = (Marshal.SizeOf<MinimapConstants>() + CbAlignmentMask) & ~CbAlignmentMask;
        _constantBuffer = _device!.CreateBuffer(new BufferDescription(
            (uint)cbSize,
            BindFlags.ConstantBuffer,
            ResourceUsage.Dynamic,
            CpuAccessFlags.Write));
    }

    private void CreateWindowsBuffer()
    {
        var desc = new BufferDescription
        {
            ByteWidth = (uint)(WindowBufferCapacity * WindowStructBytes),
            BindFlags = BindFlags.ShaderResource,
            Usage = ResourceUsage.Dynamic,
            CPUAccessFlags = CpuAccessFlags.Write,
            MiscFlags = ResourceOptionFlags.BufferStructured,
            StructureByteStride = (uint)WindowStructBytes
        };
        _windowsBuffer = _device!.CreateBuffer(desc);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.Unknown,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Buffer = new BufferShaderResourceView { FirstElement = 0, NumElements = (uint)WindowBufferCapacity }
        };
        _windowsSrv = _device.CreateShaderResourceView(_windowsBuffer, srvDesc);
    }

    private void CreateIconAtlas()
    {
        var desc = new Texture2DDescription
        {
            Width = IconAtlasPx,
            Height = IconAtlasPx,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        };
        _iconAtlas = _device!.CreateTexture2D(desc);
        _iconAtlasSrv = _device.CreateShaderResourceView(_iconAtlas);

        _iconSampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MaxLOD = float.MaxValue
        });
    }

    // Icon pixels arrive on the UI thread but the D3D context belongs to the
    // render thread and is not thread-safe, so uploads are queued and drained
    // inside RenderFrame.
    private readonly object _iconQueueLock = new();
    private readonly Queue<(int Slot, byte[] Pixels)> _pendingIcons = new();

    /// <summary>Queue an icon for <paramref name="slot"/>. Safe to call from any thread.</summary>
    public void SetIcon(int slot, byte[] bgra)
    {
        if (slot < 0 || slot >= IconSlotCount) return;
        if (bgra.Length < IconPx * IconPx * 4) return;
        lock (_iconQueueLock)
        {
            _pendingIcons.Enqueue((slot, bgra));
        }
    }

    private void DrainIconUploads()
    {
        while (true)
        {
            (int Slot, byte[] Pixels) job;
            lock (_iconQueueLock)
            {
                if (_pendingIcons.Count == 0) return;
                job = _pendingIcons.Dequeue();
            }

            int x = (job.Slot % IconsPerRow) * IconPx;
            int y = (job.Slot / IconsPerRow) * IconPx;
            var region = new Vortice.Mathematics.Box(x, y, 0, x + IconPx, y + IconPx, 1);

            _context!.UpdateSubresource(job.Pixels, _iconAtlas!, 0,
                IconPx * 4, IconPx * IconPx * 4, region);

        }
    }


    public Action? OnFrameTick;

    private volatile bool _running;
    private volatile bool _alive = true;
    private volatile bool _renderThreadIdle = true;
    private readonly System.Threading.ManualResetEventSlim _wakeEvent = new(false);
    private System.Threading.Thread? _renderThread;

    // Fixed from Initialize. Set once before any render, so no volatile.
    private int _mapOriginX, _mapOriginY, _mapW, _mapH;

    // Shader-facing state (UI thread writes, render thread reads).
    private volatile float _vpMinX, _vpMinY, _vpMaxX, _vpMaxY;
    private readonly float[] _windowRects = new float[WindowBufferCapacity * WindowStructFloats];

    private volatile int _windowCount;
    private readonly object _windowsLock = new();

    /// <summary>
    /// Project the canvas into map-pixel rects and hand them to the renderer.
    /// Called from the UI thread; the render thread picks up the new data on
    /// the next vsync. <paramref name="windows"/> should be topmost-first so
    /// the shader's first-hit wins matches OS z-order.
    /// </summary>
    public void UpdateSnapshot(
        IReadOnlyList<MinimapWindow> windows,

        (double minX, double minY, double maxX, double maxY)? extents,
        (double x, double y, double w, double h) viewport,
        double extentsPadding = 0.10,
        int minRectSizePx = 2)
    {
        // Frame = extents ∪ viewport, padded. If there are no windows yet,
        // viewport alone defines the frame.
        double minX = viewport.x;
        double minY = viewport.y;
        double maxX = viewport.x + viewport.w;
        double maxY = viewport.y + viewport.h;
        if (extents is var (eMinX, eMinY, eMaxX, eMaxY))
        {
            minX = Math.Min(minX, eMinX);
            minY = Math.Min(minY, eMinY);
            maxX = Math.Max(maxX, eMaxX);
            maxY = Math.Max(maxY, eMaxY);
        }

        double worldW = maxX - minX;
        double worldH = maxY - minY;
        minX -= worldW * extentsPadding; maxX += worldW * extentsPadding;
        minY -= worldH * extentsPadding; maxY += worldH * extentsPadding;
        worldW = maxX - minX;
        worldH = maxY - minY;

        if (worldW < 1 || worldH < 1)
        {
            _windowCount = 0;
            return;
        }

        double scale = Math.Min((_mapW - 2) / worldW, (_mapH - 2) / worldH);
        double offX = (_mapW - worldW * scale) / 2;
        double offY = (_mapH - worldH * scale) / 2;

        _vpMinX = (float)(offX + (viewport.x - minX) * scale);
        _vpMinY = (float)(offY + (viewport.y - minY) * scale);
        _vpMaxX = (float)(offX + (viewport.x + viewport.w - minX) * scale);
        _vpMaxY = (float)(offY + (viewport.y + viewport.h - minY) * scale);

        lock (_windowsLock)
        {
            int count = 0;
            foreach (var entry in windows)
            {
                if (count >= WindowBufferCapacity) break;
                var w = entry.Rect;
                if (w.State != WindowState.Normal) continue;

                float mnX = (float)(offX + (w.X - minX) * scale);
                float mnY = (float)(offY + (w.Y - minY) * scale);
                float mxX = (float)(offX + (w.X + w.W - minX) * scale);
                float mxY = (float)(offY + (w.Y + w.H - minY) * scale);

                if (mxX - mnX < minRectSizePx) mxX = mnX + minRectSizePx;
                if (mxY - mnY < minRectSizePx) mxY = mnY + minRectSizePx;

                int o = count * WindowStructFloats;
                _windowRects[o]     = mnX;
                _windowRects[o + 1] = mnY;
                _windowRects[o + 2] = mxX;
                _windowRects[o + 3] = mxY;
                _windowRects[o + 4] = w.PinnedToScreen ? 1f : 0f;
                _windowRects[o + 5] = entry.IconSlot;
                count++;

            }
            _windowCount = count;
        }
    }

    public void Start()
    {
        _running = true;
        _wakeEvent.Set();
    }

    public void Stop()
    {
        _running = false;
    }

    public void StartThread()
    {
        _renderThread = new System.Threading.Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "MinimapRenderer"
        };
        _renderThread.Start();
    }

    private void RenderLoop()
    {
        while (_alive)
        {
            _renderThreadIdle = true;
            _wakeEvent.Wait();
            _renderThreadIdle = false;

            while (_running && _alive)
            {
                RenderFrame();
            }

            _wakeEvent.Reset();
        }
        _renderThreadIdle = true;
    }

    public void Resize(int width, int height)
    {
        if (_swapChain == null || (_width == width && _height == height)) return;

        bool wasRunning = _running;
        _running = false;
        _wakeEvent.Reset();
        while (!_renderThreadIdle) System.Threading.Thread.Yield();

        _width = width;
        _height = height;
        _rtv?.Dispose();
        _rtv = null;
        _swapChain.ResizeBuffers(1, (uint)width, (uint)height, Format.R8G8B8A8_UNorm, 0);
        CreateRenderTarget();

        if (wasRunning)
        {
            _running = true;
            _wakeEvent.Set();
        }
    }

    private void RenderFrame()
    {
        if (_context == null || _swapChain == null) return;

        var cbMapped = _context.Map(_constantBuffer!, MapMode.WriteDiscard);
        var constants = new MinimapConstants
        {
            ScreenW = _width,
            ScreenH = _height,
            MapOriginX = _mapOriginX,
            MapOriginY = _mapOriginY,
            MapW = _mapW,
            MapH = _mapH,
            WindowCount = _windowCount,
            ViewportMinX = _vpMinX,
            ViewportMinY = _vpMinY,
            ViewportMaxX = _vpMaxX,
            ViewportMaxY = _vpMaxY
        };
        Marshal.StructureToPtr(constants, cbMapped.DataPointer, false);
        _context.Unmap(_constantBuffer!);

        if (_windowCount > 0)
        {
            var mapped = _context.Map(_windowsBuffer!, MapMode.WriteDiscard);
            lock (_windowsLock)
            {
                Marshal.Copy(_windowRects, 0, mapped.DataPointer, _windowCount * WindowStructFloats);

            }
            _context.Unmap(_windowsBuffer!);
        }

        DrainIconUploads();

        _context.OMSetRenderTargets(_rtv!);

        _context.RSSetViewport(0, 0, _width, _height);

        _context.VSSetShader(_vertexShader);
        _context.PSSetShader(_pixelShader);
        _context.PSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShaderResource(0, _windowsSrv!);
        _context.PSSetShaderResource(1, _iconAtlasSrv!);
        _context.PSSetSampler(0, _iconSampler!);

        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.Draw(FullscreenTriangleVertexCount, 0);

        _swapChain.Present(VsyncInterval, PresentFlags.None);
        OnFrameTick?.Invoke();
    }

    public void Dispose()
    {
        _alive = false;
        _running = false;
        _wakeEvent.Set();

        // Only reclaim once the render thread is provably gone: disposing these
        // underneath a thread still inside RenderFrame throws on that thread,
        // and an unhandled background exception takes the process down.
        if (_renderThread != null && !_renderThread.Join(RenderThreadJoinTimeoutMs))
            return;

        _wakeEvent.Dispose();

        _rtv?.Dispose();
        _constantBuffer?.Dispose();
        _iconSampler?.Dispose();
        _iconAtlasSrv?.Dispose();
        _iconAtlas?.Dispose();
        _windowsSrv?.Dispose();
        _windowsBuffer?.Dispose();

        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
