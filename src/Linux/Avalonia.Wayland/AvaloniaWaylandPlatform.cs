using System;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.FreeDesktop;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Wayland;
using NWayland.Protocols.FractionalScaleV1;
using NWayland.Protocols.PointerGesturesUnstableV1;
using NWayland.Protocols.TextInputUnstableV3;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.XdgActivationV1;
using NWayland.Protocols.XdgDecorationUnstableV1;
using NWayland.Protocols.XdgForeignUnstableV2;
using NWayland.Protocols.XdgShell;

namespace Avalonia.Wayland
{
    internal class AvaloniaWaylandPlatform : IWindowingPlatform, IDisposable, XdgWmBase.IEvents
    {
        public AvaloniaWaylandPlatform(WaylandPlatformOptions options)
        {
            Options = options;
            WlDisplay = WlDisplay.Connect();
            var registry = WlDisplay.GetRegistry();
            WlRegistryHandler = new WlRegistryHandler(registry);
            WlDisplay.Roundtrip();

            WlCompositor = WlRegistryHandler.BindRequiredInterface(WlCompositor.BindFactory, WlCompositor.InterfaceName, WlCompositor.InterfaceVersion);
            WlSeat = WlRegistryHandler.BindRequiredInterface(WlSeat.BindFactory, WlSeat.InterfaceName, WlSeat.InterfaceVersion);
            WlShm = WlRegistryHandler.BindRequiredInterface(WlShm.BindFactory, WlShm.InterfaceName, WlShm.InterfaceVersion);
            WlDataDeviceManager = WlRegistryHandler.BindRequiredInterface(WlDataDeviceManager.BindFactory, WlDataDeviceManager.InterfaceName, WlDataDeviceManager.InterfaceVersion);
            XdgWmBase = WlRegistryHandler.BindRequiredInterface(XdgWmBase.BindFactory, XdgWmBase.InterfaceName, XdgWmBase.InterfaceVersion);
            XdgActivation = WlRegistryHandler.BindRequiredInterface(XdgActivationV1.BindFactory, XdgActivationV1.InterfaceName, XdgActivationV1.InterfaceVersion);
            WpFractionalScaleManager = WlRegistryHandler.Bind(WpFractionalScaleManagerV1.BindFactory, WpFractionalScaleManagerV1.InterfaceName, WpFractionalScaleManagerV1.InterfaceVersion);
            ZxdgDecorationManager = WlRegistryHandler.Bind(ZxdgDecorationManagerV1.BindFactory, ZxdgDecorationManagerV1.InterfaceName, ZxdgDecorationManagerV1.InterfaceVersion);
            ZxdgExporter = WlRegistryHandler.Bind(ZxdgExporterV2.BindFactory, ZxdgExporterV2.InterfaceName, ZxdgExporterV2.InterfaceVersion);
            ZwpTextInput = WlRegistryHandler.Bind(ZwpTextInputManagerV3.BindFactory, ZwpTextInputManagerV3.InterfaceName, ZwpTextInputManagerV3.InterfaceVersion);
            ZwpPointerGestures = WlRegistryHandler.Bind(ZwpPointerGesturesV1.BindFactory, ZwpPointerGesturesV1.InterfaceName, ZwpPointerGesturesV1.InterfaceVersion);

            XdgWmBase.Events = this;

            var wlDataHandler = new WlDataHandler(this);
            AvaloniaLocator.CurrentMutable
                .Bind<IWindowingPlatform>().ToConstant(this)
                .Bind<IPlatformThreadingInterface>().ToConstant(new WlPlatformThreading(this))
                .Bind<IRenderTimer>().ToConstant(new DefaultRenderTimer(60))
                .Bind<IRenderLoop>().ToConstant(new RenderLoop())
                .Bind<PlatformHotkeyConfiguration>().ToConstant(new PlatformHotkeyConfiguration(KeyModifiers.Control))
                .Bind<IPlatformSettings>().ToSingleton<DefaultPlatformSettings>()
                .Bind<IKeyboardDevice>().ToConstant(new KeyboardDevice())
                .Bind<ICursorFactory>().ToConstant(new WlCursorFactory(this))
                .Bind<IClipboard>().ToConstant(wlDataHandler)
                .Bind<IPlatformDragSource>().ToConstant(wlDataHandler)
                .Bind<IPlatformIconLoader>().ToConstant(new WlIconLoader())
                .Bind<IStorageProviderFactory>().ToConstant(new WlStorageProviderFactory())
                .Bind<IMountedVolumeInfoProvider>().ToConstant(new LinuxMountedVolumeInfoProvider());

            WlScreens = new WlScreens(this);
            WlInputDevice = new WlInputDevice(this);

            if (ZwpTextInput is not null)
                WlTextInputMethod = new WlTextInputMethod(this);

            WlDisplay.Roundtrip();

            DBusHelper.TryInitialize();

            IPlatformGraphics? platformGraphics = null;

            if (options.UseGpu)
            {
                const int EGL_PLATFORM_WAYLAND_KHR = 0x31D8;
                platformGraphics = EglPlatformGraphics.TryCreate(() => new EglDisplay(new EglDisplayCreationOptions
                {
                    PlatformType = EGL_PLATFORM_WAYLAND_KHR,
                    PlatformDisplay = WlDisplay.Handle,
                    SupportsContextSharing = true,
                    SupportsMultipleContexts = true
                }));

                if (platformGraphics is not null)
                    AvaloniaLocator.CurrentMutable.Bind<IPlatformGraphics>().ToConstant(platformGraphics);
            }

            if (options.UseCompositor)
                Compositor = new Compositor(AvaloniaLocator.Current.GetRequiredService<IRenderLoop>(), platformGraphics);
            else
                RenderInterface = new PlatformRenderInterfaceContextManager(platformGraphics);
        }

