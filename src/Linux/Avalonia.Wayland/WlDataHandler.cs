using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.FreeDesktop;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using NWayland.Interop;
using NWayland.Protocols.Wayland;

namespace Avalonia.Wayland
{
    internal class WlDataHandler : IClipboard, IPlatformDragSource, IDisposable
    {
        private readonly AvaloniaWaylandPlatform _platform;
        private readonly WlDataDevice _wlDataDevice;
        private readonly WlDataDeviceHandler _wlDataDeviceHandler;

        private WlDataSourceHandler? _currentDataSourceHandler;

        public WlDataHandler(AvaloniaWaylandPlatform platform)
        {
            _platform = platform;
            _wlDataDevice = platform.WlDataDeviceManager.GetDataDevice(platform.WlSeat);
            _wlDataDeviceHandler = new WlDataDeviceHandler(platform);
            _wlDataDevice.Events = _wlDataDeviceHandler;
        }

        public Task<string> GetTextAsync() => Task.FromResult(_wlDataDeviceHandler.SelectionOffer?.GetText() ?? string.Empty);

        public Task SetTextAsync(string text)
        {
            var data = new DataObject();
            data.Set(DataFormats.Text, text);
            return SetDataObjectAsync(data);
        }

        public Task ClearAsync()
        {
            _wlDataDevice.SetSelection(null, _platform.WlInputDevice.KeyboardEnterSerial);
            return Task.CompletedTask;
        }

        public Task SetDataObjectAsync(IDataObject data)
        {
            var dataSource = _platform.WlDataDeviceManager.CreateDataSource();
            _currentDataSourceHandler = new WlDataSourceHandler(_platform, dataSource, data);
            _wlDataDevice.SetSelection(dataSource, _platform.WlInputDevice.KeyboardEnterSerial);
            return Task.CompletedTask;
        }

        public Task<string[]> GetFormatsAsync() =>
            _wlDataDeviceHandler.SelectionOffer is null
                ? Task.FromResult(Array.Empty<string>())
                : Task.FromResult(_wlDataDeviceHandler.SelectionOffer.GetDataFormats().ToArray());

        public Task<object?> GetDataAsync(string format) => Task.FromResult(_wlDataDeviceHandler.SelectionOffer?.Get(format));

        public Task<DragDropEffects> DoDragDrop(PointerEventArgs triggerEvent, IDataObject data, DragDropEffects allowedEffects)
        {
            var window = _platform.WlScreens.ActiveWindow;
            if (window is null)
                return Task.FromResult(DragDropEffects.None);
            triggerEvent.Pointer.Capture(null);
            var dataSource = _platform.WlDataDeviceManager.CreateDataSource();
            _currentDataSourceHandler = new WlDataSourceHandler(_platform, dataSource, data, allowedEffects);
            _wlDataDevice.StartDrag(dataSource, window.WlSurface, null, _platform.WlInputDevice.Serial);
            return _currentDataSourceHandler.DnD;
        }

        public void Dispose() => _wlDataDevice.Dispose();

        private sealed class WlDataDeviceHandler : IDisposable, WlDataDevice.IEvents
        {
            private readonly AvaloniaWaylandPlatform _platform;

            private uint _enterSerial;
            private Point _position;
            private WlDataObject? _currentOffer;
            private WlDataObject? _dndOffer;

            public WlDataDeviceHandler(AvaloniaWaylandPlatform platform)
            {
                _platform = platform;
            }

            internal WlDataObject? SelectionOffer { get; private set; }

            public void OnDataOffer(WlDataDevice eventSender, WlDataOffer id) => _currentOffer = new WlDataObject(_platform, id);

