using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace IDevelop.Desktop.Conversation;

/// <summary>Where a link in a message points, as iDevelop treats it.</summary>
public abstract record LinkTarget(string Destination)
{
    /// <summary>An absolute http or https address, the only kind a browser may open.</summary>
    public sealed record Web(string Destination, Uri Uri) : LinkTarget(Destination);

    /// <summary>Any other destination, such as a relative path or a script, which stays copyable text.</summary>
    public sealed record TextOnly(string Destination) : LinkTarget(Destination);

    public static LinkTarget Parse(string? destination)
    {
        var text = destination?.Trim() ?? "";
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? new Web(text, uri)
            : new TextOnly(text);
    }
}

/// <summary>
/// Every link activation in a conversation, by click, key, or context menu, comes here. A web address opens only after the
/// person confirms its actual destination. Anything else can only be copied.
/// </summary>
public sealed class ConversationLinkRouter
{
    /// <summary>The router for the links inside a control. Every message under the conversation view inherits it.</summary>
    public static readonly AttachedProperty<ConversationLinkRouter?> RouterProperty =
        AvaloniaProperty.RegisterAttached<ConversationLinkRouter, Control, ConversationLinkRouter?>("Router", inherits: true);

    private readonly Func<Uri, Task<bool>> _launch;
    private readonly Func<string, Task> _copy;

    /// <param name="launch">Opens a web address in the person's browser.</param>
    /// <param name="copy">Puts text on the clipboard.</param>
    /// <param name="confirm">Asks the person whether to open the address, shown at the link. Null asks with a flyout.</param>
    public ConversationLinkRouter(Func<Uri, Task<bool>> launch, Func<string, Task> copy, Func<Control, LinkTarget.Web, Task<bool>>? confirm = null)
    {
        _launch = launch;
        _copy = copy;
        Confirm = confirm ?? AskWithFlyout;
    }

    private Func<Control, LinkTarget.Web, Task<bool>> Confirm { get; }

    public static ConversationLinkRouter? GetRouter(Control control) => control.GetValue(RouterProperty);

    public static void SetRouter(Control control, ConversationLinkRouter? router) => control.SetValue(RouterProperty, router);

    /// <summary>Opens a web address after the person confirms it, or shows any other destination for copying.</summary>
    public async Task ActivateAsync(Control link, string destination)
    {
        switch (LinkTarget.Parse(destination))
        {
            case LinkTarget.Web web:
                if (await Confirm(link, web))
                {
                    await _launch(web.Uri);
                }

                break;
            case LinkTarget.TextOnly text:
                ShowText(link, text);
                break;
        }
    }

    public Task CopyAsync(string destination) => _copy(destination);

    private Task<bool> AskWithFlyout(Control link, LinkTarget.Web web)
    {
        var answer = new TaskCompletionSource<bool>();
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var open = new Button { Classes = { "primary" }, Content = "Open in browser" };
        var copy = new Button { Classes = { "action" }, Content = "Copy address" };
        Avalonia.Automation.AutomationProperties.SetAutomationId(open, "OpenLink");
        Avalonia.Automation.AutomationProperties.SetAutomationId(copy, "CopyLink");
        open.Click += (_, _) =>
        {
            answer.TrySetResult(true);
            flyout.Hide();
        };
        copy.Click += async (_, _) =>
        {
            flyout.Hide();
            await _copy(web.Uri.AbsoluteUri);
        };
        flyout.Closed += (_, _) => answer.TrySetResult(false);
        flyout.Content = Prompt("Open this address in your browser?", web.Uri.AbsoluteUri, open, copy);
        flyout.ShowAt(link);
        return answer.Task;
    }

    private void ShowText(Control link, LinkTarget.TextOnly text)
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        var copy = new Button { Classes = { "action" }, Content = "Copy address" };
        Avalonia.Automation.AutomationProperties.SetAutomationId(copy, "CopyLink");
        copy.Click += async (_, _) =>
        {
            flyout.Hide();
            await _copy(text.Destination);
        };
        flyout.Content = Prompt("iDevelop opens only web addresses. You can copy this one.", text.Destination, copy);
        flyout.ShowAt(link);
    }

    private static Control Prompt(string question, string destination, params Button[] buttons)
    {
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.AddRange(buttons);
        var address = new SelectableTextBlock { Classes = { "mdMono" }, Text = destination, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Avalonia.Automation.AutomationProperties.SetAutomationId(address, "LinkDestination");
        return new StackPanel
        {
            Spacing = 8,
            MaxWidth = 420,
            Children = { new TextBlock { Text = question, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, address, actions },
        };
    }
}
