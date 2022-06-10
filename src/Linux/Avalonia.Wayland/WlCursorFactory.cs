using System;
using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Platform;

namespace Avalonia.Wayland
{
    internal class WlCursorFactory : ICursorFactory, IDisposable
    {
        private readonly AvaloniaWaylandPlatform _platform;
        private readonly IntPtr _theme;
        private readonly Dictionary<StandardCursorType, WlThemeCursor> _wlCursorCache = new();

        public WlCursorFactory(AvaloniaWaylandPlatform platform)
        {
            _platform = platform;
            var themeName = Environment.GetEnvironmentVariable("XCURSOR_THEME") ?? "default";
            var themeSizeVar = Environment.GetEnvironmentVariable("XCURSOR_SIZE");
            var themeSize = themeSizeVar is null ? 32 : int.Parse(themeSizeVar);
            _theme = LibWaylandCursor.wl_cursor_theme_load(themeName, themeSize, platform.WlShm.Handle);
        }

        public unsafe ICursorImpl GetCursor(StandardCursorType cursorType)
        {
            if (_wlCursorCache.TryGetValue(cursorType, out var wlCursor))
                return wlCursor;
            foreach (var name in _standardCursorNames[cursorType])
            {
                var cursor = LibWaylandCursor.wl_cursor_theme_get_cursor(_theme, name);
                if (cursor is null)
                    continue;
                wlCursor = new WlThemeCursor(cursor, _platform);
                _wlCursorCache.Add(cursorType, wlCursor);
                return wlCursor;
            }

            return GetCursor(StandardCursorType.Arrow);
        }

        public ICursorImpl CreateCursor(IBitmapImpl cursor, PixelPoint hotSpot)
        {
            return null; // TODO
        }

        private static readonly Dictionary<StandardCursorType, string[]> _standardCursorNames = new()
        {
            { StandardCursorType.Arrow, new[] { "left_ptr", "default", "top_left_arrow" } },
            { StandardCursorType.UpArrow, new [] { "up_arrow" } },
            { StandardCursorType.Cross, new [] { "cross" } },
            { StandardCursorType.Wait, new [] { "wait", "watch", "0426c94ea35c87780ff01dc239897213" } },
            { StandardCursorType.Ibeam, new [] { "ibeam", "text", "xterm" } },
            { StandardCursorType.SizeNorthSouth, new[] { "size_ver", "ns-resize", "v_double_arrow", "00008160000006810000408080010102" } },
            { StandardCursorType.SizeWestEast, new [] { "size_hor", "ew-resize", "h_double_arrow", "028006030e0e7ebffc7f7070c0600140" } },
            { StandardCursorType.SizeAll, new [] { "size_all" } },
            { StandardCursorType.None, new [] { "blank" } },
            { StandardCursorType.TopSide, new [] { "n-resize", "top_side" } },
            { StandardCursorType.BottomSide, new [] { "s-resize", "bottom_side" } },
            { StandardCursorType.LeftSide, new [] { "w-resize", "left_side" } },
            { StandardCursorType.RightSide, new [] { "e-resize", "right_side" } },
            { StandardCursorType.TopLeftCorner, new [] { "nw-resize", "top_left_corner" } },
            { StandardCursorType.TopRightCorner, new [] { "ne-resize", "top_right_corner" } },
            { StandardCursorType.BottomLeftCorner, new [] { "sw-resize", "bottom_left_corner" } },
            { StandardCursorType.BottomRightCorner, new [] { "se-resize", "bottom_right_corner" } },
            { StandardCursorType.Hand, new [] { "openhand", "fleur", "5aca4d189052212118709018842178c0", "9d800788f1b08800ae810202380a0822" } },
            { StandardCursorType.Help, new [] { "whats_this", "help", "question_arrow", "5c6cd98b3f3ebcb1f9c7f1c204630408", "d9ce0ab605698f320427677b458ad60b" } },
            { StandardCursorType.AppStarting, new [] { "left_ptr_watch", "half-busy", "progress", "00000000000000020006000e7e9ffc3f", "08e8e1c95fe2fc01f976f1e063a24ccd" } },
            { StandardCursorType.DragCopy, new [] { "dnd-copy", "copy" } },
            { StandardCursorType.DragLink, new [] { "dnd-link", "link" } },
            { StandardCursorType.DragMove, new [] { "dnd-move", "move"} },
            { StandardCursorType.No, new [] { string.Empty } }
        };

        public void Dispose()
        {
            LibWaylandCursor.wl_cursor_theme_destroy(_theme);
        }
    }
}