            public void OnEnter(WlDataDevice eventSender, uint serial, WlSurface surface, WlFixed x, WlFixed y, WlDataOffer? id)
            {
                DisposeDnD();
                if (_currentOffer is null || _currentOffer.WlDataOffer != id)
                    return;
                _enterSerial = serial;
                _dndOffer = _currentOffer;
                _currentOffer = null;
                if (_platform.WlScreens.ActiveWindow?.InputRoot is null)
                    return;
                _position = new Point((int)x, (int)y);
                var dragDropDevice = AvaloniaLocator.Current.GetRequiredService<IDragDropDevice>();
                var inputRoot = _platform.WlScreens.ActiveWindow.InputRoot;
                var modifiers = _platform.WlInputDevice.RawInputModifiers;
                var args = new RawDragEvent(dragDropDevice, RawDragEventType.DragEnter, inputRoot, _position, _dndOffer, _dndOffer.OfferedDragDropEffects, modifiers);
                _platform.WlScreens.ActiveWindow.Input?.Invoke(args);
                var preferredAction = GetPreferredEffect(args.Effects, _platform.WlInputDevice.RawInputModifiers);
                _dndOffer.WlDataOffer.SetActions((WlDataDeviceManager.DndActionEnum)args.Effects, (WlDataDeviceManager.DndActionEnum)preferredAction);
                Accept(args);
            }

            public void OnLeave(WlDataDevice eventSender) => DisposeDnD();

            public void OnMotion(WlDataDevice eventSender, uint time, WlFixed x, WlFixed y)
            {
                var window = _platform.WlScreens.ActiveWindow;
                if (window?.InputRoot is null || _dndOffer is null)
                    return;
                _position = new Point((int)x, (int)y);
                var dragDropDevice = AvaloniaLocator.Current.GetRequiredService<IDragDropDevice>();
                var modifiers = _platform.WlInputDevice.RawInputModifiers;
                var args = new RawDragEvent(dragDropDevice, RawDragEventType.DragOver, window.InputRoot, _position, _dndOffer, _dndOffer.OfferedDragDropEffects, modifiers);
                window.Input?.Invoke(args);
                Accept(args);
            }

            public void OnDrop(WlDataDevice eventSender)
            {
                var window = _platform.WlScreens.ActiveWindow;
                if (window?.InputRoot is null || _dndOffer is null)
                    return;
                var dragDropDevice = AvaloniaLocator.Current.GetRequiredService<IDragDropDevice>();
                var modifiers = _platform.WlInputDevice.RawInputModifiers;
                var args = new RawDragEvent(dragDropDevice, RawDragEventType.Drop, window.InputRoot, _position, _dndOffer, _dndOffer.MatchedDragDropEffects, modifiers);
                window.Input?.Invoke(args);
                if (args.Effects != DragDropEffects.None)
                    _dndOffer.WlDataOffer.Finish();
                DisposeDnD();
            }

            public void OnSelection(WlDataDevice eventSender, WlDataOffer? id)
            {
                DisposeSelection();
                if (_currentOffer?.WlDataOffer != id)
                    return;
                SelectionOffer = _currentOffer;
                _currentOffer = null;
            }

            public void Dispose()
            {
                DisposeSelection();
                DisposeDnD();
            }

            private void Accept(RawDragEvent args)
            {
                var preferredAction = GetPreferredEffect(args.Effects, _platform.WlInputDevice.RawInputModifiers);
                _dndOffer!.WlDataOffer.SetActions((WlDataDeviceManager.DndActionEnum)args.Effects, (WlDataDeviceManager.DndActionEnum)preferredAction);
                if (_dndOffer.MimeTypes.Count > 0 && args.Effects != DragDropEffects.None)
                    _dndOffer.WlDataOffer.Accept(_enterSerial, _dndOffer.MimeTypes[0]);
                else
                    _dndOffer.WlDataOffer.Accept(_enterSerial, null);
            }

            private void DisposeSelection()
            {
                SelectionOffer?.Dispose();
                SelectionOffer = null;
            }

            private void DisposeDnD()
            {
                _dndOffer?.Dispose();
                _dndOffer = null;
            }

            private static DragDropEffects GetPreferredEffect(DragDropEffects effect, RawInputModifiers modifiers)
            {
                if (effect is DragDropEffects.Copy or DragDropEffects.Move or DragDropEffects.None)
                    return effect; // No need to check for the modifiers.
                if (effect.HasAllFlags(DragDropEffects.Copy) && modifiers.HasAllFlags(RawInputModifiers.Control))
                    return DragDropEffects.Copy;
                return DragDropEffects.Move;
            }
        }