        internal WaylandPlatformOptions Options { get; }

        internal Compositor? Compositor { get; }

        internal PlatformRenderInterfaceContextManager? RenderInterface { get; }

        internal WlDisplay WlDisplay { get; }

        internal WlRegistryHandler WlRegistryHandler { get; }

        internal WlCompositor WlCompositor { get; }

        internal WlSeat WlSeat { get; }

        internal WlShm WlShm { get; }

        internal WlDataDeviceManager WlDataDeviceManager { get; }

        internal XdgWmBase XdgWmBase { get; }

        internal XdgActivationV1 XdgActivation { get; }

        internal WpFractionalScaleManagerV1? WpFractionalScaleManager { get; }

        internal ZxdgDecorationManagerV1? ZxdgDecorationManager { get; }

        internal ZxdgExporterV2? ZxdgExporter { get; }

        internal ZwpTextInputManagerV3? ZwpTextInput { get; }

        internal ZwpPointerGesturesV1? ZwpPointerGestures { get; }

        internal WlScreens WlScreens { get; }

        internal WlInputDevice WlInputDevice { get; }

        internal WlTextInputMethod? WlTextInputMethod { get; }

        public IWindowImpl CreateWindow() => new WlToplevel(this);

        public IWindowImpl CreateEmbeddableWindow() => throw new NotSupportedException();

        public ITrayIconImpl? CreateTrayIcon()
        {
            var dbusTrayIcon = new DBusTrayIconImpl();
            if (!dbusTrayIcon.IsActive)
                return null;
            dbusTrayIcon.IconConverterDelegate = static impl => impl is WlIconData wlIconData ? wlIconData.Data : Array.Empty<uint>();
            return dbusTrayIcon;
        }

        public void OnPing(XdgWmBase eventSender, uint serial) => XdgWmBase.Pong(serial);

        public void Dispose()
        {
            WlTextInputMethod?.Dispose();
            ZxdgDecorationManager?.Dispose();
            ZxdgExporter?.Dispose();
            ZwpTextInput?.Dispose();
            ZwpPointerGestures?.Dispose();
            WlDataDeviceManager.Dispose();
            WlInputDevice.Dispose();
            WlScreens.Dispose();
            WlSeat.Dispose();
            WlShm.Dispose();
            XdgActivation.Dispose();
            XdgWmBase.Dispose();
            WlCompositor.Dispose();
            WlRegistryHandler.Dispose();
            WlDisplay.Dispose();
        }
    }
}

namespace Avalonia
{
    public static class AvaloniaWaylandPlatformExtensions
    {
        public static T UseWayland<T>(this T builder) where T : AppBuilderBase<T>, new() =>
            builder.UseWindowingSubsystem(static () =>
            {
                var options = AvaloniaLocator.Current.GetService<WaylandPlatformOptions>() ?? new WaylandPlatformOptions();
                var platform = new AvaloniaWaylandPlatform(options);
                AvaloniaLocator.CurrentMutable.BindToSelf(platform);
            });
    }

    public class WaylandPlatformOptions
    {
        /// <summary>
        /// Determines whether to use GPU for rendering in your project. The default value is true.
        /// </summary>
        public bool UseGpu { get; set; } = true;

        public bool UseCompositor { get; set; } = true;

        /// <summary>
        /// Deferred renderer would be used when set to true. Immediate renderer when set to false. The default value is true.
        /// </summary>
        /// <remarks>
        /// Avalonia has two rendering modes: Immediate and Deferred rendering.
        /// Immediate re-renders the whole scene when some element is changed on the scene. Deferred re-renders only changed elements.
        /// </remarks>
        public bool UseDeferredRendering { get; set; } = true;

        /// <summary>
        /// The app ID identifies the general class of applications to which the surface belongs. <br/>
        /// The compositor can use this to group multiple surfaces together, or to determine how to launch a new application. <br/>
        /// As a best practice, it is suggested to select app ID's that match the basename of the application's .desktop file. For example, "org.freedesktop.FooViewer" where the .desktop file is "org.freedesktop.FooViewer.desktop".
        /// </summary>
        public string? AppId { get; set; }
    }
}
