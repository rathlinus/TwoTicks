using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinWhatsApp.App.Models;

namespace WinWhatsApp.App.Controls;

/// <summary>Picks the template of a row in the conversation.</summary>
public sealed partial class MessageTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Day { get; set; }
    public DataTemplate? Unread { get; set; }
    public DataTemplate? System { get; set; }
    public DataTemplate? Incoming { get; set; }
    public DataTemplate? Outgoing { get; set; }
    public DataTemplate? Bare { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        DayItem => Day,
        UnreadItem => Unread,
        MessageItem message => message.TemplateKey switch
        {
            "System" => System,
            "Bare" => Bare,
            "Outgoing" => Outgoing,
            _ => Incoming,
        },
        _ => null,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