        private sealed class WlDataSourceHandler : WlDataSource.IEvents
        {
            private readonly AvaloniaWaylandPlatform _platform;
            private readonly WlDataSource _wlDataSource;
            private readonly IDataObject _dataObject;

            private WlDataDeviceManager.DndActionEnum _lastDnDAction;

            public WlDataSourceHandler(AvaloniaWaylandPlatform platform, WlDataSource wlDataSource, IDataObject dataObject)
            {
                _platform = platform;
                _wlDataSource = wlDataSource;
                _wlDataSource.Events = this;
                _dataObject = dataObject;
                foreach (var format in dataObject.GetDataFormats())
                {
                    switch (format)
                    {
                        case DataFormats.Text:
                            _wlDataSource.Offer(MimeTypes.Text);
                            _wlDataSource.Offer(MimeTypes.TextUtf8);
                            break;
                        case DataFormats.FileNames:
                            _wlDataSource.Offer(MimeTypes.UriList);
                            break;
                    }
                }
            }

            public WlDataSourceHandler(AvaloniaWaylandPlatform platform, WlDataSource wlDataSource, IDataObject dataObject, DragDropEffects allowedEffects) : this(platform, wlDataSource, dataObject)
            {
                var actions = WlDataDeviceManager.DndActionEnum.None;
                if (allowedEffects.HasAllFlags(DragDropEffects.Copy))
                    actions |= WlDataDeviceManager.DndActionEnum.Copy;
                if (allowedEffects.HasAllFlags(DragDropEffects.Move))
                    actions |= WlDataDeviceManager.DndActionEnum.Move;
                wlDataSource.SetActions(actions);
            }

            private TaskCompletionSource<DragDropEffects>? _dnd;
            internal Task<DragDropEffects> DnD => _dnd?.Task ?? (_dnd = new TaskCompletionSource<DragDropEffects>()).Task;

            public void OnTarget(WlDataSource eventSender, string? mimeType) { }

            public unsafe void OnSend(WlDataSource eventSender, string mimeType, int fd)
            {
                var content = mimeType switch
                {
                    MimeTypes.Text or MimeTypes.TextUtf8 when _dataObject.GetText() is { } text => ToBytes(text),
                    MimeTypes.UriList when _dataObject.GetFileNames() is { } uris => ToBytes(uris),
                    _ => null
                };

                if (content is not null)
                    fixed (byte* ptr = content)
                        LibC.write(fd, (IntPtr)ptr, content.Length);

                LibC.close(fd);
            }

            public void OnCancelled(WlDataSource eventSender)
            {
                _wlDataSource.Dispose();
                _dnd?.TrySetResult(DragDropEffects.None);
            }

            public void OnDndDropPerformed(WlDataSource eventSender) => _dnd?.TrySetResult((DragDropEffects)_lastDnDAction);

            public void OnDndFinished(WlDataSource eventSender) => _wlDataSource.Dispose();

            public void OnAction(WlDataSource eventSender, WlDataDeviceManager.DndActionEnum dndAction)
            {
                _lastDnDAction = dndAction;
                var cursorFactory = AvaloniaLocator.Current.GetRequiredService<ICursorFactory>();
                ICursorImpl? cursor = null;
                if (dndAction.HasAllFlags(WlDataDeviceManager.DndActionEnum.Copy))
                    cursor = cursorFactory.GetCursor(StandardCursorType.DragCopy);
                else if (dndAction.HasAllFlags(WlDataDeviceManager.DndActionEnum.Move))
                    cursor = cursorFactory.GetCursor(StandardCursorType.DragMove);
                _platform.WlInputDevice.SetCursor(cursor as WlCursor);
            }

            private static byte[] ToBytes(string text) => Encoding.UTF8.GetBytes(text);

            private static byte[] ToBytes(IEnumerable<string> uris) => uris.SelectMany(static x => Encoding.UTF8.GetBytes(x).Append((byte)'\n')).ToArray();
        }
    }
}
