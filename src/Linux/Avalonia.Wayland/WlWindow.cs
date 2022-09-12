using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.TextInput;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Utilities;
using Avalonia.Wayland.Egl;
using Avalonia.Wayland.Framebuffer;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.XdgActivationV1;
using NWayland.Protocols.XdgShell;

namespace Avalonia.Wayland
{
    internal abstract class WlWindow : IWindowBaseImpl, ITopLevelImplWithTextInputMethod, WlSurface.IEvents, WlCallback.IEvents, XdgSurface.IEvents, XdgActivationTokenV1.IEvents
    {
        private readonly AvaloniaWaylandPlatform _platform;
        private readonly WlFramebufferSurface _wlFramebufferSurface;
        private readonly WlRegion _wlRegion;
        private readonly IntPtr _eglWindow;

        private WlCallback? _frameCallback;

        protected WlWindow(AvaloniaWaylandPlatform platform)
        {
            _platform = platform;
            _wlRegion = platform.WlCompositor.CreateRegion();
            WlSurface = platform.WlCompositor.CreateSurface();
            WlSurface.Events = this;
            XdgSurface = platform.XdgWmBase.GetXdgSurface(WlSurface);
            XdgSurface.Events = this;

            platform.WlScreens.AddWindow(this);

            TextInputMethod = platform.WlTextInputMethod;

            var screens = _platform.WlScreens.AllScreens;
            ClientSize = screens.Count > 0
                ? new Size(screens[0].WorkingArea.Width * 0.75, screens[0].WorkingArea.Height * 0.7)
                : new Size(400, 600);

            _wlFramebufferSurface = new WlFramebufferSurface(platform, this);
            var surfaces = new List<object> { _wlFramebufferSurface };

            var glFeature = AvaloniaLocator.Current.GetService<IPlatformOpenGlInterface>();
            if (glFeature is EglPlatformOpenGlInterface egl)
            {
                _eglWindow = LibWaylandEgl.wl_egl_window_create(WlSurface.Handle, (int)ClientSize.Width, (int)ClientSize.Height);
                var surfaceInfo = new WlEglSurfaceInfo(this, _eglWindow);
                var platformSurface = new WlEglGlPlatformSurface(egl, surfaceInfo);
                surfaces.Insert(0, platformSurface);
            }

            Surfaces = surfaces.ToArray();
        }

        public IPlatformHandle Handle { get; }

        public ITextInputMethodImpl? TextInputMethod { get; }

        public Size MaxAutoSizeHint => _platform.WlScreens.AllScreens.Select(static s => s.Bounds.Size.ToSize(s.PixelDensity)).OrderByDescending(static x => x.Width + x.Height).FirstOrDefault();

        public Size ClientSize { get; private set; }

        public Size? FrameSize => null;

        public PixelPoint Position { get; protected set; }

        public double RenderScaling { get; private set; } = 1;

        public double DesktopScaling => RenderScaling;

        public WindowTransparencyLevel TransparencyLevel { get; private set; }

        public AcrylicPlatformCompensationLevels AcrylicCompensationLevels => default;

        public IScreenImpl Screen => _platform.WlScreens;

        public IEnumerable<object> Surfaces { get; }

        public Action<RawInputEventArgs>? Input { get; set; }

        public Action<Rect>? Paint { get; set; }

        public Action<Size, PlatformResizeReason>? Resized { get; set; }

        public Action<double>? ScalingChanged { get; set; }

        public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }

        public Action? Activated { get; set; }

        public Action? Deactivated { get; set; }

        public Action? LostFocus { get; set; }

        public Action? Closed { get; set; }

        public Action<PixelPoint>? PositionChanged { get; set; }

        internal IInputRoot? InputRoot { get; private set; }

        internal WlWindow? Parent { get; set; }

        internal WlSurface WlSurface { get; }

        internal XdgSurface XdgSurface { get; }

        internal uint XdgSurfaceConfigureSerial { get; private set; }

        protected WlOutput? WlOutput { get; private set; }

        protected PixelSize PendingSize { get; set; }

        public IRenderer CreateRenderer(IRenderRoot root)
        {
            var loop = AvaloniaLocator.Current.GetRequiredService<IRenderLoop>();
            var customRendererFactory = AvaloniaLocator.Current.GetService<IRendererFactory>();

            if (customRendererFactory is not null)
                return customRendererFactory.Create(root, loop);
            if (_platform.Options.UseCompositor)
                return new CompositingRenderer(root, _platform.Compositor!);
            if (_platform.Options.UseDeferredRendering)
                return new DeferredRenderer(root, loop);
            return new ImmediateRenderer(root);
        }

        public void Invalidate(Rect rect) => WlSurface.DamageBuffer((int)rect.X, (int)rect.Y, (int)(rect.Width * RenderScaling), (int)(rect.Height * RenderScaling));

