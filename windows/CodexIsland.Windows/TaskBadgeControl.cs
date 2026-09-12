using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace CodexIsland.Windows;

// Pointer input belongs to the header gesture; assistive invocation is independent.
public sealed class TaskBadgeControl : Border
{
    internal Action? Invoked { get; set; }
    protected override AutomationPeer OnCreateAutomationPeer() => new TaskBadgePeer(this);

    private sealed class TaskBadgePeer(TaskBadgeControl owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetClassNameCore() => nameof(TaskBadgeControl);
        protected override bool IsControlElementCore() => true;
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
        public void Invoke() => owner.Dispatcher.BeginInvoke(() => owner.Invoked?.Invoke());
    }
}
