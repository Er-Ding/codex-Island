using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CodexIsland.Core;

namespace CodexIsland.Windows;

// Explicitly opted-in diagnostics only: the caller supplies a demo window and isolated settings.
internal static class NativeDragSmoke
{
    private const uint LeftDown = 0x0002, LeftUp = 0x0004, RightDown = 0x0008, RightUp = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData { [FieldOffset(0)] public MouseInput Mouse; }

    // Sequential layout inserts the required padding before the union on 64-bit Windows.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public InputData Data; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    internal static async Task RunAsync(IslandWindow window, SettingsStore settings, string output)
    {
        if (!string.Equals(Path.GetFullPath(settings.FilePath), Path.Combine(Path.GetFullPath(output), "settings.json"),
            StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native input checks require the smoke directory's isolated settings file.");
        if (!window.Dispatcher.CheckAccess()) throw new InvalidOperationException("Native input checks must run on the window's dispatcher.");
        if (!window.Activity.IsDemo) throw new InvalidOperationException("Native input checks require demo tasks to prevent real navigation.");
        if (IsLeftDown) throw new InvalidOperationException("The left mouse button is already held; native input checks were not started.");
        if ((GetAsyncKeyState(0x02) & 0x8000) != 0)
            throw new InvalidOperationException("The right mouse button is already held; native input checks were not started.");
        if (Marshal.SizeOf<NativeInput>() != (IntPtr.Size == 8 ? 40 : 28))
            throw new InvalidOperationException("Unexpected Win32 INPUT layout.");

        var originalPointer = NativeMethods.Pointer;
        if (!double.IsFinite(originalPointer.X) || !double.IsFinite(originalPointer.Y))
            throw new InvalidOperationException("Cannot read the original pointer position.");
        var originalMenuState = window.Interaction.IsMenuOpen;
        var foreground = NativeMethods.GetForegroundWindow();
        var buttonHeldByTest = false;
        var rightButtonHeldByTest = false;
        var checks = new List<string>();
        var header = (Button)window.FindName("HeaderButton");

        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException("Native input: " + description);
            checks.Add("PASS: " + description);
        }
        async Task Pump(int milliseconds)
        {
            await Task.Delay(milliseconds);
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        }
        PointD HeaderPoint(double horizontalFraction = 0.25)
        {
            var point = header.PointToScreen(new Point(header.ActualWidth * horizontalFraction, header.ActualHeight / 2));
            return new(point.X, point.Y);
        }
        Task SeparateClickSequence() => Pump(checked((int)GetDoubleClickTime()) + 40);
        void Press()
        {
            if (IsLeftDown) throw new InvalidOperationException("The left mouse button became held before the test press.");
            var pointer = NativeMethods.Pointer;
            var hit = WindowFromPoint(new NativePoint { X = (int)Math.Round(pointer.X), Y = (int)Math.Round(pointer.Y) });
            if (hit != window.Handle && GetAncestor(hit, 2) != window.Handle)
                throw new InvalidOperationException("The pointer is not over the supplied island window; no click was sent.");
            // Set this first so cleanup still releases the button if delivery reports an error.
            buttonHeldByTest = true;
            SendMouse(LeftDown);
        }
        void Release()
        {
            SendMouse(LeftUp);
            buttonHeldByTest = false;
        }

        try
        {
            Check(foreground != window.Handle, "test starts with another window in the foreground");
            window.CancelHeaderPress();
            window.Interaction.SetMenuOpen(false);
            window.ResetPosition();
            window.SetSize(IslandSize.Percent100);
            var work = window.Display.WorkArea;
            MovePointer(new(work.CenterX, work.Bottom - 10));
            await Pump(80); // The real pointer exit clears manual-collapse suppression.
            Check(!window.Interaction.IsExpanded, "reset leaves the island compact before natural mouse entry");

            var compactPoint = MovePointer(HeaderPoint());
            Press(); // No dispatcher wait between entering the compact header and pressing.
            await Pump(20);
            Check(window.IsHeaderPressed && window.Interaction.IsExpanded && !window.Interaction.IsPinned,
                "native pressing during compact entry captures the same sequence while expanding");
            var compactOrigin = window.VisibleFrame;
            MovePointer(new(compactPoint.X + 1, compactPoint.Y));
            await Pump(30);
            Check(window.IsHeaderDragging && Math.Abs(window.VisibleFrame.X - compactOrigin.X - 1) < .5,
                "the first native pixel after compact entry moves the panel without another press");
            Release();
            await Pump(30);
            Check(!window.IsHeaderPressed && settings.Load().Position is { IsValid: true },
                "native release saves a drag that began during compact entry");
            window.ResetPosition();
            MovePointer(new(work.CenterX, work.Bottom - 10));
            await Pump(80);

            MovePointer(HeaderPoint());
            await Pump(240);
            Check(window.Interaction.IsExpanded && !window.Interaction.IsPinned && !window.IsAnimating,
                "real mouse entry expands the compact island");
            MovePointer(HeaderPoint());
            Press();
            await Pump(50);
            Check(window.IsHeaderPressed && header.IsMouseCaptured, "native button down reaches the header and captures the pointer");
            Release();
            await Pump(80);
            Check(window.Interaction.IsExpanded && window.Interaction.IsPinned && !window.IsHeaderPressed,
                "native short click still pins the expanded island");

            window.Interaction.TogglePin();
            window.ApplyState(false);
            window.UpdateLayout();
            await SeparateClickSequence();
            var origin = window.VisibleFrame;
            var pressPoint = MovePointer(HeaderPoint());
            Press();
            await Pump(20);
            Check(window.IsHeaderPressed && !window.IsHeaderDragging && !window.Interaction.IsPinned,
                "native button down alone does not drag or pin");

            var roomRight = work.Right - origin.Right;
            var roomLeft = origin.X - work.X;
            var direction = roomRight >= roomLeft ? 1 : -1;
            var room = Math.Max(roomRight, roomLeft);
            if (room < 12) throw new InvalidOperationException("The display has insufficient space for an out-of-window native drag check.");
            var scale = window.Display.DpiScale;
            var y = Math.Min(work.Bottom - 20, pressPoint.Y + 65 * scale);
            var firstMove = MovePointer(new(pressPoint.X + direction, pressPoint.Y));
            await Pump(30);
            Check(window.IsHeaderDragging && window.Interaction.IsExpanded && !window.Interaction.IsPinned,
                "one physical pixel of native movement immediately starts dragging without a hold delay");
            Check(Math.Abs(window.VisibleFrame.X - (origin.X + firstMove.X - pressPoint.X)) < 0.5
                && Math.Abs(window.VisibleFrame.Y - origin.Y) < 0.5,
                "the expanded panel follows the first physical pixel of movement");

            // Jump beyond the original native rectangle; movement must survive leaving its old bounds.
            var jumpDistance = Math.Min(40 * scale, room / 2);
            var jump = new PointD(direction > 0 ? origin.Right + jumpDistance : origin.X - jumpDistance, y);
            Check(!origin.Contains(jump), "the native pointer path jumps outside the original island rectangle");
            MovePointer(jump);
            await Pump(65);
            var finalPoint = MovePointer(new(
                Math.Clamp(jump.X + direction * Math.Min(16, room / 4), work.X + 2, work.Right - 2),
                Math.Min(work.Bottom - 20, y + 12)));
            await Pump(65);
            var expectedDraft = origin with { X = origin.X + finalPoint.X - pressPoint.X, Y = origin.Y + finalPoint.Y - pressPoint.Y };
            Check(window.IsHeaderDragging && Near(window.VisibleFrame, expectedDraft),
                "the native drag follows continuous movement and the out-of-window jump");
            Check(settings.Load().Position is null, "native dragging does not save before release");
            Release();
            await Pump(90);

            var settled = window.VisibleFrame;
            Check(!window.IsHeaderPressed && !window.IsHeaderDragging && !header.IsMouseCaptured
                && window.Interaction.IsExpanded && !window.Interaction.IsPinned && !window.Interaction.IsAdjusting,
                "native release finishes capture and preserves the expanded unpinned panel");
            Check(Near(settled, IslandPlacement.Clamp(expectedDraft, window.Display.WorkArea)),
                "native release settles the dragged frame inside the display work area");
            var saved = settings.Load().Position;
            Check(saved is { IsValid: true } && saved == SavedPosition.Capture(settled, window.Display),
                "native release persists the new position to the isolated settings file");
            window.RefreshEnvironment();
            Check(Near(window.VisibleFrame, settled), "the native drag position survives an environment restore");
            window.SavePreview(Path.Combine(output, "native-drag-expanded.png"));

            await SeparateClickSequence();
            var beforeCancel = window.VisibleFrame;
            var settingsBeforeCancel = File.ReadAllText(settings.FilePath);
            var cancelPoint = MovePointer(HeaderPoint());
            Press();
            await Pump(20);
            MovePointer(new(cancelPoint.X + direction * 15, cancelPoint.Y + 20));
            await Pump(30);
            Check(window.IsHeaderDragging, "native cancellation case starts with a captured moving panel");
            rightButtonHeldByTest = true;
            SendMouse(RightDown);
            await Pump(30);
            Check(!window.IsHeaderPressed && !window.IsHeaderDragging && !header.IsMouseCaptured
                && Near(window.VisibleFrame, beforeCancel) && File.ReadAllText(settings.FilePath) == settingsBeforeCancel,
                "native right mouse press cancels a held drag and restores settings without saving the draft");
            SendMouse(RightUp);
            rightButtonHeldByTest = false;
            Release();
            await Pump(30);
            Check(!window.Interaction.IsPinned && File.ReadAllText(settings.FilePath) == settingsBeforeCancel,
                "releasing the cancelled native press does not pin, navigate or save");

            // Use a non-default size to catch resets that accidentally discard display preferences.
            window.SetSize(IslandSize.Percent125);
            await CheckDoubleClickReset(initiallyPinned: false);

            MovePointer(new(work.CenterX, work.Bottom - 10));
            await Pump(80);
            MovePointer(HeaderPoint());
            await Pump(240);
            Check(window.Interaction.IsExpanded && !window.Interaction.IsPinned,
                "the island can naturally expand again after a double-click reset");
            window.Interaction.TogglePin();
            window.ApplyState(false);
            await SeparateClickSequence();
            var pinnedPress = MovePointer(HeaderPoint());
            Press();
            await Pump(20);
            MovePointer(new(pinnedPress.X + direction * 30, pinnedPress.Y + 70));
            await Pump(30);
            Release();
            await Pump(50);
            Check(window.Interaction.IsPinned && settings.Load().Position is { IsValid: true },
                "native dragging a pinned panel saves a moved position without unpinning it");
            await CheckDoubleClickReset(initiallyPinned: true);
            Check(NativeMethods.GetForegroundWindow() == foreground,
                "native hover, click, dragging and both double-click resets preserve the original foreground window");

            async Task CheckDoubleClickReset(bool initiallyPinned)
            {
                await SeparateClickSequence();
                var savedBefore = settings.Load();
                Check(savedBefore.Position is { IsValid: true } && window.Interaction.IsExpanded
                    && window.Interaction.IsPinned == initiallyPinned,
                    $"the {(initiallyPinned ? "pinned" : "unpinned")} double-click case starts expanded at a saved moved position");
                var before = window.VisibleFrame;
                var doubleClickPoint = MovePointer(HeaderPoint(0.1));
                Check(Math.Abs(doubleClickPoint.X - before.CenterX) > IslandWindow.CompactWidth / 2 * window.UiScale * window.Display.DpiScale,
                    $"the {(initiallyPinned ? "pinned" : "unpinned")} double click targets the expanded header outside compact bounds");
                Press();
                await Pump(20);
                Release();
                await Pump(20);
                Check(window.Interaction.IsExpanded && window.Interaction.IsPinned != initiallyPinned
                    && Near(window.VisibleFrame, before) && !window.IsHeaderPressed,
                    $"the first {(initiallyPinned ? "pinned" : "unpinned")} header click toggles pinning without shrinking the second-click target");
                Press();
                await Pump(20);
                Release();
                await Pump(60);

                var restored = settings.Load();
                var primary = IslandPlacement.Select(NativeMethods.Displays(), null);
                var factor = IslandPlacement.Scale(primary,
                    savedBefore.DisplaySizes.GetValueOrDefault(primary.Id, IslandSize.Automatic)) * primary.DpiScale;
                var expected = IslandPlacement.Clamp(new(primary.WorkArea.CenterX - IslandWindow.CompactWidth / 2 * factor,
                    primary.WorkArea.Y, IslandWindow.CompactWidth * factor, 36 * factor), primary.WorkArea);
                Check(!window.Interaction.IsExpanded && !window.Interaction.IsPinned && !window.IsHeaderPressed
                    && !window.IsHeaderDragging && !header.IsMouseCaptured && restored.Position is null
                    && window.Display.Id == primary.Id && Near(window.VisibleFrame, expected),
                    $"the natural {(initiallyPinned ? "pinned" : "unpinned")} double click resets to the primary display's initial compact position");
                Check(restored.DisplaySizes.Count == savedBefore.DisplaySizes.Count
                    && savedBefore.DisplaySizes.All(entry => restored.DisplaySizes.TryGetValue(entry.Key, out var size) && size == entry.Value)
                    && window.CurrentSize == IslandSize.Percent125,
                    $"the {(initiallyPinned ? "pinned" : "unpinned")} double-click reset preserves display size preferences");
            }
        }
        finally
        {
            try
            {
                try { if (rightButtonHeldByTest) SendMouse(RightUp); }
                finally { if (buttonHeldByTest) SendMouse(LeftUp); }
            }
            finally
            {
                try { window.CancelHeaderPress(); }
                finally
                {
                    window.Interaction.SetMenuOpen(originalMenuState);
                    MovePointer(originalPointer);
                }
            }
        }

        File.WriteAllLines(Path.Combine(output, "native-input-checks.txt"),
            checks.Append($"{checks.Count} native input checks passed."));
        Console.WriteLine($"{checks.Count} native input checks passed.");
    }

    private static bool IsLeftDown => (GetAsyncKeyState(0x01) & 0x8000) != 0;
    private static void SendMouse(uint flags)
    {
        var input = new NativeInput { Data = new InputData { Mouse = new MouseInput { Flags = flags } } };
        if (SendInput(1, [input], Marshal.SizeOf<NativeInput>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput did not deliver the native mouse event.");
    }
    private static PointD MovePointer(PointD point)
    {
        if (!SetCursorPos((int)Math.Round(point.X), (int)Math.Round(point.Y)))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot position the native pointer.");
        var actual = NativeMethods.Pointer;
        if (!double.IsFinite(actual.X) || !double.IsFinite(actual.Y))
            throw new InvalidOperationException("Cannot read the native pointer after moving it.");
        return actual;
    }
    private static bool Near(RectD first, RectD second) => Math.Abs(first.X - second.X) < 2
        && Math.Abs(first.Y - second.Y) < 2 && Math.Abs(first.Width - second.Width) < 2 && Math.Abs(first.Height - second.Height) < 2;
}
