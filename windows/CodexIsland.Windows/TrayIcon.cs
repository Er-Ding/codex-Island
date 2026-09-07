using System.Drawing;
using System.Drawing.Drawing2D;
using CodexIsland.Core;
using Forms = System.Windows.Forms;

namespace CodexIsland.Windows;

internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Icon image;

    public TrayIcon(IslandWindow window, QuotaStore store, Action quit)
    {
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add(new Forms.ToolStripMenuItem(store.IsDemo ? "Codex Island · 演示" : "Codex Island") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        var visibility = Add("隐藏灵动岛", window.ToggleVisibility);
        Add("刷新额度", () => _ = store.RefreshAsync());
        menu.Items.Add(new Forms.ToolStripSeparator());
        var sizes = new Forms.ToolStripMenuItem("显示大小");
        foreach (var size in Enum.GetValues<IslandSize>())
            sizes.DropDownItems.Add(new Forms.ToolStripMenuItem(SizeTitle(size), null, (_, _) => window.SetSize(size)) { Tag = size });
        menu.Items.Add(sizes);
        var position = Add("调整位置…", () =>
        {
            if (window.Interaction.IsAdjusting) window.FinishAdjustment(); else window.BeginAdjustment();
        });
        var cancel = Add("取消位置调整", window.CancelAdjustment);
        Add("恢复默认位置", window.ResetPosition);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("选择 Codex 程序…", window.ChooseCodex);
        Add("自动查找 Codex", window.AutoFindCodex);
        menu.Items.Add(new Forms.ToolStripSeparator());
        Add("退出 Codex Island", quit);
        menu.Opening += (_, _) =>
        {
            window.SetMenuOpen(true);
            visibility.Text = window.Interaction.IsVisible ? "隐藏灵动岛" : "显示灵动岛";
            position.Text = window.Interaction.IsAdjusting ? "完成并锁定位置" : "调整位置…";
            cancel.Visible = window.Interaction.IsAdjusting;
            sizes.Enabled = !window.Interaction.IsAdjusting;
            foreach (Forms.ToolStripMenuItem item in sizes.DropDownItems)
                item.Checked = (IslandSize)item.Tag! == window.CurrentSize;
        };
        menu.Closed += (_, _) => window.SetMenuOpen(false);
        image = CreateIcon();
        icon = new Forms.NotifyIcon { Icon = image, Text = store.IsDemo ? "Codex Island · 演示数据" : "Codex Island · 查看额度", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) window.ToggleVisibility(); };
    }

    private Forms.ToolStripMenuItem Add(string title, Action action)
    {
        var item = new Forms.ToolStripMenuItem(title, null, (_, _) => action());
        menu.Items.Add(item);
        return item;
    }
    private static string SizeTitle(IslandSize size) => size == IslandSize.Automatic ? "自动适配" : $"{(int)size}%";
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var background = new SolidBrush(Color.FromArgb(15, 22, 25));
            using var mint = new SolidBrush(Color.FromArgb(117, 224, 179));
            graphics.FillEllipse(background, 0, 0, 31, 31);
            graphics.FillRectangle(mint, 7, 17, 4, 8);
            graphics.FillRectangle(mint, 14, 11, 4, 14);
            graphics.FillRectangle(mint, 21, 6, 4, 19);
        }
        var handle = bitmap.GetHicon();
        try { using var temporary = Icon.FromHandle(handle); return (Icon)temporary.Clone(); }
        finally { NativeMethods.DestroyIcon(handle); }
    }
    public void Dispose() { icon.Visible = false; icon.Dispose(); menu.Dispose(); image.Dispose(); }
}
