using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Controls;

public sealed partial class Ticks : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(int), typeof(Ticks), new PropertyMetadata(int.MinValue, (d, _) => ((Ticks)d).Refresh()));

    public Ticks()
    {
        InitializeComponent();
    }

    public int Status
    {
        get => (int)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    private void Refresh()
    {
        int status = Status;
        string? grey = status switch
        {
            MessageStatus.Pending => "Pending",
            MessageStatus.Sent => "Sent",
            MessageStatus.Delivered => "Delivered",
            _ => null,
        };
        if (grey is not null)
        {
            Grey.Kind = grey;
            Grey.Size = status == MessageStatus.Pending ? 12 : 16;
        }
        Grey.Visibility = grey is null ? Visibility.Collapsed : Visibility.Visible;
        Read.Visibility = status >= MessageStatus.Read ? Visibility.Visible : Visibility.Collapsed;
        Failed.Visibility = status == MessageStatus.Failed ? Visibility.Visible : Visibility.Collapsed;
    }
}