        public void SetInputRoot(IInputRoot inputRoot) => InputRoot = inputRoot;

        public Point PointToClient(PixelPoint point) => new(point.X, point.Y);

        public PixelPoint PointToScreen(Point point) => new((int)point.X, (int)point.Y);

        public void SetCursor(ICursorImpl? cursor) => _platform.WlInputDevice.SetCursor(cursor as WlCursor);

        public IPopupImpl CreatePopup() => new WlPopup(_platform, this);

        public void SetTransparencyLevelHint(WindowTransparencyLevel transparencyLevel)
        {
            if (transparencyLevel == TransparencyLevel)
                return;
            WlSurface.SetOpaqueRegion(transparencyLevel == WindowTransparencyLevel.None ? _wlRegion : null);
            TransparencyLevel = transparencyLevel;
            TransparencyLevelChanged?.Invoke(transparencyLevel);
        }

        public virtual void Show(bool activate, bool isDialog) => Paint?.Invoke(Rect.Empty);

        public abstract void Hide();

        public void Activate()
        {
            var activationToken = _platform.XdgActivation.GetActivationToken();
            activationToken.Events = this;
            var serial = _platform.WlInputDevice.UserActionDownSerial;
            if (serial != 0)
                activationToken.SetSerial(serial, _platform.WlSeat);
            var focusedWindow = _platform.WlScreens.KeyboardFocus;
            if (focusedWindow is not null)
                activationToken.SetSurface(focusedWindow.WlSurface);
            if (_platform.Options.AppId is not null)
                activationToken.SetAppId(_platform.Options.AppId);
            activationToken.Commit();
        }

        public void SetTopmost(bool value) { }

        public void Resize(Size clientSize, PlatformResizeReason reason = PlatformResizeReason.Application)
        {
            PendingSize = new PixelSize((int)clientSize.Width, (int)clientSize.Height);
            if (XdgSurfaceConfigureSerial != 0)
                return;
            var pendingSize = new Size(PendingSize.Width, PendingSize.Height);
            if (PendingSize == PixelSize.Empty || pendingSize == ClientSize)
                return;
            _wlRegion.Subtract(0, 0, (int)ClientSize.Width, (int)ClientSize.Height);
            _wlRegion.Add(0, 0, PendingSize.Width, PendingSize.Height);
            ClientSize = pendingSize;
            if (_eglWindow != IntPtr.Zero)
                LibWaylandEgl.wl_egl_window_resize(_eglWindow, PendingSize.Width, PendingSize.Height, 0, 0);
            Resized?.Invoke(ClientSize, reason);
        }

        public void OnEnter(WlSurface eventSender, WlOutput output)
        {
            WlOutput = output;
            var screen = _platform.WlScreens.ScreenFromOutput(output);
            if (MathUtilities.AreClose(screen.PixelDensity, RenderScaling))
                return;
            RenderScaling = screen.PixelDensity;
            ScalingChanged?.Invoke(RenderScaling);
            WlSurface.SetBufferScale((int)RenderScaling);
        }

        public void OnLeave(WlSurface eventSender, WlOutput output) { }

        public void OnDone(WlCallback eventSender, uint callbackData)
        {
            _frameCallback!.Dispose();
            _frameCallback = null;
            var pendingSize = new Size(PendingSize.Width, PendingSize.Height);
            if (PendingSize == PixelSize.Empty || pendingSize == ClientSize)
                return;
            ClientSize = pendingSize;
            if (_eglWindow != IntPtr.Zero)
                LibWaylandEgl.wl_egl_window_resize(_eglWindow, PendingSize.Width, PendingSize.Height, 0, 0);
            Resized?.Invoke(ClientSize, PlatformResizeReason.User);
            Paint?.Invoke(new Rect(ClientSize));
        }

        public void OnConfigure(XdgSurface eventSender, uint serial)
        {
            if (XdgSurfaceConfigureSerial == serial)
                return;
            XdgSurfaceConfigureSerial = serial;
            XdgSurface.AckConfigure(serial);
            if (_frameCallback is null)
                Paint?.Invoke(new Rect(ClientSize));
        }

        public void OnDone(XdgActivationTokenV1 eventSender, string token)
        {
            eventSender.Dispose();
            _platform.XdgActivation.Activate(token, WlSurface);
        }

        public virtual void Dispose()
        {
            _platform.WlScreens.RemoveWindow(this);
            if (_eglWindow != IntPtr.Zero)
                LibWaylandEgl.wl_egl_window_destroy(_eglWindow);
            _wlRegion.Dispose();
            _wlFramebufferSurface.Dispose();
            XdgSurface.Dispose();
            WlSurface.Dispose();
        }

        internal void RequestFrame()
        {
            if (_frameCallback is not null)
                return;
            _frameCallback = WlSurface.Frame();
            _frameCallback.Events = this;
        }
    }
}
