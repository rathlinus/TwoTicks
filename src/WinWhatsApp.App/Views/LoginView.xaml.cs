using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using QRCoder;
using Windows.System;
using WinWhatsApp.App.Models;
using WinWhatsApp.Core;

namespace WinWhatsApp.App.Views;

/// <summary>Linking: the QR code to scan with the phone, or a code to type into it.</summary>
public sealed partial class LoginView : UserControl
{
    private string? _shownCode;

    public LoginView()
    {
        InitializeComponent();
        Session.PropertyChanged += OnSessionChanged;
        Refresh();
    }

    private static Session Session => App.Current.Session;

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Session.QrCode) or nameof(Session.State) or nameof(Session.StateMessage))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        string? code = Session.QrCode;
        bool expired = Session.State == "qr" && Session.StateMessage == "expired";
        if (code != _shownCode && code is not null)
        {
            _shownCode = code;
            using var generator = new QRCodeGenerator();
            using QRCodeData data = generator.CreateQrCode(code, QRCodeGenerator.ECCLevel.L);
            byte[] png = new PngByteQRCode(data).GetGraphic(10, [0x11, 0x1B, 0x21], [0xFF, 0xFF, 0xFF], drawQuietZones: false);
            QrImage.Source = Images.FromBytes(png);
        }
        QrProgress.IsActive = code is null && !expired;
        QrImage.Opacity = expired ? 0.12 : 1;
        ReloadButton.Visibility = expired ? Visibility.Visible : Visibility.Collapsed;

        string? problem = Session.State == "qr" && !expired ? Session.StateMessage : Session.ConnectionText;
        StatusText.Text = problem ?? "";
        StatusText.Visibility = string.IsNullOrEmpty(problem) || Session.State is "connecting" or "starting" ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        ReloadButton.Visibility = Visibility.Collapsed;
        QrProgress.IsActive = true;
        _shownCode = null;
        await Session.ReloadQrAsync();
    }

    private void OnSwitchClick(object sender, RoutedEventArgs e)
    {
        bool showPhone = PhonePanel.Visibility == Visibility.Collapsed;
        PhonePanel.Visibility = showPhone ? Visibility.Visible : Visibility.Collapsed;
        QrSteps.Visibility = showPhone ? Visibility.Collapsed : Visibility.Visible;
        SwitchButton.Content = showPhone ? Loc.T("login.scanQr") : Loc.T("login.linkWithPhone");
        if (showPhone)
        {
            PhoneBox.Focus(FocusState.Programmatic);
        }
    }

    private void OnPhoneKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnGetCodeClick(sender, e);
        }
    }

    private async void OnGetCodeClick(object sender, RoutedEventArgs e)
    {
        if (PhoneBox.Text.Count(char.IsDigit) < 7)
        {
            StatusText.Text = Loc.T("login.wholeNumber");
            StatusText.Visibility = Visibility.Visible;
            return;
        }
        CodeButton.IsEnabled = false;
        try
        {
            string code = await Session.Client.PairWithPhoneAsync(PhoneBox.Text);
            CodeText.Text = code.Length == 8 ? code[..4] + "-" + code[4..] : code;
            CodePanel.Visibility = Visibility.Visible;
            StatusText.Visibility = Visibility.Collapsed;
        }
        catch (BridgeException ex)
        {
            StatusText.Text = ex.Message;
            StatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            CodeButton.IsEnabled = true;
        }
    }
}
